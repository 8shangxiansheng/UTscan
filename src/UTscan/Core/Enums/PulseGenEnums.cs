namespace UTscan.Core.Enums;

/// <summary>
/// 脉冲模式（L-1 归位：原定义于 IPulseGenerator.cs / PulseParams.cs，统一移入 Enums）
/// </summary>
public enum PulseMode
{
    /// <summary>自发自收（Pulse-Echo）</summary>
    PulseEcho = 0,

    /// <summary>一发一收（Through-Transmission）</summary>
    ThroughTransmission = 1
}

/// <summary>触发模式（L-1 归位：原定义于 PulseParams.cs）</summary>
public enum TriggerMode
{
    /// <summary>外部触发</summary>
    External = 0,

    /// <summary>内部触发</summary>
    Internal = 1
}

/// <summary>阻尼挡位（L-1 归位：原定义于 PulseParams.cs）</summary>
public enum DampingSetting
{
    Damping50 = 0,
    Damping100 = 1,
    Damping200 = 2,
    Damping500 = 3
}
