using System.Drawing;
using System.Windows.Forms;
using UTscan.Core.Models;

namespace UTscan.UI.Forms;

/// <summary>
/// TCG（时间补偿增益/深度补偿）曲线编辑器。
/// 折线控制点：深度(mm) 横轴 × 补偿增益(dB) 纵轴，两点间线性插值。
/// 支持：拖拽控制点、右键删点、双击加点、重置、声速/零点联动（µs↔mm 已由 TcgCurve 换算）。
/// 参照 EPOCH 650 DAC/TCG 模式：对厚大衰减工件随深度自动提升接收增益。
/// </summary>
public class TcgCurveEditorForm : Form
{
    private readonly TcgCurve _curve;
    private readonly PictureBox _pic;
    private readonly Label _lblHint;
    private readonly NumericUpDown _numVelocity;
    private readonly Button _btnReset, _btnAdd, _btnDone;
    private int _dragIndex = -1;
    private const int PaddingL = 56, PaddingR = 20, PaddingT = 24, PaddingB = 48;
    private const int PointRadius = 5;

    public TcgCurve Edited => _curve;

    public TcgCurveEditorForm(TcgCurve curve)
    {
        _curve = curve;
        Text = "TCG 曲线编辑器（深度补偿增益）";
        ClientSize = new System.Drawing.Size(640, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
        _lblHint = new Label { Text = "拖拽控制点调整补偿增益；双击添加点；右键删除点", Left = 8, Top = 10, Width = 380 };
        top.Controls.Add(_lblHint);
        top.Controls.Add(new Label { Text = "声速(m/s):", Left = 400, Top = 10, Width = 72, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _numVelocity = new NumericUpDown { Left = 474, Top = 8, Width = 80, Minimum = 500, Maximum = 10000, Value = (decimal)_curve.SoundVelocity, Increment = 10 };
        _numVelocity.ValueChanged += (_, _) => { _curve.SoundVelocity = (float)_numVelocity.Value; _pic.Invalidate(); };
        top.Controls.Add(_numVelocity);
        _btnAdd = new Button { Text = "加", Left = 560, Top = 6, Width = 28, Height = 26 };
        _btnAdd.Click += (_, _) => AddMidPoint();
        top.Controls.Add(_btnAdd);
        _btnReset = new Button { Text = "重置", Left = 590, Top = 6, Width = 44, Height = 26 };
        _btnReset.Click += (_, _) => { _curve.Reset(); _pic.Invalidate(); };
        top.Controls.Add(_btnReset);
        Controls.Add(top);

        _pic = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black };
        _pic.Paint += OnPaintCurve;
        _pic.MouseDown += OnMouseDown;
        _pic.MouseMove += OnMouseMove;
        _pic.MouseUp += (_, _) => _dragIndex = -1;
        _pic.DoubleClick += OnDoubleClick;
        Controls.Add(_pic);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8) };
        _btnDone = new Button { Text = "完成", Left = 500, Top = 6, Width = 80, Height = 28, DialogResult = DialogResult.OK };
        bottom.Controls.Add(_btnDone);
        Controls.Add(bottom);
    }

    /// <summary>在相邻断点中间插入一个点（取两断点深度中点，增益取两断点均值）。</summary>
    private void AddMidPoint()
    {
        for (int i = 1; i < _curve.PointCount; i++)
        {
            var (d0, g0) = _curve.GetPoint(i - 1);
            var (d1, g1) = _curve.GetPoint(i);
            float midD = (d0 + d1) / 2f, midG = (g0 + g1) / 2f;
            if (Math.Abs(midD - d0) > 0.1f && Math.Abs(midD - d1) > 0.1f)
            {
                _curve.SetPoint(midD, midG);
                _pic.Invalidate();
                return;
            }
        }
    }

