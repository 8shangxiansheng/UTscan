using System.Windows.Forms;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Services;
using UTscan.Services.SignalProcessing;

namespace UTscan.UI.Forms;

/// <summary>
/// 扫查成像窗体
/// </summary>
public partial class ScanForm : Form
{
    private readonly IScanEngine _engine;
    private readonly IMotionController _motion;
    private readonly IDataAcquisition _daq;
    private readonly ConnectionConfig _config;

    private NumericUpDown _numStartX = null!, _numStartY = null!, _numWidth = null!, _numHeight = null!, _numStepX = null!, _numStepY = null!;
    private NumericUpDown _numGateStart = null!, _numGateWidth = null!;
    private NumericUpDown _numVelocity = null!, _numAcceleration = null!;
    private ComboBox _cmbMode = null!;
    private ComboBox _cmbStrategy = null!;
    private ComboBox _cmbWaveType = null!;
    private Button _btnStart = null!, _btnStop = null!, _btnPause = null!, _btnResume = null!, _btnSaveImage = null!, _btnResumeScan = null!;
    // P1-B：离线滤波（中值/低通）+ D 扫视图
    private Button _btnFilterMedian = null!, _btnFilterLowPass = null!, _btnDScan = null!;
    private Label _lblStatus = null!;
    private PictureBox _pic = null!;
    private ProgressBar _progressBar = null!;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// H-1 接线（三）：扫查完成（或停止）时触发，携带最近一次扫查的原始数据，
    /// 供 MainForm 推送到 B 扫视图（点扫模式无 LineScanComplete，需此路径）。
    /// </summary>
    public event EventHandler? ScanDataUpdated;

    /// <summary>获取最近一次扫查数据快照（B 扫视图数据源）。P3：委托 ScanSession。</summary>
    public (float[][] Ascans, float[] Positions, float SampleRate) GetScanData()
        => _session.GetScanData();

    // 成像链接线（修复审查报告 P0-2 / 短板①：GateAnalyzer 闸门成像 + CScanImageService LockBits 渲染）
    private readonly GateAnalyzer _gateAnalyzer = new();
    private readonly CScanImageService _cscanImage = new();
    /// <summary>P3：扫查会话（C 扫矩阵 + 原始 A 扫累积的数据状态，与窗体生命周期解耦）</summary>
    private readonly ScanSession _session = new();

    // TCG（时间补偿增益/深度补偿）：随深度自动提升接收增益，厚大衰减工件定量关键
    private readonly TcgCurve _tcg = new();
    /// <summary>TCG 曲线实例（共享给 MainForm 统一入口）</summary>
    public TcgCurve TcgCurve => _tcg;
    private CheckBox _chkTcg = null!;
    private Button _btnTcgEdit = null!;

    // 批次2：颜色条选择（5 种预设色带，可切换）+ 自定义显示范围（自动/手动）
    private Colormap _colormap = Colormap.Jet;
    private ComboBox _cmbColormap = null!;
    private CheckBox _chkAutoRange = null!;
    private NumericUpDown _numDispMin = null!, _numDispMax = null!;
    private PictureBox _picColorBar = null!;
    private Label _lblCbMax = null!, _lblCbMin = null!;

    // 渲染节流：热图重绘最快 150ms 一次，避免每个扫查点全图渲染冻结 UI
    private const int RenderIntervalMs = 150;
    private long _lastRenderTick;

    // 扫查参数快照：写入只在 OnStart（UI 线程），读取在事件回调线程。
    // 通过 Interlocked.Exchange 原子替换引用，保证回调线程读到完整一致的快照。
    private volatile ScanSnapshot? _snapshot;

    /// <summary>
    /// 4-FIX（审查 20260828）：C 扫成像闸门阈值——由外部（MainForm）从 A 扫当前数据闸门
    /// 同步传入，替代各成像路径硬编码 0.05V。默认 0.05V 保持旧行为。
    /// </summary>
    public float GateThresholdV { get; set; } = 0.05f;

    public ScanForm(IScanEngine engine, IMotionController motion, IDataAcquisition daq, ConnectionConfig config)
    {
        _engine = engine;
        _motion = motion;
        _daq = daq;
        _config = config;

        Text = "扫查成像";
        ClientSize = new System.Drawing.Size(800, 500);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        BuildUI();

        _engine.PointDataReady += EngineOnPointDataReady;
        _engine.ProgressChanged += EngineOnProgressChanged;

        FormClosed += (_, _) =>
        {
            _engine.PointDataReady -= EngineOnPointDataReady;
            _engine.ProgressChanged -= EngineOnProgressChanged;
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
        };
    }

