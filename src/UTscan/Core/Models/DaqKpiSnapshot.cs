namespace UTscan.Core.Models;

/// <summary>
/// DAQ KPI 快照（P5：从 SpectrumDaqCard 提升至共享模型）。
/// 采集线程写，GetKpis 锁内读快照——跨线程安全。
/// </summary>
public readonly record struct DaqKpiSnapshot(
    long PublishedFrames,      // 已发布帧数（每通道计一次）
    long CycleCount,           // 采集处理周期数
    double LastCycleMs,        // 最近一周期耗时（WAITDMA→发布）
    double MaxCycleMs,         // 最大周期耗时（峰值延迟）
    double LastCallbackMs,     // DataReady 最近一次回调耗时
    double MaxCallbackMs,      // DataReady 回调峰值耗时（慢订阅方阻塞采集的量化）
    long FifoOverrunTotal,     // 硬件 FIFO 溢出累计（丢帧统计）
    long AcquisitionThreadAborts); // 采集线程异常退出累计