    private void OnPaintCurve(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        int w = _pic.ClientSize.Width, h = _pic.ClientSize.Height;
        if (w <= PaddingL + PaddingR || h <= PaddingT + PaddingB) return;
        int plotW = w - PaddingL - PaddingR, plotH = h - PaddingT - PaddingB;

        // 坐标范围：深度 0~maxDepth（按曲线最大断点+20%），增益 -20~+20dB 固定
        float maxDepth = 10f;
        for (int i = 0; i < _curve.PointCount; i++)
            maxDepth = Math.Max(maxDepth, _curve.GetPoint(i).DepthMm);
        maxDepth = maxDepth <= 0 ? 100f : maxDepth * 1.2f;
        const float gMin = -20f, gMax = 20f;

        float X(float depthMm) => PaddingL + depthMm / maxDepth * plotW;
        float Y(float db) => PaddingT + (gMax - db) / (gMax - gMin) * plotH;

        // 网格 + 坐标轴
        using var gridPen = new Pen(Color.FromArgb(40, 40, 40));
        using var axisPen = new Pen(Color.DarkGray);
        using var font = new Font("Consolas", 8f);
        for (int i = 0; i <= 5; i++)
        {
            int x = PaddingL + plotW * i / 5;
            g.DrawLine(gridPen, x, PaddingT, x, h - PaddingB);
            g.DrawString($"{maxDepth * i / 5:0.#}", font, Brushes.Gray, x - 12, h - PaddingB + 4);
        }
        for (int j = 0; j <= 4; j++)
        {
            int y = PaddingT + plotH * j / 4;
            g.DrawLine(gridPen, PaddingL, y, w - PaddingR, y);
            float db = gMax - (gMax - gMin) * j / 4;
            g.DrawString($"{db:+#;-#;0}", font, Brushes.Gray, 4, y - 7);
        }
        g.DrawLine(axisPen, PaddingL, PaddingT, PaddingL, h - PaddingB);
        g.DrawLine(axisPen, PaddingL, h - PaddingB, w - PaddingR, h - PaddingB);
        g.DrawString("深度(mm)", font, Brushes.DimGray, PaddingL + plotW / 2 - 30, h - PaddingB + 18);
        g.DrawString("补偿(dB)", font, Brushes.DimGray, 2, PaddingT - 18);

        // 折线
        if (_curve.PointCount >= 2)
        {
            using var linePen = new Pen(Color.LimeGreen, 2f);
            var pts = new PointF[_curve.PointCount];
            for (int i = 0; i < _curve.PointCount; i++)
            {
                var (d, db) = _curve.GetPoint(i);
                pts[i] = new PointF(X(d), Y(db));
            }
            g.DrawLines(linePen, pts);

            // 控制点 + 数值标签
            using var ptBrush = new SolidBrush(Color.LimeGreen);
            using var valBrush = new SolidBrush(Color.Yellow);
            for (int i = 0; i < _curve.PointCount; i++)
            {
                var (d, db) = _curve.GetPoint(i);
                g.FillEllipse(ptBrush, X(d) - PointRadius, Y(db) - PointRadius, PointRadius * 2, PointRadius * 2);
                g.DrawString($"{d:0.#}mm/{db:+#;-#;0}dB", font, valBrush, X(d) + 6, Y(db) - 8);
            }
        }
    }

    private (float d, float db)? HitTest(int px, int py)
    {
        int w = _pic.ClientSize.Width, h = _pic.ClientSize.Height;
        if (w <= PaddingL + PaddingR || h <= PaddingT + PaddingB) return null;
        int plotW = w - PaddingL - PaddingR, plotH = h - PaddingT - PaddingB;
        float maxDepth = 10f;
        for (int i = 0; i < _curve.PointCount; i++)
            maxDepth = Math.Max(maxDepth, _curve.GetPoint(i).DepthMm);
        maxDepth = maxDepth <= 0 ? 100f : maxDepth * 1.2f;
        float d = (px - PaddingL) / (float)plotW * maxDepth;
        float db = 20f - (py - PaddingT) / (float)plotH * 40f;
        return (d, db);
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            // 右键删除最近控制点
            int w = _pic.ClientSize.Width, h = _pic.ClientSize.Height;
            if (w <= PaddingL + PaddingR || h <= PaddingT + PaddingB) return;
            int plotW = w - PaddingL - PaddingR, plotH = h - PaddingT - PaddingB;
            float maxDepth = 10f;
            for (int i = 0; i < _curve.PointCount; i++)
                maxDepth = Math.Max(maxDepth, _curve.GetPoint(i).DepthMm);
            maxDepth = maxDepth <= 0 ? 100f : maxDepth * 1.2f;
            int best = -1; float bestDist = 20f;
            for (int i = 0; i < _curve.PointCount; i++)
            {
                var (d, db) = _curve.GetPoint(i);
                float dx = PaddingL + d / maxDepth * plotW - e.X;
                float dy = PaddingT + (20f - db) / 40f * plotH - e.Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            if (best >= 0 && _curve.RemovePoint(best)) _pic.Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        // 命中控制点则开始拖拽
        int w2 = _pic.ClientSize.Width, h2 = _pic.ClientSize.Height;
        if (w2 <= PaddingL + PaddingR || h2 <= PaddingT + PaddingB) return;
        int plotW2 = w2 - PaddingL - PaddingR, plotH2 = h2 - PaddingT - PaddingB;
        float maxDepth2 = 10f;
        for (int i = 0; i < _curve.PointCount; i++)
            maxDepth2 = Math.Max(maxDepth2, _curve.GetPoint(i).DepthMm);
        maxDepth2 = maxDepth2 <= 0 ? 100f : maxDepth2 * 1.2f;
        for (int i = 0; i < _curve.PointCount; i++)
        {
            var (d, db) = _curve.GetPoint(i);
            float dx = PaddingL + d / maxDepth2 * plotW2 - e.X;
            float dy = PaddingT + (20f - db) / 40f * plotH2 - e.Y;
            if (dx * dx + dy * dy <= (PointRadius + 4) * (PointRadius + 4)) { _dragIndex = i; return; }
        }
        _dragIndex = -1;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragIndex < 0) return;
        var hit = HitTest(e.X, e.Y);
        if (hit is null) return;
        var (d, db) = hit.Value;
        _curve.SetPoint(Math.Max(0f, d), Math.Clamp(db, -20f, 20f));
        _pic.Invalidate();
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        var hit = HitTest(_pic.PointToClient(Cursor.Position).X, _pic.PointToClient(Cursor.Position).Y);
        if (hit is null) return;
        var (d, db) = hit.Value;
        _curve.SetPoint(Math.Max(0f, d), Math.Clamp(db, -20f, 20f));
        _pic.Invalidate();
    }
}
