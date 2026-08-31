namespace UTscan.Core.Models;

/// <summary>
/// 轴配置
/// </summary>
public class AxisConfig
{
    /// <summary>轴名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>脉冲当量（脉冲/毫米，按说明书 §5.2.1：XY=10000/5=2000，Z=10000/10=1000）</summary>
    public float PulsesPerUnit { get; set; } = 2000f;

    /// <summary>最大速度（mm/s）</summary>
    public float MaxVelocity { get; set; } = 100f;

    /// <summary>最大加速度（mm/s²）</summary>
    public float MaxAcceleration { get; set; } = 500f;

    /// <summary>正向限位开关IO地址</summary>
    public int PositiveLimitIo { get; set; }

    /// <summary>负向限位开关IO地址</summary>
    public int NegativeLimitIo { get; set; }

    /// <summary>原点IO地址</summary>
    public int HomeIo { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;
}
