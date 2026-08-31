using System.Numerics;

namespace UTscan.Services.SignalProcessing;

/// <summary>
/// 频域滤波器（移植自旧项目 DyZMC/HP.cs，并修正了原实现的相位丢失问题）。
///
/// 吸收的算法：
/// - FFT 高通滤波（FFT_HP）：变换后将低于截止频率的分量置零，再逆变换。
/// - FFT 带通滤波（FFT_BP）：保留 [f1, f3] 频带，其余置零。
/// - 巴特沃斯 4 阶高通 IIR（ButterworthHighPass）：固定系数
///   B0=0.662016, B1=-2.64806, B2=3.972095, B3=-2.64806, B4=0.662016;
///   A1=-2.11216, A2=3.861194, A3=-2.11216, A4=0.4382651
///   （原设计指标：fc=10MHz 通带, fst=3MHz 阻带, rp=6dB, rs=40dB, fs=200MHz ⇒ N=4）。
/// - 改进：旧版 ifft 取模导致输出恒为正、相位丢失；本实现保留复数逆变换取实部，
///   输出为真实波形（含符号），算法语义与旧版一致且物理正确。
/// </summary>
public static class FrequencyDomainFilters
{
    // 巴特沃斯 4 阶高通系数（与旧项目 FFT_HF 逐位一致）
    private const double B0 = 0.662016, B1 = -2.64806, B2 = 3.972095, B3 = -2.64806, B4 = 0.662016;
    private const double A1 = 2.11216, A2 = -3.861194, A3 = 2.11216, A4 = -0.4382651;

    /// <summary>FFT 高通滤波（消除低于 f1 的频率分量）。f1/Fs 单位一致（Hz）。</summary>
    public static double[] FftHighPass(double[] signal, double cutoffHz, double sampleRateHz)
    {
        if (signal == null || cutoffHz <= 0) return signal ?? Array.Empty<double>();

        var spectrum = FftReal(signal);
        int n = spectrum.Length;
        int halfN = n / 2;

        // L8-FIX（审查 20260828）：实数 FFT 共轭对称 X[k]=conj(X[n-k])，镜像 bin 是 n-k
        // 而非 n-1-k。原实现置零 [0,bins) 与 [n-1-bins, n-1]——镜像偏半个 bin，
        // IFFT 实部产生频谱泄漏。DC(0)/Nyquist(halfN) 各为单点无镜像，单独处理。
        int bins = (int)(cutoffHz * n / sampleRateHz);
        if (bins > halfN) bins = halfN;   // 截止不高于奈奎斯特
        spectrum[0] = Complex.Zero;       // DC 单点
        for (int i = 1; i <= bins; i++)
        {
            spectrum[i] = Complex.Zero;            // 下边带
            spectrum[n - i] = Complex.Zero;        // 上边带镜像（共轭对称配对）
        }
        return InverseFftReal(spectrum);
    }

    /// <summary>FFT 带通滤波：保留 [f1, f3] 频带（共轭对称处理上下边带）。</summary>
    public static double[] FftBandPass(double[] signal, double f1Hz, double f3Hz, double sampleRateHz)
    {
        if (signal == null || f1Hz <= 0) return signal ?? Array.Empty<double>();

        var spectrum = FftReal(signal);
        int n = spectrum.Length;
        int halfN = n / 2;
        // L8-FIX：镜像 bin 为 n-k（非 n-1-k）；频带 [lo,hi] 镜像为 [n-hi, n-lo]
        int lo = (int)(f1Hz * n / sampleRateHz);
        int hi = (int)(f3Hz * n / sampleRateHz);
        if (hi > halfN) hi = halfN;
        if (lo > hi) lo = hi;

        for (int i = 0; i < n; i++)
        {
            bool inBand = (i >= lo && i <= hi) || (i >= n - hi && i <= n - lo);
            if (!inBand) spectrum[i] = Complex.Zero;
        }
        return InverseFftReal(spectrum);
    }

    /// <summary>
    /// 巴特沃斯 4 阶高通 IIR 滤波（直接型 II 差分方程，系数与旧项目一致）。
    /// 适用于超声 RF 信号去低频漂移。
    /// </summary>
    public static double[] ButterworthHighPass(double[] x)
    {
        int n = x.Length;
        var y = new double[n];

        for (int i = 0; i < n; i++)
        {
            double acc = B0 * x[i];
            if (i >= 1) acc += B1 * x[i - 1] + A1 * y[i - 1];
            if (i >= 2) acc += B2 * x[i - 2] + A2 * y[i - 2];
            if (i >= 3) acc += B3 * x[i - 3] + A3 * y[i - 3];
            if (i >= 4) acc += B4 * x[i - 4] + A4 * y[i - 4];
            y[i] = acc;
        }
        return y;
    }

    /// <summary>FFT 低通滤波（消除高于截止频率的分量）。</summary>
    public static double[] FftLowPass(double[] signal, double cutoffHz, double sampleRateHz)
    {
        if (signal == null || cutoffHz <= 0) return signal ?? Array.Empty<double>();

        var spectrum = FftReal(signal);
        int n = spectrum.Length;
        int halfN = n / 2;
        // L8-FIX：保留 [0, bins] 与镜像 [n-bins, n-1]；中间（含 Nyquist）置零。
        // 原实现 [bins, n-1-bins] 置零把 DC 也零掉且镜像偏半个 bin——IFFT 实部泄漏。
        int bins = (int)(cutoffHz * n / sampleRateHz);
        if (bins > halfN) bins = halfN;
        for (int i = bins + 1; i < n - bins; i++)
            spectrum[i] = Complex.Zero;
        return InverseFftReal(spectrum);
    }

    // ── 基 2 FFT 内部实现（与 FftProcessor 相同算法，独立内联以便静态使用）──

    /// <summary>实信号 FFT（自动尾部补零到 2^N，返回补零后长度频谱）</summary>
    public static Complex[] FftReal(double[] signal)
    {
        int n = NextPow2(signal.Length);
        var x = new Complex[n];
        for (int i = 0; i < signal.Length; i++) x[i] = new Complex(signal[i], 0);
        FftInPlace(x);
        return x;
    }

    /// <summary>逆 FFT 后取实部（保留符号，物理正确）</summary>
    public static double[] InverseFftReal(Complex[] spectrum)
    {
        int n = spectrum.Length;
        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = Complex.Conjugate(spectrum[i]);
        FftInPlace(x);
        var result = new double[n];
        for (int i = 0; i < n; i++) result[i] = x[i].Real / n;
        return result;
    }

    /// <summary>幅度谱（与旧版 fft(double[]) 输出一致：各频率分量模值）</summary>
    public static double[] MagnitudeSpectrum(double[] signal)
    {
        var spectrum = FftReal(signal);
        var mag = new double[spectrum.Length];
        for (int i = 0; i < spectrum.Length; i++) mag[i] = spectrum[i].Magnitude;
        return mag;
    }

    private static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    private static void FftInPlace(Complex[] x)
    {
        int n = x.Length;
        if (n <= 1) return;

        // 位反转重排
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }

        // 蝶形运算
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = x[i + j];
                    var v = x[i + j + len / 2] * w;
                    x[i + j] = u + v;
                    x[i + j + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
