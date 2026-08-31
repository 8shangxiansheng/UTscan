namespace UTscan.Core.Models;

/// <summary>
/// 闸门测量结果
/// </summary>
public class GateResult
{
    /// <summary>闸门名称</summary>
    public string GateName { get; set; } = string.Empty;

    /// <summary>峰值幅度（V）：取 |max| 与 |min| 较大者，保留符号</summary>
    public float PeakAmplitude { get; set; }

    /// <summary>峰值位置（μs，绝对时间，相对触发时刻 t=0）</summary>
    public float PeakPositionUs { get; set; }

    /// <summary>
    /// 闸门内相对时间（μs，相对闸门起点 StartUs）。P2-5-FIX：本字段是"闸门内峰位偏移"，
    /// 非物理飞行时间（物理 TOF = 绝对峰值时刻 − 零点偏移），命名保留但语义已注明，
    /// 导出/测量勿误读为物理 TOF。
    /// </summary>
    public float TimeOfFlightUs { get; set; }

    /// <summary>是否超阈值</summary>
    public bool IsAboveThreshold { get; set; }

    /// <summary>正向最大幅值（V）</summary>
    public float PositivePeak { get; set; }

    /// <summary>负向最大幅值（V，负数）</summary>
    public float NegativePeak { get; set; }

    /// <summary>峰峰值（V）</summary>
    public float PeakToPeak { get; set; }

    /// <summary>正向峰值位置（μs，绝对时间）</summary>
    public float PositivePeakPositionUs { get; set; }

    /// <summary>负向峰值位置（μs，绝对时间）</summary>
    public float NegativePeakPositionUs { get; set; }

    /// <summary>正向首次越过阈值的时间（μs，绝对时间）；未越过为 -1</summary>
    public float PositiveThresholdCrossUs { get; set; } = -1f;

    /// <summary>负向首次越过 -阈值 的时间（μs，绝对时间）；未越过为 -1</summary>
    public float NegativeThresholdCrossUs { get; set; } = -1f;

    /// <summary>同步闸门内首个越过阈值波形的相对偏移（μs）；无则 -1</summary>
    public float SyncFirstCrossOffsetUs { get; set; } = -1f;
}

