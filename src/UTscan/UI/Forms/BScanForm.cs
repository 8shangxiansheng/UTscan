using System.Windows.Forms;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Services.Imaging;
using UTscan.Services.SignalProcessing;

namespace UTscan.UI.Forms;

/// <summary>
/// B 扫截面查看器（说明书 3.6.1）。
/// 横轴 = 扫查位置（mm），纵轴 = 深度（mm），颜色 = 幅值。
/// 数据来源：① 编码器触发扫查的 LineScanComplete 事件实时更新；② .adtx 导入数据。
/// </summary>
public class BScanForm : Form
{
    private readonly PictureBox _pic;
    private readonly ComboBox _cmbColormap;
    private readonly NumericUpDown _numMaxDepth;
    private readonly BScanImageService _bscan = new();

    // 当前 B 扫数据缓存（事件回调线程写入，UI 线程渲染时快照引用）
    private readonly object _dataLock = new();
    private float[][] _ascans = Array.Empty<float[]>();
    private float[] _positions = Array.Empty<float>();
    private float _sampleRate = 100e6f;
    private float _soundVelocity = 1480f;
    private float _zeroOffsetUs;

    private bool _subscribedToScan;

    public BScanForm()
    {
        Text = "B 扫截面";
        ClientSize = new System.Drawing.Size(900, 640);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 200, Padding = new Padding(8) };
        int y = 10;
        leftPanel.Controls.Add(new Label { Text = "B 扫截面", Left = 8, Top = y, Width = 170, Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold) });
        y += 30;

        leftPanel.Controls.Add(new Label { Text = "色图:", Left = 8, Top = y, Width = 60 });
        _cmbColormap = new ComboBox { Left = 70, Top = y - 3, Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var c in Colormap.Presets) _cmbColormap.Items.Add(c.Name);
        _cmbColormap.SelectedIndex = 0;   // Jet
        _cmbColormap.SelectedIndexChanged += (_, _) => Render();
        leftPanel.Controls.Add(_cmbColormap);
        y += 32;

        leftPanel.Controls.Add(new Label { Text = "最大深度 (mm):", Left = 8, Top = y, Width = 100 });
        _numMaxDepth = new NumericUpDown { Left = 108, Top = y - 3, Width = 72, Minimum = 0, Maximum = 1000, Increment = 1, Value = 0 };
        _numMaxDepth.ValueChanged += (_, _) => Render();
        leftPanel.Controls.Add(_numMaxDepth);
        y += 34;

        var btnSave = new Button { Text = "保存图像...", Left = 8, Top = y, Width = 170, Height = 30 };
        btnSave.Click += (_, _) => SaveImage();
        leftPanel.Controls.Add(btnSave);
        y += 40;

        var lblHint = new Label
        {
            Text = "数据源：\n· 编码器触发扫查时实时更新\n· 文件菜单导入 .adtx",
            Left = 8, Top = y, Width = 180, Height = 70, ForeColor = System.Drawing.Color.DimGray
        };
        leftPanel.Controls.Add(lblHint);

        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_pic);
        Controls.Add(leftPanel);
    }

    /// <summary>
    /// 订阅扫查引擎的 LineScanComplete 事件（编码器触发连续扫查时按行实时刷新）。
    /// </summary>
    public void SubscribeToScan(IScanEngine engine)
    {
        if (_subscribedToScan || engine == null) return;
        _subscribedToScan = true;
        engine.LineScanComplete += EngineOnLineScanComplete;
        FormClosed += (_, _) => engine.LineScanComplete -= EngineOnLineScanComplete;
    }

    private void EngineOnLineScanComplete(object? sender, LineScanCompleteEventArgs e)
    {
        lock (_dataLock)
        {
            _positions = e.Positions;
            _ascans = e.Waveforms;
            _sampleRate = e.SampleRate > 0 ? e.SampleRate : _sampleRate;
        }
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) BeginInvoke(Render);
        else Render();
    }

    /// <summary>加载一组 A 扫（.adtx 导入 / 扫查结果），刷新显示。</summary>
    public void UpdateData(float[][] ascans, float[] positions, float sampleRate, float soundVelocity = 1480f, float zeroOffsetUs = 0f)
    {
        lock (_dataLock)
        {
            _ascans = ascans;
            _positions = positions;
            if (sampleRate > 0) _sampleRate = sampleRate;
            _soundVelocity = soundVelocity;
            _zeroOffsetUs = zeroOffsetUs;
        }
        Render();
    }

    private void Render()
    {
        if (IsDisposed || Disposing) return;

        float[][] ascans;
        float[] positions;
        float sampleRate, soundVelocity, zeroOffset;
        lock (_dataLock)
        {
            ascans = _ascans;
            positions = _positions;
            sampleRate = _sampleRate;
            soundVelocity = _soundVelocity;
            zeroOffset = _zeroOffsetUs;
        }

        if (ascans.Length == 0 || ascans[0].Length == 0)
        {
            _pic.Image = null;
            return;
        }

        var cmap = Colormap.FromName(_cmbColormap.SelectedItem?.ToString() ?? "Jet");
        Bitmap bmp;
        try
        {
            bmp = _bscan.Render(ascans, positions, sampleRate, soundVelocity, zeroOffset, cmap,
                maxDepthMm: (float)_numMaxDepth.Value);
        }
        catch (Exception) { return; }   // 渲染异常不致命，跳过本轮

        var old = _pic.Image;
        _pic.Image = bmp;
        old?.Dispose();
    }

    private void SaveImage()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存 B 扫图像",
            Filter = "PNG 图像 (*.png)|*.png|JPEG 图像 (*.jpg)|*.jpg|位图 (*.bmp)|*.bmp",
            DefaultExt = "png",
            FileName = $"bscan-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _pic.Image?.Save(dlg.FileName);
            MessageBox.Show(this, $"图像已保存到：{dlg.FileName}", "保存图像", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
