namespace UTscan.Core.Models;

/// <summary>
/// 扫查区域定义
/// </summary>
public class ScanRegion
{
    /// <summary>起点X（mm）</summary>
    public float StartX { get; set; }

    /// <summary>起点Y（mm）</summary>
    public float StartY { get; set; }

    /// <summary>扫查宽度（mm）</summary>
    public float Width { get; set; } = 10f;

    /// <summary>扫查高度（mm）</summary>
    public float Height { get; set; } = 10f;

    /// <summary>X方向步距（mm）</summary>
    public float StepX { get; set; } = 0.1f;

    /// <summary>Y方向步距（mm）</summary>
    public float StepY { get; set; } = 0.1f;

    /// <summary>X 方向扫查点数（审查 P2-2：公式单一来源，含起点共 Width/StepX+1 点。
    /// L10-FIX（审查 20260828）：截断改为上取整——原 (int) 向零截断使尾段未扫
    /// （如 Width=10 Step=0.3 得 34 点，实际只到 9.9mm）。ceil 保证覆盖完整声明宽度。</summary>
    public int PointCountX => Math.Max(1, (int)Math.Ceiling(Width / Math.Max(StepX, 0.001f) - 1e-4f) + 1);

    /// <summary>Y 方向扫查点数</summary>
    public int PointCountY => Math.Max(1, (int)Math.Ceiling(Height / Math.Max(StepY, 0.001f) - 1e-4f) + 1);
}
