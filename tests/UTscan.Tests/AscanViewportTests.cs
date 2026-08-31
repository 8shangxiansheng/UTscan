using UTscan.Services.SignalProcessing;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// A 扫纵轴稳定标度引擎（AscanViewport）测试。
/// 验证三处缺陷修复的关键行为：
///  1. 纵轴稳定不随噪声逐帧跳动（消除抖动）；
///  2. 不同样品回波幅值差异不被归一化抹平（波形随样品变化）；
///  3. 增益/幅值变化只改变幅值标度，不逐一帧跳变。
/// </summary>
public class AscanViewportTests
{
    [Fact]
    public void Update_FirstFrame_EstablishesScaleFromPeak()
    {
        var vp = new AscanViewport();
        float range = vp.RangeHalfV(0.5f);
        // 首次以峰值建立标度 + 余量
        Assert.True(range > 0.5f);
        Assert.True(range < 0.5f * 2f);
        Assert.Equal(0.5f, vp.DisplayPeakV, 3);
    }

    [Fact]
    public void Update_StrongerSignal_ImmediatelyExpands_NoClipping()
    {
        var vp = new AscanViewport();
        vp.RangeHalfV(0.5f);              // 建立
        float before = vp.RangeHalfV(0.5f);
        // 更强信号 → 立即放大到不削顶（拒绝超范围）
        float after = vp.RangeHalfV(3.0f);
        Assert.True(after > before);
        // 放大量程必须 ≥ 新峰值（否则削顶）
        Assert.True(vp.DisplayPeakV >= 3.0f);
    }

    [Fact]
    public void Update_NoisySameLevel_DoesNotDitherScale()
    {
        // 关键：消除抖动。信号峰值在 0.98~1.02V 间微小噪声波动时，纵轴标度应保持稳定，
        // 而不是逐帧按瞬时峰值跳变。
        var vp = new AscanViewport();
        vp.RangeHalfV(1.0f);
        float initial = vp.DisplayPeakV;

        for (int i = 0; i < 50; i++)
        {
            float noisyPeak = 0.98f + 0.04f * (i % 2);   // 0.98 / 1.02 交替
            vp.RangeHalfV(noisyPeak);
            // 不会突然放大（噪声峰值 < 当前标度 → 走慢释放，最多掉一个 ReleaseFactor）
            Assert.True(vp.DisplayPeakV > 0.9f);
        }
        // 慢释放只应缓慢收敛，不应崩溃到噪声量级
        Assert.True(vp.DisplayPeakV > 0.85f);
        _ = initial;
    }

    [Fact]
    public void Update_WeakSignal_SlowRelease_NoInstantCrash()
    {
        var vp = new AscanViewport();
        vp.RangeHalfV(1.0f);
        // 信号突然变弱到 0.1V，标度应缓慢下降而非瞬间定格在旧值不动（保留自适应）
        vp.RangeHalfV(0.1f);
        Assert.True(vp.DisplayPeakV < 1.0f);          // 已开始收敛
        Assert.True(vp.DisplayPeakV >= 0.1f);         // 未越过目标
    }

    [Fact]
    public void UpdateFromSamples_ComputesPeakAndScales()
    {
        var vp = new AscanViewport();
        var samples = new float[] { -0.3f, 0.0f, 0.7f, -0.7f, 0.2f, -0.15f };
        float range = vp.UpdateFromSamples(samples);
        Assert.True(range >= 0.7f);   // 以峰值 0.7V + 余量建立
        Assert.Equal(0.7f, vp.DisplayPeakV, 4);
    }

    [Fact]
    public void Update_ZeroSignal_StaysFinite()
    {
        var vp = new AscanViewport();
        float r = vp.RangeHalfV(0f);
        Assert.True(float.IsFinite(r));
        Assert.True(r > 0f);
    }

    [Fact]
    public void Reset_ClearsScale()
    {
        var vp = new AscanViewport();
        vp.RangeHalfV(2.0f);
        Assert.True(vp.DisplayPeakV > 0);
        vp.Reset();
        Assert.Equal(0f, vp.DisplayPeakV);
    }

    [Fact]
    public void Material_AmplitudeDifference_Preserved()
    {
        // 不同材质回波幅值差异（1.0V vs 0.2V）不应被归一化抹平为相同满屏高度。
        // 本测试验证：弱信号帧峰值原始幅值被保留在标度中，未被强制放大到与强信号相同。
        var vpStrong = new AscanViewport();
        var vpWeak = new AscanViewport();
        vpStrong.UpdateFromSamples(Repeat(1.0f));
        vpWeak.UpdateFromSamples(Repeat(0.2f));
        // 两个独立视图的纵轴量程应反映各自幅值差异（1.0 明显大于 0.2）
        Assert.True(vpStrong.DisplayPeakV > vpWeak.DisplayPeakV * 2f);
    }

    private static float[] Repeat(float v) => new float[] { -v, v, v * 0.5f, -v * 0.5f, 0f };

    // ── ComputeVisibleRange：延迟/窗口裁剪（修复"改延迟后 A 扫红叉"）──

    [Fact]
    public void ComputeVisibleRange_NormalWindow_ReturnsExpectedRange()
    {
        // 采样率 100M：dt=0.01µs/点，1024 点 → 总 10.24µs
        AscanViewport.ComputeVisibleRange(0f, 10.24f, 0.01f, 1024, out int i0, out int i1);
        Assert.Equal(0, i0);
        Assert.Equal(1023, i1);
    }

