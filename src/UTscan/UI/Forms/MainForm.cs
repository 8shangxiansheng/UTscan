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
/// 主窗体：MDI 父窗体，承载运动控制面板、脉冲源/采集参数面板、设备状态指示灯、操作日志。
/// 批次1核心生产功能：信号采集、脉冲收发仪、扫描参数、运动控制、UI 呈现。
/// </summary>
public partial class MainForm : Form
{
    // ── 硬件引用 ──
    private readonly IMotionController _motion;
    private readonly IDataAcquisition _daq;
    private readonly IPulseGenerator _pulse;
    private readonly IScanEngine _scanEngine;
    private readonly AuthService _auth;
    private readonly ConnectionConfig _config;
    private readonly IHardwareConfigService _configService;
    private readonly ConnectionOrchestrator _orchestrator;

    // ── 坐标显示 + 设备状态指示灯 ──
    private readonly Label[] _axisLabels = new Label[5];
    private Label _lblLedMotion = null!, _lblLedDaq = null!, _lblLedPulse = null!;
    private Label _lblMotionStatus = null!, _lblDaqStatus = null!, _lblPulseStatus = null!;

    // ── 运动控制面板 ──
    private ComboBox _cmbSpeed = null!;
    private NumericUpDown _numAccel = null!;
    private NumericUpDown _numTargetX = null!, _numTargetY = null!, _numTargetZ = null!;
    private Button _btnMoveTo = null!;
    // P0-D：相对步进 + 轴置零 + 运动自检控件
    private NumericUpDown _numStepMm = null!;
    private Label _lblAxisStatusX = null!, _lblAxisStatusY = null!, _lblAxisStatusZ = null!;
    private float _jogSpeed = 100f;
    private AxisId _jogAxis;
    private int _jogDir;

    // ── 脉冲收发仪面板（Tab） ──
    private ComboBox _cmbPulseChannel = null!;
    private NumericUpDown _numGain = null!, _numWidth = null!, _numPrf = null!, _numVoltage = null!;
    private NumericUpDown _numEnergyLevel = null!;
    private ComboBox _cmbPulseMode = null!, _cmbDamping = null!, _cmbTriggerSource = null!, _cmbSignalSelect = null!;
    private ComboBox _cmbLowPass = null!, _cmbHighPass = null!;
    private Button _btnPulseApply = null!, _btnPulseReadback = null!, _btnPulseLed = null!, _btnPulseOutput = null!;
    private TextBox _txtPulseReadback = null!;
    private Label _lblPulseOutput = null!;

    // L-4：脉宽控件提示（SDK 无 PulseWidth 属性，DPR500 脉宽由脉冲器硬件决定，仅记录）
    private readonly System.Windows.Forms.ToolTip _pulseWidthTip = new();

    // ── 信号采集面板（Tab） ──
    private NumericUpDown _numSampleRateMHz = null!, _numSampleLengthUs = null!, _numSampleCount = null!;
    private bool _updatingSampleSync;   // 采样率/长度/点数联动防重入
    private ComboBox _cmbInputRange = null!, _cmbDaqChannel = null!, _cmbImpedance = null!, _cmbAcqMode = null!;
    private NumericUpDown _numAverages = null!, _numTrigLevelMv = null!, _numTrigDelayUs = null!;
    private readonly System.Windows.Forms.ToolTip _trigDelayTip = new();
    private CheckBox _chkTimestamp = null!;
    private Button _btnDaqApply = null!, _btnDaqStart = null!, _btnDaqStop = null!;
    private TextBox _txtDaqReadback = null!;

    // ── 系统参数（Tab） ──
    private NumericUpDown _numSoundVelocity = null!, _numFocalLength = null!, _numZeroOffset = null!;

    // ── 操作日志面板 ──
    private RichTextBox _txtLog = null!;

    // ── 状态栏 ──
    private ToolStripStatusLabel _lblUser = null!, _lblConn = null!;

    // ── 轴报警监控 ──
    private readonly System.Windows.Forms.Timer _axisMonitor;
    private bool _axisAlarm;

    // ── 系统参数缓存 ──
    private SystemParams _systemParams = new();

    // ── 连接状态（防重入；手动“连接”与启动自动连接共用）──
    private bool _connectRunning;
    private Task? _connectTask;
    private ToolStripMenuItem? _connectMenuItem;

    // ── 日志级别 ──
    private enum LogLevel { Debug, Info, Success, Warning, Error }

