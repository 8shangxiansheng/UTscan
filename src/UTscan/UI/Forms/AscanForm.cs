using System.IO;
using System.Text;
using System.Windows.Forms;
using UTscan.Core;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.Daq;
using UTscan.UI.Controls;

namespace UTscan.UI.Forms;

/// <summary>
/// A 扫显示窗体（批次2 增强）。
/// 在原实时波形基础上新增：
///  1. 闸门显示——闸门起点/宽度虚线矩形 + ±阈值电平线 + 闸门测量读数（峰值/位置/峰峰/判定）；
///  2. 冻结功能——冻结当前波形画面，闸门参数仍可调整以便离线分析，解冻后恢复实时刷新。
/// </summary>
public class AscanForm : Form
{
    private readonly IDataAcquisition _daq;
    private readonly IPulseGenerator? _pulse;   // H-A：用于诊断"DPR 未发射"场景
    private readonly WaveformView _view;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    // 闸门参数面板控件
    private NumericUpDown _numGateStart = null!, _numGateWidth = null!, _numThreshold = null!;
    private CheckBox _chkGateEnabled = null!;
    private Button _btnFreeze = null!;
    private Button _btnRescale = null!;
    private Label _lblLiveStatus = null!;

    // P0-A（对焦找波）：延迟时间（波形平移）+ 采样长度（波形缩放）控件
    private NumericUpDown _numDelayUs = null!, _numSampleLenUs = null!;
    private Button _btnPanLeft = null!, _btnPanRight = null!, _btnZoomIn = null!, _btnZoomOut = null!;

    // P0-B：波形类型选择（RF/检波/正半波/负半波）
    private ComboBox _cmbWaveType = null!;

    // ── 新功能（20260828）：叠加/平均/滤波/游标/异常检测/回放 ──
    private readonly UTscan.Services.SignalProcessing.GateAnalyzer _analyzer = new();
    private CheckBox _chkOverlay = null!;
    private CheckBox _chkAverage = null!;
    private ComboBox _cmbFilter = null!;
    private CheckBox _chkCursors = null!;
    private Label _lblCursor = null!;
    private readonly List<AScanData> _overlayFrames = new();   // 叠加缓存（最多 20 帧）
    private const int MaxOverlayFrames = 20;
    private readonly List<float[]> _averageWindow = new();     // 平均窗口（最近 N 帧 Samples）
    private const int AverageWindowSize = 16;
    private readonly Queue<float> _peakPosWindow = new();       // 峰位滑动窗（异常检测）
    private readonly Queue<float> _peakAmpWindow = new();       // 幅值滑动窗（异常检测）
    private const int DetectWindowSize = 10;
    private Label _lblDetect = null!;
    private Label _lblKpi = null!;
    private Label _lblGateOverflow = null!;
    // P2 回放：历史帧环形缓冲
    private readonly List<AScanData> _historyBuffer = new();
    private const int HistoryCapacity = 512;
    private bool _playbackMode;
    private int _playbackIndex;
    private Button _btnPlayback = null!, _btnPlaybackPrev = null!, _btnPlaybackNext = null!;
    private Button _btnExportHistory = null!;
    private CheckBox _chkDepthAxis = null!;
    private NumericUpDown _numSoundVelocity = null!;
    private bool _axisUs = true;          // 顶部时间类控件当前单位：true=µs, false=mm
    private bool _updatingAxis;            // 轴单位切换/换算时防 ValueChanged 重入
    private Label _lblGateStart = null!, _lblGateWidth = null!, _lblPan = null!, _lblSampleLen = null!;
    private Panel _topPanel = null!;       // 全屏模式：隐藏/恢复
    private bool _fullScreen;              // 全屏标志（F11 切换）
    private TrackBar _gainBar = null!;     // 增益实时条（联动 DPR500）
    private Label _lblGainVal = null!;
    private System.Windows.Forms.Timer? _gainDebounce;   // 增益防抖（停止拖动后才下发）
    private Button _btnMinus6Db = null!;   // -6dB 定量
    private Label _lblMinus6Db = null!;
    private Button _btnTcgOverlay = null!;  // TCG 曲线叠加（A 扫可视化）
    private readonly UTscan.Core.Models.TcgCurve _tcgAscan = new();   // A 扫本地 TCG 曲线（与扫查窗独立实例）
    private Button _btnFullScreen = null!;   // 全屏切换按钮

    // P0-B：同步闸门（黄色）——GateAnalyzer 联动已实现，此处提供 UI 配置
    private CheckBox _chkSyncGate = null!;
    private NumericUpDown _numSyncStart = null!, _numSyncWidth = null!, _numSyncThreshold = null!;
    private readonly GateConfig _syncGate = new()
    {
        Name = "Sync",
        Role = UTscan.Core.Enums.GateRole.Sync,
        StartUs = 0f,
        WidthUs = 3f,
        ThresholdV = 0.5f,
        Enabled = false,
        GateColor = System.Drawing.Color.Yellow
    };

    // P1-A：多数据闸门（说明书 3.3.4，最多 10 个）——下拉选择当前编辑闸门 + 添加/删除
    private ComboBox _cmbGateSelect = null!;
    private Button _btnAddGate = null!, _btnRemoveGate = null!;
    private readonly List<GateConfig> _dataGates = new()
    {
        new() { Name = "G1", StartUs = 2f, WidthUs = 8f, ThresholdV = 0.5f, GateColor = System.Drawing.Color.Red }
    };

    /// <summary>冻结状态：true 时停止从采集卡拉取新数据，画面停留在冻结瞬间</summary>
    private bool _frozen;

    private long _lastFrameCount;
    private DateTime _lastNewFrameUtc = DateTime.MinValue;
    private const int StaleFrameMs = 1000;


