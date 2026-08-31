namespace UTscan.Core.Enums;

/// <summary>
/// DPR500 连接种类（P5：从 Dpr500Controller 提升至共享枚举）。
/// 不再通过型号字符串是否包含 (Sim) 判断安全状态。
/// </summary>
public enum DprConnectionKind
{
    /// <summary>未连接</summary>
    Disconnected,
    /// <summary>物理设备连接</summary>
    Physical,
    /// <summary>仿真模式（仅 config.UseMock=true 时允许）</summary>
    Simulation,
}