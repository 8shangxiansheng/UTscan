namespace UTscan.Core.Enums;

/// <summary>
/// A 扫显示滤波模式（P1-2 新增）。滤波仅作用于显示层（WaveformView.OnPaint 的显示副本），
/// 不改变原始采集数据——导出/成像/测量始终用未滤波的 Samples。
/// </summary>
public enum DisplayFilterMode
{
    /// <summary>原始数据（默认）</summary>
    None = 0,

    /// <summary>3 点中值滤波（消除单点毛刺，保持边缘锐度）</summary>
    Median3 = 1,

    /// <summary>5 点中值滤波（更强降噪，边缘略钝）</summary>
    Median5 = 2,

    /// <summary>低通平滑（3 点滑动平均，适合观察包络）</summary>
    LowPass = 3,
}
