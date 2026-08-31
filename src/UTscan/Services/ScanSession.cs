using System;
using System.Collections.Generic;
using UTscan.Core.Enums;
using UTscan.Core.Models;
using UTscan.Services.SignalProcessing;

namespace UTscan.Services;

/// <summary>
/// 扫查会话（P3）：承载 C 扫成像矩阵与原始 A 扫累积的纯数据状态。
/// 从 ScanForm 抽离，使扫查数据与窗体生命周期解耦、可脱离 UI 测试。
/// 线程契约：EngineOnPointDataReady 由采集回调线程调用，内部 _lock 串行化。
/// </summary>
public sealed class ScanSession
{
    private readonly GateAnalyzer _gateAnalyzer = new();

    // 成像矩阵（行=Y 方向，列=X 方向）
    private float[,] _matrix = new float[0, 0];
    private float _min = float.MaxValue, _max = float.MinValue;
    private readonly object _lock = new();

    // 原始 A 扫累积（.adtx 导出数据源）
    private readonly List<float[]> _scanAscans = new();
    private readonly List<float> _scanPositions = new();
    private readonly Dictionary<int, int> _pointIndexMap = new();   // 矩阵格 → 波形索引
    private float _lastSampleRate;

    // 最近一次扫查几何
    private ScanRegion? _lastRegion;
    private float _lastStartX, _lastStartY, _lastStepX;

    /// <summary>当前矩阵行数</summary>
    public int Rows => _matrix.GetLength(0);
    /// <summary>当前矩阵列数</summary>
    public int Cols => _matrix.GetLength(1);
    /// <summary>当前最小值（增量维护）</summary>
    public float Min => _min;
    /// <summary>当前最大值（增量维护）</summary>
    public float Max => _max;

    /// <summary>最近一次扫查原始数据快照（B 扫视图数据源）</summary>
    public (float[][] Ascans, float[] Positions, float SampleRate) GetScanData()
    {
        lock (_lock)
        {
            return (_scanAscans.ToArray(), _scanPositions.ToArray(), _lastSampleRate);
        }
    }

    /// <summary>初始化矩阵尺寸并清空累积状态（新扫查开始前调用）。</summary>
    public void BeginScan(ScanRegion region, int cols, int rows, float startX, float startY, float stepX)
    {
        lock (_lock)
        {
            _matrix = new float[rows, cols];
            _min = float.MaxValue;
            _max = float.MinValue;
            _scanAscans.Clear();
            _scanPositions.Clear();
            _pointIndexMap.Clear();
            _lastRegion = region;
            _lastStartX = startX;
            _lastStartY = startY;
            _lastStepX = stepX;
            _lastSampleRate = 0f;
        }
    }

    /// <summary>
    /// 处理单点数据（采集回调线程）：闸门成像值写入矩阵 + 累积原始波形。
    /// 由扫查事件订阅者调用（原 ScanForm.EngineOnPointDataReady）。
    /// </summary>
    public void OnPointData(
        float x, float y, AScanData data,
        ScanSnapshot snap, TcgCurve tcg)
    {
        int ix = snap.StepX > 0 ? (int)Math.Round((x - snap.StartX) / snap.StepX) : 0;
        int iy = snap.StepY > 0 ? (int)Math.Round((y - snap.StartY) / snap.StepY) : 0;
        if (ix < 0 || ix >= snap.Cols || iy < 0 || iy >= snap.Rows) return;

        var gate = new GateConfig
        {
            Name = "C扫",
            StartUs = snap.GateStartUs,
            WidthUs = snap.GateWidthUs,
            ThresholdV = snap.GateThresholdV
        };
        float v = _gateAnalyzer.ComputeImagingValue(data, gate, snap.ImagingMode, snap.WaveType, tcg);

        lock (_lock)
        {
            _matrix[iy, ix] = v;
            if (v < _min) _min = v;
            if (v > _max) _max = v;
            _scanAscans.Add((float[])data.Samples.Clone());
            _scanPositions.Add(x);
            _lastSampleRate = data.SampleRate > 0 ? data.SampleRate : _lastSampleRate;
            _pointIndexMap[iy * snap.Cols + ix] = _scanAscans.Count - 1;
        }
    }

    /// <summary>矩阵值（索引检查安全）</summary>
    public float GetValue(int iy, int ix)
    {
        lock (_lock)
        {
            if (iy < 0 || iy >= _matrix.GetLength(0) || ix < 0 || ix >= _matrix.GetLength(1)) return 0f;
            return _matrix[iy, ix];
        }
    }

    /// <summary>获取矩阵行（渲染用）</summary>
    public float[] GetRow(int iy)
    {
        lock (_lock)
        {
            int cols = _matrix.GetLength(1);
            var row = new float[cols];
            for (int ix = 0; ix < cols; ix++) row[ix] = _matrix[iy, ix];
            return row;
        }
    }

    /// <summary>矩阵格 → 波形索引（C 扫双击跳转）</summary>
    public bool TryGetPointIndex(int iy, int ix, out int index)
    {
        lock (_lock) { return _pointIndexMap.TryGetValue(iy * Cols + ix, out index); }
    }

    /// <summary>克隆整个矩阵（渲染用，避免持锁渲染）</summary>
    public float[,] CloneMatrix()
    {
        lock (_lock)
        {
            return (float[,])_matrix.Clone();
        }
    }

    /// <summary>是否有可导出的最近扫查数据</summary>
    public bool HasData
    {
        get { lock (_lock) { return _scanAscans.Count > 0; } }
    }

    /// <summary>导出/切片数据快照（.adtx 导出、D 扫、B 扫数据源）</summary>
    public (float[][] Ascans, float[] Positions, ScanRegion? Region, float SampleRate) GetExportData()
    {
        lock (_lock)
        {
            return (_scanAscans.ToArray(), _scanPositions.ToArray(), _lastRegion, _lastSampleRate);
        }
    }

    /// <summary>整体替换矩阵与 min/max（离线滤波重算 / ADTX 导入后回填）</summary>
    public void ReplaceMatrix(float[,] matrix, float min, float max)
    {
        lock (_lock)
        {
            _matrix = matrix;
            _min = min;
            _max = max;
        }
    }

    /// <summary>设置原始 A 扫累积数据（ADTX 导入），不清除点位映射。随后调用方需重建矩阵。</summary>
    public void SetRawAscans(float[][] ascans, float[] positions, ScanRegion? region, float sampleRate)
    {
        lock (_lock)
        {
            _scanAscans.Clear();
            _scanPositions.Clear();
            _scanAscans.AddRange(ascans);
            _scanPositions.AddRange(positions);
            _lastRegion = region;
            _lastSampleRate = sampleRate;
        }
    }

    /// <summary>重建点位映射（ADTX 导入填充矩阵后调用）</summary>
    public void RebuildPointIndexMap(int rows, int cols)
    {
        lock (_lock)
        {
            _pointIndexMap.Clear();
            for (int i = 0; i < _scanAscans.Count && i < rows * cols; i++)
                _pointIndexMap[i] = i;
        }
    }
}

/// <summary>
/// 不可变扫描参数快照（P3：从 ScanForm 提升共享）。
/// 写入在 UI 线程（OnStart），读取在回调线程——不可变 record 保证无并发问题。
/// </summary>
public sealed record ScanSnapshot(
    float StartX, float StartY, float StepX, float StepY, int Cols, int Rows,
    float GateStartUs, float GateWidthUs, float GateThresholdV,
    CScanImagingMode ImagingMode, WaveformType WaveType);
