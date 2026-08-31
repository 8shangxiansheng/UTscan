using UTscan.Services.SignalProcessing;
using UTscan.Core.Models;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// 信号处理算法测试：FFT / IFFT / 中值滤波 / 闸门分析。
/// </summary>
public class CoreAlgorithmsTests
{
    // ---------- FFT ----------

    [Fact]
    public void Fft_SineWave_PeakAtCorrectBin()
    {
        // 256 点、8 个完整周期的正弦 → 频谱峰应在 bin=8
        int n = 256;
        var input = new float[n];
        for (int i = 0; i < n; i++)
            input[i] = MathF.Sin(2f * MathF.PI * 8f * i / n);

        var proc = new FftProcessor();
        var spectrum = proc.Fft(input);

        int peakBin = 0;
        for (int i = 1; i < n / 2; i++)          // 只看正频率半谱
            if (spectrum[i] > spectrum[peakBin]) peakBin = i;

        Assert.Equal(8, peakBin);
    }

    [Fact]
    public void Fft_ParsevalEnergyConservation()
    {
        // Parseval 定理：sum(|X(k)|²) = N · sum(|x(n)|²)，验证 FFT 正变换数值正确
        var rng = new Random(42);
        int n = 128;
        var input = new float[n];
        double timeEnergy = 0;
        for (int i = 0; i < n; i++)
        {
            input[i] = (float)(rng.NextDouble() - 0.5);
            timeEnergy += input[i] * (double)input[i];
        }

        var proc = new FftProcessor();
        var spectrum = proc.Fft(input);

        double freqEnergy = 0;
        for (int i = 0; i < n; i++)
            freqEnergy += (double)spectrum[i] * spectrum[i];

        Assert.Equal(n * timeEnergy, freqEnergy, 0);   // 相对容差
    }

    [Fact]
    public void Ifft_KnownSpectrum_ProducesSine()
    {
        // 幅度谱 Interpretation：单频正弦的幅度谱在 ±f 各有一个 ~N/2 的峰。
        // Ifft 输入该幅度谱 → 输出应为同频正弦（符号/相位由谱对称性决定）
        int n = 64;
        var spectrum = new float[n];
        spectrum[4] = n / 2f;

        var proc = new FftProcessor();
        var output = proc.Ifft(spectrum);

        // 单边谱 X(4)=N/2 的逆变换是幅度 0.5 的复指数的实部 → 0.5·cos(2π·4·i/n)
        for (int i = 0; i < 8; i++)
        {
            double expected = 0.5 * Math.Cos(2 * Math.PI * 4 * i / n);
            Assert.Equal(expected, output[i], 3);
        }
    }

    // ---------- MedianFilter ----------

    [Fact]
    public void MedianFilter_RemovesImpulseNoise()
    {
        // 平台信号中插入单个尖脉冲
        var input = new float[] { 1, 1, 1, 9, 1, 1, 1 };
        var filter = new MedianFilter();
        var output = filter.Apply(input, 3);

        Assert.All(output, v => Assert.Equal(1f, v, 3));
    }

    [Fact]
    public void MedianFilter_EmptyInput_ReturnsEmpty()
    {
        var filter = new MedianFilter();
        Assert.Empty(filter.Apply(Array.Empty<float>(), 3));
    }

    // ---------- GateAnalyzer ----------

    [Fact]
    public void GateAnalyzer_FindsPeakInWindow()
    {
        // 采样率 1 MHz → 1μs/点；构造一个 100 点信号，在第 40 点放一个 3.0V 峰
        var data = new AScanData
        {
            SampleRate = 1_000_000f,      // dt = 1 μs
            Samples = new float[100]
        };
        data.Samples[40] = 3.0f;
        data.Samples[41] = 1.0f;

        var gate = new GateConfig { Name = "G1", StartUs = 10f, WidthUs = 80f, ThresholdV = 0.5f };
        var analyzer = new GateAnalyzer();
        var result = analyzer.Analyze(data, gate);

        Assert.Equal(3.0f, result.PeakAmplitude, 3);
        Assert.Equal(40f, result.PeakPositionUs, 1);
        Assert.True(result.IsAboveThreshold);
    }

    [Fact]
    public void GateAnalyzer_PeakOutsideGate_Ignored()
    {
        var data = new AScanData
        {
            SampleRate = 1_000_000f,
            Samples = new float[100]
        };
        data.Samples[5] = 5.0f;    // 在闸门外（gate 从 10μs 开始）

        var gate = new GateConfig { Name = "G1", StartUs = 10f, WidthUs = 80f, ThresholdV = 0.5f };
        var analyzer = new GateAnalyzer();
        var result = analyzer.Analyze(data, gate);

        Assert.False(result.IsAboveThreshold);
        Assert.Equal(0f, result.PeakAmplitude, 3);
    }

    // ---------- FFT 长度校验（审查报告 H-2）----------

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(100)]
    public void Fft_NonPowerOfTwoEvenLength_Throws(int n)
    {
        // 原校验 n % 2 != 0 放过偶数非 2 幂长度并静默算出错误结果；
        // 修复后必须抛 ArgumentException
        var proc = new FftProcessor();
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = MathF.Sin(2f * MathF.PI * i / n);

        Assert.Throws<ArgumentException>(() => proc.Fft(input));
    }

    [Fact]
    public void Fft_PowerOfTwo_DoesNotThrow()
    {
        var proc = new FftProcessor();
        var input = new float[512];
        for (int i = 0; i < 512; i++) input[i] = 0.5f;

        var spectrum = proc.Fft(input);
        Assert.Equal(512, spectrum.Length);
    }

    [Fact]
    public void Fft_MedianFilter_DelegatesToMedianFilter()
    {
        // MedianFilter 与 MedianFilter.Apply 应行为一致（审查报告 H-2：单一实现）
        float[] signal = { 1f, 2f, 100f, 3f, 4f, 5f };  // 尖峰 100
        var proc = new FftProcessor();

        var viaInterface = proc.MedianFilter(signal, 3);
        var viaClass = new MedianFilter().Apply(signal, 3);

        Assert.Equal(viaClass, viaInterface);
        Assert.Equal(2f, viaClass[1], 3);  // 尖峰被中值抑制
        Assert.True(viaClass[2] < 10f);
    }
}
