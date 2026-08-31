using System.Numerics;
using UTscan.Core.Interfaces;

namespace UTscan.Services.SignalProcessing;

/// <summary>
/// FFT信号处理器
/// </summary>
public class FftProcessor : ISignalProcessor
{
    public float[] Fft(float[] input)
    {
        int n = input.Length;
        var complex = new Complex[n];
        for (int i = 0; i < n; i++)
            complex[i] = new Complex(input[i], 0);

        FftInPlace(complex);

        var result = new float[n];
        for (int i = 0; i < n; i++)
            result[i] = (float)complex[i].Magnitude;
        return result;
    }

    public float[] Ifft(float[] input)
    {
        int n = input.Length;
        var complex = new Complex[n];
        for (int i = 0; i < n; i++)
            complex[i] = new Complex(input[i], 0);

        IfftInPlace(complex);

        var result = new float[n];
        for (int i = 0; i < n; i++)
            result[i] = (float)complex[i].Real;
        return result;
    }

    public float[] MedianFilter(float[] input, int kernelSize)
    {
        // 单一实现（审查报告 H-2）：委托 MedianFilter.Apply，避免同算法双份代码
        return new MedianFilter().Apply(input, kernelSize);
    }

    // 闸门分析单一实现（审查 P1-5）：委托给完整版 GateAnalyzer，
    // 消除旧版简化实现的两个 bug（空数据 Math.Clamp 抛异常、空闸门泄漏 float.MinValue）
    private readonly GateAnalyzer _gateAnalyzer = new();

    public Core.Models.GateResult AnalyzeGate(Core.Models.AScanData data, Core.Models.GateConfig gate)
        => _gateAnalyzer.Analyze(data, gate);

    private static void FftInPlace(Complex[] x)
    {
        int n = x.Length;
        if (n <= 1) return;

        // 真正的 2 的幂校验（审查报告 H-2）：原 n % 2 != 0 只查偶数，
        // 6/10/12 等偶数非 2 幂长度会静默算出错误结果；位运算校验唯一正确。
        if ((n & (n - 1)) != 0)
            throw new ArgumentException("FFT requires power-of-2 length");

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = x[i + j];
                    var v = x[i + j + len / 2] * w;
                    x[i + j] = u + v;
                    x[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    private static void IfftInPlace(Complex[] x)
    {
        for (int i = 0; i < x.Length; i++)
            x[i] = Complex.Conjugate(x[i]);

        FftInPlace(x);

        for (int i = 0; i < x.Length; i++)
            x[i] = Complex.Conjugate(x[i]) / x.Length;
    }
}
