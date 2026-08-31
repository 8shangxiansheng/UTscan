using System.Drawing;

namespace UTscan.Services.SignalProcessing;

/// <summary>
/// 色图（说明书 3.3.5 / 3.11）：将标量值映射为颜色，用于 C 扫成像着色。
/// 预设多种色带，支持自定义。
/// </summary>
public class Colormap
{
    /// <summary>色带名称</summary>
    public string Name { get; }

    private readonly Color[] _stops;

    private Colormap(string name, Color[] stops)
    {
        Name = name;
        _stops = stops;
    }

    /// <summary>按归一化值 t∈[0,1] 取色</summary>
    public Color Map(float t)
    {
        if (_stops.Length == 0) return Color.Black;
        t = Math.Clamp(t, 0f, 1f);
        float pos = t * (_stops.Length - 1);
        int i0 = (int)Math.Floor(pos);
        int i1 = Math.Min(i0 + 1, _stops.Length - 1);
        float f = pos - i0;
        Color a = _stops[i0], b = _stops[i1];
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * f),
            (int)(a.G + (b.G - a.G) * f),
            (int)(a.B + (b.B - a.B) * f));
    }

    /// <summary>按值取色（自动归一化）</summary>
    public Color Map(float value, float min, float max)
    {
        float range = max - min;
        float t = range > 1e-9f ? (value - min) / range : 0.5f;
        return Map(t);
    }

    public static readonly Colormap Jet = new("Jet", new[]
    {
        Color.FromArgb(0,0,128), Color.FromArgb(0,0,255), Color.FromArgb(0,255,255),
        Color.FromArgb(128,255,0), Color.FromArgb(255,255,0), Color.FromArgb(255,128,0),
        Color.FromArgb(255,0,0)
    });

    public static readonly Colormap Viridis = new("Viridis", new[]
    {
        Color.FromArgb(68,1,84), Color.FromArgb(59,82,139), Color.FromArgb(33,145,140),
        Color.FromArgb(94,201,98), Color.FromArgb(253,231,37)
    });

    public static readonly Colormap Hot = new("Hot", new[]
    {
        Color.Black, Color.FromArgb(70,0,0), Color.FromArgb(160,20,0),
        Color.FromArgb(230,80,0), Color.FromArgb(255,180,0), Color.White
    });

    public static readonly Colormap Gray = new("Gray", new[]
    {
        Color.Black, Color.FromArgb(80,80,80), Color.FromArgb(160,160,160), Color.White
    });

    public static readonly Colormap CoolWarm = new("CoolWarm", new[]
    {
        Color.FromArgb(59,76,192), Color.FromArgb(144,178,254), Color.FromArgb(221,221,221),
        Color.FromArgb(245,156,125), Color.FromArgb(180,4,38)
    });

    /// <summary>预设色带列表</summary>
    public static readonly Colormap[] Presets = { Jet, Viridis, Hot, Gray, CoolWarm };

    /// <summary>按名称查找预设，找不到返回 Jet</summary>
    public static Colormap FromName(string name) =>
        Presets.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Jet;
}
