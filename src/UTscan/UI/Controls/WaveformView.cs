using System.Drawing;
using System.Windows.Forms;
using UTscan.Core.Enums;
using UTscan.Core.Models;
using UTscan.Services.SignalProcessing;

namespace UTscan.UI.Controls;

/// <summary>
/// A 扫波形渲染控件（可复用）。
/// 实时视图（AscanForm）与 C 扫跳转详情视图（AscanDetailForm）共用同一渲染逻辑：
/// 时间轴（µs）、网格、波形、闸门可视化（起点/宽度矩形 + 阈值线）、闸门测量读数。
/// 冻结语义由宿主窗体控制（停止更新 <see cref="Data"/> 即冻结画面）。
/// </summary>
public class WaveformView : Control
{
    // 审查 P3-2：GDI 资源提为字段复用，控件销毁时统一释放
    private readonly Pen _axisPen = new(Color.DarkGray, 0.5f);
    private readonly Pen _gridPen = new(Color.FromArgb(35, 35, 35), 0.5f);
    private readonly Pen _tracePen = new(Color.LimeGreen, 1.5f);
    private readonly Font _labelFont = new("Consolas", 8f);
    private readonly Font _gateFont = new("Microsoft YaHei UI", 8f);
    private readonly StringFormat _farFmt = new() { Alignment = StringAlignment.Far };

    private readonly GateAnalyzer _analyzer = new();

    /// <summary>
    /// 纵轴（电压）稳定标度引擎。替代原"每帧按 min/max 自适应满屏"：
    /// 快攻慢释放迟滞使纵轴稳定不抖动，保留不同样品回波幅值/位置的真实差异。
    /// </summary>
    private readonly AscanViewport _viewport = new();

    /// <summary>纵轴标度最近一次更新所基于的数据引用（同一引用不重复更新，避免冻结/重绘时标度漂移）。</summary>
    private AScanData? _lastViewportData;

    /// <summary>当前显示的波形数据（null 或空时不绘制）</summary>
    public AScanData? Data { get; set; }

    /// <summary>
    /// P0-A（对焦找波）：波形可见窗口起点（µs）——对应采集卡"延迟时间"，
    /// 横向平移波形移除无信号区；0 表示从采样点 0 开始。
    /// </summary>
    public float StartTimeUs { get; set; }

    /// <summary>
    /// P0-A（对焦找波）：波形可见窗口宽度（µs）——对应"采样长度"缩放，
    /// ≤0 表示显示全部采样范围；大于 0 时仅显示 [StartTimeUs, StartTimeUs+DisplayTimeUs]。
    /// </summary>
    public float DisplayTimeUs { get; set; }

    // ── P0-深度：横轴单位切换（µs ↔ mm 深度）──
    private bool _depthAxis;
    private float _soundVelocity = 1480f;
    /// <summary>横轴是否以深度(mm)显示（用声速换算 depth = t_us × v / 2000）</summary>
    public bool DepthAxis
    {
        get => _depthAxis;
        set { _depthAxis = value; Invalidate(); }
    }
    /// <summary>材料声速（m/s），默认 1480（与 SystemParams 一致）</summary>
    public float SoundVelocity
    {
        get => _soundVelocity;
        set { _soundVelocity = value > 0 ? value : 1480f; Invalidate(); }
    }
    /// <summary>时间 µs → 深度 mm（往返 ÷2，µs→s 与 m→mm 同式完成）</summary>
    public float TimeUsToDepthMm(float tUs) => tUs * _soundVelocity / 2000f;

    /// <summary>TCG 曲线叠加显示（null 或未启用=不绘制）</summary>
    public UTscan.Core.Models.TcgCurve? TcgOverlay { get; set; }

    /// <summary>
    /// P0-B：波形类型（RF/检波/正半波/负半波）——渲染前经 GateAnalyzer.Preprocess 预处理。
    /// 对焦观察时切换波形类型可更清晰识别表面波/回波极性。
    /// </summary>
    public UTscan.Core.Enums.WaveformType WaveformType { get; set; } = UTscan.Core.Enums.WaveformType.RF;

