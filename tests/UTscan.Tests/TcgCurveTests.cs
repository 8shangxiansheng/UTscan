using UTscan.Core.Models;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// TCG（时间补偿增益/深度补偿）曲线模型测试。
/// 覆盖：折线插值、µs↔mm 换算、dB→幅值因子、断点增删与排序、默认平直线、外推饱和。
/// </summary>
public class TcgCurveTests
{
    private static TcgCurve MakeCurve()
    {
        var c = new TcgCurve { SoundVelocity = 1480f };
        c.SetPoint(0f, 0f);
        c.SetPoint(20f, 10f);
        c.SetPoint(50f, 20f);
        return c;
    }

    [Fact]
    public void Default_FlatZeroLine()
    {
        var c = new TcgCurve();
        Assert.Equal(2, c.PointCount);
        Assert.Equal(0f, c.GainAtDepthMm(0f));
        Assert.Equal(0f, c.GainAtDepthMm(50f));
        Assert.Equal(0f, c.GainAtDepthMm(200f));
        Assert.False(c.Enabled);
    }

    [Fact]
    public void GainAtDepth_LinearInterpolation()
    {
        var c = MakeCurve();
        Assert.Equal(0f, c.GainAtDepthMm(0f), 3);       // 端点
        Assert.Equal(10f, c.GainAtDepthMm(20f), 3);     // 端点
        Assert.Equal(20f, c.GainAtDepthMm(50f), 3);     // 端点
        Assert.Equal(5f, c.GainAtDepthMm(10f), 3);      // 0→20 段中点
        Assert.Equal(15f, c.GainAtDepthMm(35f), 3);     // 20→50 段中点
    }

    [Fact]
    public void GainAtDepth_OutOfRange_SaturatesToEndpoint()
    {
        // 默认曲线含 (100,0) 末点——外推 200mm 应取末点值 0（非 20）
        var c = MakeCurve();
        Assert.Equal(0f, c.GainAtDepthMm(-10f), 3);    // 低于首点 → 首点值 0
        Assert.Equal(0f, c.GainAtDepthMm(200f), 3);    // 高于末点(100,0) → 末点值 0
    }

    [Fact]
    public void GainAtTimeUs_UsesVelocityRoundTrip()
    {
        var c = MakeCurve();
        // 10µs @1480m/s → 10×1480/2000 = 7.4mm → 落在 0→20 段 → 3.7dB
        Assert.Equal(7.4f * 0.5f, c.GainAtTimeUs(10f), 3);
        // 30µs → 22.2mm → 20→50 段 → 10 + (22.2-20)/30×10 ≈ 10.73
        float expect = 10f + (22.2f - 20f) / 30f * 10f;
        Assert.Equal(expect, c.GainAtTimeUs(30f), 3);
    }

    [Fact]
    public void DbToAmplitudeFactor_StandardValues()
    {
        Assert.Equal(1f, TcgCurve.DbToAmplitudeFactor(0f), 4);
        // 6dB ≈ 2×（1.9953），20dB = 10×，-6dB ≈ 0.5×（用 1e-2 容差）
        Assert.True(Math.Abs(TcgCurve.DbToAmplitudeFactor(6f) - 2f) < 0.01f, "+6dB 应≈2×");
        Assert.True(Math.Abs(TcgCurve.DbToAmplitudeFactor(20f) - 10f) < 0.01f, "+20dB 应=10×");
        Assert.True(Math.Abs(TcgCurve.DbToAmplitudeFactor(-6f) - 0.5f) < 0.01f, "-6dB 应≈0.5×");
    }

    [Fact]
    public void SetPoint_SortsByDepth()
    {
        var c = new TcgCurve();   // 默认 2 点：(0,0)+(100,0)
        c.SetPoint(50f, 20f);     // 新增
        c.SetPoint(20f, 10f);     // 新增
        Assert.Equal(4, c.PointCount);
        Assert.Equal(0f, c.GetPoint(0).DepthMm);
        Assert.Equal(20f, c.GetPoint(1).DepthMm);
        Assert.Equal(50f, c.GetPoint(2).DepthMm);
        Assert.Equal(100f, c.GetPoint(3).DepthMm);   // 默认末点保留
    }

    [Fact]
    public void SetPoint_ExistingDepth_Updates()
    {
        var c = new TcgCurve();   // 默认含 (0,0)
        c.SetPoint(0f, 10f);      // 命中默认 0 点 → 更新不新增
        Assert.Equal(2, c.PointCount);
        Assert.Equal(10f, c.GainAtDepthMm(0f), 3);
    }

    [Fact]
    public void RemovePoint_KeepsMinimumTwo()
    {
        // 默认 2 点 (0,0)+(100,0) + 新增 (20,10) = 3 点
        var c = new TcgCurve();
        c.SetPoint(20f, 10f);
        Assert.Equal(3, c.PointCount);
        Assert.True(c.RemovePoint(1));   // 删 (20,10) → 剩 2
        Assert.Equal(2, c.PointCount);
        Assert.False(c.RemovePoint(0));  // 只剩 2 个不能再删
        Assert.Equal(2, c.PointCount);
    }

    [Fact]
    public void Reset_RestoresFlatLine()
    {
        var c = MakeCurve();
        c.Reset();
        Assert.Equal(2, c.PointCount);
        Assert.Equal(0f, c.GainAtDepthMm(0f));
        Assert.Equal(0f, c.GainAtDepthMm(50f));
    }
}
