using UTscan.Core.Enums;

namespace UTscan.Core.Models;

/// <summary>
/// 扫查参数
/// </summary>
public class ScanParams
{
    /// <summary>扫查模式</summary>
    public ScanMode Mode { get; set; } = ScanMode.Raster;

    /// <summary>运动速度（mm/s）</summary>
    public float Velocity { get; set; } = 10f;

    /// <summary>运动加速度（mm/s²）</summary>
    public float Acceleration { get; set; } = 50f;

    /// <summary>单点重复采集次数（NL-1 语义说明）：当前版本**未参与扫描流程**——
    /// 扫描循环每点仅取一帧（见 ScanService），此值仅保留供未来平均功能；
    /// 设为 >1 不产生实际多次采集/平均效果。</summary>
    public int AcquisitionsPerPoint { get; set; } = 1;

    /// <summary>采样率（Hz）</summary>
    public float SampleRate { get; set; } = 100f;

    /// <summary>
    /// 扫查取数通道索引（0=CH0, 1=CH1；默认 0）。
    /// 双通道采集时指定 C 扫成像使用哪个通道的数据；越界由 DAQ 层回退到默认通道。
    /// </summary>
    public int ChannelIndex { get; set; } = 0;

    /// <summary>扫查策略：走点-停-采 或 编码器触发连续扫查</summary>
    public ScanStrategy Strategy { get; set; } = ScanStrategy.PointByPoint;

    /// <summary>
    /// H-1：ZMC 单次触发输出 IO 口编号（驱动 DPR500 External Trigger Input）。
    /// 必须按现场接线确定；-1 表示未配置严格单次触发（ScanService 据此拒绝启动或回退到 Internal PRF）。
    /// 不得复用轴使能占用的 IO0/3/10/11/12。
    /// </summary>
    public int TriggerIo { get; set; } = -1;

    /// <summary>H-1：单次触发脉冲高电平保持时间（ms）。</summary>
    public int TriggerPulseWidthMs { get; set; } = 5;
}
