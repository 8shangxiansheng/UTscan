namespace UTscan.Core.Enums;

/// <summary>
/// 闸门角色（说明书 3.3.4）。
/// 同步闸门（黄色）用于定位表面波；数据闸门（红/蓝等）用于采集峰值成像。
/// </summary>
public enum GateRole
{
    /// <summary>同步闸门：寻找同步波形，定位数据闸门起始时间</summary>
    Sync = 0,

    /// <summary>数据闸门：采集峰值数据用于成像</summary>
    Data = 1
}
