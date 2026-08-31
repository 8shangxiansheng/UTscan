using UTscan.Hardware.Daq;

namespace UTscan.Core.Models;

/// <summary>
/// DAQ 参数快照：UI 线程捕获控件值后传给后台连接编排器（避免后台线程读控件）。
/// 原为 MainForm 私有结构。P2 提升至共享模型，供 ConnectionOrchestrator 消费。
/// </summary>
public readonly struct DaqSnapshot
{
    public SpectrumAcquisitionMode AcquisitionMode { get; init; }
    public int ChannelMask { get; init; }
    public int InputRangeMv { get; init; }
    public bool InputFiftyOhm { get; init; }
    public int Averages { get; init; }
    public bool EnableTimestamp { get; init; }
    public int ExternalTriggerLevelMv { get; init; }
    public float TriggerDelayUs { get; init; }   // SPC_TRIG_DELAY（µs）
    public float SampleRate { get; init; }
    public int SampleCount { get; init; }
}