    public MainForm(IMotionController motion, IDataAcquisition daq, IPulseGenerator pulse,
        IScanEngine scanEngine, AuthService auth, ConnectionConfig config,
        IHardwareConfigService configService)
    {
        _motion = motion;
        _daq = daq;
        _pulse = pulse;
        _scanEngine = scanEngine;
        _auth = auth;
        _config = config;
        _configService = configService;
        _orchestrator = new ConnectionOrchestrator(motion, daq, pulse, config);
        SubscribeOrchestrator();

        // M-1：DPR500 断连报警（ConnectionLost 原无 UI 订阅者——现场断连后操作员无提示，扫查继续但数据为空）
        if (pulse is UTscan.Hardware.PulseGen.Dpr500Controller dpr500)
        {
            dpr500.ConnectionLost += (_, msg) =>
            {
                LogE("DPR", $"⚠ 断连: {msg}（可重连）");
                if (!IsDisposed && !Disposing && InvokeRequired) BeginInvoke(new Action(() =>
                {
                    SetLed(_lblLedPulse, _lblPulseStatus, false);
                    _lblConn.Text = "状态：DPR500 已断开";
                }));
            };
            // P5-FIX：控制器内部 SDK 操作/参数写入/关断确认事件 → 统一写文件日志
            // （Release 包 Debug.WriteLine 被裁剪，现场无法看到逐属性写入结果；此通道补齐）
            dpr500.LogEvent += (_, msg) =>
            {
                if (string.IsNullOrEmpty(msg)) return;
                var (level, text) = msg.StartsWith("[ERROR]") ? (LogLevel.Error, msg[7..])
                    : msg.StartsWith("[WARN]") ? (LogLevel.Warning, msg[6..])
                    : (LogLevel.Info, msg.StartsWith("[INFO]") ? msg[6..] : msg);
                Log("DPR", text, level);
            };
        }

        // 5-FIX（审查 20260828）：ZMC 通信中断报警——断开后状态栏区分"未连接/通信中断"
        if (motion is UTscan.Hardware.Zmc.ZmcMotionController zmc)
        {
            zmc.ConnectionLost += (_, msg) =>
            {
                LogE("ZMC", $"⚠ {msg}（可重连）");
                if (!IsDisposed && !Disposing && InvokeRequired) BeginInvoke(new Action(() =>
                {
                    SetLed(_lblLedMotion, _lblMotionStatus, false);
                    _lblConn.Text = "状态：运动控制器通信中断";
                }));
            };
        }

        InitializeComponent();
        // 版本号读取 version.json（软件更新方案）：消除硬编码 v1.0
        Text = $"超声显微扫查系统 {Program.VersionText} — {(_config.UseMock ? "Mock 模式" : "硬件模式")}";
        UpdateUserLabel();
        UpdateConnLabel();
        SubscribeMotion();
        UpdateDeviceLeds(false, false, false);

        _axisMonitor = new System.Windows.Forms.Timer { Interval = 500 };
        _axisMonitor.Tick += (_, _) => PollAxisAlarm();

        LogS("系统", "系统启动");
        LogI("系统", $"运行模式: {(_config.UseMock ? "Mock（模拟）" : "硬件（真机）")}");
    }

    /// <summary>
    /// 窗体加载完成：真机模式下先探测硬件（DLL+连接状态），再自动连接全部硬件。
    /// Mock 模式不探测不自动连接（供开发调试手动触发）。
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_config.UseMock)
        {
                        LogI("系统", "真机模式：启动硬件探测...");
            _lblConn.Text = "状态：探测中...";
            Task.Run(async () =>
            {
                try
                {
                    var probe = new HardwareProbeService(_motion, _daq, _pulse, _config);
                    var report = await probe.ProbeAllAsync();

                    // UI 线程显示探测结果
                    BeginInvoke(new Action(() =>
                    {
                        foreach (var item in report.Items)
                        {
                            Log($"[{item.StatusIcon}] {item.Device}: {item.Message}",
                                item.Status == ProbeStatus.Fail
                                    ? LogLevel.Error
                                    : item.Status == ProbeStatus.Warn
                                        ? LogLevel.Warning
                                        : LogLevel.Info);
                        }
                        LogI("系统", report.Summary);

                        if (report.HasFail)
                        {
                            _lblConn.Text = "状态：硬件探测有异常，请查看日志";
                            _lblConn.ForeColor = System.Drawing.Color.Red;
                        }
                        else
                        {
                            _lblConn.Text = "状态：硬件探测完成，正在连接...";
                        }
                    }));

                    // 必需依赖探测失败时 fail-closed，不继续进入 P/Invoke 连接流程。
                    if (!report.HasFail)
                        BeginInvoke(new Action(() => BeginConnectSequence()));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                            LogE("系统", $"硬件探测异常: {ex.Message}");
                        _lblConn.Text = "状态：探测失败";
                        _lblConn.ForeColor = System.Drawing.Color.Red;
                    }));
                }
            });
        }
    }
}
