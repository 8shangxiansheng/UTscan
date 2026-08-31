using UTscan.Core.Models;

namespace UTscan.Core.Interfaces;

/// <summary>
/// 扫查引擎接口
/// </summary>
public interface IScanEngine
{
    /// <summary>是否正在扫查</summary>
    bool IsScanning { get; }

    /// <summary>是否有可恢复的断点（手动/异常停止后 true，正常完成/新开始后 false）</summary>
    bool HasBreakpoint { get; }

    /// <summary>断点进度百分比（0-100，供 UI 显示"已扫 X%，从断点继续"）</summary>
    float BreakpointPercent { get; }

    /// <summary>进度变化事件</summary>
    event EventHandler<ScanProgressEventArgs>? ProgressChanged;

    /// <summary>点数据就绪事件</summary>
    event EventHandler<PointDataReadyEventArgs>? PointDataReady;

    /// <summary>单行扫描完成事件（仅编码器触发连续扫查策略触发）</summary>
    event EventHandler<LineScanCompleteEventArgs>? LineScanComplete;

    /// <summary>开始扫查</summary>
    Task StartScanAsync(ScanRegion region, ScanParams parameters, CancellationToken ct);

    /// <summary>断点续扫：从上次停止位置恢复（跳过已扫行/列，数据不重复）</summary>
    Task<bool> ResumeFromBreakpointAsync(CancellationToken ct = default);

    /// <summary>清除断点（放弃续扫，重新开始）</summary>
    void ClearBreakpoint();

    /// <summary>暂停扫查</summary>
    Task PauseAsync();

    /// <summary>恢复扫查</summary>
    Task ResumeAsync();

    /// <summary>停止扫查</summary>
    Task StopAsync();
}

/// <summary>
/// 扫查进度事件参数
/// </summary>
public class ScanProgressEventArgs : EventArgs
{
    /// <summary>进度百分比（0-100）</summary>
    public float ProgressPercent { get; set; }

    /// <summary>当前X坐标（mm）</summary>
    public float CurrentX { get; set; }

    /// <summary>当前Y坐标（mm）</summary>
    public float CurrentY { get; set; }

    /// <summary>总点数</summary>
    public int TotalPoints { get; set; }

    /// <summary>已完成点数</summary>
    public int CompletedPoints { get; set; }
}

/// <summary>
/// 点数据就绪事件参数
/// </summary>
public class PointDataReadyEventArgs : EventArgs
{
    /// <summary>X坐标（mm）</summary>
    public float X { get; set; }

    /// <summary>Y坐标（mm）</summary>
    public float Y { get; set; }

    /// <summary>A扫数据</summary>
    public AScanData Data { get; set; } = new();
}

/// <summary>
/// 单行扫描完成事件参数：编码器触发连续扫查时按行成帧，
/// 携带该行全部触发点的位置序列与对应 A 扫波形，供 B 扫实时成像。
/// </summary>
public class LineScanCompleteEventArgs : EventArgs
{
    /// <summary>行号（从 0 开始）</summary>
    public int LineIndex { get; set; }

    /// <summary>行 Y 坐标（mm）</summary>
    public float Y { get; set; }

    /// <summary>采样率（Hz）</summary>
    public float SampleRate { get; set; }

    /// <summary>该行各触发点的 X 位置序列（mm）</summary>
    public float[] Positions { get; set; } = Array.Empty<float>();

    /// <summary>该行各触发点的 A 扫波形（与 Positions 一一对应）</summary>
    public float[][] Waveforms { get; set; } = Array.Empty<float[]>();
}
