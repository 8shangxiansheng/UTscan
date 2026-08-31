namespace UTscan.Services.SignalProcessing;

/// <summary>
/// 直流基线去除（旧项目未吸收项）。
/// 超声回波信号常带直流偏置与低频漂移，影响闸门幅值判读。
/// </summary>
public static class BaselineRemoval
{
    /// <summary>
    /// 去直流：减去信号均值。最常用的基线校正。
    /// </summary>
    public static double[] RemoveMean(double[] x)
    {
        double mean = 0;
        for (int i = 0; i < x.Length; i++) mean += x[i];
        mean /= x.Length;

        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] - mean;
        return y;
    }

    /// <summary>
    /// 线性去趋势：拟合并减去一次直线基线（a + b·t），
    /// 消除楔形漂移（如耦合变化引起的缓慢上升/下降）。
    /// </summary>
    public static double[] DetrendLinear(double[] x)
    {
        int n = x.Length;
        if (n < 2) return (double[])x.Clone();

        // 最小二乘拟合 y = a + b·t
        double sumT = 0, sumY = 0, sumTT = 0, sumTY = 0;
        for (int t = 0; t < n; t++)
        {
            sumT += t;
            sumY += x[t];
            sumTT += (double)t * t;
            sumTY += (double)t * x[t];
        }
        double denom = n * sumTT - sumT * sumT;
        double b = denom != 0 ? (n * sumTY - sumT * sumY) / denom : 0;
        double a = (sumY - b * sumT) / n;

        var y = new double[n];
        for (int t = 0; t < n; t++) y[t] = x[t] - (a + b * t);
        return y;
    }

    /// <summary>
    /// 滑动均值基线扣除：用窗口宽度 window 的滑动平均估计基线，
    /// 再从原信号中减去。适合抑制缓慢起伏、保留快速超声回波。
    /// </summary>
    public static double[] RemoveMovingAverage(double[] x, int window)
    {
        if (window < 3 || x.Length < window) return RemoveMean(x);

        int half = window / 2;
        var y = new double[x.Length];

        // 前缀和求滑动均值（边界用可用样本数平均）
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + x[i];

        for (int i = 0; i < x.Length; i++)
        {
            int lo = Math.Max(0, i - half);
            int hi = Math.Min(x.Length - 1, i + half);
            double baseline = (prefix[hi + 1] - prefix[lo]) / (hi - lo + 1);
            y[i] = x[i] - baseline;
        }
        return y;
    }

    /// <summary>float 重载：去直流 + 线性去趋势（超声 A 扫标准前处理）</summary>
    public static float[] Process(float[] samples, bool detrend = false)
    {
        var x = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) x[i] = samples[i];

        var y = detrend ? DetrendLinear(x) : RemoveMean(x);

        var result = new float[y.Length];
        for (int i = 0; i < y.Length; i++) result[i] = (float)y[i];
        return result;
    }
}
