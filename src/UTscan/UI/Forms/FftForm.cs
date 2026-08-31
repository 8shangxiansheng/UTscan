using System.Drawing;
using System.Windows.Forms;
using UTscan.Core.Interfaces;
using UTscan.Services.SignalProcessing;

namespace UTscan.UI.Forms;

/// <summary>
/// FFT 频谱窗体（说明书 3.2.1/3.6.3：确认探头频率、设置滤波范围的依据）。
/// 从采集卡取当前 A 扫 → FFT 幅值谱 → 显示频谱图 + 峰值频率。
/// 数据源：实时 A 扫（每次刷新重算）或冻结帧（冻结后静态分析）。
/// </summary>
public class FftForm : Form
{
    private readonly IDataAcquisition _daq;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly FftProcessor _fft = new();
    private readonly PictureBox _pic;
    private readonly Label _lblPeak;
    private readonly CheckBox _chkFreeze;
    private readonly CheckBox _chkWindow;
    private float[]? _frozenSpectrum;
    private float _frozenSampleRate;

    public FftForm(IDataAcquisition daq)
    {
        _daq = daq;
        Text = "FFT 频谱分析";
        ClientSize = new System.Drawing.Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        // 顶部：冻结 + 窗函数 + 峰值信息
        var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6) };
        _chkFreeze = new CheckBox { Text = "冻结（停止刷新）", Left = 6, Top = 6, Width = 120 };
        top.Controls.Add(_chkFreeze);
        _chkWindow = new CheckBox { Text = "Hann窗", Left = 132, Top = 6, Width = 64 };
        _chkWindow.CheckedChanged += (_, _) => { if (!_chkFreeze.Checked) UpdateSpectrum(); };
        top.Controls.Add(_chkWindow);
        _lblPeak = new Label { Text = "峰值频率: --", Left = 200, Top = 8, Width = 400, ForeColor = Color.DimGray };
        top.Controls.Add(_lblPeak);
        Controls.Add(top);

        _pic = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
        Controls.Add(_pic);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _refreshTimer.Tick += (_, _) => { if (!_chkFreeze.Checked) UpdateSpectrum(); };
        _refreshTimer.Start();

        FormClosed += (_, _) => _refreshTimer.Stop();
        UpdateSpectrum();
    }

    private void UpdateSpectrum()
    {
        var data = _daq.GetCurrentData();
        if (data.PointCount < 2) return;

        float[] samples = data.Samples;
        float sampleRate = data.SampleRate > 0 ? data.SampleRate : 100e6f;

        // FFT 输入长度受逻辑采样点数限制（池化数组可能超长、尾部为归还清零的冗余，
        // 直接取 Samples.Length 会把零值当信号，频谱被 sinc 插值污染）。
        int maxLen = Math.Min(data.PointCount, samples.Length);
        int n = 2;
        while (n * 2 <= maxLen) n *= 2;
        if (n < 2) return;

        var input = new float[n];
        Array.Copy(samples, input, n);

        // 可选窗函数：Hann 窗抑制矩形窗频谱泄漏（旁瓣），改善探头频率峰分辨。
        // 默认关闭（矩形窗=原始数据，与既往行为一致）；开启时频谱能量需 ×2 归一化（Hann 相干增益 0.5）。
        if (_chkWindow.Checked)
            ApplyHannWindow(input);

        float[] spectrum;
        try { spectrum = _fft.Fft(input); }
        catch (ArgumentException) { return; }   // 非 2 幂（理论上不会，截取已保证）

        _frozenSpectrum = spectrum;
        _frozenSampleRate = sampleRate;
        RenderSpectrum(spectrum, sampleRate, n);
    }

    /// <summary>Hann 窗（周期性）：w[i] = 0.5×(1 − cos(2πi/N))，抑制频谱泄漏。</summary>
    private static void ApplyHannWindow(float[] x)
    {
        int n = x.Length;
        for (int i = 0; i < n; i++)
            x[i] *= 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / n));
    }

    private void RenderSpectrum(float[] spectrum, float sampleRate, int n)
    {
        // 只画正频率半谱（0 ~ Nyquist = sampleRate/2）
        int halfN = n / 2;
        var bmp = new Bitmap(_pic.ClientSize.Width > 50 ? _pic.ClientSize.Width : 740,
                             _pic.ClientSize.Height > 50 ? _pic.ClientSize.Height : 480);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        const int ml = 60, mr = 16, mt = 24, mb = 40;
        int plotW = bmp.Width - ml - mr, plotH = bmp.Height - mt - mb;
        if (plotW <= 0 || plotH <= 0) { bmp.Dispose(); return; }

        // 幅值范围
        float maxAmp = 0;
        for (int i = 1; i < halfN; i++) if (spectrum[i] > maxAmp) maxAmp = spectrum[i];
        if (maxAmp < 1e-9f) maxAmp = 1f;

        // 峰值频率（跳过 DC bin 0）
        int peakBin = 1;
        for (int i = 2; i < halfN; i++) if (spectrum[i] > spectrum[peakBin]) peakBin = i;
        float peakHz = (float)peakBin / n * sampleRate;
        _lblPeak.Text = $"峰值频率: {peakHz / 1e6f:F2} MHz (bin {peakBin}, 幅值 {spectrum[peakBin]:G3})";

        // 网格 + 坐标
        using var gridPen = new Pen(Color.FromArgb(35, 35, 35), 0.5f);
        using var axisPen = new Pen(Color.DarkGray, 0.5f);
        using var tracePen = new Pen(Color.LimeGreen, 1.2f);
        using var font = new Font("Consolas", 8f);
        for (int i = 1; i <= 5; i++)
        {
            int x = ml + plotW * i / 5;
            g.DrawLine(gridPen, x, mt, x, bmp.Height - mb);
            float hz = (float)i / 5 * sampleRate / 2;
            g.DrawString($"{hz / 1e6f:F0}MHz", font, Brushes.Gray, x - 20, bmp.Height - mb + 4);
        }
        g.DrawLine(axisPen, ml, mt, ml, bmp.Height - mb);
        g.DrawLine(axisPen, ml, bmp.Height - mb, bmp.Width - mr, bmp.Height - mb);
        g.DrawString($"{maxAmp:G3}", font, Brushes.Gray, 2, mt - 10);
        g.DrawString($"N={n}  Fs={sampleRate / 1e6f:F0}MHz", font, Brushes.Gray, ml, 4);

        // 频谱折线（正频率半谱）
        var pts = new PointF[halfN];
        for (int i = 0; i < halfN; i++)
        {
            float x = ml + (float)i / (halfN - 1) * plotW;
            float y = (bmp.Height - mb) - spectrum[i] / maxAmp * plotH;
            pts[i] = new PointF(x, y);
        }
        g.DrawLines(tracePen, pts);

        var old = _pic.Image;
        _pic.Image = bmp;
        old?.Dispose();
    }
}
