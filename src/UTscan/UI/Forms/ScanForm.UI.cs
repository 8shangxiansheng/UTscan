using System.Windows.Forms;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Services;
using UTscan.Services.SignalProcessing;

namespace UTscan.UI.Forms;

/// <summary>
/// 扫查成像窗体 partial：UI 初始化（控件构建）。
/// </summary>
public partial class ScanForm : Form
{

    private void BuildUI()
    {
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(10) };
        int y = 10;

        leftPanel.Controls.Add(new Label { Text = "扫查参数", Left = 10, Top = y, Width = 220, Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold) });
        y += 28;

        _numStartX = AddNum(leftPanel, "起点 X (mm):", ref y, 0, -1000, 1000, 0);
        _numStartY = AddNum(leftPanel, "起点 Y (mm):", ref y, 0, -1000, 1000, 0);
        _numWidth = AddNum(leftPanel, "宽度 (mm):", ref y, 50, 0.1m, 1000, 10);
        _numHeight = AddNum(leftPanel, "高度 (mm):", ref y, 50, 0.1m, 1000, 10);
        // IO6-FIX（审查 20260828）：UI 步距范围与 ScanService.ValidateScanRegion 后端校验一致
        // （X: 0.1~1000，Y: 0.001~100）——原 UI 允许 X=0.05 会被后端拒绝、Y 下限 0.01 过宽。
        _numStepX = AddNum(leftPanel, "X步距 (mm):", ref y, 0.5m, 0.1m, 1000, 0.1m);
        _numStepY = AddNum(leftPanel, "Y步距 (mm):", ref y, 0.5m, 0.001m, 100, 0.01m);
        _numGateStart = AddNum(leftPanel, "闸门起始 (µs):", ref y, 0, 0, 1000000, 10);
        _numGateWidth = AddNum(leftPanel, "闸门宽度 (µs):", ref y, 1000, 1, 1000000, 10);
        _numVelocity = AddNum(leftPanel, "扫查速度(mm/s):", ref y, 10, 0.1m, 500, 1);
        _numAcceleration = AddNum(leftPanel, "加速度(mm/s²):", ref y, 50, 1, 5000, 10);

        leftPanel.Controls.Add(new Label { Text = "扫查策略:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _cmbStrategy = new ComboBox
        {
            Left = 115, Top = y - 3, Width = 95, DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "逐点扫描", "编码器触发" }
        };
        _cmbStrategy.SelectedIndex = 0;
        leftPanel.Controls.Add(_cmbStrategy);
        y += 30;

        leftPanel.Controls.Add(new Label { Text = "成像模式:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _cmbMode = new ComboBox
        {
            Left = 115, Top = y - 3, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList,
            // M-7：与 CScanImagingMode 枚举一一对应（审查报告 M-7：原仅暴露 6/9 种且 switch 映射易错）
            Items =
            {
                "峰值幅度", "峰峰值", "正峰", "负峰",
                "正峰声程", "负峰声程", "正阈值声程", "负阈值声程", "相位反转", "均值"
            }
        };
        _cmbMode.SelectedIndex = 0;
        leftPanel.Controls.Add(_cmbMode);
        y += 30;

        // TCG（时间补偿增益）：随深度自动提升接收增益，厚大衰减工件定量关键
        leftPanel.Controls.Add(new Label { Text = "TCG:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _chkTcg = new CheckBox { Text = "深度补偿", Left = 115, Top = y, Width = 90 };
        _chkTcg.CheckedChanged += (_, _) => { _tcg.Enabled = _chkTcg.Checked; };
        leftPanel.Controls.Add(_chkTcg);
        _btnTcgEdit = new Button { Text = "编辑曲线...", Left = 210, Top = y - 3, Width = 30, Height = 22, Font = new System.Drawing.Font("Microsoft YaHei UI", 6.5F) };
        _btnTcgEdit.Click += (_, _) => { using var dlg = new TcgCurveEditorForm(_tcg); dlg.ShowDialog(this); };
        leftPanel.Controls.Add(_btnTcgEdit);
        y += 28;

        // D2：成像波形类型（与 A 扫显示的波形预处理一致，避免图像与分析值不一致）
        leftPanel.Controls.Add(new Label { Text = "波形类型:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _cmbWaveType = new ComboBox
        {
            Left = 115, Top = y - 3, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "射频", "检波", "正半波", "负半波" }
        };
        _cmbWaveType.SelectedIndex = 1;   // 默认检波（与原硬编码一致）
        leftPanel.Controls.Add(_cmbWaveType);
        y += 30;

        // ── 批次2：颜色条选择 + 显示范围（说明书 3.3.5 / 3.11 色带设置）──
        leftPanel.Controls.Add(new Label { Text = "颜色条:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _cmbColormap = new ComboBox
        {
            Left = 115, Top = y - 3, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList,
            // 与 Colormap.Presets 一一对应（下拉顺序即预设顺序）
            Items = { "Jet(彩虹)", "Viridis", "Hot(热力)", "Gray(灰度)", "CoolWarm(冷暖)" }
        };
        _cmbColormap.SelectedIndex = 0;   // 默认 Jet
        _cmbColormap.SelectedIndexChanged += OnColormapChanged;
        leftPanel.Controls.Add(_cmbColormap);
        y += 30;

        _chkAutoRange = new CheckBox { Text = "自动显示范围", Left = 10, Top = y, Width = 130, Checked = true };
        _chkAutoRange.CheckedChanged += OnDisplayRangeChanged;
        leftPanel.Controls.Add(_chkAutoRange);
        y += 26;

        leftPanel.Controls.Add(new Label { Text = "显示下限:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _numDispMin = new NumericUpDown { Left = 115, Top = y - 3, Width = 120, Minimum = -1000000, Maximum = 1000000, Value = 0, DecimalPlaces = 3, Enabled = false };
        _numDispMin.ValueChanged += OnDisplayRangeChanged;
        leftPanel.Controls.Add(_numDispMin);
        y += 28;

        leftPanel.Controls.Add(new Label { Text = "显示上限:", Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _numDispMax = new NumericUpDown { Left = 115, Top = y - 3, Width = 120, Minimum = -1000000, Maximum = 1000000, Value = 1, DecimalPlaces = 3, Enabled = false };
        _numDispMax.ValueChanged += OnDisplayRangeChanged;
        leftPanel.Controls.Add(_numDispMax);
        y += 32;

        _btnStart = new Button { Text = "开始扫查", Left = 10, Top = y, Width = 220, Height = 32, BackColor = System.Drawing.Color.SteelBlue, ForeColor = System.Drawing.Color.White };
        _btnStart.Click += OnStart;
        leftPanel.Controls.Add(_btnStart);
        y += 36;

        // 断点续扫（20260828）：手动/异常停止后可从中断点恢复（跳过已扫行）
        _btnResumeScan = new Button { Text = "续扫", Left = 10, Top = y, Width = 220, Height = 28, Enabled = false, BackColor = System.Drawing.Color.MediumSeaGreen, ForeColor = System.Drawing.Color.White };
        _btnResumeScan.Click += async (_, _) => await OnResumeScanAsync();
        leftPanel.Controls.Add(_btnResumeScan);
        y += 32;

        _btnStop = new Button { Text = "停止", Left = 10, Top = y, Width = 105, Height = 28 };
        _btnStop.Click += (_, _) => OnStop();
        leftPanel.Controls.Add(_btnStop);

        _btnPause = new Button { Text = "暂停", Left = 125, Top = y, Width = 105, Height = 28, Enabled = false };
        _btnPause.Click += async (_, _) => await OnPauseAsync();
        leftPanel.Controls.Add(_btnPause);
        y += 32;

        _btnResume = new Button { Text = "继续", Left = 10, Top = y, Width = 105, Height = 28, Enabled = false };
        _btnResume.Click += async (_, _) => await OnResumeAsync();
        leftPanel.Controls.Add(_btnResume);

        // P0-F：C 扫保存图像（bmp/png，说明书数据分析基本输出）
        _btnSaveImage = new Button { Text = "保存图像", Left = 125, Top = y, Width = 105, Height = 28 };
        _btnSaveImage.Click += (_, _) => SaveCScanImage();
        leftPanel.Controls.Add(_btnSaveImage);
        y += 34;

        // P1-B：离线滤波（中值去噪 / 低通平滑，FirFilter/MedianFilter 后端已存在）
        _btnFilterMedian = new Button { Text = "中值滤波", Left = 10, Top = y, Width = 105, Height = 28 };
        _btnFilterMedian.Click += (_, _) => ApplyOfflineFilter(FilterKind.Median);
        leftPanel.Controls.Add(_btnFilterMedian);
        _btnFilterLowPass = new Button { Text = "低通滤波", Left = 125, Top = y, Width = 105, Height = 28 };
        _btnFilterLowPass.Click += (_, _) => ApplyOfflineFilter(FilterKind.LowPass);
        leftPanel.Controls.Add(_btnFilterLowPass);
        y += 34;

        // P1-B：D 扫视图（按固定 X 列切数据 → BScanImageService 渲染，数据已具备）
        _btnDScan = new Button { Text = "D 扫视图", Left = 10, Top = y, Width = 220, Height = 28 };
        _btnDScan.Click += (_, _) => OpenDScanView();
        leftPanel.Controls.Add(_btnDScan);
        y += 34;

        _progressBar = new ProgressBar { Left = 10, Top = y, Width = 220, Height = 20 };
        leftPanel.Controls.Add(_progressBar);
        y += 24;

        _lblStatus = new Label { Text = "就绪", Left = 10, Top = y, Width = 220, Height = 60, ForeColor = System.Drawing.Color.DimGray };
        leftPanel.Controls.Add(_lblStatus);

        _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
        // 批次2：C 扫双击跳转 A 扫（联动导航，说明书 3.6.2 C 扫交互）
        _pic.DoubleClick += OnCScanDoubleClick;
        Controls.Add(_pic);

        // ── 右侧：颜色条（竖直色带 + 上下限标注，说明书 3.11）──
        var cbPanel = new Panel { Dock = DockStyle.Right, Width = 78, Padding = new Padding(4) };
        _lblCbMax = new Label { Left = 0, Top = 0, Width = 74, Height = 30, Text = "max", TextAlign = System.Drawing.ContentAlignment.BottomCenter, ForeColor = System.Drawing.Color.DimGray, Font = new System.Drawing.Font("Consolas", 7.5f) };
        _picColorBar = new PictureBox { Left = 14, Top = 32, Width = 44, Height = 300, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
        _lblCbMin = new Label { Left = 0, Top = 334, Width = 74, Height = 30, Text = "min", TextAlign = System.Drawing.ContentAlignment.TopCenter, ForeColor = System.Drawing.Color.DimGray, Font = new System.Drawing.Font("Consolas", 7.5f) };
        cbPanel.Controls.Add(_lblCbMax);
        cbPanel.Controls.Add(_picColorBar);
        cbPanel.Controls.Add(_lblCbMin);
        Controls.Add(cbPanel);

        Controls.Add(leftPanel);
        leftPanel.AutoScroll = true;   // 参数增多后允许滚动，避免小窗体下控件被裁剪
    }

    private static NumericUpDown AddNum(Panel parent, string label, ref int y, decimal val, decimal min, decimal max, decimal inc)
    {
        parent.Controls.Add(new Label { Text = label, Left = 10, Top = y, Width = 100, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        var num = new NumericUpDown { Left = 115, Top = y - 3, Width = 95, Minimum = min, Maximum = max, Value = val, Increment = inc, DecimalPlaces = inc < 1 ? 2 : 0 };
        parent.Controls.Add(num);
        y += 28;
        return num;
    }
}
