using UTscan.Core.Enums;

namespace UTscan.Core.Models;

/// <summary>
/// 闸门配置（说明书 3.3.4）
/// </summary>
public class GateConfig
{
    /// <summary>闸门名称（数据闸门名称长度不超过 6 字符）</summary>
    public string Name { get; set; } = "Gate A";

    /// <summary>闸门角色（同步/数据）</summary>
    public GateRole Role { get; set; } = GateRole.Data;

    /// <summary>闸门颜色（ARGB），默认红色。JSON 序列化友好（L-4：
    /// 保留 int 存储，另提供 <see cref="GateColor"/> 便于 UI 使用 System.Drawing.Color）</summary>
    public int Color { get; set; } = unchecked((int)0xFFFF0000);

    /// <summary>闸门颜色（System.Drawing.Color 视图，L-4 新增；设置时同步写回 <see cref="Color"/>）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Drawing.Color GateColor
    {
        get => System.Drawing.Color.FromArgb(Color);
        set => Color = value.ToArgb();
    }

    /// <summary>闸门起点（μs）</summary>
    public float StartUs { get; set; }

    /// <summary>闸门宽度（μs），不可为 0</summary>
    public float WidthUs { get; set; } = 10f;

    /// <summary>闸门阈值电平（V）</summary>
    public float ThresholdV { get; set; } = 0.5f;

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否使用底波（仅同步闸门）</summary>
    public bool UseBottomEcho { get; set; } = false;
}

