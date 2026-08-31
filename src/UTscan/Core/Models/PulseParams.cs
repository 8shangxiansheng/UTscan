using UTscan.Core.Enums;

namespace UTscan.Core.Models;

/// <summary>
/// 脉冲收发仪参数（说明书 3.3.3）。
/// 对应 DPR500 / 多浦乐脉冲收发仪的全部可配置项。
/// </summary>
public class PulseParams
{
    /// <summary>设备型号</summary>
    public string Model { get; set; } = "DPR500";

    /// <summary>通道号（默认 1）</summary>
    public int Channel { get; set; } = 1;

    /// <summary>电源灯开/关</summary>
    public bool PowerOn { get; set; } = true;

    /// <summary>接收模式：ECHO（自发自收）或 THRU（一发一收）</summary>
    public PulseMode Mode { get; set; } = PulseMode.PulseEcho;

    /// <summary>增益（dB，范围 -50~50）</summary>
    public float GainDb { get; set; } = 0f;

    /// <summary>低通频率（Hz，高于此频率信号被滤除）。0 表示不限制。</summary>
    public float LowPassHz { get; set; } = 50e6f;

    /// <summary>高通频率（Hz，低于此频率信号被滤除）。0 表示不限制。</summary>
    public float HighPassHz { get; set; } = 1e6f;

    /// <summary>使能状态</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>触发模式：INTERNAL（DPR500 自主 PRF 发射，TRIG/SYNC 输出同步脉冲给采集卡）或 EXTERNAL（等待外部 3~5V 脉冲）。
    /// 默认 Internal —— 项目接线为 DPR500 TRIG/SYNC → Spectrum 专用 EXT0（H-1 修复：原默认 External 使 DPR500 等待
    /// 外部脉冲但不发射，导致真机采集零数据，见 docs/最终代码审查-PInvoke与时序.md H-1）</summary>
    public TriggerMode TriggerMode { get; set; } = TriggerMode.Internal;

    /// <summary>脉冲重复频率 PRF（Hz，范围 100~5000）</summary>
    public float PrfHz { get; set; } = 1000f;

    /// <summary>激发电压（V，范围 100~330）</summary>
    public float Voltage { get; set; } = 200f;

    /// <summary>激发能量挡位（1~4）</summary>
    public int EnergyLevel { get; set; } = 2;

    /// <summary>单位脉冲能量（μJ，非负）</summary>
    public float EnergyPerPulseUj { get; set; } = 0f;

    /// <summary>匹配阻尼</summary>
    public DampingSetting Damping { get; set; } = DampingSetting.Damping50;

    /// <summary>匹配阻抗（Ω）</summary>
    public float Impedance { get; set; } = 50f;

    /// <summary>脉冲宽度（ns）</summary>
    public float PulseWidthNs { get; set; } = 100f;
}
