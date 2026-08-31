namespace UTscan.Core.Models;

/// <summary>
/// 系统参数（说明书 3.7）。
/// </summary>
public class SystemParams
{
    /// <summary>标尺单位（mm 或 °）</summary>
    public string RulerUnit { get; set; } = "mm";

    /// <summary>材料声速（m/s）</summary>
    public float SoundVelocity { get; set; } = 1480f;

    /// <summary>仿形焦距（mm）</summary>
    public float FocalLength { get; set; } = 25f;

    /// <summary>零点校准值（μs）</summary>
    public float ZeroOffsetUs { get; set; } = 0f;
}