    // ── 批次2：颜色条 / 显示范围变更处理 ──

    /// <summary>切换色带：按下拉索引取 Colormap.Presets 预设，立即重绘热图与色条</summary>
    private void OnColormapChanged(object? sender, EventArgs e)
    {
        int i = Math.Clamp(_cmbColormap.SelectedIndex, 0, Colormap.Presets.Length - 1);
        _colormap = Colormap.Presets[i];
        RenderHeatmap();
    }

    /// <summary>显示范围切换：自动=按数据 min/max 归一化；手动=用户指定上下限（低于下限全部压到色带低端）</summary>
    private void OnDisplayRangeChanged(object? sender, EventArgs e)
    {
        bool auto = _chkAutoRange.Checked;
        _numDispMin.Enabled = !auto;
        _numDispMax.Enabled = !auto;
        RenderHeatmap();
    }

    private void EngineOnPointDataReady(object? sender, PointDataReadyEventArgs e)
    {
        var snap = _snapshot;
        if (snap == null) return;

        // P3：成像值计算与矩阵/波形累积下沉至 ScanSession
        _session.OnPointData(e.X, e.Y, e.Data, snap, _tcg);
    }

    private void EngineOnProgressChanged(object? sender, ScanProgressEventArgs e)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateProgress(e)); return; }
        UpdateProgress(e);
    }

    private void UpdateProgress(ScanProgressEventArgs e)
    {
        _lblStatus.Text = $"{e.ProgressPercent:0.0}%  ({e.CompletedPoints}/{e.TotalPoints})";
        _progressBar.Value = Math.Min(100, (int)e.ProgressPercent);

        // 渲染节流：距上次渲染不足 RenderIntervalMs 则跳过本轮重绘
        long now = Environment.TickCount64;
        if (now - _lastRenderTick >= RenderIntervalMs)
        {
            _lastRenderTick = now;
            RenderHeatmap();
        }
    }

    private void OnStart(object? sender, EventArgs e)
    {
        if (_engine.IsScanning) return;

        float startX = (float)_numStartX.Value;
        float startY = (float)_numStartY.Value;
        float stepX = (float)_numStepX.Value;
        float stepY = (float)_numStepY.Value;
        var region = new ScanRegion { StartX = startX, StartY = startY, Width = (float)_numWidth.Value, Height = (float)_numHeight.Value, StepX = stepX, StepY = stepY };
        int cols = region.PointCountX;   // 公式单一来源（审查 P2-2）
        int rows = region.PointCountY;

        // NH-7 修复：矩阵分配前校验总点数与内存上限——
        // UI 上限 1000mm/0.01mm 步距可算出 ~100001² 点（约 40GB），x86 进程必 OOM。
        long totalPoints = (long)rows * cols;
        const long MaxMatrixPoints = 50_000_000;      // 5000 万点（约 200MB float）
        const long MaxMatrixBytes = 512L * 1024 * 1024; // 512MB 上限（x86 地址空间保护）
        long matrixBytes = totalPoints * sizeof(float);
        if (totalPoints > MaxMatrixPoints || matrixBytes > MaxMatrixBytes)
        {
            MessageBox.Show(this,
                $"扫查点数过多（{rows:N0}×{cols:N0} = {totalPoints:N0} 点，约 {matrixBytes / 1024.0 / 1024 / 1024:F1} GB），" +
                $"超出安全上限（{MaxMatrixPoints:N0} 点 / 512MB）。请增大步距或减小区域（防内存耗尽）",
                "扫查参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // P3：矩阵与累积状态委托 ScanSession 初始化
        _session.BeginScan(region, cols, rows, startX, startY, stepX);
        _lastRenderTick = 0;
        _snapshot = new ScanSnapshot(startX, startY, stepX, stepY, cols, rows,
            (float)_numGateStart.Value, (float)_numGateWidth.Value, GateThresholdV,
            SelectedImagingMode(), SelectedWaveType());

        var parameters = new ScanParams
        {
            Mode = ScanMode.Raster,
            // 审查 P1-3 接线：Strategy 原无 UI 入口，编码器触发行缓存逻辑为死代码路径
            Strategy = _cmbStrategy.SelectedIndex == 1 ? ScanStrategy.EncoderTriggered : ScanStrategy.PointByPoint,
            // 批次1：扫查速度/加速度由 UI 输入（原硬编码 10/50）
            Velocity = (float)_numVelocity.Value,
            Acceleration = (float)_numAcceleration.Value,
            // H-1 修复：TriggerIo 从 ConnectionConfig 传递到 ScanParams，
            // 修复真机严格单次触发路径不可达的接线断层。
            TriggerIo = _config.TriggerIo,
            TriggerPulseWidthMs = _config.TriggerPulseWidthMs
        };

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _engine.ClearBreakpoint();   // 新开始扫查：清除旧断点
        _lblStatus.Text = "扫查中...";
        _btnStart.Enabled = false;
        _btnPause.Enabled = true;
        _btnResume.Enabled = false;
        _btnResumeScan.Enabled = false;

        Task.Run(async () =>
        {
            try
            {
                await _engine.StartScanAsync(region, parameters, _cts.Token);
                if (!IsDisposed && !Disposing) BeginInvoke(() => { _lblStatus.Text = $"完成，共 {rows * cols} 个点"; _progressBar.Value = 100; RenderHeatmap(); });
                ScanDataUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { if (!IsDisposed && !Disposing) BeginInvoke(() => _lblStatus.Text = "已取消"); ScanDataUpdated?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { if (!IsDisposed && !Disposing) BeginInvoke(() => _lblStatus.Text = $"失败：{ex.Message}"); ScanDataUpdated?.Invoke(this, EventArgs.Empty); }
            finally { if (!IsDisposed && !Disposing) BeginInvoke(() => { _btnStart.Enabled = true; _btnPause.Enabled = false; _btnResume.Enabled = false; }); }
        });
    }

    private void OnStop()
    {
        _lblStatus.Text = "已请求停止";
        _btnPause.Enabled = false;
        _btnResume.Enabled = false;
        _ = _engine.StopAsync();
        // 停止后若产生断点，启用续扫（异步，StopAsync 立即返回但断点在循环 finally 记录）
        _ = Task.Delay(500).ContinueWith(_ => { if (!IsDisposed && !Disposing) BeginInvoke(RefreshResumeScanButton); });
    }

    /// <summary>断点续扫（20260828）：从中断点恢复——保留已扫数据（不重扫、不重复），
    /// 从上次停止行继续。无断点时不可用。</summary>
    private async Task OnResumeScanAsync()
    {
        if (_engine.IsScanning || !_engine.HasBreakpoint) return;
        _lblStatus.Text = $"续扫中（从 {_engine.BreakpointPercent:0}% 处继续）...";
        _btnStart.Enabled = false;
        _btnResumeScan.Enabled = false;
        _btnPause.Enabled = true;
        _btnResume.Enabled = false;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            bool ok = await _engine.ResumeFromBreakpointAsync(_cts.Token);
            if (ok && !IsDisposed && !Disposing)
                BeginInvoke(() => { _lblStatus.Text = "续扫完成"; _progressBar.Value = 100; RenderHeatmap(); });
            else if (!ok && !IsDisposed && !Disposing)
                BeginInvoke(() => _lblStatus.Text = "续扫未完成（已停止）");
            ScanDataUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing) BeginInvoke(() => _lblStatus.Text = "续扫已取消");
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing) BeginInvoke(() => _lblStatus.Text = $"续扫失败：{ex.Message}");
        }
        finally
        {
            if (!IsDisposed && !Disposing)
                BeginInvoke(() => { _btnStart.Enabled = true; _btnPause.Enabled = false; _btnResume.Enabled = false; RefreshResumeScanButton(); });
        }
    }

    /// <summary>刷新续扫按钮可用性（停止/异常后出现断点 → 启用）</summary>
    private void RefreshResumeScanButton()
    {
        if (_btnResumeScan == null) return;
        bool has = _engine.HasBreakpoint && !_engine.IsScanning;
        _btnResumeScan.Enabled = has;
        _btnResumeScan.Text = has ? $"续扫（{_engine.BreakpointPercent:0}%）" : "续扫";
    }

    /// <summary>P0-F：保存当前 C 扫热图（bmp/png，说明书数据分析输出）</summary>
    private void SaveCScanImage()
    {
        var img = _pic.Image;
        if (img == null)
        {
            MessageBox.Show(this, "当前无 C 扫图像（请先执行扫查）", "保存图像",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Title = "保存 C 扫图像",
            Filter = "PNG 图像 (*.png)|*.png|位图 (*.bmp)|*.bmp|JPEG 图像 (*.jpg)|*.jpg",
            DefaultExt = "png",
            FileName = $"cscan-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            img.Save(dlg.FileName);
            MessageBox.Show(this, $"C 扫图像已保存到：{dlg.FileName}", "保存图像",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task OnPauseAsync()
    {
        try
        {
            await _engine.PauseAsync();
            _btnPause.Enabled = false;
            _btnResume.Enabled = true;
            _lblStatus.Text = "已暂停";
        }
        catch (Exception ex) { _lblStatus.Text = $"暂停失败：{ex.Message}"; }
    }

    private async Task OnResumeAsync()
    {
        try
        {
            await _engine.ResumeAsync();
            _btnPause.Enabled = true;
            _btnResume.Enabled = false;
            _lblStatus.Text = "扫查中...";
        }
        catch (Exception ex) { _lblStatus.Text = $"继续失败：{ex.Message}"; }
    }

    // M-7：下拉文字与枚举显式映射（审查报告 M-7：补全 9 种成像模式；
    // 保持原 UI 习惯——索引 0 "峰值幅度"= MaxPeak，不按枚举顺序直排）
    private CScanImagingMode SelectedImagingMode() => _cmbMode.SelectedIndex switch
    {
        1 => CScanImagingMode.PeakPeak,
        2 => CScanImagingMode.PositivePeak,
        3 => CScanImagingMode.NegativePeak,
        4 => CScanImagingMode.TofPositivePeak,
        5 => CScanImagingMode.TofNegativePeak,
        6 => CScanImagingMode.TofPositiveThreshold,
        7 => CScanImagingMode.TofNegativeThreshold,
        8 => CScanImagingMode.PhaseReversal,
        9 => CScanImagingMode.Mean,
        _ => CScanImagingMode.MaxPeak
    };

    // D2：成像波形类型（下拉索引与 WaveformType 枚举一一对应：0=射频,1=检波,2=正半波,3=负半波）
    private WaveformType SelectedWaveType() => (WaveformType)_cmbWaveType.SelectedIndex;

    // P1-B：离线滤波类型
    private enum FilterKind { Median, LowPass }

    /// <summary>
    /// P1-B：离线滤波（说明书 3.6 滤波）——对已采集的全部 A 扫应用中值/低通滤波，
    /// 重算成像值并重渲染 C 扫。滤波不改变原始累积数据（新建数组），可反复应用。
    /// </summary>
    private void ApplyOfflineFilter(FilterKind kind)
    {
        // P3：数据快照委托 ScanSession
        var data = _session.GetExportData();
        if (data.Ascans.Length == 0)
        {
            MessageBox.Show(this, "无扫查数据（请先执行扫查）", "滤波", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        float[][] ascans = data.Ascans;
        float sampleRate = data.SampleRate;

        try
        {
            // 滤波处理（中值：kernel=7 与旧项目一致；低通：数字角频率通带 0.5 / 阻带 0.7 rad/sample
            // ≈ 通带 8MHz / 阻带 11MHz @100MS/s，覆盖超声探头典型频带）
            var median = new MedianFilter();
            var lowPass = FirFilter.LowPass(0.5, 0.7, FirWindow.Hamming);
            for (int i = 0; i < ascans.Length; i++)
            {
                ascans[i] = kind == FilterKind.Median
                    ? median.Apply(ascans[i], 7)
                    : lowPass.Filter(ascans[i]);
            }

            // 按滤波后数据重算成像值矩阵
            var snap = _snapshot;
            if (snap == null) return;
            var gate = new GateConfig { StartUs = snap.GateStartUs, WidthUs = snap.GateWidthUs, ThresholdV = snap.GateThresholdV };
            var newMatrix = new float[snap.Rows, snap.Cols];
            float nmin = float.MaxValue, nmax = float.MinValue;
            int pt = 0;
            for (int iy = 0; iy < snap.Rows; iy++)
            {
                for (int ix = 0; ix < snap.Cols; ix++)
                {
                    if (pt < ascans.Length)
                    {
                        var d = new AScanData { Samples = ascans[pt++], SampleRate = sampleRate };
                        float v = _gateAnalyzer.ComputeImagingValue(d, gate, snap.ImagingMode, snap.WaveType, _tcg);
                        newMatrix[iy, ix] = v;
                        if (v < nmin) nmin = v;
                        if (v > nmax) nmax = v;
                    }
                }
            }

            // P3：矩阵回填 ScanSession
            _session.ReplaceMatrix(newMatrix, nmin, nmax);
            RenderHeatmap();
            _lblStatus.Text = $"已应用{(kind == FilterKind.Median ? "中值" : "低通")}滤波（{ascans.Length} 条 A 扫）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"滤波失败：{ex.Message}", "滤波", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// P1-B：D 扫视图——沿固定 X 列切片的多行 A 扫数据（Y-深度截面随时间变化）。
    /// 数据已具备（_scanAscans 按行主序累积），按列切后复用 BScanImageService 渲染。
    /// 实现：打开 BScanForm 传入按列切片数据（等效 D 扫 = 某一 X 列的 Y-深度图）。
    /// </summary>
    private void OpenDScanView()
    {
        // P3：数据快照委托 ScanSession
        var data = _session.GetExportData();
        if (data.Ascans.Length == 0)
        {
            MessageBox.Show(this, "无扫查数据（请先执行扫查）", "D 扫", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        float[][] ascans = data.Ascans;
        float[] positions = data.Positions;
        float sampleRate = data.SampleRate;

        // D 扫 = 固定列（取中间列）的 Y-深度切片：行 = Y 索引，列 = 深度（复用 B 扫渲染）
        var snap = _snapshot;
        if (snap == null || snap.Cols <= 1) return;
        int midCol = snap.Cols / 2;
        var dAscans = new List<float[]>();
        var dPositions = new List<float>();
        for (int iy = 0; iy < snap.Rows; iy++)
        {
            int idx = iy * snap.Cols + midCol;
            if (idx < ascans.Length)
            {
                dAscans.Add(ascans[idx]);
                dPositions.Add(positions[idx]);
            }
        }

        var bscan = Application.OpenForms.OfType<BScanForm>().FirstOrDefault();
        if (bscan == null)
        {
            bscan = new BScanForm();
            bscan.Text = "D 扫（Y-深度截面）";
            bscan.Show();
        }
        bscan.UpdateData(dAscans.ToArray(), dPositions.ToArray(), sampleRate);
        _lblStatus.Text = $"D 扫已更新（X 列 {midCol + 1}/{snap.Cols}，{dAscans.Count} 行）";
    }

    /// <summary>
    /// 渲染热图。委托 CScanImageService.Render（LockBits 像素块写入）。
    /// 批次2：着色上下界支持两种模式——
    ///  自动：使用回调线程增量维护的 _min/_max；
    ///  手动：使用用户在 UI 指定的显示下限/上限（自定义颜色区间，超限值钳位到色带端点）。
    /// 同时刷新右侧颜色条与上下限标注。
    /// </summary>
    private void RenderHeatmap()
    {
        // P3：矩阵与范围委托 ScanSession（克隆渲染，避免持锁）
        int rows = _session.Rows, cols = _session.Cols;
        if (rows == 0 || cols == 0) return;
        float[,] matrix = _session.CloneMatrix();
        float min = _session.Min;
        float max = _session.Max;

        // 手动范围：钳位 NumericUpDown 值，保证 min<max
        if (!_chkAutoRange.Checked)
        {
            min = (float)_numDispMin.Value;
            max = (float)_numDispMax.Value;
        }
        if (min > max) { min = 0f; max = 1f; }             // 尚无数据点 / 范围无效
        if (max - min < 1e-9f) max = min + 1f;

        // 单格像素自适应：总图控制在约 1000×1000 以内
        int cellW = Math.Clamp(1024 / cols, 1, 16);
        int cellH = Math.Clamp(1024 / rows, 1, 16);

        Bitmap bmp;
        try { bmp = _cscanImage.Render(matrix, _colormap, min, max, cellW, cellH); }
        catch (Exception) { return; }                       // 尺寸超限等异常不致命，跳过本轮

        var old = _pic.Image;
        _pic.Image = bmp;
        old?.Dispose();

        RefreshColorBar(min, max);
    }

    /// <summary>刷新右侧颜色条位图与上下限数值标注（批次2）</summary>
    private void RefreshColorBar(float min, float max)
    {
        var old = _picColorBar.Image;
        _picColorBar.Image = _cscanImage.RenderColorBar(_colormap, min, max, 44, 300);
        old?.Dispose();
        _lblCbMax.Text = $"{max:G4}";
        _lblCbMin.Text = $"{min:G4}";
    }

    // ── 批次2：C 扫双击跳转 A 扫（联动导航）──

    /// <summary>
    /// 双击 C 扫热图：像素坐标 → 矩阵格 (ix,iy) → 该点原始 A 扫波形，
    /// 打开 AscanDetailForm 显示冻结波形 + 当前闸门测量，标题带物理坐标与成像值。
    /// </summary>
    private void OnCScanDoubleClick(object? sender, EventArgs e)
    {
        var img = _pic.Image;
        var snap = _snapshot;
        if (img == null || snap == null) return;

        // PictureBox 为 Zoom 模式：控件坐标 → 图像坐标需按等比缩放 + 居中偏移换算
        var cursorPos = _pic.PointToClient(Cursor.Position);
        if (!MapZoomToImage(_pic, img.Width, img.Height, cursorPos.X, cursorPos.Y, out int imgX, out int imgY))
            return;

        // 图像坐标 → 矩阵列/行
        int ix = imgX * snap.Cols / img.Width;
        int iy = imgY * snap.Rows / img.Height;
        if (ix < 0 || ix >= snap.Cols || iy < 0 || iy >= snap.Rows) return;

        // P3：跳转索引/波形/成像值委托 ScanSession
        if (!_session.TryGetPointIndex(iy, ix, out int idx)) return;   // 该格尚无数据
        var export = _session.GetExportData();
        float[] samples = (float[])export.Ascans[idx].Clone();
        float sampleRate = export.SampleRate;
        float value = _session.GetValue(iy, ix);

        float physX = snap.StartX + ix * snap.StepX;
        float physY = snap.StartY + iy * snap.StepY;
        var data = new AScanData { Samples = samples, SampleRate = sampleRate };

        // 闸门取当前 UI 参数，与 C 扫成像闸门一致
        var gate = new GateConfig
        {
            Name = "C扫闸门",
            StartUs = snap.GateStartUs,
            WidthUs = snap.GateWidthUs,
            ThresholdV = snap.GateThresholdV   // 4-FIX：同步快照阈值
        };

        var detail = new AscanDetailForm(data,
            $"({physX:0.###}, {physY:0.###}) mm  [{ix},{iy}]  成像值 {value:G4}", new[] { gate })
        {
            MdiParent = MdiParent   // 与扫查窗体同为 MDI 子窗体，便于并排对照
        };
        detail.Show();
    }

    /// <summary>
    /// Zoom 模式下控件坐标 → 原图像坐标换算（等比缩放 + 居中）。
    /// 返回 false 表示点击落在图像显示区之外。
    /// </summary>
    private static bool MapZoomToImage(PictureBox pic, int imgW, int imgH, int cx, int cy, out int imgX, out int imgY)
    {
        imgX = imgY = 0;
        int pw = pic.ClientSize.Width, ph = pic.ClientSize.Height;
        if (pw <= 0 || ph <= 0 || imgW <= 0 || imgH <= 0) return false;

        float scale = Math.Min((float)pw / imgW, (float)ph / imgH);
        float dispW = imgW * scale, dispH = imgH * scale;
        float offX = (pw - dispW) / 2f, offY = (ph - dispH) / 2f;

        if (cx < offX || cx > offX + dispW || cy < offY || cy > offY + dispH) return false;
        imgX = (int)((cx - offX) / scale);
        imgY = (int)((cy - offY) / scale);
        return imgX >= 0 && imgX < imgW && imgY >= 0 && imgY < imgH;
    }

    // ── H-1 接线：保存/加载设置（.acf）—— 扫查窗体负责其参数子集 ──

    /// <summary>从当前 UI 构建扫查相关配置（供 MainForm 保存设置聚合）</summary>
    public UTscan.Services.ScanSessionConfig BuildSessionConfig(UTscan.Services.ScanSessionConfig cfg)
    {
        cfg.Region = new ScanRegion
        {
            StartX = (float)_numStartX.Value,
            StartY = (float)_numStartY.Value,
            Width = (float)_numWidth.Value,
            Height = (float)_numHeight.Value,
            StepX = (float)_numStepX.Value,
            StepY = (float)_numStepY.Value
        };
        cfg.Scan = new ScanParams
        {
            Mode = ScanMode.Raster,
            Strategy = _cmbStrategy.SelectedIndex == 1 ? ScanStrategy.EncoderTriggered : ScanStrategy.PointByPoint,
            Velocity = (float)_numVelocity.Value,
            Acceleration = (float)_numAcceleration.Value,
            // D7-FIX（审查 20260828）：TriggerIo/脉宽随 .acf 持久化——触发配置来源单一化
            // （此前仅 hardware.json，.acf 加载不改变触发 IO，配置来源分裂）。
            TriggerIo = _config.TriggerIo,
            TriggerPulseWidthMs = _config.TriggerPulseWidthMs
        };
        cfg.ImagingMode = SelectedImagingMode();
        // D2：成像波形类型持久化（会话级，存于 Daq.WaveformType 供保存/加载一致性）
        cfg.Daq.WaveformType = SelectedWaveType();

        // 批次2：色带 + 显示范围持久化（手动范围时才写入上下限）
        cfg.ColormapName = _colormap.Name;
        cfg.DisplayMin = _chkAutoRange.Checked ? null : (float?)_numDispMin.Value;
        cfg.DisplayMax = _chkAutoRange.Checked ? null : (float?)_numDispMax.Value;
        return cfg;
    }

    /// <summary>将配置回填到当前 UI（供 MainForm 加载设置后应用）</summary>
    public void ApplySessionConfig(UTscan.Services.ScanSessionConfig cfg)
    {
        _numStartX.Value = ClampDecimal(cfg.Region.StartX, _numStartX);
        _numStartY.Value = ClampDecimal(cfg.Region.StartY, _numStartY);
        _numWidth.Value = ClampDecimal(cfg.Region.Width, _numWidth);
        _numHeight.Value = ClampDecimal(cfg.Region.Height, _numHeight);
        _numStepX.Value = ClampDecimal(cfg.Region.StepX, _numStepX);
        _numStepY.Value = ClampDecimal(cfg.Region.StepY, _numStepY);
        _cmbStrategy.SelectedIndex = cfg.Scan.Strategy == ScanStrategy.EncoderTriggered ? 1 : 0;
        // D7-FIX：.acf 显式配置了触发 IO（非默认 -1）时回写 _config——触发配置来源单一化。
        if (cfg.Scan.TriggerIo >= 0)
        {
            _config.TriggerIo = cfg.Scan.TriggerIo;
            _config.TriggerPulseWidthMs = cfg.Scan.TriggerPulseWidthMs;
        }
        _cmbMode.SelectedIndex = ImagingModeToIndex(cfg.ImagingMode);
        // D2：还原成像波形类型（合法范围 0..3）
        int wt = (int)cfg.Daq.WaveformType;
        _cmbWaveType.SelectedIndex = wt >= 0 && wt <= 3 ? wt : 1;
        if (cfg.Scan.Velocity > 0)
            _numVelocity.Value = ClampDecimal(cfg.Scan.Velocity, _numVelocity);
        if (cfg.Scan.Acceleration > 0)
            _numAcceleration.Value = ClampDecimal(cfg.Scan.Acceleration, _numAcceleration);

        // 批次2：色带 + 显示范围回填（色带按名称查预设，找不到回落 Jet）
        _colormap = Colormap.FromName(cfg.ColormapName ?? "Jet");
        int ci = Array.IndexOf(Colormap.Presets, _colormap);
        _cmbColormap.SelectedIndex = ci >= 0 ? ci : 0;

        if (cfg.DisplayMin is float dmin || cfg.DisplayMax is float dmax)
        {
            _chkAutoRange.Checked = false;
            if (cfg.DisplayMin is float mn) _numDispMin.Value = ClampDecimal(mn, _numDispMin);
            if (cfg.DisplayMax is float mx) _numDispMax.Value = ClampDecimal(mx, _numDispMax);
        }
        else
        {
            _chkAutoRange.Checked = true;
        }
    }

    private static decimal ClampDecimal(float v, NumericUpDown num)
    {
        decimal d = (decimal)v;
        return Math.Clamp(d, num.Minimum, num.Maximum);
    }

    private static int ImagingModeToIndex(CScanImagingMode mode) => mode switch
    {
        CScanImagingMode.PeakPeak => 1,
        CScanImagingMode.PositivePeak => 2,
        CScanImagingMode.NegativePeak => 3,
        CScanImagingMode.TofPositivePeak => 4,
        CScanImagingMode.TofNegativePeak => 5,
        CScanImagingMode.TofPositiveThreshold => 6,
        CScanImagingMode.TofNegativeThreshold => 7,
        CScanImagingMode.PhaseReversal => 8,
        _ => 0   // MaxPeak
    };

    // ── H-1 接线（二）：.adtx 导出数据源 ──

    /// <summary>是否有可导出的最近扫查数据（P3：委托 ScanSession）</summary>
    public bool HasScanData => _session.HasData;

    /// <summary>
    /// 将最近一次扫查的原始数据导出为 .adtx（审查 H-1 接线：AdtxDataService 原无消费者）。
    /// </summary>
    public void ExportScanDataToAdtx(string path)
    {
        // P3：数据快照委托 ScanSession
        var data = _session.GetExportData();
        if (data.Ascans.Length == 0)
            throw new InvalidOperationException("尚无扫查数据（请先执行一次扫查）");
        float[][] ascans = data.Ascans;
        float[] positions = data.Positions;
        ScanRegion? region = data.Region;
        float sampleRate = data.SampleRate;

        // 位置轴使用相对起点（B 扫横轴语义），样本数统一
        new AdtxDataService().Save(path, ascans, positions, region ?? new ScanRegion(),
            new SystemParams { SoundVelocity = 1480f }, sampleRate > 0 ? sampleRate : 100e6f);
    }

    /// <summary>导入 .adtx 数据到本窗体（供 B 扫视图展示，H-1 接线（二））。P3：数据委托 ScanSession。</summary>
    public void LoadAdtxData(AdtxData loaded)
    {
        // 同步刷新 C 扫热图（按加载数据重渲染）
        if (loaded.ColumnCount > 0 && loaded.SampleCount > 0)
        {
            int rows = loaded.Region.PointCountY;
            int cols = loaded.Region.PointCountX;

            // NEW-M-7 修复：ADTX 导入前校验矩阵内存——与扫描开始路径使用相同上限，
            // 防止损坏/异常 ADTX 文件触发超大 float[] 分配导致 x86 OOM。
            long totalPoints = (long)rows * cols;
            const long MaxMatrixPoints = 50_000_000;
            const long MaxMatrixBytes = 512L * 1024 * 1024;
            long matrixBytes = totalPoints * sizeof(float);
            if (totalPoints > MaxMatrixPoints || matrixBytes > MaxMatrixBytes)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Adtx] 导入拒绝: 矩阵 {rows}×{cols} = {totalPoints:N0} 点 ({matrixBytes / 1024.0 / 1024 / 1024:F1} GB) 超限");
                _lblStatus.Text = $"导入失败：矩阵过大（{totalPoints:N0} 点），超出 512MB 上限";
                return;
            }

            // IO1-FIX（审查 20260828）：原实现只分配矩阵不填充 → C 扫热图全空。
            // 用与扫查点位相同的 GateAnalyzer.ComputeImagingValue 逐点计算成像值，
            // 按行主序（row = 扫查 Y 索引，col = X 索引）填充，并重建 min/max 与点位映射。
            var gate = new GateConfig
            {
                Name = "C扫",
                StartUs = (float)_numGateStart.Value,
                WidthUs = (float)_numGateWidth.Value,
                ThresholdV = GateThresholdV   // 4-FIX：用 ScanForm 当前阈值（外部可同步）
            };
            CScanImagingMode mode = SelectedImagingMode();
            WaveformType wave = SelectedWaveType();
            var newMatrix = new float[rows, cols];
            float nmin = float.MaxValue, nmax = float.MinValue;
            int idx = 0;
            for (int iy = 0; iy < rows && idx < loaded.Ascans.Length; iy++)
            {
                for (int ix = 0; ix < cols && idx < loaded.Ascans.Length; ix++, idx++)
                {
                    var ad = new AScanData
                    {
                        Samples = loaded.Ascans[idx],
                        SampleRate = loaded.SampleRate
                    };
                    float v = _gateAnalyzer.ComputeImagingValue(ad, gate, mode, wave, _tcg);
                    newMatrix[iy, ix] = v;
                    if (v < nmin) nmin = v;
                    if (v > nmax) nmax = v;
                }
            }
            // P3：原始数据 + 矩阵 + 点位映射一次性回填 ScanSession
            _session.SetRawAscans(loaded.Ascans, loaded.Positions, loaded.Region, loaded.SampleRate);
            _session.ReplaceMatrix(newMatrix, nmin, nmax);
            _session.RebuildPointIndexMap(rows, cols);
        }
        _lblStatus.Text = $"已导入 {loaded.ColumnCount} 条 A 扫";
        RenderHeatmap();
    }
}