    [Fact]
    public void ComputeVisibleRange_DelayWithinRange_SharesAtLeastTwoPoints()
    {
        // 延迟 5µs、窗口 3µs → 采样 [500, 800)，正常可绘制
        AscanViewport.ComputeVisibleRange(5f, 3f, 0.01f, 1024, out int i0, out int i1);
        Assert.True(i1 - i0 + 1 >= 2, "窗口内应有足够点数可绘制折线");
        Assert.Equal(500, i0);
    }

    [Fact]
    public void ComputeVisibleRange_DelayBeyondRange_NoDegenerateState()
    {
        // 延迟远超过采集总时长（总 10.24µs，延迟 100µs）：
        // 原实现产生单点/NaN → DrawLines 抛异常 → 红叉。修复后 i0==i1（count<2），
        // 调用方跳过绘制而非崩溃。
        AscanViewport.ComputeVisibleRange(100f, 10.24f, 0.01f, 1024, out int i0, out int i1);
        Assert.Equal(i0, i1);
        Assert.True(i0 >= 0 && i0 < 1024);
    }

    [Fact]
    public void ComputeVisibleRange_DelayJustBeyondEnd_ClampsToLastPoint()
    {
        AscanViewport.ComputeVisibleRange(10.5f, 5f, 0.01f, 1024, out int i0, out int i1);
        // i0 已超过末点 → 钳制到末点，i1==i0（单点，跳过绘制）
        Assert.Equal(1023, i0);
        Assert.Equal(1023, i1);
    }

    [Fact]
    public void ComputeVisibleRange_NoSampleRate_FallsBackToFullRange()
    {
        AscanViewport.ComputeVisibleRange(0f, 10f, 0f, 1024, out int i0, out int i1);
        Assert.Equal(0, i0);
        Assert.Equal(1023, i1);
    }

    [Fact]
    public void ComputeVisibleRange_ZeroWidthWindow_ReturnsEmpty()
    {
        // L12-FIX 回归（审查 20260828）：零宽窗口应隐藏波形（单点，调用方 n>=2 跳过绘制），
        // 而非退化为整段全显。
        AscanViewport.ComputeVisibleRange(0f, 0f, 0.01f, 1024, out int i0, out int i1);
        Assert.Equal(0, i0);
        Assert.Equal(0, i1);   // 单点 → 无波形可绘
    }

    [Fact]
    public void ComputeVisibleRange_InvalidPointCount_ReturnsEmpty()
    {
        AscanViewport.ComputeVisibleRange(0f, 10f, 0.01f, 0, out int i0, out int i1);
        Assert.True(i1 < i0);
    }

    [Fact]
    public void ComputeVisibleRange_SinglePoint_ReturnsOnePoint()
    {
        AscanViewport.ComputeVisibleRange(0f, 10f, 0.01f, 1, out int i0, out int i1);
        Assert.Equal(0, i0);
        Assert.Equal(0, i1);
    }

    // ── P0-1：时间→像素横坐标映射（延迟≠0 时波形不随 startUs 抵消）──

    [Fact]
    public void SampleToPixelX_NonZeroDelay_UsesAbsoluteTime()
    {
        const int ml = 50, plotW = 1000;
        const float dt = 0.01f, startUs = 2f, viewUs = 10f;

        // 第 0 点绝对时刻 0，窗口起点 2 → 相对 -2µs → 像素在 ml 左侧
        float x0 = AscanViewport.SampleToPixelX(0, dt, startUs, viewUs, plotW, ml);
        Assert.Equal(50f - 2f / 10f * 1000f, x0, 3);

        // 第 500 点绝对时刻 5µs → 相对 3µs → 像素 50+300
        float x500 = AscanViewport.SampleToPixelX(500, dt, startUs, viewUs, plotW, ml);
        Assert.Equal(50f + 3f / 10f * 1000f, x500, 3);

        // 单调递增（波形从左到右连续，不因 startUs 抵消而右端溢出）
        Assert.True(x500 > x0, "延迟非零时波形点应随索引单调右移（绝对时刻映射）");
    }

    [Fact]
    public void SampleToPixelX_ZeroDelay_MapsTimeToPlot()
    {
        float x = AscanViewport.SampleToPixelX(500, 0.01f, 0f, 10f, 1000, 50);
        Assert.Equal(50f + 500f * 0.01f / 10f * 1000f, x, 3);
    }

    [Fact]
    public void PixelToTimeUs_RoundTrip_Consistent()
    {
        const int ml = 50, plotW = 1000;
        const float dt = 0.01f, startUs = 2f, viewUs = 10f;
        float px = AscanViewport.SampleToPixelX(500, dt, startUs, viewUs, plotW, ml);
        float tUs = AscanViewport.PixelToTimeUs(px, startUs, viewUs, plotW, ml);
        Assert.Equal(500f * dt, tUs, 3);
    }

    [Fact]
    public void TimeUsToIndex_ClampsBounds()
    {
        Assert.Equal(0, AscanViewport.TimeUsToIndex(-5f, 0.01f, 100));
        Assert.Equal(500, AscanViewport.TimeUsToIndex(5f, 0.01f, 1000));
        Assert.Equal(999, AscanViewport.TimeUsToIndex(999f, 0.01f, 1000));
    }
}
