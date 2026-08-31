namespace UTscan.Core.Enums;

/// <summary>
/// C 扫成像模式（说明书 3.6.3 闸门模式）。
/// 每个扫查点取数据闸门内一段波形，按下列算法得到一个标量值用于着色成像。
/// </summary>
public enum CScanImagingMode
{
    /// <summary>峰峰值：闸门内最大幅值与最小幅值之差</summary>
    PeakPeak = 0,

    /// <summary>正向峰值：闸门内正向最大幅值</summary>
    PositivePeak = 1,

    /// <summary>负向峰值：闸门内负向最大幅值（保留符号）</summary>
    NegativePeak = 2,

    /// <summary>最大峰值：取 |max| 与 |min| 中较大者（保留符号）</summary>
    MaxPeak = 3,

    /// <summary>TOF 正峰值：正向幅值最大点的时间值（μs，相对闸门起点）</summary>
    TofPositivePeak = 4,

    /// <summary>TOF 负峰值：负向幅值最大点的时间值（μs，相对闸门起点）</summary>
    TofNegativePeak = 5,

    /// <summary>TOF 正阈值：正向幅值首次越过阈值的时间值（μs，相对闸门起点）</summary>
    TofPositiveThreshold = 6,

    /// <summary>TOF 负阈值：负向幅值首次越过 -阈值 的时间值（μs，相对闸门起点）</summary>
    TofNegativeThreshold = 7,

    /// <summary>相位反转：取闸门内峰值符号取反后的幅值</summary>
    PhaseReversal = 8,

    /// <summary>均值（Mean）：闸门内全部采样点的算术平均值（说明书 2.6 明确列出的成像模式）</summary>
    Mean = 9
}