    /// <summary>叠加显示的闸门列表（Enabled=true 的闸门才会绘制）</summary>
    public List<GateConfig> Gates { get; } = new();

    /// <summary>
    /// 叠加帧列表（多帧对比）：在实时波形后方用半透明灰度绘制历史帧。
    /// null 或空 = 不绘制叠加层。叠加帧使用与实时帧相同的纵轴标度，保证幅值对比不失真。
    /// </summary>
    public List<AScanData>? OverlayFrames { get; set; }

    /// <summary>
    /// 显示滤波模式：None=原始数据，Median3=3点中值，Median5=5点中值，LowPass=低通平滑。
    /// 滤波仅作用于显示层，不影响原始数据/导出/成像。
    /// </summary>
    public DisplayFilterMode DisplayFilter { get; set; } = DisplayFilterMode.None;

    // ── P0-2 测量游标（双游标 A/B，µs 位置，启用时绘制竖线+读数）──
    /// <summary>游标是否启用</summary>
    public bool CursorsEnabled { get; set; }

    /// <summary>游标 A 位置（µs）</summary>
    public float CursorAUs { get; set; } = -1f;

    /// <summary>游标 B 位置（µs）</summary>
    public float CursorBUs { get; set; } = -1f;

    /// <summary>游标读数回调（宿主更新标签用）：返回 A/B 位置与幅值</summary>
    public Action<(float AUs, float BUs, float AV, float BV, float DeltaTUs, float DeltaV)>? CursorReadoutChanged { get; set; }

    // ── 鼠标滚轮缩放（20260829）：横轴时间缩放锚定光标处；Ctrl+滚轮=纵轴幅值缩放（对称 0V 中心）──
    private const float MinViewUs = 0.05f;
    private const float MaxViewUs = 1e7f;
    private const float HWheelZoomIn = 0.82f;
    private const float HWheelZoomOut = 1f / 0.82f;
    private const float VWheelZoomIn = 0.82f;
    private const float VWheelZoomOut = 1f / 0.82f;
    private const float MinHalfScaleV = 1e-4f;

    /// <summary>视图窗口变化通知（滚轮缩放后同步数值控件用）：(startTimeUs, displayTimeUs)</summary>
    public event Action<float, float>? ViewWindowChanged;

    /// <summary>首次放置游标但游标未启用时，请求宿主勾选"游标"复选框</summary>
    public event Action? CursorsAutoEnableRequested;

    /// <summary>
    /// P4-A：重置纵轴稳定标度（波形类型切换/通道切换/首次数据显示时调用），
    /// 避免沿用旧标度导致新波形过小或过大。
    /// </summary>
    public void ResetViewport()
    {
        _viewport.Reset();
        _lastViewportData = null;
    }

