namespace UTscan.Core.Enums;

/// <summary>
/// 扫查模式
/// </summary>
public enum ScanMode
{
    Linear = 0,
    Raster = 1,
    Arc = 2
}

/// <summary>
/// 扫查策略（说明书 2.6 双线程扫查）
/// </summary>
public enum ScanStrategy
{
    /// <summary>走点-停-采：移动到点位 → 停稳 → 取一帧 → 移动到下一点</summary>
    PointByPoint = 0,

    /// <summary>
    /// 编码器触发（NH-5 语义说明）：当前实现为"逐点停稳采集 + 按行缓存成帧"——
    /// 与 PointByPoint 相同走点停稳，仅额外按行聚合波形并触发 LineScanComplete 供 B 扫实时成像。
    /// **并非真编码器位置同步触发**（未配置 ZMC 位置比较输出/编码器脉冲接入 Spectrum），
    /// 命名与硬件同步语义存在差异，真机验收时需按实际触发链确认（见审查报告 NH-5）。
    /// </summary>
    EncoderTriggered = 1
}
