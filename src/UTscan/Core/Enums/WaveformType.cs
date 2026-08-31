namespace UTscan.Core.Enums;

/// <summary>
/// 波形类型（说明书 3.3.2 波形类型）。
/// 决定 A 扫数据在分析与成像前的预处理方式。
/// </summary>
public enum WaveformType
{
    /// <summary>射频（原始 RF 信号，保留正负）</summary>
    RF = 0,

    /// <summary>检波（取绝对值，即包络）</summary>
    Detected = 1,

    /// <summary>正半波（仅保留正幅值，负值置零）</summary>
    PositiveHalf = 2,

    /// <summary>负半波（仅保留负幅值，正值置零）</summary>
    NegativeHalf = 3
}