    public WaveformView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = true;   // 接收鼠标滚轮/点击交互
        BackColor = Color.Black;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Color.Black);

        var data = Data;
        if (data is not { PointCount: > 1 })
        {
            g.DrawString("无数据", _labelFont, Brushes.DimGray, 8, 8);
            return;
        }

        const int ml = 56, mr = 16, mt = 22, mb = 34;
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= ml + mr || h <= mt + mb) return;
        int plotW = w - ml - mr, plotH = h - mt - mb;

        // ── 坐标范围 ──
        // P0-B：波形类型预处理（RF 原样 / 检波取绝对值 / 正负半波置零另一极性）
        // P1-2：显示滤波仅作用于此显示副本（原始 Samples 不变，导出/成像不受影响）。
        float[] samples = ApplyDisplayFilter(GateAnalyzer.Preprocess(data.Samples, WaveformType));
        // 时间轴：优先用采样率换算为 µs；采样率无效时按 0..100% 相对刻度
        float dt = data.SampleRate > 0 ? 1f / data.SampleRate * 1e6f : 0f;
        float totalUs = dt > 0 ? data.PointCount * dt : 100f;
        // P0-A：可见窗口（延迟时间偏移 + 采样长度缩放）
        // P0-2：显示时间 startUs 相对触发时刻(t=0)，而 samples[0] 对应 −TriggerOffsetUs。
        // 计算采样索引时需加偏移（使 t=0 对应触发时刻）。
        float startUs = (StartTimeUs > 0 ? StartTimeUs : 0f) + data.TriggerOffsetUs;
        float viewUs = DisplayTimeUs > 0 ? DisplayTimeUs : totalUs - (StartTimeUs > 0 ? StartTimeUs : 0f);
        if (viewUs <= 0) viewUs = totalUs;

        // P4-A：纵轴稳定标度（替代原每帧 min/max 自适应满屏）。
        // 用当前帧峰值驱动迟滞标度，纵轴以 0V 为中心、幅值到屏幕高度映射稳定，
        // 从而消除噪声逐帧跳动的显示抖动，并保留不同样品回波幅值的真实差异。
        // 仅在新数据帧到达时更新标度（同一数据引用在冻结/重绘时不重复更新，避免标度漂移）。
        if (!ReferenceEquals(_lastViewportData, data))
        {
            _lastViewportData = data;
            _viewport.UpdateFromSamples(samples);
        }
        float halfScale = _viewport.RangeHalfV(_viewport.DisplayPeakV);
        float min = -halfScale, max = halfScale, range = max - min;

        // ── 网格与坐标轴刻度（自适应密度） ──
        const int xTicks = 5, yTicks = 4;
        g.DrawLine(_axisPen, ml, mt, ml, h - mb);
        g.DrawLine(_axisPen, ml, h - mb, w - mr, h - mb);

        // X 轴：主刻度数 = xTicks（含首尾），间隔 = viewUs / xTicks。
        // 标签值 = startUs + i*interval；位数按数值量级自适应。
        // P0-深度：DepthAxis 时标签换算为深度 mm（t_us × v / 2000），单位标注 mm。
        for (int i = 0; i <= xTicks; i++)
        {
            int x = ml + plotW * i / xTicks;
            float tUs = startUs + viewUs * i / xTicks;
            string label = dt > 0
                ? (DepthAxis ? FormatAxisDepth(TimeUsToDepthMm(tUs)) : FormatAxisUs(tUs))
                : $"{i * 100 / xTicks}%";
            // 首尾标签靠两端贴齐，中间居中对齐；相邻标签间距最小 40px 防重叠
            bool first = i == 0, last = i == xTicks;
            var fmt = new StringFormat { Alignment = first ? StringAlignment.Near : (last ? StringAlignment.Far : StringAlignment.Center) };
            float lx = first ? ml : (last ? w - mr : x);
            g.DrawString(label, _labelFont, Brushes.Gray, new PointF(lx, h - mb + 4), fmt);
            if (i > 0) g.DrawLine(_gridPen, x, mt, x, h - mb);   // 首条与轴重合，跳过
        }
        if (dt > 0)
            g.DrawString(DepthAxis ? "mm" : "µs", _labelFont, Brushes.DimGray, w - mr - 18, h - mb + 4, _farFmt);

        // Y 轴：主刻度数 = yTicks（含 0 与正负满量程），间隔 = range / yTicks。
        // 标签值 = min + j*interval；位数按电压量级自适应（mV/V 自动换算）。
        for (int j = 0; j <= yTicks; j++)
        {
            int y = (h - mb) - plotH * j / yTicks;
            float v = min + range * j / yTicks;
            string label = FormatAxisV(v, max);
            g.DrawString(label, _labelFont, Brushes.Gray, new PointF(2, y - 7));
            if (j > 0) g.DrawLine(_gridPen, ml, y, w - mr, y);
        }
        // Y 轴单位标注（5-FIX：纵轴顶部醒目标注，格式 "幅值(V)"）
        if (range > 0)
            g.DrawString($"幅值({FormatAxisVUnit(max)})", _labelFont, Brushes.DimGray, new PointF(2, mt - 4));

        g.DrawString($"Points: {data.PointCount}", _labelFont, Brushes.Gray, w - mr - 90, 4, _farFmt);

        // ── 叠加帧（多帧对比，在实时波形后方用半透明灰度绘制──
        if (OverlayFrames is { Count: > 0 })
        {
            float alphaStep = 0.6f / Math.Max(OverlayFrames.Count, 1);
            for (int fi = 0; fi < OverlayFrames.Count; fi++)
            {
                var of = OverlayFrames[fi];
                if (of is not { PointCount: > 1 }) continue;
                float[] ofSamples = GateAnalyzer.Preprocess(of.Samples, WaveformType);
                AscanViewport.ComputeVisibleRange(startUs, viewUs, dt, of.PointCount, out int of0, out int of1);
                int ofN = of1 - of0 + 1;
                if (ofN < 2) continue;
                float alpha = alphaStep * (fi + 1);
                int gray = (int)(80 + 120 * (float)Math.Sqrt(alpha));
                using var overlayPen = new Pen(Color.FromArgb(gray, gray, gray), 1f);
                var pts = new PointF[ofN];
                for (int i = of0; i <= of1; i++)
                {
                    // P0-1-FIX：绝对时间映射（与主波形/闸门/游标同源）
                    float x = AscanViewport.SampleToPixelX(i, dt, startUs, viewUs, plotW, ml);
                    float y = (h - mb) - (ofSamples[i] - min) / range * plotH;
                    pts[i - of0] = new PointF(x, y);
                }
                g.DrawLines(overlayPen, pts);
            }
        }

        // ── 波形折线（P0-A：按可见窗口裁剪采样点）──
        // P4-A：由 AscanViewport.ComputeVisibleRange 计算合法索引区间，杜绝
        // "延迟过大"时产生非法/单点/NaN 坐标（DrawLines 抛异常 → OnPaint 崩溃 → 红叉）。
        // 1-FIX（审查 20260828）：x 坐标用绝对时间映射而非窗口内归一化——
        // 原实现 (i-i0)/(n-1)*plotW 在"采样长 > 实际时长"（i1 被钳到 last）时
        // 波形被错误拉伸铺满整个窗口，视觉上"相对位置不变"。
        // 现按 tUs=(startUs + i*dt) 在 [startUs, startUs+viewUs] 中的绝对位置映射，
        // 超窗部分自然留白，波形随采样长正确缩放。
        int i0, i1;
        AscanViewport.ComputeVisibleRange(startUs, viewUs, dt, data.PointCount, out i0, out i1);
        int n = i1 - i0 + 1;
        if (n >= 2)
        {
            var points = new PointF[n];
            for (int i = i0; i <= i1; i++)
            {
                // P0-1-FIX：绝对时间映射（与闸门/游标同源），超窗部分正确留白
                float x = AscanViewport.SampleToPixelX(i, dt, startUs, viewUs, plotW, ml);
                float y = (h - mb) - (samples[i] - min) / range * plotH;
                points[i - i0] = new PointF(x, y);
            }
            g.DrawLines(_tracePen, points);
        }

        // ── TCG 曲线叠加（随深度补偿增益，深黄色线 + 右轴 dB）──
        if (TcgOverlay is { Enabled: true, PointCount: >= 2 })
        {
            using var tcgPen = new Pen(Color.DarkOrange, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.DashDot };
            int tcgN = 64;
            var tcgPts = new PointF[tcgN];
            for (int k = 0; k < tcgN; k++)
            {
                float tUs = startUs + viewUs * k / (tcgN - 1);
                float depthMm = TimeUsToDepthMm(tUs);
                float gainDb = TcgOverlay.GainAtDepthMm(depthMm);
                // 补偿曲线映射到右轴：满幅 ±30dB 对应图区高度（视觉参考，非精确刻度）
                float ny = mt + (30f - Math.Clamp(gainDb, -30f, 30f)) / 60f * (h - mb - mt);
                float nx = ml + (tUs - startUs) / viewUs * plotW;
                tcgPts[k] = new PointF(nx, ny);
            }
            g.DrawLines(tcgPen, tcgPts);
            g.DrawString("TCG", _labelFont, Brushes.DarkOrange, ml + plotW - 30, mt + 2);
        }

        // ── 闸门可视化与测量（闸门时间坐标相对窗口起点）──
        // D1：同步闸门（Role=Sync）启用且测到有效首穿偏移时，数据闸门按
        // "同步首穿偏移 + 标称起点"联动测量与绘制（与 GateAnalyzer.ComputeDataGateStart 一致）。
        float syncOffsetUs = GetSyncGateOffsetUs(data);
        foreach (var gate in Gates)
        {
            if (!gate.Enabled) continue;
            bool isSync = gate.Role == UTscan.Core.Enums.GateRole.Sync;
            float effectiveStartUs = (isSync || syncOffsetUs < 0f) ? gate.StartUs : gate.StartUs + syncOffsetUs;
            DrawGate(g, gate, data, dt, startUs, viewUs, min, range, ml, mt, plotW, plotH, h, mb, effectiveStartUs);
        }

        // ── P0-2 测量游标：A/B 竖线 + 读数 ──
        if (CursorsEnabled && dt > 0)
        {
            DrawCursor(g, data, CursorAUs, Color.Cyan, "A", startUs, viewUs, min, range, ml, mt, plotW, plotH, h, mb, dt);
            DrawCursor(g, data, CursorBUs, Color.Orange, "B", startUs, viewUs, min, range, ml, mt, plotW, plotH, h, mb, dt);
            // 回调读数（宿主刷新标签）
            CursorReadoutChanged?.Invoke(ComputeCursorReadout(data, dt));
        }
    }

    /// <summary>游标读数：A/B 位置 µs 与对应采样幅值、时间差、幅值差。</summary>
    private (float AUs, float BUs, float AV, float BV, float DeltaTUs, float DeltaV) ComputeCursorReadout(
        AScanData data, float dt)
    {
        float SampleAt(float us)
        {
            if (dt <= 0) return 0f;
            int idx = (int)(us / dt);
            if (idx < 0 || idx >= Math.Min(data.PointCount, data.Samples.Length)) return 0f;
            return data.Samples[idx];
        }
        float aUs = CursorAUs >= 0 ? CursorAUs : 0f;
        float bUs = CursorBUs >= 0 ? CursorBUs : 0f;
        float aV = CursorAUs >= 0 ? SampleAt(aUs) : 0f;
        float bV = CursorBUs >= 0 ? SampleAt(bUs) : 0f;
        return (aUs, bUs, aV, bV, Math.Abs(bUs - aUs), Math.Abs(bV - aV));
    }

    private void DrawCursor(Graphics g, AScanData data, float us, Color color, string tag,
        float startUs, float viewUs, float min, float range, int ml, int mt, int plotW, int plotH, int h, int mb, float dt)
    {
        if (us < 0) return;
        float x = ml + (us - startUs) / viewUs * plotW;
        if (x < ml || x > ml + plotW) return;
        using var pen = new Pen(color, 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawLine(pen, x, mt, x, h - mb);
        // 幅值读数（标签在顶部）
        int idx = (int)(us / dt);
        float v = (idx >= 0 && idx < Math.Min(data.PointCount, data.Samples.Length)) ? data.Samples[idx] : 0f;
        using var brush = new SolidBrush(color);
        // 深度轴同步：DepthAxis 时游标位置以 mm 标注（与 X 轴刻度一致，避免混合单位）
        string posLabel = DepthAxis ? $"{TimeUsToDepthMm(us):0.##}mm" : $"{us:0.##}µs";
        g.DrawString($"{tag}:{posLabel} {v:G3}V", _labelFont, brush, Math.Max(x - 40, ml), mt - 16);
    }

/// <summary>
    /// 显示滤波（P1-2）：None 原样返回；中值/低通作用于显示副本。
    /// 不修改调用方数组——Preprocess 已返回新数组，此处就地滤波安全。
    /// </summary>
    private float[] ApplyDisplayFilter(float[] samples)
    {
        switch (DisplayFilter)
        {
            case DisplayFilterMode.Median3:
                return MedianFilter3(samples);
            case DisplayFilterMode.Median5:
                return MedianFilter5(samples);
            case DisplayFilterMode.LowPass:
                return LowPassSmooth(samples);
            default:
                return samples;
        }
    }

    private static float[] MedianFilter3(float[] s)
    {
        if (s.Length < 3) return s;
        var r = (float[])s.Clone();
        for (int i = 1; i < s.Length - 1; i++)
        {
            float a = s[i - 1], b = s[i], c = s[i + 1];
            r[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }
        return r;
    }

    private static float[] MedianFilter5(float[] s)
    {
        if (s.Length < 5) return s;
        var r = (float[])s.Clone();
        for (int i = 2; i < s.Length - 2; i++)
        {
            var w = new[] { s[i - 2], s[i - 1], s[i], s[i + 1], s[i + 2] };
            Array.Sort(w);
            r[i] = w[2];
        }
        return r;
    }

    private static float[] LowPassSmooth(float[] s)
    {
        if (s.Length < 3) return s;
        var r = new float[s.Length];
        r[0] = s[0];
        r[^1] = s[^1];
        for (int i = 1; i < s.Length - 1; i++)
            r[i] = (s[i - 1] + s[i] + s[i + 1]) / 3f;
        return r;
    }

    /// <summary>X 轴刻度值格式化：µs 量级自适应位数。
    /// &lt;0.1µs 显示 3 位小数；&lt;1 显示 2 位；&lt;100 显示 1 位；≥100 无小数（避免标签拥挤）。
    /// </summary>
    internal static string FormatAxisUs(float us)
    {
        if (us < 0.1f) return us.ToString("0.000");
        if (us < 1f) return us.ToString("0.00");
        if (us < 100f) return us.ToString("0.0");
        return us.ToString("0");
    }

    /// <summary>P0-深度：深度轴刻度格式化（mm，自适应位数）。</summary>
    internal static string FormatAxisDepth(float mm)
    {
        if (mm < 0.01f) return mm.ToString("0.000");
        if (mm < 1f) return mm.ToString("0.00");
        if (mm < 100f) return mm.ToString("0.0");
        return mm.ToString("0");
    }

    /// <summary>
    /// Y 轴刻度值格式化：按轴满量程（fullScale）统一换算单位，保证刻度值与轴单位标注一致。
    /// 满量程 ≥0.1V → V（1 位小数）；≥1mV → 整数 mV；≥1µV → 整数 µV；否则科学计数。
    /// 阈值与 <see cref="FormatAxisVUnit"/> 严格一致。
    /// </summary>
    internal static string FormatAxisV(float v, float fullScale)
    {
        float a = Math.Abs(fullScale);
        if (a >= 1e-1f) return v.ToString("0.0");
        if (a >= 1e-3f) return (v * 1e3f).ToString("0");
        if (a >= 1e-6f) return (v * 1e6f).ToString("0");
        return v.ToString("0.0e0");
    }

    /// <summary>Y 轴单位标注（按当前满量程量级；0/未知量程默认 V）。阈值与 FormatAxisV 一致。
    /// 5-FIX（审查 20260828）：去掉括号，更醒目地标注坐标轴单位。</summary>
    internal static string FormatAxisVUnit(float peakV)
    {
        float a = Math.Abs(peakV);
        if (a >= 1e-1f || a == 0f) return "V";
        if (a >= 1e-3f) return "mV";
        return "µV";
    }

    /// <summary>D1：返回已启用同步闸门的首穿偏移（µs）；无同步闸门/未启用/未测到穿越时返回负数。</summary>
    private float GetSyncGateOffsetUs(AScanData data)
    {
        foreach (var gate in Gates)
        {
            if (gate.Role != UTscan.Core.Enums.GateRole.Sync || !gate.Enabled) continue;
            var r = _analyzer.Analyze(data, gate);
            if (r.SyncFirstCrossOffsetUs >= 0f)
                return r.SyncFirstCrossOffsetUs;
        }
        return -1f;
    }

    /// <summary>
    /// 绘制单个闸门：虚线矩形、±阈值电平线、闸门测量读数。
    /// <paramref name="effectiveStartUs"/> 为实际测量起点（D1：数据闸门=同步偏移+标称起点）。
    /// </summary>
    private void DrawGate(Graphics g, GateConfig gate, AScanData data, float dt, float startUs,
        float viewUs, float min, float range, int ml, int mt, int plotW, int plotH, int h, int mb,
        float effectiveStartUs)
    {
        // 闸门绝对时间 → 窗口内相对坐标（P0-A：随波形平移缩放正确移动；D1：用联动后的实际起点）
        float x1 = ml + (effectiveStartUs - startUs) / viewUs * plotW;
        float x2 = ml + (effectiveStartUs + gate.WidthUs - startUs) / viewUs * plotW;
        if (x2 < ml || x1 > ml + plotW) return;   // 闸门完全在视窗外

        x1 = Math.Max(x1, ml);
        x2 = Math.Min(x2, ml + plotW);
        float yTop = mt + 6, yBottom = h - mb - 6;

        using var gatePen = new Pen(gate.GateColor, 1.4f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawRectangle(gatePen, x1, yTop, Math.Max(x2 - x1, 1f), yBottom - yTop);

        // 阈值电平线（±ThresholdV 对称绘制，RF 波形正负都有意义）
        float yTh = (h - mb) - (gate.ThresholdV - min) / range * plotH;
        float yThN = (h - mb) - (-gate.ThresholdV - min) / range * plotH;
        using var thPen = new Pen(Color.FromArgb(160, gate.GateColor), 1f)
        { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        g.DrawLine(thPen, x1, yTh, x2, yTh);
        g.DrawLine(thPen, x1, yThN, x2, yThN);

        // 闸门测量读数（GateAnalyzer.Analyze：峰值幅度 / 峰值位置 / 超阈值判定）
        if (dt > 0)
        {
            // D1：测量在联动后的实际区间上进行——用副本闸门在 effectiveStartUs 处分析。
            // GateConfig 为无托管外资源 POCO，无需释放。
            var eff = new GateConfig
            {
                Name = gate.Name,
                StartUs = effectiveStartUs,
                WidthUs = gate.WidthUs,
                ThresholdV = gate.ThresholdV,
                GateColor = gate.GateColor
            };
            var r = _analyzer.Analyze(data, eff);
            // 4b-FIX（审查 20260828）：未超阈值时读数灰显并明示"未达阈值"——
            // 原实现无条件显示彩色读数，0.5V 阈值 vs 0.03V 回波时仍以亮色输出峰值，易误读为检出。
            bool above = r.IsAboveThreshold;
            using var brush = new SolidBrush(above ? gate.GateColor : System.Drawing.Color.DimGray);
            string status = above ? "超阈值" : "未达阈值";
            // 深度轴同步：DepthAxis 时峰值位置以 mm 标注（与 X 轴刻度一致，避免混合单位）
            string posLabel = DepthAxis
                ? $"{TimeUsToDepthMm(r.PeakPositionUs):0.##}mm"
                : $"{r.PeakPositionUs:0.##}µs";
            string txt = $"{gate.Name}  峰值 {r.PeakAmplitude:G3}V @ {posLabel}  " +
                         $"峰峰 {r.PeakToPeak:G3}V  {status}";
            g.DrawString(txt, _gateFont, brush, Math.Max(x1, ml), 4);
        }
        else
        {
            using var brush = new SolidBrush(gate.GateColor);
            g.DrawString(gate.Name, _gateFont, brush, Math.Max(x1, ml), 4);
        }
    }

    // ── 鼠标滚轮缩放 / 游标点击（20260829）──
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused) Focus();   // 确保滚轮消息路由到本控件
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Data is { PointCount: > 1 })
            HandleWheel(e);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button is MouseButtons.Left or MouseButtons.Right)
            PlaceCursor(e);
    }

    /// <summary>点击放置游标：左键=A，右键=B；游标未启用时自动请求宿主启用。</summary>
    private void PlaceCursor(MouseEventArgs e)
    {
        if (!CursorsEnabled)
        {
            CursorsAutoEnableRequested?.Invoke();
            CursorsEnabled = true;
        }
        var data = Data;
        if (data is not { PointCount: > 1, SampleRate: > 0 }) return;
        float dt = 1f / data.SampleRate * 1e6f;
        float totalUs = data.PointCount * dt;
        int w = ClientSize.Width, h = ClientSize.Height;
        const int ml = 56, mr = 16, mt = 22, mb = 34;
        if (w <= ml + mr || h <= mt + mb) return;
        int plotW = w - ml - mr;
        float px = Math.Clamp(e.X, ml, ml + plotW);
        float startUs = (StartTimeUs > 0 ? StartTimeUs : 0f) + data.TriggerOffsetUs;
        float viewUs = DisplayTimeUs > 0 ? DisplayTimeUs : totalUs - (StartTimeUs > 0 ? StartTimeUs : 0f);
        if (viewUs <= 0) viewUs = totalUs;
        float tUs = startUs + (px - ml) / plotW * viewUs;
        tUs = Math.Clamp(tUs, 0f, totalUs + data.TriggerOffsetUs);
        if (e.Button == MouseButtons.Left) CursorAUs = tUs;
        else CursorBUs = tUs;
        Invalidate();
    }

    private void HandleWheel(MouseEventArgs e)
    {
        var data = Data!;
        int w = ClientSize.Width, h = ClientSize.Height;
        const int ml = 56, mr = 16, mt = 22, mb = 34;
        if (w <= ml + mr || h <= mt + mb) return;
        int plotW = w - ml - mr, plotH = h - mt - mb;
        float dt = data.SampleRate > 0 ? 1f / data.SampleRate * 1e6f : 0f;
        float totalUs = dt > 0 ? data.PointCount * dt : 100f;
        float startUs = (StartTimeUs > 0 ? StartTimeUs : 0f) + data.TriggerOffsetUs;
        float viewUs = DisplayTimeUs > 0 ? DisplayTimeUs : totalUs - (StartTimeUs > 0 ? StartTimeUs : 0f);
        if (viewUs <= 0) viewUs = totalUs;

        if (ModifierKeys.HasFlag(Keys.Control))
        {
            // 纵轴幅值缩放（对称 0V 中心）；ManualHalfScale 优先于迟滞自动标度
            float half = _viewport.RangeHalfV(_viewport.DisplayPeakV);
            float newHalf = Math.Clamp(half * (e.Delta > 0 ? VWheelZoomIn : VWheelZoomOut), MinHalfScaleV, 1e6f);
            _viewport.ManualHalfScale = newHalf;
        }
        else
        {
            // 横轴时间缩放，锚定光标处时间（保持光标下时刻不动）
            float px = Math.Clamp(e.X, ml, ml + plotW);
            float tAnchor = startUs + (px - ml) / plotW * viewUs;
            float ratio = (tAnchor - startUs) / viewUs;
            float factor = e.Delta > 0 ? HWheelZoomIn : HWheelZoomOut;
            float newViewUs = Math.Clamp(viewUs * factor, MinViewUs, Math.Max(MaxViewUs, totalUs));
            if (newViewUs >= totalUs * 0.999f)
            {
                // 缩放到接近全程 → 复位为"显示全部"（StartTimeUs=0 且 DisplayTimeUs=0）
                StartTimeUs = 0f;
                DisplayTimeUs = 0f;
                ViewWindowChanged?.Invoke(0f, 0f);
            }
            else
            {
                float newStartAbs = tAnchor - ratio * newViewUs;
                float newStartApp = Math.Max(0f, newStartAbs - data.TriggerOffsetUs);
                StartTimeUs = newStartApp;
                DisplayTimeUs = newViewUs;
                ViewWindowChanged?.Invoke(newStartApp, newViewUs);
            }
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _axisPen.Dispose();
            _gridPen.Dispose();
            _tracePen.Dispose();
            _labelFont.Dispose();
            _gateFont.Dispose();
            _farFmt.Dispose();
        }
        base.Dispose(disposing);
    }
}
