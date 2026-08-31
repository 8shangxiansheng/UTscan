namespace UTscan.Services.SignalProcessing;

/// <summary>
/// FIR 滤波器设计（窗口法，移植自旧项目 DyZMC/FIR-filter.cs 并补全窗口实现）。
///
/// 吸收与改进：
/// - 滤波器长度公式 N ≈ 2.1·2π/(Ws−Wp)（旧项目原式，凯撒窗经验近似）。
/// - 理想高通脉冲响应 hd[n] = (sin(π(n−α)) − sin(Wc(n−α))) / (π(n−α))，n=α 时取 (π−Wc)/π。
/// - 旧项目 Get_Win 只实现了不完整的三角窗；本实现补全汉明窗/汉宁窗/三角窗，
///   h[n] = hd[n]·w[n]，线性相位（α=(N−1)/2）。
/// </summary>
public class FirFilter
{
    /// <summary>滤波器长度（奇数最佳，保证线性相位）</summary>
    public int N { get; }

    private readonly double[] _kernel;

    /// <param name="wp">通带边界数字角频率（rad/sample）</param>
    /// <param name="ws">阻带边界数字角频率（rad/sample）</param>
    /// <param name="wc">截止数字角频率（rad/sample），取 (wp+ws)/2</param>
    /// <param name="window">窗函数类型，默认汉明窗</param>
    public FirFilter(double wp, double ws, double? wc = null, FirWindow window = FirWindow.Hamming)
        : this(wp, ws, wc ?? (wp + ws) / 2, window, highPass: true)
    {
    }

    private FirFilter(double wp, double ws, double wc, FirWindow window, bool highPass)
    {
        // 旧项目长度公式：N = ceil(2.1·2π / (Ws − Wp))
        double nEst = (2.1 * 2 * Math.PI) / (ws - wp);
        int n = (int)Math.Ceiling(nEst);
        if ((n & 1) == 0) n++; // 强制奇数长度，保证第 α=(N-1)/2 点存在
        N = Math.Max(n, 3);

        double alpha = (N - 1) / 2.0;
        _kernel = new double[N];

        for (int i = 0; i < N; i++)
        {
            double d = i - alpha;
            double hd;
            if (Math.Abs(d) < 1e-12)
                hd = highPass ? (Math.PI - wc) / Math.PI : wc / Math.PI;
            else
                hd = highPass
                    ? (Math.Sin(Math.PI * d) - Math.Sin(wc * d)) / (Math.PI * d)
                    : Math.Sin(wc * d) / (Math.PI * d);

            _kernel[i] = hd * Window(i, N, window);
        }
    }

    /// <summary>低通 FIR 工厂方法</summary>
    public static FirFilter LowPass(double wp, double ws, FirWindow window = FirWindow.Hamming)
        => new(wp, ws, (wp + ws) / 2, window, highPass: false);

    /// <summary>高通 FIR 工厂方法</summary>
    public static FirFilter HighPass(double wp, double ws, FirWindow window = FirWindow.Hamming)
        => new(wp, ws, (wp + ws) / 2, window, highPass: true);

    /// <summary>滤波器系数 h[n]</summary>
    public double[] Kernel => (double[])_kernel.Clone();

    /// <summary>
    /// 卷积滤波（直接卷积，边界按零填充；输出长度 = x.Length + N − 1，常用取前 x.Length 点）。
    /// </summary>
    public double[] Filter(double[] x)
    {
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double acc = 0;
            for (int k = 0; k < N && i - k >= 0; k++)
                acc += _kernel[k] * x[i - k];
            y[i] = acc;
        }
        return y;
    }

    /// <summary>float 重载</summary>
    public float[] Filter(float[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double acc = 0;
            for (int k = 0; k < N && i - k >= 0; k++)
                acc += _kernel[k] * x[i - k];
            y[i] = (float)acc;
        }
        return y;
    }

    private static double Window(int n, int nTotal, FirWindow window) => window switch
    {
        FirWindow.Rectangular => 1.0,
        FirWindow.Hanning => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (nTotal - 1)),
        FirWindow.Hamming => 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (nTotal - 1)),
        FirWindow.Triangular => n <= (nTotal - 1) / 2.0
            ? 2.0 * n / nTotal - 1
            : 2.0 * (nTotal - 1 - n) / (nTotal - 1),
        _ => 1.0
    };
}

/// <summary>窗函数类型</summary>
public enum FirWindow
{
    Rectangular,
    Hanning,
    Hamming,
    Triangular
}
