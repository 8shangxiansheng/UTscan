using System.Diagnostics;
using System.Windows.Forms;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.Daq;
using UTscan.Hardware.PulseGen;
using UTscan.Hardware.Zmc;
using UTscan.Services;
using UTscan.Services.Connection;

namespace UTscan.UI.Forms;

/// <summary>
/// 主窗体 partial：布局初始化：InitializeComponent 与各面板/菜单/状态栏构建。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  布局初始化
    // ════════════════════════════════════════════════════════════════

    private void InitializeComponent()
    {
        IsMdiContainer = true;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        ClientSize = new System.Drawing.Size(1280, 800);
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        // 菜单栏
        BuildMenuStrip();

        // 顶部坐标 + 设备状态指示灯
        var coordPanel = new Panel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(8), BorderStyle = BorderStyle.FixedSingle };
        BuildCoordPanel(coordPanel);
        Controls.Add(coordPanel);

        // 左侧运动控制面板
        var motionPanel = new Panel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(8), BorderStyle = BorderStyle.FixedSingle, AutoScroll = true };
        BuildMotionPanel(motionPanel);
        motionPanel.Enabled = _config.UseMock || _config.EnableMotionController;
        Controls.Add(motionPanel);

        // 右侧参数面板（TabControl: 脉冲收发仪 / 信号采集 / 系统参数）
        var rightPanel = new Panel { Dock = DockStyle.Right, Width = 320, Padding = new Padding(2), BorderStyle = BorderStyle.FixedSingle };
        BuildRightPanel(rightPanel);
        Controls.Add(rightPanel);

        // 底部操作日志面板（在状态栏上方）
        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 160, Padding = new Padding(4), BorderStyle = BorderStyle.FixedSingle };
        BuildLogPanel(logPanel);
        Controls.Add(logPanel);

        // 状态栏（最底部）
        var status = new StatusStrip();
        _lblUser = new ToolStripStatusLabel { Text = "未登录", Width = 240 };
        _lblConn = new ToolStripStatusLabel { Text = "状态：未连接", Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        status.Items.Add(_lblUser);
        status.Items.Add(_lblConn);
        Controls.Add(status);
    }

    // ── 菜单栏 ──

    private void BuildMenuStrip()
    {
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("文件(&F)");
        _connectMenuItem = new ToolStripMenuItem("连接(&N)", null, OnConnectClick);
        fileMenu.DropDownItems.Add(_connectMenuItem);
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("断开(&X)", null, OnDisconnectClick));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("保存设置(&S)", null, OnSaveSettings) { ShortcutKeys = Keys.Control | Keys.S });
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("加载设置(&L)", null, OnLoadSettings) { ShortcutKeys = Keys.Control | Keys.L });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("导出 A 扫数据(&E)...", null, OnExportCsv));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("导出 .adtx(&D)...", null, OnExportAdtx));
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("导入 .adtx(&I)...", null, OnImportAdtx));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("退出(&Q)", null, (_, _) => Close()) { ShortcutKeys = Keys.Alt | Keys.F4 });
        menu.Items.Add(fileMenu);

        var viewMenu = new ToolStripMenuItem("视图(&V)");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("A 扫显示(&A)", null, OnOpenAscan) { ShortcutKeys = Keys.F2 });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("扫查成像(&S)", null, OnOpenScan)
        {
            ShortcutKeys = Keys.F3,
            Enabled = _config.UseMock || _config.EnableMotionController,
            ToolTipText = _config.EnableMotionController ? "" : "运动控制器已禁用；当前仅联调 DPR500 + Spectrum"
        });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("B 扫截面(&B)", null, OnOpenBscan) { ShortcutKeys = Keys.F4 });
        // P0-C：FFT 频谱窗体（确认探头频率/滤波范围）
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("FFT 频谱(&F)", null, OnOpenFft) { ShortcutKeys = Keys.F5 });
        // TCG：深度补偿曲线统一入口（打开扫查窗的曲线编辑器）
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("深度补偿曲线(&T)...", null, OnOpenTcgEditor));
        menu.Items.Add(viewMenu);

        var helpMenu = new ToolStripMenuItem("帮助(&H)");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("检查更新(&U)...", null, OnCheckForUpdate));
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        // P5-FIX：通信链路诊断——原已完整实现(LinkDiagnosticsService 309行)但菜单无入口
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("通信链路诊断(&D)...", null, OnLinkDiagnostics));
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("关于(&A)...", null, OnAboutClick));
        menu.Items.Add(helpMenu);

        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    // ── 顶部坐标 + 设备状态指示灯 ──

    private void BuildCoordPanel(Panel p)
    {
        // 第一行：5 轴坐标
        string[] axisNames = { "X", "Y", "Z", "W1", "W2" };
        for (int i = 0; i < 5; i++)
        {
            p.Controls.Add(new Label
            {
                Text = $"{axisNames[i]}:",
                Left = 10 + i * 160, Top = 4, Width = 30,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold)
            });
            _axisLabels[i] = new Label
            {
                Text = "0.000 mm",
                Left = 40 + i * 160, Top = 4, Width = 115,
                BorderStyle = BorderStyle.FixedSingle, BackColor = System.Drawing.Color.White,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter
            };
            p.Controls.Add(_axisLabels[i]);
        }

        // 第二行：设备状态指示灯
        int ledStartX = 10;
        int ledSpacing = 200;

        // 运动控制器
        p.Controls.Add(new Label { Text = "运动控制器:", Left = ledStartX, Top = 34, Width = 75, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _lblLedMotion = new Label { Left = ledStartX + 78, Top = 33, Width = 16, Height = 16, BackColor = System.Drawing.Color.DimGray, BorderStyle = BorderStyle.Fixed3D };
        p.Controls.Add(_lblLedMotion);
        _lblMotionStatus = new Label { Left = ledStartX + 98, Top = 34, Width = 80, Text = "未连接", ForeColor = System.Drawing.Color.Gray };
        p.Controls.Add(_lblMotionStatus);

        // 采集卡
        p.Controls.Add(new Label { Text = "采集卡:", Left = ledStartX + ledSpacing, Top = 34, Width = 55, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _lblLedDaq = new Label { Left = ledStartX + ledSpacing + 58, Top = 33, Width = 16, Height = 16, BackColor = System.Drawing.Color.DimGray, BorderStyle = BorderStyle.Fixed3D };
        p.Controls.Add(_lblLedDaq);
        _lblDaqStatus = new Label { Left = ledStartX + ledSpacing + 78, Top = 34, Width = 80, Text = "未连接", ForeColor = System.Drawing.Color.Gray };
        p.Controls.Add(_lblDaqStatus);

        // 脉冲收发仪
        p.Controls.Add(new Label { Text = "脉冲收发仪:", Left = ledStartX + ledSpacing * 2, Top = 34, Width = 75, TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
        _lblLedPulse = new Label { Left = ledStartX + ledSpacing * 2 + 78, Top = 33, Width = 16, Height = 16, BackColor = System.Drawing.Color.DimGray, BorderStyle = BorderStyle.Fixed3D };
        p.Controls.Add(_lblLedPulse);
        _lblPulseStatus = new Label { Left = ledStartX + ledSpacing * 2 + 98, Top = 34, Width = 80, Text = "未连接", ForeColor = System.Drawing.Color.Gray };
        p.Controls.Add(_lblPulseStatus);
    }

    // ── 左侧运动控制面板 ──

    private void BuildMotionPanel(Panel p)
    {
        int y = 4;
        p.Controls.Add(new Label { Text = "运动控制", Left = 8, Top = y, Width = 250, Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold) });
        y += 28;

        // 速度
        AddLabel(p, "速度 (mm/s):", 8, y);
        _cmbSpeed = new ComboBox { Left = 100, Top = y - 3, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSpeed.Items.AddRange(new object[] { 10, 20, 40, 50, 100, 200, 500 });
        _cmbSpeed.SelectedIndex = 4;
        _cmbSpeed.SelectedIndexChanged += (_, _) => { if (_cmbSpeed.SelectedItem is int s) _jogSpeed = s; };
        p.Controls.Add(_cmbSpeed);
        y += 30;

        // 加速度
        AddLabel(p, "加速度 (mm/s²):", 8, y);
        _numAccel = new NumericUpDown { Left = 100, Top = y - 3, Width = 120, Minimum = 1, Maximum = 5000, Value = 50, Increment = 10 };
        p.Controls.Add(_numAccel);
        y += 32;

        // Jog 按钮行
        AddJogRow(p, ref y, "X", AxisId.X);
        AddJogRow(p, ref y, "Y", AxisId.Y);
        AddJogRow(p, ref y, "Z", AxisId.Z);
        y += 6;

        // 轴状态指示
        p.Controls.Add(new Label { Text = "轴状态:", Left = 8, Top = y, Width = 60, Font = new System.Drawing.Font("Microsoft YaHei UI", 8.5F) });
        _lblAxisStatusX = new Label { Left = 70, Top = y, Width = 50, Text = "X:空闲", BackColor = System.Drawing.Color.LightGreen, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, Font = new System.Drawing.Font("Microsoft YaHei UI", 8F) };
        _lblAxisStatusY = new Label { Left = 125, Top = y, Width = 50, Text = "Y:空闲", BackColor = System.Drawing.Color.LightGreen, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, Font = new System.Drawing.Font("Microsoft YaHei UI", 8F) };
        _lblAxisStatusZ = new Label { Left = 180, Top = y, Width = 50, Text = "Z:空闲", BackColor = System.Drawing.Color.LightGreen, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, Font = new System.Drawing.Font("Microsoft YaHei UI", 8F) };
        p.Controls.Add(_lblAxisStatusX);
        p.Controls.Add(_lblAxisStatusY);
        p.Controls.Add(_lblAxisStatusZ);
        y += 28;

        // 分隔线
        p.Controls.Add(new Label { Text = "── 定位控制 ──", Left = 8, Top = y, Width = 250, ForeColor = System.Drawing.Color.Gray, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
        y += 22;

        // 目标坐标输入
        AddLabel(p, "目标 X (mm):", 8, y);
        _numTargetX = new NumericUpDown { Left = 100, Top = y - 3, Width = 120, Minimum = -300, Maximum = 300, DecimalPlaces = 3, Increment = 1m, Value = 0 };
        p.Controls.Add(_numTargetX);
        y += 28;

        AddLabel(p, "目标 Y (mm):", 8, y);
        _numTargetY = new NumericUpDown { Left = 100, Top = y - 3, Width = 120, Minimum = -300, Maximum = 300, DecimalPlaces = 3, Increment = 1m, Value = 0 };
        p.Controls.Add(_numTargetY);
        y += 28;

        AddLabel(p, "目标 Z (mm):", 8, y);
        _numTargetZ = new NumericUpDown { Left = 100, Top = y - 3, Width = 120, Minimum = -100, Maximum = 100, DecimalPlaces = 3, Increment = 1m, Value = 0 };
        p.Controls.Add(_numTargetZ);
        y += 30;

        _btnMoveTo = new Button { Text = "定位移动", Left = 8, Top = y, Width = 212, Height = 30, BackColor = System.Drawing.Color.SteelBlue, ForeColor = System.Drawing.Color.White, Enabled = false };
        _btnMoveTo.Click += async (_, _) => await MoveToTargetAsync();
        p.Controls.Add(_btnMoveTo);
        y += 36;

        // ── P0-D（说明书 3.4.2/4.5）：相对步进 + 轴置零 + 运动自检 ──
        p.Controls.Add(new Label { Text = "── 相对步进/置零 ──", Left = 8, Top = y, Width = 250, ForeColor = System.Drawing.Color.Gray, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
        y += 22;
        AddLabel(p, "步进 (mm):", 8, y);
        _numStepMm = new NumericUpDown { Left = 100, Top = y - 3, Width = 120, Minimum = 0.001m, Maximum = 100, Value = 1, DecimalPlaces = 3, Increment = 0.1m };
        p.Controls.Add(_numStepMm);
        y += 30;

        // 相对步进行：每轴 [−][+]
        AddRelativeStepRow(p, ref y, "X", AxisId.X);
        AddRelativeStepRow(p, ref y, "Y", AxisId.Y);
        AddRelativeStepRow(p, ref y, "Z", AxisId.Z);
        y += 4;

        // 轴置零 + 运动自检（限位遍历验证）
        var btnZeroX = new Button { Text = "X 置零", Left = 8, Top = y, Width = 66, Height = 28 };
        btnZeroX.Click += (_, _) => { _motion.SetPositionZero(AxisId.X); LogI("ZMC", "X 轴已置零"); };
        p.Controls.Add(btnZeroX);
        var btnZeroY = new Button { Text = "Y 置零", Left = 78, Top = y, Width = 66, Height = 28 };
        btnZeroY.Click += (_, _) => { _motion.SetPositionZero(AxisId.Y); LogI("ZMC", "Y 轴已置零"); };
        p.Controls.Add(btnZeroY);
        var btnZeroZ = new Button { Text = "Z 置零", Left = 148, Top = y, Width = 72, Height = 28 };
        btnZeroZ.Click += (_, _) => { _motion.SetPositionZero(AxisId.Z); LogI("ZMC", "Z 轴已置零"); };
        p.Controls.Add(btnZeroZ);
        y += 34;

        var btnSelfTest = new Button { Text = "运动自检（限位遍历）", Left = 8, Top = y, Width = 212, Height = 28, BackColor = System.Drawing.Color.Orange, Enabled = false };
        btnSelfTest.Click += async (_, _) => await RunMotionSelfTestAsync();
        p.Controls.Add(btnSelfTest);
        y += 36;

        // 分隔线
        p.Controls.Add(new Label { Text = "── 原点/急停/伺服 ──", Left = 8, Top = y, Width = 250, ForeColor = System.Drawing.Color.Gray, TextAlign = System.Drawing.ContentAlignment.MiddleCenter });
        y += 22;

        var btnHome = new Button { Text = "返回零点", Left = 8, Top = y, Width = 100, Height = 30 };
        btnHome.Click += async (_, _) => await HomeAllAxesAsync();
        p.Controls.Add(btnHome);

        var btnEStop = new Button { Text = "紧急停止", Left = 116, Top = y, Width = 100, Height = 30, BackColor = System.Drawing.Color.IndianRed, ForeColor = System.Drawing.Color.White };
        btnEStop.Click += async (_, _) => { await _motion.EmergencyStopAsync(); LogW("ZMC", "紧急停止已触发"); };
        p.Controls.Add(btnEStop);
        y += 36;

        var btnServoOn = new Button { Text = "使能 XYZ", Left = 8, Top = y, Width = 100, Height = 28 };
        btnServoOn.Click += async (_, _) => { await _motion.EnableAxisAsync(AxisId.X); await _motion.EnableAxisAsync(AxisId.Y); await _motion.EnableAxisAsync(AxisId.Z); Log("XYZ 轴已使能"); };
        p.Controls.Add(btnServoOn);
        var btnServoOff = new Button { Text = "关闭伺服", Left = 116, Top = y, Width = 100, Height = 28 };
        btnServoOff.Click += async (_, _) => { await _motion.DisableAxisAsync(AxisId.X); await _motion.DisableAxisAsync(AxisId.Y); await _motion.DisableAxisAsync(AxisId.Z); Log("伺服已关闭"); };
        p.Controls.Add(btnServoOff);
    }

    // ── 右侧参数面板（TabControl） ──

    private void BuildRightPanel(Panel p)
    {
        var tabControl = new TabControl { Dock = DockStyle.Fill, Font = new System.Drawing.Font("Microsoft YaHei UI", 9F) };

        // Tab 1: 脉冲收发仪
        var tabPulse = new TabPage("脉冲收发仪") { AutoScroll = true, Padding = new Padding(8) };
        BuildPulseTab(tabPulse);
        tabControl.TabPages.Add(tabPulse);

        // Tab 2: 信号采集
        var tabDaq = new TabPage("信号采集") { AutoScroll = true, Padding = new Padding(8) };
        BuildDaqTab(tabDaq);
        tabControl.TabPages.Add(tabDaq);

        // Tab 3: 系统参数
        var tabSystem = new TabPage("系统参数") { AutoScroll = true, Padding = new Padding(8) };
        BuildSystemTab(tabSystem);
        tabControl.TabPages.Add(tabSystem);

        p.Controls.Add(tabControl);
    }

    // ── 脉冲收发仪 Tab ──

    private void BuildPulseTab(TabPage page)
    {
        int y = 8;

        // 通道选择
        AddLabel(page, "通道:", 8, y);
        _cmbPulseChannel = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbPulseChannel.Items.AddRange(new object[] { "A (通道1)", "B (通道2)" });
        _cmbPulseChannel.SelectedIndex = 0;
        page.Controls.Add(_cmbPulseChannel);
        y += 30;

        // 增益
        AddLabel(page, "增益 (dB):", 8, y);
        _numGain = new NumericUpDown { Left = 95, Top = y - 3, Width = 130, Minimum = -50, Maximum = 66, DecimalPlaces = 1, Increment = 0.5m, Value = 0 };
        page.Controls.Add(_numGain);
        y += 28;

        // 脉宽——4a-FIX（审查 20260828）：硬件固定（RP-L2），灰化禁用编辑，消除"以为能改"的误导。
        // 保留显示记录值（只读），工具提示明确硬件决定。
        AddLabel(page, "脉宽 (ns):", 8, y);
        _numWidth = new NumericUpDown
        {
            Left = 95, Top = y - 3, Width = 130,
            Minimum = 10, Maximum = 1000, Increment = 10, Value = 100,
            ReadOnly = true, Enabled = false   // 硬件固定：灰化 + 禁改
        };
        page.Controls.Add(_numWidth);
        // L-4：JSR SDK 无 PulseWidth 属性（已证实 N/A），DPR500 脉宽由远程脉冲器型号（RP-L2/RP-H2）硬件决定。
        // 此处仅记录参数，不影响实际硬件——提示操作员避免误以为可调。
        _pulseWidthTip.SetToolTip(_numWidth, "DPR500 脉宽由脉冲器硬件决定（RP-L2/RP-H2），不可软件修改；此处仅显示记录值");
        y += 28;

        // PRF
        AddLabel(page, "PRF (Hz):", 8, y);
        _numPrf = new NumericUpDown { Left = 95, Top = y - 3, Width = 130, Minimum = 100, Maximum = 5000, Increment = 100, Value = 1000 };
        page.Controls.Add(_numPrf);
        y += 28;

        // 模式
        AddLabel(page, "模式:", 8, y);
        _cmbPulseMode = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbPulseMode.Items.AddRange(new object[] { "自发自收", "一发一收" });
        _cmbPulseMode.SelectedIndex = 0;
        page.Controls.Add(_cmbPulseMode);
        y += 28;

        // 电压
        AddLabel(page, "电压 (V):", 8, y);
        _numVoltage = new NumericUpDown { Left = 95, Top = y - 3, Width = 130, Minimum = 100, Maximum = 330, Increment = 10, Value = 200 };
        page.Controls.Add(_numVoltage);
        y += 28;

        // 能量挡位
        AddLabel(page, "能量挡位:", 8, y);
        _numEnergyLevel = new NumericUpDown { Left = 95, Top = y - 3, Width = 130, Minimum = 1, Maximum = 4, Value = 2 };
        page.Controls.Add(_numEnergyLevel);
        y += 28;

        // 阻尼
        AddLabel(page, "阻尼:", 8, y);
        _cmbDamping = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbDamping.Items.AddRange(new object[] { "50Ω", "100Ω", "200Ω", "500Ω" });
        _cmbDamping.SelectedIndex = 0;
        page.Controls.Add(_cmbDamping);
        y += 28;

        // 触发源
        AddLabel(page, "触发源:", 8, y);
        _cmbTriggerSource = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbTriggerSource.Items.AddRange(new object[] { "内部", "外部", "Slave" });
        // 两设备联调默认内部触发（DPR500 自主 PRF 发射，TRIG/SYNC 输出给 Spectrum EXT0）；
        // 原默认"外部"使 DPR500 等待外部脉冲但不发射 → 真机采集零数据
        _cmbTriggerSource.SelectedIndex = 0;
        page.Controls.Add(_cmbTriggerSource);
        y += 28;

        // 信号选择
        AddLabel(page, "信号选择:", 8, y);
        _cmbSignalSelect = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSignalSelect.Items.AddRange(new object[] { "T/R Echo", "Through", "Both" });
        _cmbSignalSelect.SelectedIndex = 0;
        page.Controls.Add(_cmbSignalSelect);
        y += 28;

        // 低通滤波
        AddLabel(page, "低通 (MHz):", 8, y);
        _cmbLowPass = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbLowPass.Items.AddRange(new object[] { "3", "7.5", "10", "15", "22.5", "50", "全通" });
        _cmbLowPass.SelectedIndex = 5;
        page.Controls.Add(_cmbLowPass);
        y += 28;

        // 高通滤波
        AddLabel(page, "高通 (MHz):", 8, y);
        _cmbHighPass = new ComboBox { Left = 95, Top = y - 3, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbHighPass.Items.AddRange(new object[] { "0", "1", "2.5", "5", "7.5", "12.5" });
        _cmbHighPass.SelectedIndex = 1;
        page.Controls.Add(_cmbHighPass);
        y += 30;

        // 应用按钮
        _btnPulseApply = new Button { Text = "应用参数", Left = 8, Top = y, Width = 100, Height = 30, Enabled = false, BackColor = System.Drawing.Color.SteelBlue, ForeColor = System.Drawing.Color.White };
        _btnPulseApply.Click += async (_, _) => await ApplyPulseParamsAsync();
        page.Controls.Add(_btnPulseApply);

        // 回读按钮
        _btnPulseReadback = new Button { Text = "参数回读", Left = 116, Top = y, Width = 100, Height = 30, Enabled = false };
        _btnPulseReadback.Click += (_, _) => ReadbackPulseParams();
        page.Controls.Add(_btnPulseReadback);
        y += 36;

        // P1-A：DPR500 LED 识别（机内识别板卡，SetPulserLedIdentifyAsync；仅真机）
        _btnPulseLed = new Button { Text = "LED 识别", Left = 8, Top = y, Width = 100, Height = 28, Enabled = false };
        _btnPulseLed.Click += async (_, _) =>
        {
            try
            {
                // P5：SetPulserLedIdentifyAsync 已提升至接口，Mock 空操作
                await _pulse.SetPulserLedIdentifyAsync(true);
                await Task.Delay(1500);   // 常亮 1.5s 便于识别
                await _pulse.SetPulserLedIdentifyAsync(false);
            LogI("DPR", "DPR500 LED 识别已执行");
            }
            catch (Exception ex) { LogE("DPR", $"LED 识别失败: {ex.Message}"); }
        };
        page.Controls.Add(_btnPulseLed);
        y += 34;

        // 脉冲发射开关（对标 JSR Control Panel 的 Enable/Disable Pulser）：参数应用后需显式启用才开始发射，
        // 发射时 DPR500 经 TRIG/SYNC 输出同步脉冲驱动 Spectrum EXT0，形成"参数→触发→采集→显示"闭环。
        _btnPulseOutput = new Button { Text = "启用发射", Left = 8, Top = y, Width = 100, Height = 28, Enabled = false, BackColor = System.Drawing.Color.ForestGreen, ForeColor = System.Drawing.Color.White };
        _btnPulseOutput.Click += async (_, _) => await TogglePulseOutputAsync();
        page.Controls.Add(_btnPulseOutput);
        _lblPulseOutput = new Label { Text = "发射状态: 关闭", Left = 116, Top = y + 4, Width = 100, ForeColor = System.Drawing.Color.DimGray };
        page.Controls.Add(_lblPulseOutput);
        y += 34;

        // 回读显示
        page.Controls.Add(new Label { Text = "参数回读:", Left = 8, Top = y, Width = 80 });
        _txtPulseReadback = new TextBox { Left = 8, Top = y + 20, Width = 208, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new System.Drawing.Font("Consolas", 8.5F) };
        page.Controls.Add(_txtPulseReadback);
    }

    // ── 信号采集 Tab ──

    private void BuildDaqTab(TabPage page)
    {
        int y = 8;

        // 采样率
        AddLabel(page, "采样率 (MHz):", 8, y);
        _numSampleRateMHz = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 9, Maximum = 500, DecimalPlaces = 1, Increment = 1, Value = 100 };
        _numSampleRateMHz.ValueChanged += (_, _) => SyncSampleLengthToCount();   // 改采样率 → 同步点数（保持长度）
        page.Controls.Add(_numSampleRateMHz);
        y += 28;

        // 采样长度
        AddLabel(page, "采样长度 (µs):", 8, y);
        _numSampleLengthUs = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 0.1m, Maximum = 10000, DecimalPlaces = 1, Increment = 1, Value = 10.2m };
        _numSampleLengthUs.ValueChanged += (_, _) => SyncSampleLengthToCount();   // 改长度 → 同步点数
        page.Controls.Add(_numSampleLengthUs);
        y += 28;

        // 采样点数（直接配置；与采样长度双向联动：点数 = 长度×采样率/1e6）
        AddLabel(page, "采样点数:", 8, y);
        _numSampleCount = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 16, Maximum = 10000000, Increment = 8, Value = 1024 };
        _numSampleCount.ValueChanged += (_, _) => SyncSampleCountToLength();
        page.Controls.Add(_numSampleCount);
        y += 28;

        // 输入量程
        AddLabel(page, "输入量程 (mV):", 8, y);
        _cmbInputRange = new ComboBox { Left = 110, Top = y - 3, Width = 115, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbInputRange.Items.AddRange(new object[] { "200", "500", "1000", "2000", "5000", "10000" });
        _cmbInputRange.SelectedIndex = 3;
        page.Controls.Add(_cmbInputRange);
        y += 28;

        // 通道
        AddLabel(page, "通道:", 8, y);
        _cmbDaqChannel = new ComboBox { Left = 110, Top = y - 3, Width = 115, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbDaqChannel.Items.AddRange(new object[] { "CH1", "CH2", "CH1+CH2" });
        _cmbDaqChannel.SelectedIndex = 0;
        page.Controls.Add(_cmbDaqChannel);
        y += 28;

        // 输入阻抗
        AddLabel(page, "输入阻抗:", 8, y);
        _cmbImpedance = new ComboBox { Left = 110, Top = y - 3, Width = 115, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbImpedance.Items.AddRange(new object[] { "50Ω HF", "1MΩ 缓冲" });
        _cmbImpedance.SelectedIndex = 0;
        page.Controls.Add(_cmbImpedance);
        y += 28;

        // 采集模式
        AddLabel(page, "采集模式:", 8, y);
        _cmbAcqMode = new ComboBox { Left = 110, Top = y - 3, Width = 115, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbAcqMode.Items.AddRange(new object[] { "FifoSingle", "FifoMulti", "FifoGate", "FifoAverage", "FifoBoxcar", "FifoAba" });
        _cmbAcqMode.SelectedIndex = 1;
        page.Controls.Add(_cmbAcqMode);
        y += 28;

        // 平均次数
        AddLabel(page, "平均次数:", 8, y);
        _numAverages = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 1, Maximum = 65536, Value = 1 };
        page.Controls.Add(_numAverages);
        y += 28;

        // 触发电平
        AddLabel(page, "触发电平 (mV):", 8, y);
        _numTrigLevelMv = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = -10000, Maximum = 10000, Value = 1000, Increment = 100 };
        page.Controls.Add(_numTrigLevelMv);
        y += 28;

        // 触发后延时（SPC_TRIG_DELAY）：跳过始波保留后续底波
        AddLabel(page, "触发延迟 (µs):", 8, y);
        _numTrigDelayUs = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 0, Maximum = 10000, Value = 0, Increment = 1, DecimalPlaces = 1 };
        _trigDelayTip.SetToolTip(_numTrigDelayUs, "触发后延时：将触发事件延迟 N µs 再开始采集（SPC_TRIG_DELAY），用于跳过始波直接采集底波。0=禁用");
        page.Controls.Add(_numTrigDelayUs);
        y += 28;

        // 时间戳
        _chkTimestamp = new CheckBox { Left = 110, Top = y, Width = 115, Text = "启用时间戳" };
        page.Controls.Add(_chkTimestamp);
        y += 28;

        // 应用按钮
        _btnDaqApply = new Button { Text = "应用采集参数", Left = 8, Top = y, Width = 105, Height = 30, Enabled = false, BackColor = System.Drawing.Color.SteelBlue, ForeColor = System.Drawing.Color.White };
        _btnDaqApply.Click += async (_, _) => await ApplyDaqParamsAsync();
        page.Controls.Add(_btnDaqApply);
        y += 36;

        // 采集启停
        _btnDaqStart = new Button { Text = "开始采集", Left = 8, Top = y, Width = 105, Height = 28, Enabled = false, BackColor = System.Drawing.Color.MediumSeaGreen, ForeColor = System.Drawing.Color.White };
        _btnDaqStart.Click += async (_, _) => await DaqStartAsync();
        page.Controls.Add(_btnDaqStart);

        _btnDaqStop = new Button { Text = "停止采集", Left = 120, Top = y, Width = 105, Height = 28, Enabled = false, BackColor = System.Drawing.Color.IndianRed, ForeColor = System.Drawing.Color.White };
        _btnDaqStop.Click += async (_, _) => await DaqStopAsync();
        page.Controls.Add(_btnDaqStop);
        y += 36;

        // 回读显示
        page.Controls.Add(new Label { Text = "参数回读:", Left = 8, Top = y, Width = 80 });
        _txtDaqReadback = new TextBox { Left = 8, Top = y + 20, Width = 217, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new System.Drawing.Font("Consolas", 8.5F) };
        page.Controls.Add(_txtDaqReadback);
    }

    // ── 系统参数 Tab ──

    private void BuildSystemTab(TabPage page)
    {
        int y = 12;

        page.Controls.Add(new Label { Text = "系统参数（说明书 3.7）", Left = 8, Top = y, Width = 250, Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold) });
        y += 32;

        AddLabel(page, "材料声速 (m/s):", 8, y);
        _numSoundVelocity = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 500, Maximum = 10000, Value = 1480, Increment = 10 };
        page.Controls.Add(_numSoundVelocity);
        y += 30;

        AddLabel(page, "仿形焦距 (mm):", 8, y);
        _numFocalLength = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = 1, Maximum = 500, DecimalPlaces = 1, Value = 25, Increment = 1m };
        page.Controls.Add(_numFocalLength);
        y += 30;

        AddLabel(page, "零点校准 (µs):", 8, y);
        _numZeroOffset = new NumericUpDown { Left = 110, Top = y - 3, Width = 115, Minimum = -1000, Maximum = 1000, DecimalPlaces = 2, Value = 0, Increment = 0.1m };
        page.Controls.Add(_numZeroOffset);
        y += 36;

        var btnApplySys = new Button { Text = "应用系统参数", Left = 8, Top = y, Width = 217, Height = 30, BackColor = System.Drawing.Color.SteelBlue, ForeColor = System.Drawing.Color.White };
        btnApplySys.Click += (_, _) => ApplySystemParams();
        page.Controls.Add(btnApplySys);
    }

    // ── 底部操作日志面板 ──

    private void BuildLogPanel(Panel p)
    {
        p.Controls.Add(new Label { Text = "操作日志", Left = 8, Top = 2, Width = 80, Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold) });

        var btnClear = new Button { Text = "清空", Left = 88, Top = 0, Width = 60, Height = 22, FlatStyle = FlatStyle.Flat };
        btnClear.Click += (_, _) => _txtLog.Clear();
        p.Controls.Add(btnClear);

        _txtLog = new RichTextBox { Left = 4, Top = 24, Width = p.Width - 12, Height = p.Height - 30, Dock = DockStyle.Fill, ReadOnly = true, Font = new System.Drawing.Font("Consolas", 9F), BackColor = System.Drawing.Color.White };
        p.Controls.Add(_txtLog);
        // 由于 Dock=Fill 会覆盖上方控件，改为手动定位
        _txtLog.Dock = DockStyle.None;
        _txtLog.Left = 4;
        _txtLog.Top = 26;
        _txtLog.Width = p.Width - 12;
        _txtLog.Height = p.Height - 32;
        p.Resize += (_, _) => { _txtLog.Width = p.Width - 12; };
    }


    // ════════════════════════════════════════════════════════════════
    //  辅助方法
    // ════════════════════════════════════════════════════════════════

    private static void AddLabel(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Left = x,
            Top = y + 3,
            Width = 85,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        });
    }
}