    public AscanForm(IDataAcquisition daq, IPulseGenerator? pulse = null)
    {
        _daq = daq;
        _pulse = pulse;
        _lastFrameCount = daq.GetCurrentFrameCount();

        Text = "A 扫显示";
        ClientSize = new System.Drawing.Size(900, 540);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        // ── 顶部：闸门参数 + 冻结按钮 ──
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 6, 0) };
        _topPanel = topPanel;   // 全屏模式：保持引用以隐藏/恢复

        // P1-A：多数据闸门选择 + 添加/删除（最多 10 个）
        topPanel.Controls.Add(new Label { Text = "数据闸门:", Left = 4, Top = 10, Width = 58, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _cmbGateSelect = new ComboBox { Left = 64, Top = 7, Width = 56, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbGateSelect.Items.Add("G1");
        _cmbGateSelect.SelectedIndex = 0;
        topPanel.Controls.Add(_cmbGateSelect);
        _btnAddGate = new Button { Text = "+", Left = 124, Top = 6, Width = 30, Height = 26 };
        _btnRemoveGate = new Button { Text = "−", Left = 156, Top = 6, Width = 30, Height = 26 };
        topPanel.Controls.Add(_btnAddGate);
        topPanel.Controls.Add(_btnRemoveGate);

        _lblGateStart = new Label { Text = "闸门起始(µs):", Left = 196, Top = 10, Width = 72, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
        topPanel.Controls.Add(_lblGateStart);
        // 1-FIX：控件默认值与 G1 数据一致（2µs 起点）
        _numGateStart = new NumericUpDown { Left = 270, Top = 7, Width = 70, Minimum = 0, Maximum = 1000000, Value = 2, DecimalPlaces = 1, Increment = 1 };
        topPanel.Controls.Add(_numGateStart);

        _lblGateWidth = new Label { Text = "宽度(µs):", Left = 348, Top = 10, Width = 52, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
        topPanel.Controls.Add(_lblGateWidth);
        // 1-FIX：控件默认值与 G1 数据一致（8µs 宽度，不超默认 10.2µs 采样窗）
        _numGateWidth = new NumericUpDown { Left = 402, Top = 7, Width = 70, Minimum = 1, Maximum = 1000000, Value = 8, DecimalPlaces = 1, Increment = 1 };
        topPanel.Controls.Add(_numGateWidth);

        topPanel.Controls.Add(new Label { Text = "阈值(V):", Left = 480, Top = 10, Width = 52, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _numThreshold = new NumericUpDown { Left = 534, Top = 7, Width = 65, Minimum = -10, Maximum = 10, Value = 0.5m, DecimalPlaces = 2, Increment = 0.1m };
        topPanel.Controls.Add(_numThreshold);

        // 1/2-FIX（审查 20260828）：闸门超窗警示——宽度超过当前采样窗口时提示，
        // 不禁止输入（闸门配置合法，仅显示受限于窗口），消除"配置参数 vs 绘图区间"混淆。
        _lblGateOverflow = new Label
        {
            Text = "",
            Left = 600, Top = 10, Width = 90, Height = 20,
            ForeColor = System.Drawing.Color.OrangeRed,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 7.5F),
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        };
        topPanel.Controls.Add(_lblGateOverflow);

        _chkGateEnabled = new CheckBox { Text = "显示", Left = 608, Top = 8, Width = 50, Checked = true };
        topPanel.Controls.Add(_chkGateEnabled);

        // 冻结/解冻按钮：冻结时画面保持最后一帧，闸门参数仍可调（对快照重算测量值）
        _btnFreeze = new Button { Text = "冻结", Left = 700, Top = 5, Width = 60, Height = 27 };
        topPanel.Controls.Add(_btnFreeze);
        // 重置标度按钮：纵轴量程回零，让下一帧峰值重新建立标度（快攻慢释放）。
        // 用于切换样品后信号变弱时立即恢复真实幅值比例，避免慢释放掩盖差异。
        _btnRescale = new Button { Text = "重标", Left = 768, Top = 5, Width = 40, Height = 27 };
        topPanel.Controls.Add(_btnRescale);
        _lblLiveStatus = new Label
        {
            Text = "等待新帧",
            Left = 818,
            Top = 10,
            Width = 70,
            ForeColor = System.Drawing.Color.DarkOrange,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        };
        topPanel.Controls.Add(_lblLiveStatus);

        // ── P0-A（对焦找波，说明书 4.3/4.9）：延迟时间 + 波形平移/缩放 ──
        // 第二行：延迟时间（µs）数值输入 + 左右平移按钮；采样长度（µs）数值输入 + 放大/缩小按钮。
        // 操作语义：调延迟移除无信号区 → 缩采样长度显示完整 A 信号 → 找表面波。
        topPanel.Height = 76;

        _lblPan = new Label { Text = "平移(µs):", Left = 4, Top = 42, Width = 60, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
        topPanel.Controls.Add(_lblPan);
        _numDelayUs = new NumericUpDown { Left = 66, Top = 39, Width = 70, Minimum = 0, Maximum = 1000000, Value = 0, DecimalPlaces = 1, Increment = 1 };
        topPanel.Controls.Add(_numDelayUs);

        _btnPanLeft = new Button { Text = "◀", Left = 142, Top = 39, Width = 32, Height = 25 };
        _btnPanRight = new Button { Text = "▶", Left = 176, Top = 39, Width = 32, Height = 25 };
        topPanel.Controls.Add(_btnPanLeft);
        topPanel.Controls.Add(_btnPanRight);

        _lblSampleLen = new Label { Text = "采样长(µs):", Left = 218, Top = 42, Width = 75, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
        topPanel.Controls.Add(_lblSampleLen);
        _numSampleLenUs = new NumericUpDown { Left = 295, Top = 39, Width = 70, Minimum = 0, Maximum = 1000000, Value = 0, DecimalPlaces = 1, Increment = 1 };
        topPanel.Controls.Add(_numSampleLenUs);

        _btnZoomIn = new Button { Text = "+", Left = 371, Top = 39, Width = 32, Height = 25 };
        _btnZoomOut = new Button { Text = "−", Left = 405, Top = 39, Width = 32, Height = 25 };
        topPanel.Controls.Add(_btnZoomIn);
        topPanel.Controls.Add(_btnZoomOut);

        topPanel.Controls.Add(new Label
        {
            Text = "对焦：调延迟移无信号区→缩采样长显示完整波形",
            Left = 450, Top = 42, Width = 240, Height = 22,
            ForeColor = System.Drawing.Color.DimGray, TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        });

        // ── 新功能（20260828）第二行右侧：叠加/平均/滤波/游标（5-FIX：加宽容纳中文标签）──
        _chkOverlay = new CheckBox { Text = "叠加", Left = 688, Top = 40, Width = 56 };
        topPanel.Controls.Add(_chkOverlay);
        _chkAverage = new CheckBox { Text = "平均", Left = 744, Top = 40, Width = 56 };
        topPanel.Controls.Add(_chkAverage);
        _cmbFilter = new ComboBox
        {
            Left = 800, Top = 39, Width = 64, DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbFilter.Items.AddRange(new object[] { "原始", "中值3", "中值5", "平滑" });
        _cmbFilter.SelectedIndex = 0;
        topPanel.Controls.Add(_cmbFilter);

        // 游标开关 + 读数标签
        _chkCursors = new CheckBox { Text = "游标", Left = 688, Top = 74, Width = 56 };
        topPanel.Controls.Add(_chkCursors);
        // -6dB 定量：自动搜索闸门内峰值→包络半功率点→宽度与缺陷定量（OmniScan 通用定量法）
        _btnMinus6Db = new Button { Text = "-6dB", Left = 746, Top = 73, Width = 48, Height = 24, Enabled = false };
        _btnMinus6Db.Click += (_, _) => ComputeMinus6Db();
        topPanel.Controls.Add(_btnMinus6Db);
        _lblMinus6Db = new Label { Text = "", Left = 796, Top = 76, Width = 100, ForeColor = System.Drawing.Color.Yellow, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Font = new System.Drawing.Font("Microsoft YaHei UI", 7.5F) };
        topPanel.Controls.Add(_lblMinus6Db);
        topPanel.Controls.Add(_chkCursors);
        topPanel.Controls.Add(_chkCursors);
        _lblCursor = new Label { Text = "", Left = 740, Top = 74, Width = 150, ForeColor = System.Drawing.Color.LightGray, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        topPanel.Controls.Add(_lblCursor);
        // 20260829：A 扫交互提示（滚轮缩放 / 游标）
        topPanel.Controls.Add(new Label
        {
            Text = "滚轮:时间缩放 | Ctrl+滚轮:幅值 | 左键游标A 右键游标B",
            Left = 6, Top = 106, Width = 384, Height = 18,
            ForeColor = System.Drawing.Color.DimGray, TextAlign = System.Drawing.ContentAlignment.MiddleLeft
        });

        // ── P0-B：波形类型选择 + 同步闸门（第三行）──
        topPanel.Height = 140;
        topPanel.Controls.Add(new Label { Text = "波形类型:", Left = 4, Top = 74, Width = 60, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _cmbWaveType = new ComboBox { Left = 66, Top = 71, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbWaveType.Items.AddRange(new object[] { "射频", "检波", "正半波", "负半波" });
        _cmbWaveType.SelectedIndex = 0;
        topPanel.Controls.Add(_cmbWaveType);

        _chkSyncGate = new CheckBox { Text = "同步闸门(黄)", Left = 165, Top = 72, Width = 95 };
        topPanel.Controls.Add(_chkSyncGate);
        topPanel.Controls.Add(new Label { Text = "起(µs):", Left = 262, Top = 74, Width = 42, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _numSyncStart = new NumericUpDown { Left = 306, Top = 71, Width = 60, Minimum = 0, Maximum = 1000000, Value = 0, DecimalPlaces = 1, Increment = 1 };
        topPanel.Controls.Add(_numSyncStart);
        topPanel.Controls.Add(new Label { Text = "宽(µs):", Left = 372, Top = 74, Width = 42, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _numSyncWidth = new NumericUpDown { Left = 416, Top = 71, Width = 60, Minimum = 1, Maximum = 1000000, Value = 3, DecimalPlaces = 1, Increment = 1 };
        topPanel.Controls.Add(_numSyncWidth);
        topPanel.Controls.Add(new Label { Text = "阈值(V):", Left = 482, Top = 74, Width = 52, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _numSyncThreshold = new NumericUpDown { Left = 536, Top = 71, Width = 60, Minimum = -10, Maximum = 10, Value = 0.5m, DecimalPlaces = 2, Increment = 0.1m };
        topPanel.Controls.Add(_numSyncThreshold);

        // ── 波形视图 ──
        _view = new WaveformView { Dock = DockStyle.Fill };
        // P1-A：全部数据闸门 + 同步闸门加入视图
        foreach (var g in _dataGates) _view.Gates.Add(g);

        Controls.Add(_view);
        Controls.Add(topPanel);

        // P1-A：闸门选择切换 → 刷新参数面板为选中闸门值
        _cmbGateSelect.SelectedIndexChanged += (_, _) => LoadGateToPanel(CurrentGate);
        _btnAddGate.Click += (_, _) => AddDataGate();
        _btnRemoveGate.Click += (_, _) => RemoveDataGate();

        // 闸门参数变更 → 同步到当前选中闸门并重绘（冻结/非冻结均生效，支持冻结后调闸门分析）。
        // 深度轴同步：mm 模式下控件值需经 AxisValueToUs 换算为内部 µs 存储。
        _numGateStart.ValueChanged += (_, _) => { CurrentGate.StartUs = AxisValueToUs(_numGateStart.Value); _view.Invalidate(); RefreshGateOverflowHint(); };
        _numGateWidth.ValueChanged += (_, _) => { CurrentGate.WidthUs = AxisValueToUs(_numGateWidth.Value); _view.Invalidate(); RefreshGateOverflowHint(); };
        _numThreshold.ValueChanged += (_, _) => { CurrentGate.ThresholdV = (float)_numThreshold.Value; _view.Invalidate(); };
        _chkGateEnabled.CheckedChanged += (_, _) => { CurrentGate.Enabled = _chkGateEnabled.Checked; _view.Invalidate(); };
        _btnFreeze.Click += OnFreezeToggle;
        _btnRescale.Click += (_, _) => { _view.ResetViewport(); _view.Invalidate(); };

        // ── 新功能事件（20260828）──
        _chkOverlay.CheckedChanged += (_, _) =>
        {
            if (!_chkOverlay.Checked) _view.OverlayFrames = null;
            else _view.OverlayFrames = _overlayFrames.ToList();
            _view.Invalidate();
        };
        _chkAverage.CheckedChanged += (_, _) =>
        {
            if (!_chkAverage.Checked) { _view.Data = _daq.GetCurrentData(); }
            else { _averageWindow.Clear(); }
            _view.Invalidate();
        };
        _cmbFilter.SelectedIndexChanged += (_, _) =>
        {
            _view.DisplayFilter = _cmbFilter.SelectedIndex switch
            {
                1 => DisplayFilterMode.Median3,
                2 => DisplayFilterMode.Median5,
                3 => DisplayFilterMode.LowPass,
                _ => DisplayFilterMode.None
            };
            _view.Invalidate();
        };
        _chkCursors.CheckedChanged += (_, _) =>
        {
            _view.CursorsEnabled = _chkCursors.Checked;
            if (!_chkCursors.Checked) _lblCursor.Text = "";
            _view.Invalidate();
        };
        _view.CursorReadoutChanged = r =>
        {
            if (_chkCursors.Checked)
            {
                if (_view.DepthAxis)
                {
                    float aMm = _view.TimeUsToDepthMm(r.AUs), bMm = _view.TimeUsToDepthMm(r.BUs);
                    _lblCursor.Text = $"A:{aMm:0.##}mm/{r.AV:G3}V  B:{bMm:0.##}mm/{r.BV:G3}V  Δd:{Math.Abs(bMm - aMm):0.##}mm";
                }
                else
                    _lblCursor.Text = $"A:{r.AUs:0.##}µs/{r.AV:G3}V  B:{r.BUs:0.##}µs/{r.BV:G3}V  Δt:{r.DeltaTUs:0.##}µs ΔV:{r.DeltaV:G3}V";
            }
        };
        // 回放按钮（P2，第四行）
        _lblDetect = new Label { Text = "检测中...", Left = 540, Top = 104, Width = 140, ForeColor = System.Drawing.Color.DimGray, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        topPanel.Controls.Add(_lblDetect);
        _lblKpi = new Label { Text = "", Left = 400, Top = 104, Width = 130, ForeColor = System.Drawing.Color.DimGray, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        topPanel.Controls.Add(_lblKpi);
        _btnPlayback = new Button { Text = "回放", Left = 688, Top = 104, Width = 56, Height = 24, Enabled = false };
        _btnPlaybackPrev = new Button { Text = "◀", Left = 746, Top = 104, Width = 30, Height = 24, Enabled = false };
        _btnPlaybackNext = new Button { Text = "▶", Left = 778, Top = 104, Width = 30, Height = 24, Enabled = false };
        _btnPlayback.Click += (_, _) => TogglePlayback();
        _btnPlaybackPrev.Click += (_, _) => StepPlayback(-1);
        _btnPlaybackNext.Click += (_, _) => StepPlayback(+1);
        topPanel.Controls.Add(_btnPlayback);
        topPanel.Controls.Add(_btnPlaybackPrev);
        topPanel.Controls.Add(_btnPlaybackNext);

        // P0-导出：历史帧批量导出按钮
        _btnExportHistory = new Button { Text = "导出历史", Left = 812, Top = 104, Width = 70, Height = 24, Enabled = false };
        _btnExportHistory.Click += (_, _) => ExportHistoryCsv();
        topPanel.Controls.Add(_btnExportHistory);

        // P0-深度：横轴 µs ↔ mm 深度切换 + 声速输入（第五行）
        topPanel.Height = 168;
        _chkDepthAxis = new CheckBox { Text = "深度(mm)", Left = 10, Top = 136, Width = 76 };
        _chkDepthAxis.CheckedChanged += (_, _) =>
        {
            _view.DepthAxis = _chkDepthAxis.Checked;
            ToggleAxisUnits(_chkDepthAxis.Checked);
            _view.Invalidate();
        };
        topPanel.Controls.Add(_chkDepthAxis);
        topPanel.Controls.Add(new Label { Text = "声速(m/s):", Left = 88, Top = 138, Width = 66, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _numSoundVelocity = new NumericUpDown { Left = 156, Top = 136, Width = 80, Minimum = 500, Maximum = 10000, Value = 1480, Increment = 10 };
        _numSoundVelocity.ValueChanged += (_, _) => { _view.SoundVelocity = (float)_numSoundVelocity.Value; _view.Invalidate(); };
        topPanel.Controls.Add(_numSoundVelocity);

        // 增益实时条：直接调节 DPR500 接收增益（联动 SetGainAsync），免切主界面。
        // 范围与 DPR500 一致（-13~66dB）；实机范围在连接后由硬件 limit 确定，此处用通用范围。
        topPanel.Controls.Add(new Label { Text = "增益(dB):", Left = 245, Top = 138, Width = 62, TextAlign = System.Drawing.ContentAlignment.MiddleRight });
        _gainBar = new TrackBar { Left = 310, Top = 130, Width = 180, Minimum = -13, Maximum = 66, SmallChange = 1, LargeChange = 5, TickFrequency = 10, Value = 0 };
        _gainBar.ValueChanged += (_, _) => { if (_lblGainVal != null) _lblGainVal.Text = $"{_gainBar.Value} dB"; };
        _gainBar.MouseUp += (_, _) => ApplyGainDebounced();
        _gainBar.KeyUp += (_, _) => ApplyGainDebounced();
        topPanel.Controls.Add(_gainBar);
        _lblGainVal = new Label { Text = "0 dB", Left = 495, Top = 138, Width = 55, ForeColor = System.Drawing.Color.LightGreen, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        topPanel.Controls.Add(_lblGainVal);
        // TCG 叠加（A 扫可视化深度补偿曲线；曲线编辑器与扫查窗共享的 _tcg 实例）
        _btnTcgOverlay = new Button { Text = "TCG", Left = 552, Top = 136, Width = 40, Height = 22, Enabled = false };
        _btnTcgOverlay.Click += (_, _) =>
        {
            using var dlg = new TcgCurveEditorForm(_tcgAscan);
            dlg.ShowDialog(this);
            _view.TcgOverlay = _tcgAscan.Enabled ? _tcgAscan : null;
            _view.Invalidate();
        };
        topPanel.Controls.Add(_btnTcgOverlay);

        // 全屏切换按钮（F11 亦可）；全屏时按钮文字切换提示退出
        _btnFullScreen = new Button { Text = "全屏", Left = 596, Top = 136, Width = 46, Height = 22 };
        _btnFullScreen.Click += (_, _) => { ToggleFullScreen(); _btnFullScreen.Text = _fullScreen ? "退出" : "全屏"; };
        topPanel.Controls.Add(_btnFullScreen);
        // 防抖：停止拖动/按键后 300ms 才下发（避免拖动过程高频写串口寄存器）
        _gainDebounce = new System.Windows.Forms.Timer { Interval = 300 };
        _gainDebounce.Tick += (_, _) => { _gainDebounce.Stop(); ApplyGainNow(); };

        // P0-A：延迟时间 → 波形窗口起点；采样长度 → 窗口宽度（0=全部）。
        // 深度轴同步：mm 模式下控件值经 AxisValueToUs 换算为内部 µs。
        _numDelayUs.ValueChanged += (_, _) => { _view.StartTimeUs = AxisValueToUs(_numDelayUs.Value); _view.Invalidate(); };
        _numSampleLenUs.ValueChanged += (_, _) =>
        {
            _view.DisplayTimeUs = AxisValueToUs(_numSampleLenUs.Value);
            // 2-FIX：采样长变化时若闸门宽度超窗，自动钳到窗口的 80%（不改变绝对起始位置）
            float windowUs = _view.DisplayTimeUs;
            if (windowUs > 0 && CurrentGate.WidthUs > windowUs * 0.9f)
            {
                float newWidth = Math.Max(windowUs * 0.8f, 1f);
                CurrentGate.WidthUs = newWidth;
                _numGateWidth.Value = Math.Clamp((decimal)UsToAxisValue(newWidth), _numGateWidth.Minimum, _numGateWidth.Maximum);
            }
            _view.Invalidate();
            RefreshGateOverflowHint();
        };
        // 平移：左右移动窗口起点（步进 = 当前窗口宽度的 10%，或 10µs）
        _btnPanLeft.Click += (_, _) => PanView(-PanStepUs());
        _btnPanRight.Click += (_, _) => PanView(+PanStepUs());
        // 缩放：放大=窗口减半（围绕当前起点），缩小=窗口加倍
        _btnZoomIn.Click += (_, _) => ZoomView(0.5f);
        _btnZoomOut.Click += (_, _) => ZoomView(2f);

        // P0-B：波形类型切换 → 视图预处理重绘
        _cmbWaveType.SelectedIndexChanged += (_, _) =>
        {
            _view.WaveformType = (UTscan.Core.Enums.WaveformType)_cmbWaveType.SelectedIndex;
            _view.ResetViewport();   // P4-A：类型变更重置纵轴标度，避免沿用旧标度
            _view.Invalidate();
        };

        // P0-B：同步闸门配置（GateAnalyzer 联动已实现——数据闸门起点 = 同步首穿偏移 + 标称起点）
        _chkSyncGate.CheckedChanged += (_, _) =>
        {
            _syncGate.Enabled = _chkSyncGate.Checked;
            _view.Invalidate();
        };
        _numSyncStart.ValueChanged += (_, _) => { _syncGate.StartUs = (float)_numSyncStart.Value; _view.Invalidate(); };
        _numSyncWidth.ValueChanged += (_, _) => { _syncGate.WidthUs = (float)_numSyncWidth.Value; _view.Invalidate(); };
        _numSyncThreshold.ValueChanged += (_, _) => { _syncGate.ThresholdV = (float)_numSyncThreshold.Value; _view.Invalidate(); };
        // 同步闸门加入视图（初始禁用，勾选后显示黄色）
        _view.Gates.Add(_syncGate);

        // 20260829：滚轮缩放 / 游标点击 → 同步窗体数值控件与游标开关
        _view.ViewWindowChanged += (s, d) =>
        {
            _numDelayUs.Value = Math.Clamp((decimal)s, _numDelayUs.Minimum, _numDelayUs.Maximum);
            _numSampleLenUs.Value = Math.Clamp((decimal)d, _numSampleLenUs.Minimum, _numSampleLenUs.Maximum);
        };
        _view.CursorsAutoEnableRequested += () =>
        {
            if (!_chkCursors.Checked) _chkCursors.Checked = true;
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _refreshTimer.Tick += (_, _) => UpdatePlot();
        _refreshTimer.Start();
        LoadGateToPanel(CurrentGate);          // 1-FIX：初始回填闸门面板（控件默认值可能被 G1 数据覆盖）
        RefreshGateOverflowHint();             // 1/2-FIX：初始闸门超窗提示

        // 缺陷修复：若此前会话编辑过 TCG 且已启用，构造时即叠加曲线
        if (_tcgAscan.Enabled)
        {
            _view.TcgOverlay = _tcgAscan;
            _btnTcgOverlay.Enabled = true;
        }

        // 全屏模式：F11 切换隐藏/显示顶部参数面板
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F11) ToggleFullScreen(); };

        FormClosed += (_, _) => _refreshTimer.Stop();
    }

    /// <summary>P1-A：当前选中的数据闸门（下拉索引映射到列表）</summary>
    private GateConfig CurrentGate
        => _dataGates[Math.Clamp(_cmbGateSelect?.SelectedIndex ?? 0, 0, _dataGates.Count - 1)];

    /// <summary>P1-A：将选中闸门参数回填到面板控件</summary>
    private void LoadGateToPanel(GateConfig g)
    {
        if (_numGateStart == null) return;
        // 深度轴同步：内部 µs→控件显示值（mm 模式时换算）
        _numGateStart.Value = Math.Clamp((decimal)UsToAxisValue(g.StartUs), _numGateStart.Minimum, _numGateStart.Maximum);
        _numGateWidth.Value = Math.Clamp((decimal)UsToAxisValue(g.WidthUs), _numGateWidth.Minimum, _numGateWidth.Maximum);
        _numThreshold.Value = Math.Clamp((decimal)g.ThresholdV, _numThreshold.Minimum, _numThreshold.Maximum);
        _chkGateEnabled.Checked = g.Enabled;
    }

    /// <summary>P1-A：添加数据闸门（最多 10 个，命名 G1~G10）</summary>
    private void AddDataGate()
    {
        if (_dataGates.Count >= 10)
        {
            MessageBox.Show(this, "数据闸门最多 10 个（说明书 3.3.4）", "添加闸门", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int idx = _dataGates.Count;
        // 3-FIX：新增闸门默认宽度不超采样窗口的 50%
        float windowUs = GetWindowTotalUs();
        float defaultWidth = windowUs > 0 ? Math.Min(20f, windowUs * 0.5f) : 20f;
        var g = new GateConfig
        {
            Name = $"G{idx + 1}",
            StartUs = 10f + idx * 20f,
            WidthUs = defaultWidth,
            ThresholdV = 0.5f,
            GateColor = idx % 3 == 0 ? System.Drawing.Color.Red
                : idx % 3 == 1 ? System.Drawing.Color.DodgerBlue
                : System.Drawing.Color.LimeGreen
        };
        _dataGates.Add(g);
        _view.Gates.Add(g);
        _cmbGateSelect.Items.Add(g.Name);
        _cmbGateSelect.SelectedIndex = idx;
        _view.Invalidate();
    }

    /// <summary>P1-A：删除当前数据闸门（保留至少 1 个）</summary>
    private void RemoveDataGate()
    {
        if (_dataGates.Count <= 1)
        {
            MessageBox.Show(this, "至少保留 1 个数据闸门", "删除闸门", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int idx = _cmbGateSelect.SelectedIndex;
        var g = _dataGates[idx];
        _dataGates.RemoveAt(idx);
        _view.Gates.Remove(g);
        _cmbGateSelect.Items.RemoveAt(idx);
        if (_cmbGateSelect.Items.Count > 0)
            _cmbGateSelect.SelectedIndex = Math.Min(idx, _cmbGateSelect.Items.Count - 1);
        _view.Invalidate();
    }

    /// <summary>冻结/解冻切换。冻结 = 停止拉取新数据、画面与闸门参数定格，供扫查过程中分析。</summary>
    private void OnFreezeToggle(object? sender, EventArgs e)
    {
        _frozen = !_frozen;
        _btnFreeze.Text = _frozen ? "解冻" : "冻结";
        _btnFreeze.BackColor = _frozen ? System.Drawing.Color.IndianRed : System.Drawing.SystemColors.Control;
        _view.Invalidate();
    }

    /// <summary>增益实时条：防抖入口（拖动结束/按键弹起时调用，300ms 后才实际下发）。</summary>
    private void ApplyGainDebounced()
    {
        if (_gainDebounce == null) { ApplyGainNow(); return; }
        _gainDebounce.Stop();
        _gainDebounce.Start();
    }

    /// <summary>增益实时条：实际下发接收增益到 DPR500（联动 SetGainAsync）。</summary>
    private void ApplyGainNow()
    {
        if (_gainBar == null) return;
        try
        {
            _ = _pulse?.SetGainAsync(_gainBar.Value);
        }
        catch (Exception) { /* 未连接/通信失败时忽略，仅更新显示 */ }
    }

    /// <summary>
    /// 全屏模式：隐藏顶部参数面板，波形占满整个窗体（对焦找波）。F11 切换。
    /// 全屏时仍可右键/按 F11 退出；波形实时刷新与冻结功能不受影响。
    /// </summary>
    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_topPanel != null)
            _topPanel.Visible = !_fullScreen;
        // 全屏时隐藏标题栏无法（MDI 子窗体），仅面板切换；窗体自动重排 Dock
        _view.Invalidate();
    }

    /// <summary>P0-A：波形平移（延迟时间变化），deltaUs 为 µs 偏移量。深度轴同步：内部 µs 运算后换算为显示值。</summary>
    private void PanView(float deltaUs)
    {
        // 以内部 µs 为基准计算新位置（mm 模式先换算回 µs，再写控件显示值）
        float curUs = AxisValueToUs(_numDelayUs.Value);
        float newUs = Math.Max((float)_numDelayUs.Minimum, Math.Min((float)_numDelayUs.Maximum, curUs + deltaUs));
        _numDelayUs.Value = Math.Clamp((decimal)UsToAxisValue(newUs), _numDelayUs.Minimum, _numDelayUs.Maximum);
    }

    /// <summary>P0-A：波形缩放（采样长度窗口变化），factor>1 放大窗口（显示更长），<1 缩小。
    /// 深度轴同步：基于内部 µs 值运算（_view.DisplayTimeUs），经控件 ValueChanged 换算为显示单位。</summary>
    private void ZoomView(float factor)
    {
        decimal cur = (decimal)_view.DisplayTimeUs;
        decimal newVal = cur <= 0m
            ? 100m   // 当前全览 → 首次缩放给默认窗口（100µs，可由实际采样范围调整）
            : Math.Clamp(cur * (decimal)factor, _numSampleLenUs.Minimum, _numSampleLenUs.Maximum);
        _numSampleLenUs.Value = Math.Clamp((decimal)UsToAxisValue((float)newVal), _numSampleLenUs.Minimum, _numSampleLenUs.Maximum);
    }

    /// <summary>P0-A：平移步进 = 当前窗口宽度的 10%；全览（窗口=0）时按当前数据总时长的一定比例，避免一次平移直接跳出记录。
    /// 深度轴同步：基于内部 µs 值（_view.DisplayTimeUs），返回 µs 步进。</summary>
    private float PanStepUs()
    {
        float viewUs = _view.DisplayTimeUs;
        if (viewUs > 0) return viewUs * 0.1f;

        // 全览：按数据总时长比例平移（默认 10%，至少 1µs）
        var d = _view.Data;
        float totalUs = 0f;
        if (d is { PointCount: > 1, SampleRate: > 0 })
            totalUs = d.PointCount / d.SampleRate * 1e6f;
        return totalUs > 0 ? totalUs * 0.1f : 1f;
    }

    /// <summary>1/2-FIX：当前数据帧的总时长（µs）；无有效数据时返回 0。</summary>
    private float GetWindowTotalUs()
    {
        var d = _view.Data ?? _daq.GetCurrentData();
        if (d is { PointCount: > 1, SampleRate: > 0 })
            return d.PointCount / d.SampleRate * 1e6f;
        return 0f;
    }

    /// <summary>深度轴同步：把控件当前显示值换算为内部 µs（_axisUs=false=mm 模式时按声速回换算）。</summary>
    private float AxisValueToUs(decimal displayed)
    {
        if (_axisUs) return (float)displayed;
        float v = _view.SoundVelocity;
        return (float)displayed * 2000f / v;   // mm → µs
    }

    /// <summary>深度轴同步：把内部 µs 换算为控件显示值（_axisUs=false=mm 模式时按声速换算）。</summary>
    private float UsToAxisValue(float us)
    {
        if (_axisUs) return us;
        float v = _view.SoundVelocity;
        return us * v / 2000f;   // µs → mm
    }

    /// <summary>1/2-FIX：闸门结束时刻是否超出采样窗口——超时显示警示（仅提示，不禁止输入）。</summary>
    private void RefreshGateOverflowHint()
    {
        if (_lblGateOverflow == null) return;
        float windowUs = GetWindowTotalUs();
        if (windowUs <= 0) { _lblGateOverflow.Text = ""; return; }
        float gateEnd = (float)_numGateStart.Value + (float)_numGateWidth.Value;
        if (gateEnd > windowUs)
            _lblGateOverflow.Text = $"⚠ 闸门超窗(窗 {windowUs:0.##}µs)";
        else
            _lblGateOverflow.Text = "";
    }

    /// <summary>
    /// 深度轴同步：切换 4 个时间类控件（闸门起始/宽度/平移/采样长）的单位 µs↔mm，
    /// 值域按声速换算（depth = t×v/2000），标签单位同步更新。
    /// 内部存储恒为 µs，切换时仅做显示换算；编辑时由 _axisUs 标志回换算（见各 ValueChanged）。
    /// </summary>
    private void ToggleAxisUnits(bool toMm)
    {
        if (_updatingAxis) return;
        _updatingAxis = true;
        try
        {
            float v = _view.SoundVelocity;
            float factor = toMm ? v / 2000f : 2000f / v;   // µs→mm 乘 v/2000；mm→µs 乘 2000/v

            // 换算显示值（内部存 µs，切换时改显示单位）
            _numGateStart.Value = Math.Clamp(_numGateStart.Value * (decimal)factor, _numGateStart.Minimum, _numGateStart.Maximum);
            _numGateWidth.Value = Math.Clamp(_numGateWidth.Value * (decimal)factor, _numGateWidth.Minimum, _numGateWidth.Maximum);
            _numDelayUs.Value = Math.Clamp(_numDelayUs.Value * (decimal)factor, _numDelayUs.Minimum, _numDelayUs.Maximum);
            _numSampleLenUs.Value = Math.Clamp(_numSampleLenUs.Value * (decimal)factor, _numSampleLenUs.Minimum, _numSampleLenUs.Maximum);

            // 标签单位
            _lblGateStart.Text = toMm ? "闸门起始(mm):" : "闸门起始(µs):";
            _lblGateWidth.Text = toMm ? "宽度(mm):" : "宽度(µs):";
            _lblPan.Text = toMm ? "平移(mm):" : "平移(µs):";
            _lblSampleLen.Text = toMm ? "采样长(mm):" : "采样长(µs):";

            _axisUs = !toMm;
        }
        finally { _updatingAxis = false; }
        _view.Invalidate();
    }

    /// <summary>
    /// P3：DAQ 采集窗口（采样率/采样长度/量程）变更后调用——重置纵轴稳定标度，
    /// 并让显示窗口回到数据起点，避免沿用旧窗口/旧标度显示新时基的数据流。
    /// UI 线程调用（由 MainForm 在采集参数应用完成后通知）。
    /// 修复：采样长控件设为当前数据实际全范围（而非 0/fallback），
    /// 使用户在 A 扫窗口直接看到完整的采样长度，不再"固定 20.48µs"。
    /// </summary>
    public void ResetDisplay()
    {
        _numDelayUs.Value = 0;
        _view.StartTimeUs = 0;

        // 取当前帧的实际总时长，设为采样长控件值（显示全范围）
        var d = _daq.GetCurrentData();
        float totalUs = 0;
        if (d is { PointCount: > 1, SampleRate: > 0 })
        {
            totalUs = d.PointCount / d.SampleRate * 1e6f;
            // 对齐到 0.1µs 步长（控件 Increment = 1 decimal = 0.1）
            totalUs = (float)Math.Ceiling(totalUs * 10) / 10f;
        }
        // 2-FIX（审查 20260828）：采样长同步为实际采集时长——不超数据范围，
        // 消除"A 扫采样长 50µs 但实际采集仅 10.2µs"的窗口/数据不匹配。
        // 不钳 Maximum（允许用户放大视图超出数据范围，超窗部分留白）。
        float windowUs = Math.Max(totalUs, 0.1f);
        _numSampleLenUs.Value = Math.Clamp((decimal)UsToAxisValue(windowUs), _numSampleLenUs.Minimum, _numSampleLenUs.Maximum);
        _view.DisplayTimeUs = windowUs;
        _view.ResetViewport();
        _view.Invalidate();
        RefreshGateOverflowHint();
    }

    /// <summary>
    /// 2/3-FIX：把 A 扫采样长同步为给定总时长（µs）——由 MainForm 在 DAQ 参数应用后
    /// 用采集配置显式计算传入，不依赖可能为空的当前帧（重初始化后 _currentData 是空帧，
    /// 若从帧读取会得到 0 导致同步被跳过、旧窗口残留 → 波形"消失"）。
    /// </summary>
    public void SyncSampleLengthToAcquisition(float totalUs)
    {
        if (totalUs <= 0) return;
        float windowUs = (float)Math.Ceiling(totalUs * 10) / 10f;
        _numSampleLenUs.Value = Math.Clamp((decimal)UsToAxisValue(windowUs), _numSampleLenUs.Minimum, _numSampleLenUs.Maximum);
        _view.DisplayTimeUs = windowUs;
        _view.Invalidate();
        RefreshGateOverflowHint();
    }

    /// <summary>兼容旧调用（无参）：从当前帧读取总时长（可能为空，供其他调用方）。</summary>
    public void SyncSampleLengthToAcquisition()
    {
        var d = _daq.GetCurrentData();
        if (d is { PointCount: > 1, SampleRate: > 0 })
            SyncSampleLengthToAcquisition(d.PointCount / d.SampleRate * 1e6f);
    }

    private void UpdatePlot()
    {
        // P2：回放模式——显示历史缓冲帧，不消费实时新帧
        if (_playbackMode)
        {
            SetLiveStatus("回放", System.Drawing.Color.Orange);
            _view.Invalidate();
            return;
        }

        if (_frozen)
        {
            SetLiveStatus("已冻结", System.Drawing.Color.SteelBlue);
            _view.Invalidate();
            return;
        }

        if (!_daq.IsRunning)
        {
            SetLiveStatus("采集已停止", System.Drawing.Color.Firebrick);
            _view.Invalidate();
            return;
        }

        long frameCount = _daq.GetCurrentFrameCount();
        // 6-FIX：帧计数回退（停止→重启后 _frameCounter 重置为 0）时同步本地基线，
        // 否则 frameCount(0) > _lastFrameCount(旧值5000) 永假 → 波形不刷新。
        if (frameCount < _lastFrameCount)
            _lastFrameCount = frameCount;
        if (frameCount > _lastFrameCount)
        {
            // M-2：无论新帧是否为空，都推进已处理帧计数，避免对同一空帧反复拉取。
            _lastFrameCount = frameCount;
            var data = _daq.GetCurrentData();
            if (data.PointCount > 0)
            {
                // 历史缓冲（P2）：写入环形缓冲供回放
                _historyBuffer.Add(data);
                if (_historyBuffer.Count > HistoryCapacity) _historyBuffer.RemoveAt(0);
                if (!_playbackMode && _historyBuffer.Count >= 2) _btnPlayback.Enabled = true;
                if (_btnExportHistory != null && _historyBuffer.Count >= 1) _btnExportHistory.Enabled = true;
                if (_btnMinus6Db != null && _dataGates.Count > 0 && _dataGates[0].Enabled) _btnMinus6Db.Enabled = true;
                // 缺陷修复：TCG 按钮此前永远灰色（初始化 Enabled=false 但无启用点）——有新帧即可用（叠加曲线）
                if (_btnTcgOverlay != null) _btnTcgOverlay.Enabled = true;

                // 叠加帧缓存（P0-1）
                if (_chkOverlay.Checked)
                {
                    _overlayFrames.Add(data);
                    if (_overlayFrames.Count > MaxOverlayFrames) _overlayFrames.RemoveAt(0);
                    _view.OverlayFrames = _overlayFrames.ToList();
                }

                // 平均模式（P0-3）：滑动窗累积平均，缓解帧抽取随机性
                if (_chkAverage.Checked)
                {
                    _averageWindow.Add(data.Samples);
                    if (_averageWindow.Count > AverageWindowSize) _averageWindow.RemoveAt(0);
                    _view.Data = ComputeAverageFrame(data);
                }
                else
                {
                    _view.Data = data;
                }

                // 异常检测（P1-1）
                RunAnomalyDetection(data);

                _lastNewFrameUtc = DateTime.UtcNow;
                SetLiveStatus($"实时 #{frameCount}", System.Drawing.Color.ForestGreen);
            }
        }
        else if (_lastNewFrameUtc == DateTime.MinValue)
        {
            SetLiveStatus("等待触发", System.Drawing.Color.DarkOrange);
        }
        else if ((DateTime.UtcNow - _lastNewFrameUtc).TotalMilliseconds >= StaleFrameMs)
        {
            // H-A-FIX：区分"DPR 未发射"与"其他无帧原因"，给出明确诊断而非泛化"无新帧"
            if (_pulse != null && _pulse.IsConnected && !_pulse.Params.Enabled)
                SetLiveStatus("无触发：脉冲发射未启动", System.Drawing.Color.Firebrick);
            else
                SetLiveStatus("无新帧", System.Drawing.Color.Firebrick);
        }

        _view.Invalidate();
    }

    /// <summary>P0-3：计算平均窗口内各帧的平均帧（逐点平均，长度取窗口内最小帧长防御）。</summary>
    private AScanData ComputeAverageFrame(AScanData current)
    {
        if (_averageWindow.Count == 0) return current;
        int n = int.MaxValue;
        foreach (var s in _averageWindow) n = Math.Min(n, s.Length);
        if (n <= 0) return current;

        var avg = new float[n];
        foreach (var s in _averageWindow)
            for (int i = 0; i < n; i++) avg[i] += s[i];
        for (int i = 0; i < n; i++) avg[i] /= _averageWindow.Count;

        return new AScanData { Samples = avg, PointCount = n, SampleRate = current.SampleRate, ChannelIndex = current.ChannelIndex };
    }

    /// <summary>P1-1：帧间峰位漂移/幅值波动检测，异常时更新检测状态标签。</summary>
    /// <summary>
    /// -6dB 定量（OmniScan 通用缺陷定量法）：在闸门内自动搜索峰值，沿包络向两侧找
    /// 幅值 = 峰值×0.708（-6dB）的两个交点 → 时域宽度 → 换算深度/空间尺寸。
    /// 多峰回波先做包络（GateAnalyzer.Detected 绝对值近似）再搜索，避免落在相邻波谷。
    /// </summary>
    private void ComputeMinus6Db()
    {
        var data = _view.Data;
        if (data is not { Samples.Length: > 0 } || data.SampleRate <= 0) return;
        if (_dataGates.Count == 0 || !_dataGates[0].Enabled) return;

        // 包络：检波取绝对值（半功率点搜索在包络上进行）
        float[] env = UTscan.Services.SignalProcessing.GateAnalyzer.Preprocess(data.Samples, WaveformType.Detected);
        float dt = 1e6f / data.SampleRate;
        int maxIdx = Math.Min(data.PointCount, env.Length) - 1;

        // 闸门区间（时间→索引，计入触发偏移）
        var gate = _dataGates[0];
        float offUs = data.TriggerOffsetUs;
        int startIdx = Math.Clamp((int)((gate.StartUs + offUs) / dt), 0, maxIdx);
        int endIdx = Math.Clamp((int)((gate.StartUs + gate.WidthUs + offUs) / dt), 0, maxIdx);

        // 峰值与峰位
        int peakIdx = startIdx;
        for (int i = startIdx; i <= endIdx; i++)
            if (env[i] > env[peakIdx]) peakIdx = i;
        float peak = env[peakIdx];
        if (peak <= 0f) { _lblMinus6Db.Text = "无信号"; return; }

        float half = peak * 0.708f;   // -6dB ≈ 半功率 ≈ 0.708 幅值

        // 向左侧找首次 < half 的点（越出半功率带）
        int leftIdx = startIdx;
        for (int i = peakIdx; i >= startIdx; i--)
            if (env[i] < half) { leftIdx = i; break; }
        if (leftIdx == startIdx && env[startIdx] >= half) leftIdx = startIdx;   // 闸门边界兜底
        // 向右侧找首次 < half 的点
        int rightIdx = endIdx;
        for (int i = peakIdx; i <= endIdx; i++)
            if (env[i] < half) { rightIdx = i; break; }
        if (rightIdx == endIdx && env[endIdx] >= half) rightIdx = endIdx;

        float widthUs = (rightIdx - leftIdx) * dt;
        if (widthUs <= 0) { _lblMinus6Db.Text = "峰过窄"; return; }

        // 深度/空间定量：-6dB 宽度 × 声速 / 2（往返）→ 缺陷沿声束方向尺寸（mm）
        float v = _view.SoundVelocity;
        float widthMm = widthUs * v / 2000f;

        // 显示：-6dB 宽度（µs 或 mm）+ 缺陷定量尺寸
        if (_view.DepthAxis)
            _lblMinus6Db.Text = $"-6dB宽 {widthMm:0.###}mm  定量 {widthMm:0.###}mm";
        else
            _lblMinus6Db.Text = $"-6dB宽 {widthUs:0.##}µs  定量 {widthMm:0.###}mm";

        // 在波形上叠加 -6dB 区间标记（复用游标机制：把 A/B 游标临时设到两个交点）
        _view.CursorAUs = leftIdx * dt - offUs;
        _view.CursorBUs = rightIdx * dt - offUs;
        _view.CursorsEnabled = true;
        _view.Invalidate();
    }

    private void RunAnomalyDetection(AScanData data)
    {
        if (_lblDetect == null) return;

        // P3-1：触发参数可视化——KPI 实时刷新（FIFO 溢出/采集周期峰值耗时）
        if (_lblKpi != null)
        {
            var k = _daq.GetKpis();
            _lblKpi.Text = $"溢:{k.FifoOverrunTotal} 周期:{k.MaxCycleMs:0.###}ms";
            _lblKpi.ForeColor = k.FifoOverrunTotal > 0 ? System.Drawing.Color.IndianRed : System.Drawing.Color.DimGray;
        }

        // 用闸门 0（默认 G1）测峰值位置与幅值；无闸门或未启用时跳过
        if (_dataGates.Count == 0 || !_dataGates[0].Enabled) return;
        var r = _analyzer.Analyze(data, _dataGates[0]);

        _peakPosWindow.Enqueue(r.PeakPositionUs);
        _peakAmpWindow.Enqueue(Math.Abs(r.PeakAmplitude));
        if (_peakPosWindow.Count > DetectWindowSize) _peakPosWindow.Dequeue();
        if (_peakAmpWindow.Count > DetectWindowSize) _peakAmpWindow.Dequeue();
        if (_peakPosWindow.Count < DetectWindowSize) return;   // 窗口未满不判定

        float dt = data.SampleRate > 0 ? 1f / data.SampleRate * 1e6f : 0f;
        float posMean = _peakPosWindow.Average(), ampMean = _peakAmpWindow.Average();
        float posStd = MathF.Sqrt(_peakPosWindow.Sum(p => (p - posMean) * (p - posMean)) / _peakPosWindow.Count);
        float ampStd = MathF.Sqrt(_peakAmpWindow.Sum(a => (a - ampMean) * (a - ampMean)) / _peakAmpWindow.Count);

        bool posJitter = posStd > 2f * dt;                    // 峰位漂移 > 2 采样间隔
        bool ampUnstable = ampMean > 1e-6f && (ampStd / ampMean) > 0.30f;  // 幅值 CV > 30%
        bool dropped = _daq.GetKpis().FifoOverrunTotal > 0;

        if (posJitter || ampUnstable || dropped)
        {
            var parts = new List<string>();
            if (posJitter) parts.Add($"峰位漂移 σ={posStd:0.###}µs");
            if (ampUnstable) parts.Add($"幅值波动 CV={ampStd / ampMean:P0}");
            if (dropped) parts.Add("FIFO丢帧");
            _lblDetect.Text = $"⚠ {string.Join(" ", parts)}";
            _lblDetect.ForeColor = System.Drawing.Color.IndianRed;
        }
        else
        {
            _lblDetect.Text = "正常";
            _lblDetect.ForeColor = System.Drawing.Color.LimeGreen;
        }
    }

    /// <summary>P2：切换回放模式（暂停实时显示，定格到最近帧）。</summary>
    private void TogglePlayback()
    {
        if (_historyBuffer.Count == 0) return;
        _playbackMode = !_playbackMode;
        if (_playbackMode)
        {
            _playbackIndex = _historyBuffer.Count - 1;
            ShowPlaybackFrame();
        }
        else
        {
            _view.Data = _daq.GetCurrentData();
        }
        _btnPlayback.Text = _playbackMode ? "实时" : "回放";
        _btnPlaybackPrev.Enabled = _btnPlaybackNext.Enabled = _playbackMode && _historyBuffer.Count > 0;
        _view.Invalidate();
    }

    private void StepPlayback(int delta)
    {
        if (!_playbackMode || _historyBuffer.Count == 0) return;
        _playbackIndex = Math.Clamp(_playbackIndex + delta, 0, _historyBuffer.Count - 1);
        ShowPlaybackFrame();
        _view.Invalidate();
    }

    private void ShowPlaybackFrame()
    {
        if (_playbackIndex < 0 || _playbackIndex >= _historyBuffer.Count) return;
        var f = _historyBuffer[_playbackIndex];
        _view.Data = AscanFramePool.CloneForExternal(f);
    }

    /// <summary>P0-导出：历史帧批量导出为 CSV（每帧含索引/时间戳/采样率/样点）。</summary>
    private void ExportHistoryCsv()
    {
        if (_historyBuffer.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            Title = "导出历史帧",
            Filter = "CSV 文件 (*.csv)|*.csv|二进制波形 (*.bin)|*.bin",
            DefaultExt = "csv",
            FileName = $"ascan-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            if (dlg.FilterIndex == 2)  // 二进制 .bin
            {
                ExportHistoryBinary(dlg.FileName);
                return;
            }
            // CSV 格式：每帧一个块，head_行含帧索引/时间戳/采样率，data_行含样点
            var sb = new StringBuilder();
            var csv = System.Globalization.CultureInfo.InvariantCulture;
            for (int fi = 0; fi < _historyBuffer.Count; fi++)
            {
                var f = _historyBuffer[fi];
                if (f is not { Samples.Length: > 0 }) continue;
                sb.AppendLine($"# frame={fi}, sample_rate={f.SampleRate.ToString("F0", csv)} Hz, trigger_offset_us={f.TriggerOffsetUs.ToString("F3", csv)}");
                for (int i = 0; i < Math.Min(f.PointCount, f.Samples.Length); i++)
                    sb.AppendLine($"{i},{f.Samples[i].ToString("F6", csv)}");
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show(this, $"已导出 {_historyBuffer.Count} 帧到：{dlg.FileName}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, $"导出失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>P0-导出：二进制波形格式（头+原始 float 样点，紧凑可解析）。</summary>
    private void ExportHistoryBinary(string path)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        foreach (var f in _historyBuffer)
        {
            if (f is not { Samples.Length: > 0 }) continue;
            int n = Math.Min(f.PointCount, f.Samples.Length);
            bw.Write(n);                     // 采样点数
            bw.Write(f.SampleRate);          // 采样率(Hz)
            bw.Write(f.TriggerOffsetUs);     // 触发前偏移(µs)
            for (int i = 0; i < n; i++)
                bw.Write(f.Samples[i]);      // float 样点
        }
        MessageBox.Show(this, $"已导出 {_historyBuffer.Count} 帧到：{path}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetLiveStatus(string text, System.Drawing.Color color)
    {
        _lblLiveStatus.Text = text;
        _lblLiveStatus.ForeColor = color;
    }
}
