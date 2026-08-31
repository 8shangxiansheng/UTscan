using System.Windows.Forms;
using UTscan.Core.Models;

namespace UTscan.UI.Forms;

/// <summary>
/// 连接配置窗体。
/// 审查 2026-08-25 H-A：历史版本仅回传 IP/Port，对话框内对 SerialPort/BaudRate/TimeoutMs 的
/// 修改全部丢失（假反馈）。现按实际消费方如实展示：
/// - IP 地址（ZMC 以太网唯一消费）；
/// - DPR 超时（Dpr500Controller 消费，下限 5000ms 由控制器 Math.Max 兜底）；
/// - 串口/波特率只读说明"JSR SDK 自动发现"（Dpr500Controller 不消费这些字段，
///   JSR_OpenLibrary 内部按 4800/8/N/1 自动扫描，应用层无法指定 COM 号）。
/// </summary>
public class ConnectionForm : Form
{
    private TextBox _txtIp = null!;
    private NumericUpDown _numPort = null!;
    private NumericUpDown _numTimeout = null!;
    private NumericUpDown _numTriggerIo = null!;
    private NumericUpDown _numTriggerPulseMs = null!;
    private CheckBox _chkMock = null!;
    private Button _btnOk = null!;
    private Button _btnCancel = null!;

    public ConnectionConfig Config { get; private set; }

    /// <summary>是否展示 Mock 开关（运行期不可切换硬件装配时置 false，默认 true 保持兼容）</summary>
    public bool ShowMockSwitch { get; set; } = true;

    /// <summary>串口自动发现说明短语（供测试断言文案存在）</summary>
    public const string SerialAutoDiscoverNote = "JSR SDK 自动发现";

    /// <summary>超时编辑范围（ms）：下限 1000 允许调试，控制器内部 Math.Max(…,5000) 兜底</summary>
    public const int MinTimeoutMs = 1000;
    public const int MaxTimeoutMs = 60000;

    public ConnectionForm(ConnectionConfig config)
    {
        Config = new ConnectionConfig
        {
            IpAddress = config.IpAddress,
            Port = config.Port,
            TimeoutMs = config.TimeoutMs,
            TriggerIo = config.TriggerIo,
            TriggerPulseWidthMs = config.TriggerPulseWidthMs,
            UseMock = config.UseMock
        };
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "连接配置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new System.Drawing.Size(320, 300);
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label { Text = "IP地址：", Left = 20, Top = 20, Width = 70 });
        _txtIp = new TextBox { Left = 100, Top = 18, Width = 170, Text = Config.IpAddress };
        Controls.Add(_txtIp);

        Controls.Add(new Label { Text = "端口号：", Left = 20, Top = 55, Width = 70 });
        _numPort = new NumericUpDown { Left = 100, Top = 53, Width = 100, Minimum = 1, Maximum = 65535, Value = Config.Port };
        Controls.Add(_numPort);
        // NM-4：端口不参与 ZMC 连接（ZAux_OpenEth 仅用 IP），仅保留显示
        Controls.Add(new Label
        {
            Text = "注：ZMC 以太网连接仅使用 IP（SDK 固定端口），此端口仅保留显示",
            Left = 20, Top = 76, Width = 280, Height = 20,
            ForeColor = System.Drawing.Color.Gray, Font = new System.Drawing.Font("Microsoft YaHei UI", 7.5F)
        });

        // H-A：超时字段真实回传——DPR 连接超时由 Dpr500Controller.Math.Max(config,5000) 兜底，
        // 现场设备未上电扫描慢时建议调到 ≥8000ms
        Controls.Add(new Label { Text = "连接超时(ms)：", Left = 20, Top = 90, Width = 80, Height = 20 });
        _numTimeout = new NumericUpDown
        {
            Left = 100, Top = 88, Width = 100,
            Minimum = MinTimeoutMs, Maximum = MaxTimeoutMs, Value = Config.TimeoutMs,
            Increment = 500
        };
        Controls.Add(_numTimeout);

        // D2-FIX（审查 20260828）：触发输出 IO 与脉宽回写——真机严格单次触发（DPR500 External 模式）
        // 的唯一软件配置途径（此前仅能手工编辑 hardware.json；缺省 -1 导致真机扫描被拒）。
        Controls.Add(new Label { Text = "触发IO：", Left = 20, Top = 116, Width = 70, Height = 20 });
        _numTriggerIo = new NumericUpDown
        {
            Left = 100, Top = 114, Width = 60,
            Minimum = -1, Maximum = 31, Value = Config.TriggerIo
        };
        Controls.Add(_numTriggerIo);
        Controls.Add(new Label
        {
            Text = "（ZMC 数字输出口号；-1=未配置。伺服使能保留口 OP0/3/4/10/11/12 禁用）",
            Left = 166, Top = 116, Width = 250, Height = 32,
            ForeColor = System.Drawing.Color.Gray, Font = new System.Drawing.Font("Microsoft YaHei UI", 7F)
        });

        Controls.Add(new Label { Text = "触发脉宽(ms)：", Left = 20, Top = 146, Width = 80, Height = 20 });
        _numTriggerPulseMs = new NumericUpDown
        {
            Left = 100, Top = 144, Width = 60,
            Minimum = 1, Maximum = 1000, Value = Config.TriggerPulseWidthMs
        };
        Controls.Add(_numTriggerPulseMs);

        // H-A：串口/波特率为 JSR SDK 自动发现——只读说明而非可编辑假控件
        Controls.Add(new Label
        {
            Text = $"DPR500 串口: 由{SerialAutoDiscoverNote}（4800,8,N,1），无需配置",
            Left = 20, Top = 176, Width = 290, Height = 20,
            ForeColor = System.Drawing.Color.Gray, Font = new System.Drawing.Font("Microsoft YaHei UI", 7.5F)
        });

        _chkMock = new CheckBox { Text = "使用Mock模式（重启后生效）", Left = 100, Top = 205, Width = 200, Checked = Config.UseMock };
        Controls.Add(_chkMock);

        _btnOk = new Button { Text = "确定", Left = 100, Top = 245, Width = 80, Height = 30, DialogResult = DialogResult.OK };
        _btnOk.Click += (_, _) =>
        {
            Config.IpAddress = _txtIp.Text;
            Config.Port = (int)_numPort.Value;
            Config.TimeoutMs = (int)_numTimeout.Value;
            Config.TriggerIo = (int)_numTriggerIo.Value;
            Config.TriggerPulseWidthMs = (int)_numTriggerPulseMs.Value;
            if (ShowMockSwitch) Config.UseMock = _chkMock.Checked;
        };
        Controls.Add(_btnOk);

        _btnCancel = new Button { Text = "取消", Left = 190, Top = 245, Width = 80, Height = 30, DialogResult = DialogResult.Cancel };
        Controls.Add(_btnCancel);

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        // ShowMockSwitch 经对象初始化器赋值，晚于构造函数，可见性在此应用
        _chkMock.Visible = ShowMockSwitch;
        base.OnLoad(e);
    }
}
