using UTscan.Core.Enums;

namespace UTscan.Core.Models;

/// <summary>
/// 数据采集卡参数（说明书 3.3.2）。
/// </summary>
public class DaqParams
{
    /// <summary>查看通道</summary>
    public int Channel { get; set; } = 1;

    /// <summary>采样频率（Hz）</summary>
    public float SampleRate { get; set; } = 100e6f;

    /// <summary>延迟时间（μs）：开始采集时刻与触发始波的时间差。当前映射为 PRETRIGGER（窗口前移，纳入触发前数据），
    /// 语义为"预触发偏移"而非"触发后延时"。</summary>
    public float DelayUs { get; set; } = 0f;

    /// <summary>
    /// 触发后延时（μs）：经 SPC_TRIG_DELAY 将触发事件延迟 N 个采样周期后再进入 PRETRIGGER 逻辑。
    /// 用于跳过始波（表面回波）直接采集后续底波——与 DelayUs(PRETRIGGER) 方向相反且可共存。
    /// 0 = 禁用。
    /// </summary>
    public float TriggerDelayUs { get; set; } = 0f;

    /// <summary>采样长度（μs）：采集结束时刻与开始采集时刻的时间差，不可为 0</summary>
    public float SampleLengthUs { get; set; } = 10f;

    /// <summary>波形类型</summary>
    public WaveformType WaveformType { get; set; } = WaveformType.RF;

    /// <summary>计算偏移：点击后自动计算并设置偏移量</summary>
    public float ComputedOffsetUs { get; set; } = 0f;
}
