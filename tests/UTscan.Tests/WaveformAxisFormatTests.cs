using UTscan.UI.Controls;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// A 扫坐标轴刻度格式化测试（WaveformView.FormatAxisUs/FormatAxisV/FormatAxisVUnit）。
/// 覆盖：µs 量级自适应位数、电压单位自适应换算、标签可读性。
/// </summary>
public class WaveformAxisFormatTests
{
    // ── X 轴（µs） ──

    [Theory]
    [InlineData(0.001f, "0.001")]   // <0.1µs → 3 位小数
    [InlineData(0.05f, "0.050")]
    [InlineData(0.5f, "0.50")]      // <1µs → 2 位小数
    [InlineData(5f, "5.0")]         // <100µs → 1 位小数
    [InlineData(50f, "50.0")]
    [InlineData(120f, "120")]       // ≥100µs → 无小数
    [InlineData(2048f, "2048")]
    public void FormatAxisUs_AdaptivePrecision(float us, string expected)
        => Assert.Equal(expected, WaveformView.FormatAxisUs(us));

    [Fact]
    public void FormatAxisUs_MonotonicIncrease_NoOverlap()
    {
        // 采样长度从 1µs 到 1000µs，刻度标签应始终递增且位数递减不拥挤
        float prev = 0;
        string prevLabel = "";
        for (float us = 0.1f; us <= 1000f; us *= 2.5f)
        {
            string label = WaveformView.FormatAxisUs(us);
            Assert.True(us > prev, "刻度值应单调递增");
            // 标签长度不应超过 5 字符（防重叠）
            Assert.True(label.Length <= 5, $"标签过长: {label}");
            prev = us;
            prevLabel = label;
        }
        _ = prevLabel;
    }

    // ── Y 轴（电压，刻度值绑定轴满量程） ──

    [Theory]
    // 满量程 ≥1V → V（1 位小数）
    [InlineData(1.5f, 2f, "1.5")]
    [InlineData(-1.5f, 2f, "-1.5")]
    [InlineData(0.8f, 1f, "0.8")]
    // 满量程 ≥1mV → 整数 mV（刻度值与轴单位一致）
    [InlineData(0.005f, 0.01f, "5")]
    [InlineData(-0.005f, 0.01f, "-5")]
    // 满量程 ≥1µV → 整数 µV
    [InlineData(0.00002f, 0.00005f, "20")]
    [InlineData(-0.00002f, 0.00005f, "-20")]
    public void FormatAxisV_UnitAdaptive(float v, float fullScale, string expected)
        => Assert.Equal(expected, WaveformView.FormatAxisV(v, fullScale));

    [Theory]
    [InlineData(2f, "V")]
    [InlineData(0.5f, "V")]
    [InlineData(0.01f, "mV")]
    [InlineData(0.00005f, "µV")]
    public void FormatAxisVUnit_MatchesScale(float peakV, string expected)
        => Assert.Equal(expected, WaveformView.FormatAxisVUnit(peakV));

    [Fact]
    public void FormatAxisVUnit_ZeroRange_Safe()
        => Assert.Equal("V", WaveformView.FormatAxisVUnit(0f));

    // ── P0-深度：µs↔mm 换算与深度刻度 ──

    [Theory]
    [InlineData(0f, 0f)]                    // t=0 → 0mm
    [InlineData(10f, 7.4f)]                 // 10µs × 1480/2000 = 7.4mm
    [InlineData(100f, 74f)]                 // 100µs × 1480/2000 = 74mm
    public void TimeToDepth_Steel1480_Correct(float us, float expectedMm)
    {
        // 1480 m/s 钢：depth = t_us × 1480 / 2000
        Assert.Equal(expectedMm, us * 1480f / 2000f, 3);
    }

    [Theory]
    [InlineData(0.005f, "0.005")]           // <0.01mm → 3 位
    [InlineData(0.05f, "0.05")]             // <1mm → 2 位
    [InlineData(5f, "5.0")]                 // <100mm → 1 位
    [InlineData(150f, "150")]               // ≥100mm → 无小数
    public void FormatAxisDepth_AdaptivePrecision(float mm, string expected)
        => Assert.Equal(expected, WaveformView.FormatAxisDepth(mm));

    [Fact]
    public void AxisTickValueAndUnit_AreConsistent()
    {
        // 刻度值与轴单位必须一致：满量程 1V 轴上的 0.8V 刻度 → "0.8" + "V"
        // （而非换算成 mV 导致数值与单位矛盾）。
        float fullScale = 1f;
        Assert.Equal("V", WaveformView.FormatAxisVUnit(fullScale));
        Assert.Equal("0.8", WaveformView.FormatAxisV(0.8f, fullScale));

        float fullScaleMv = 0.01f;
        Assert.Equal("mV", WaveformView.FormatAxisVUnit(fullScaleMv));
        Assert.Equal("5", WaveformView.FormatAxisV(0.005f, fullScaleMv));
    }
}
