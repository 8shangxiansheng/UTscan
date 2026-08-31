using System.Diagnostics;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Hardware.PulseGen;

/// <summary>
/// H-5：DPR500 生命周期状态机（不再只用布尔 _isConnected）。
/// 区分"已断开"与"通信断开但脉冲状态未知（不安全）"等关键状态。
/// </summary>
public enum DprState
{
    /// <summary>未连接或已完全断开</summary>
    Disconnected,
    /// <summary>连接中</summary>
    Connecting,
    /// <summary>已连接就绪，输出未启用</summary>
    Ready,
    /// <summary>输出已启用（脉冲发射中）</summary>
    Running,
    /// <summary>关断确认失败或通信断开，脉冲状态未知——不得报安全断开</summary>
    FaultedUnsafe,
    /// <summary>已 Dispose</summary>
    Disposed,
}

/// <summary>
/// H-5：DPR500 安全故障异常。关断确认失败或回读仍 pulsing 时抛出，
/// 阻止后续操作并联动其他硬件复位。
/// </summary>
public class DprSafetyException : Exception
{
    public DprSafetyException(string message) : base(message) { }
    public DprSafetyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// M-4：DPR500 参数配置异常——批量参数下发失败时抛出，携带失败属性名列表。
/// </summary>
public class DprConfigurationException : Exception
{
    public DprConfigurationException(string message) : base(message) { }
    public DprConfigurationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// H-5：DPR500 关断结果——明确区分关断各阶段成功/失败，不返回模糊 void。
/// </summary>
public sealed record DprShutdownResult(
    bool TriggerDisableWritten,
    bool PulsingConfirmedStopped,
    bool ObjectsClosed,
    string Error);

/// <summary>
/// DPR500 运行诊断快照（§2.3 自检/诊断整改）。
/// </summary>
public class Dpr500Diagnostics
{
    public int LibraryDriversStatus { get; set; }
    public int InstrumentConnectStatus { get; set; }
    public int PulserPowerLimitStatus { get; set; }
    public bool IsPulsing { get; set; }
    public double EnergyPerPulseUj { get; set; }
    public double PowerLimitMaxPrfHz { get; set; }
    public double PowerLimitMaxVolts { get; set; }
    public int PowerLimitMaxEnergyIndex { get; set; }
    public bool ConnectionStatusValid { get; set; }
    public bool PowerLimitStatusValid { get; set; }
    public bool PulsingStatusValid { get; set; }

    /// <summary>电源功率是否超限（超限会导致脉冲幅度下垂，需降低 Volts/Energy/PRF）</summary>
    public bool IsPowerLimitExceeded => PulserPowerLimitStatus > JsrNative.JSR_OK;

    /// <summary>允许高压输出前所需的关键诊断是否全部有效。</summary>
    public bool CanEnableOutput => ConnectionStatusValid
        && InstrumentConnectStatus == JsrNative.JSR_OK
        && PowerLimitStatusValid && PulsingStatusValid
        && !IsPowerLimitExceeded;

    public string Describe() => !ConnectionStatusValid || !PowerLimitStatusValid || !PulsingStatusValid
        ? "诊断读取不完整，不能确认连接/功率/发射状态"
        : IsPowerLimitExceeded
            ? $"功率超限! 最大可用: PRF={PowerLimitMaxPrfHz:F0}Hz, V={PowerLimitMaxVolts:F0}V, Energy≤{PowerLimitMaxEnergyIndex + 1}"
            : $"正常 (脉冲中={IsPulsing}, 单脉冲能量={EnergyPerPulseUj:F1}μJ)";
}

/// <summary>
/// DPR500 脉冲收发仪真实实现 — 基于 JSR Common API SDK v1.3（DLL 3.3.0.0）。
///
/// 2026-08-18 §2.2/§2.3 整改要点（全部依据官方头文件与文档核对）：
/// - 参数范围不再硬编码：连接后经 JSR_GetAsciiInfo 读取 limitLo/limitHi
///   （增益/PRF/电压随接收器与脉冲器模块不同而不同，如本机 DPR500-H02-H02-300
///    增益 -22~50dB，而 50MHz 接收器为 -13~66dB —— Properties Reference §11.12）。
/// - 双通道 A/B：读取 InstrumentChannelHandles(2000) 全部句柄 + ChannelLetter(3001)，
///   SelectChannelAsync 在通道间切换并重新应用参数（§2.2 #5/#8）。
/// - SLAVE 触发源（双通道级联同步）与 BOTH 信号选择（双工）：运行时经
///   TriggerSource/SignalSelect 属性的 limitHi 探测支持度后再启用（§2.2 #6/#7、§2.3 #17）。
/// - 断连检测与重连：JSR_FAIL_INSTRUMENT_DISCONNECTED 等 6 个状态码触发
///   ConnectionLost 事件，ReconnectAsync 重建全部对象（§2.3 #8）。
/// - 线程安全：SDK 默认启用内部互斥（JSR_LIB_OPTION_DISABLE_MUTEX 未设置），
///   控制器侧再加 _sdkLock 保护句柄状态（§2.3 #12）。
/// - 仪器信息/诊断/LED/触发边沿/外触发阻抗：见 Dpr500InstrumentInfo /
///   GetDiagnostics() / SetPulserLedIdentifyAsync 等 API（§2.3 #15/#16）。
/// - 脉冲宽度：JSR_PropertyID.h 无 PulseWidth 属性，DPR500 脉冲宽度由远程脉冲器
///   型号（RP-L2 等）硬件决定，SetPulseWidthAsync 仅记录（§2.2 #4，已证实）。
/// </summary>
public sealed class Dpr500Controller : IPulseGenerator
{
    private readonly object _sdkLock = new();     // 控制器侧线程安全（SDK 内部另有互斥）

    private bool _isConnected;
    // H-5：生命周期状态机（不再只用 _isConnected 布尔）
    private DprState _state = DprState.Disconnected;
    /// <summary>H-5：当前生命周期状态（供 UI / 联动判断）</summary>
    public DprState State => _state;
    private bool _libraryOpen;
    private int _instrumentHandle;
    private int _channelHandle;
    private int _pulserHandle;
    private int _receiverHandle;
    private readonly PulseParams _params = new();

    // P4-A：上次实际下发的滤波器索引/增益（-1 表示尚未下发）。
    // ApplyParamsAsync 仅在目标值真正变化时才写 LP/HP 滤波器寄存器——
    // 否则"仅改增益"的应用也会重复写滤波器，若设备列表与 UI 下拉映射不一致，
    // 会无意改动模拟带宽 → 相位响应变化 → 波峰在时域位置偏移（增益-位置耦合）。
    private int _appliedLpIndex = -1;
    private int _appliedHpIndex = -1;
    private float _appliedGainDb = float.NaN;

    // 双通道句柄表（DPR500 = Channel A + Channel B）
    private int[] _channelHandles = Array.Empty<int>();
    private string[] _channelLetters = Array.Empty<string>();
    private int _selectedChannelIndex;

    // 运行时能力（连接后由 JSR_GetAsciiInfo 填充；未连接时用说明书回退值）
    private readonly Dpr500InstrumentInfo _info = new();
    private float _gainMinDb = Dpr500Protocol.GainMinDb;   // 回退 -13dB（RL01 50MHz 接收器）
    private float _gainMaxDb = Dpr500Protocol.GainMaxDb;   // 回退 +66dB
    private float _prfMinHz = 0f;                          // 回退 0（PL01: 0~5000，0=单次）
    private float _prfMaxHz = 5000f;
    private float _voltMin = 0f;
    private float _voltMax = 330f;                         // 保守回退值；实机由 limitHi 决定

    // 审查 2026-08-25 P1：上次连接超时时刻（Environment.TickCount64 ms；0=无待冷却）。
    // 超时后 native JSR_OpenLibrary 扫描无法取消，立即重连会与孤儿扫描/后台清理竞争。
    private long _lastTimeoutTick;

    private ConnectionConfig? _lastConfig;

    public bool IsConnected => _isConnected;
    public PulseParams Params => _params;
    public Dpr500InstrumentInfo InstrumentInfo => _info;
    // M-3：结构化连接种类（不再用型号字符串判断）
    private DprConnectionKind _connectionKind = DprConnectionKind.Disconnected;
    /// <summary>M-3：当前连接种类（Physical/Simulation/Disconnected）。UI 真机模式必须验证 Physical。</summary>
    public DprConnectionKind ConnectionKind => _connectionKind;

    /// <summary>最后一次连接失败的详细错误（供 UI 诊断日志显示，成功时清空）</summary>
    public string? LastConnectError { get; private set; }

    /// <summary>仪器断连/通信故障时触发（§2.3 #8 错误恢复）</summary>
    public event EventHandler<string>? ConnectionLost;

    /// <summary>
    /// 参数变更/SDK 操作事件（P5-FIX 20260828）：控制器内部的所有参数写入与错误
    /// 经此事件上抛，由 MainForm 订阅后写入文件日志——弥补 Debug.WriteLine 在
    /// Release 包被裁剪、现场无法看到逐属性写入结果的缺口。消息已含级别前缀。
    /// </summary>
    public event EventHandler<string>? LogEvent;

    // ═══════════════════════════════════════════════════════════════
    //  连接 / 断开 / 重连
    // ═══════════════════════════════════════════════════════════════

    public async Task<bool> ConnectAsync(ConnectionConfig config)
    {
        _lastConfig = config;
        LastConnectError = null;

        // 审查 2026-08-25 P1：重连冷却门——上次连接超时后，JSR_OpenLibrary 的 native 扫描
        // 无法被取消（P/Invoke 阻塞），CleanupAll 改为后台执行后，若立即重连会与孤儿扫描
        // 竞争 _sdkLock/串口句柄，导致句柄泄漏或二次失败。冷却期快速失败并告知剩余秒数。
        if (_lastTimeoutTick != 0)
        {
            double elapsedMs = (Environment.TickCount64 - _lastTimeoutTick) * 1.0;
            const int cooldownMs = 10_000;
            if (elapsedMs < cooldownMs)
            {
                int remainSec = (int)Math.Ceiling((cooldownMs - elapsedMs) / 1000.0);
                LastConnectError =
                    $"上次连接超时后 SDK 后台串口扫描仍在进行，请 {remainSec} 秒后重试（避免与后台清理竞争）";
                return false;
            }
            _lastTimeoutTick = 0;
        }

        // 1. 检查 DLL 可用性
        if (!JsrNative.IsDllAvailable())
        {
            LastConnectError = "JSR_Common DLL 未找到，请运行 JSRControlPanelInstaller 安装 JSR SDK";
            Debug.WriteLine("[DPR500/SDK] JSR_Common DLL 未找到。请运行 JSRControlPanelInstaller 安装 SDK。");
            return false;
        }

        try
        {
            // 2. 超时保护：JSR_OpenLibrary 内部扫描串口可能耗时较长（尤其设备未上电时），
            //    用 Task.WhenAny 确保超时真正生效——GetResult() 无法中断 native P/Invoke。
            int timeoutMs = Math.Max(config.TimeoutMs, 5000);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            var token = cts.Token;

            var connectTask = Task.Run(() => ConnectCore(config, token), token);
            var timeoutTask = Task.Delay(timeoutMs, token);

            var completed = await Task.WhenAny(connectTask, timeoutTask);
            if (completed != connectTask)
            {
                // P1：记录超时时刻供冷却门使用；CleanupAll 改后台执行——
                // 同步调用会阻塞在 _sdkLock 上（ConnectCore 的 native 扫描仍持有锁），
                // 造成"超时后 UI 卡死数秒"与后续连接排队
                _lastTimeoutTick = Environment.TickCount64;
                LastConnectError = $"连接超时 ({timeoutMs}ms)，JSR_OpenLibrary 内部串口扫描未完成，设备可能未上电或 USB 未连接";
                Debug.WriteLine($"[DPR500/SDK] 连接超时（{timeoutMs}ms），JSR_OpenLibrary 可能仍在后台扫描；清理转后台");
                _ = Task.Run(() => CleanupAll());   // fire-and-forget：不阻塞 UI/重试路径
                return false;
            }

            bool result = await connectTask;
            if (!result)
            {
                // LastConnectError 已在 ConnectCore 内设置
                CleanupAll();
                return false;
            }

            LastConnectError = null; // 成功
            Debug.WriteLine($"[DPR500/SDK] 连接成功: {_info}");
            Debug.WriteLine($"[DPR500/SDK] 通道 [{string.Join(", ", _channelLetters)}]，" +
                          $"当前 CH{_channelLetters[_selectedChannelIndex]}，" +
                          $"增益范围 {_gainMinDb}~{_gainMaxDb}dB，PRF {_prfMinHz}~{_prfMaxHz}Hz，" +
                          $"电压 {_voltMin}~{_voltMax}V");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            LastConnectError = $"JSR SDK DLL 加载失败: {ex.Message}";
            Debug.WriteLine($"[DPR500/SDK] DLL 未找到: {ex.Message}");
            CleanupAll();
            return false;
        }
        catch (Exception ex)
        {
            LastConnectError = $"连接异常: {ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[DPR500/SDK] 连接异常: {ex.Message}");
            CleanupAll();
            return false;
        }
    }

    /// <summary>
    /// 连接核心逻辑（在后台线程执行，受 CancellationToken 保护）。
    /// JSR Common SDK 内部管理串口参数（4800/8/N/1），应用层无法直接指定 COM 端口号——
    /// SDK 通过 USB vendor/product ID 或扫描默认串口自动发现 DPR500 设备。
    /// 如设备不在默认端口，需通过 JSR Control Panel 配置或确保 USB 驱动正确安装。
    /// </summary>
    private bool ConnectCore(ConnectionConfig config, CancellationToken ct)
    {
        lock (_sdkLock)
        {
            // 3. 打开库 — 先尝试连接真实硬件。
            //    M-3 修复：真机模式（config.UseMock=false）连接失败立即返回 false，不自动回退仿真。
            //    只有 config.UseMock=true 才允许 JSR_LIB_OPTION_SIMULATE。
            var models = new[] { JsrNative.JSR_MODEL_DPR500 };
            int status = JsrNative.JSR_OpenLibrary(
                JsrNative.JSR_LIB_OPTION_DEFAULT, models, models.Length, 0, 0);

            if (!JsrNative.IsPass(status))
            {
                if (!config.UseMock)
                {
                    string errorDetail = JsrNative.GetErrorString(status);
                    LastConnectError = $"JSR_OpenLibrary 失败 (status=0x{status:X}): {errorDetail}";
                    Debug.WriteLine($"[DPR500/SDK] 物理设备连接失败 ({errorDetail})，真机模式不回退仿真");
                    Debug.WriteLine($"[DPR500/SDK]   请确认: 1) DPR500 已上电 2) USB/串口线已连接 " +
                                  $"3) 串口参数(4800,8,N,1)与设备一致 4) JSR SDK 已安装且驱动正常");
                    return false;
                }

                // Mock 模式才回退仿真
                Debug.WriteLine($"[DPR500/SDK] 未找到物理设备 ({JsrNative.GetErrorString(status)})，Mock 模式尝试仿真...");
                status = JsrNative.JSR_OpenLibrary(
                    JsrNative.JSR_LIB_OPTION_SIMULATE, models, models.Length, 0, 0);

                if (!JsrNative.IsPass(status))
                {
                    LastConnectError = $"物理设备与仿真模式均失败: {JsrNative.GetErrorString(status)}";
                    Debug.WriteLine($"[DPR500/SDK] 仿真模式也失败: {JsrNative.GetErrorString(status)}");
                    return false;
                }
                _params.Model = "DPR500 (Sim)";
                _connectionKind = DprConnectionKind.Simulation;
            }
            else
            {
                _connectionKind = DprConnectionKind.Physical;
            }

            _libraryOpen = true;
            Debug.WriteLine($"[DPR500/SDK] JSR Common Library 已打开（连接种类={_connectionKind}）");

            ct.ThrowIfCancellationRequested();

            // 4. 获取 Instrument 句柄（1001）。
            // P3-FIX3c（现场 20260826）：先读属性元信息拿真实 elementCount（=扫描发现台数），
            // 按台数请求。此前硬编码请求 4 个：单台仪器时 4>1 → SDK 拒绝整次调用
            // （0x83F=JSR_FAIL_INCOUNT_TOO_HIGH），设备明明在线却报"未发现"。
            int found = 0;
            try
            {
                var info = new JsrNative.JsrAsciiInfoStruct();
                if (JsrNative.JSR_GetAsciiInfo(JsrNative.JSR_LIBRARY_HANDLE,
                        JsrNative.JSR_ID_LibraryInstrumentHandles, ref info) == JsrNative.JSR_OK
                    && info.elementCount > 0)
                    found = info.elementCount;
            }
            catch { /* 元信息读取失败则回退固定 4，行为同旧版 */ }

            var instBuf = new int[found > 0 ? found : 4];
            status = JsrNative.JSR_GetInt32(
                JsrNative.JSR_LIBRARY_HANDLE, JsrNative.JSR_ID_LibraryInstrumentHandles,
                instBuf.Length, instBuf);

            if (!JsrNative.CheckStatus(status, "获取 Instrument 句柄") || instBuf[0] == 0)
            {
                // P3-FIX3c（现场 20260826）：走到这里说明按真实台数请求仍失败——
                // 要么 found=0（扫描确实没发现），要么 SDK 返回了其他错误。
                // 透出元信息台数 + 每驱动状态码（1004），把扫描内部结果写进日志。
                string probe = "";
                try
                {
                    var info = new JsrNative.JsrAsciiInfoStruct();
                    if (JsrNative.JSR_GetAsciiInfo(JsrNative.JSR_LIBRARY_HANDLE,
                            JsrNative.JSR_ID_LibraryInstrumentHandles, ref info) == JsrNative.JSR_OK)
                        probe = $"实际发现仪器数={info.elementCount}";
                }
                catch { /* 元信息读取失败不阻塞主错误 */ }
                try
                {
                    var drv = new int[8];
                    int st2 = JsrNative.JSR_GetInt32(JsrNative.JSR_LIBRARY_HANDLE,
                        JsrNative.JSR_ID_LibraryDriversStatus, drv.Length, drv);
                    if (st2 == JsrNative.JSR_OK || st2 == JsrNative.JSR_WARN_NO_BOARDS_FOUND)
                    {
                        string codes = string.Join(",", drv.Where(v => v != 0).Select(v => $"0x{v:X}"));
                        if (codes.Length == 0) codes = "全0";
                        probe += $", 驱动状态=[{codes}] (读取=0x{st2:X})";
                    }
                }
                catch { /* 同上 */ }
                string hint = status == JsrNative.JSR_FAIL_INCOUNT_TOO_HIGH
                    ? $"扫描完成但未发现仪器[{probe}]。常见原因：" +
                      "① JSR Control Panel 或其他程序正占用串口（独占）——请完全退出后重试；" +
                      "② DPR500 未上电或 RS-232 链路（DB9→RJ45 反转适配器 + 反转网线 → RS-232 Input 口）不通"
                    : $"SDK 返回 0x{status:X}: {JsrNative.GetErrorString(status)}";
                LastConnectError = $"未发现 DPR500 仪器 ({hint})";
                CleanupLibrary();
                return false;
            }

            _instrumentHandle = instBuf[0];
            status = JsrNative.JSR_OpenObject(_instrumentHandle, JsrNative.JSR_INSTRUMENT_OPEN_DEFAULT);
            if (!JsrNative.CheckStatus(status, "打开 Instrument"))
            {
                LastConnectError = $"打开 Instrument 对象失败 (status=0x{status:X})，仪器句柄获取成功但打开失败";
                CleanupLibrary();
                return false;
            }

            ct.ThrowIfCancellationRequested();

            // 5. 读取设备型号
            ReadAscii(_instrumentHandle, JsrNative.JSR_ID_InstrumentModelName, out var modelName);
            if (!string.IsNullOrEmpty(modelName) && _params.Model != "DPR500 (Sim)")
                _params.Model = modelName;
            _info.ModelName = modelName;

            // 读取设备实际使用的 COM 端口号（供诊断显示）
            var comBuf = new int[1];
            if (JsrNative.JSR_GetInt32(_instrumentHandle, JsrNative.JSR_ID_InstrumentSerialComPort, 1, comBuf) == JsrNative.JSR_OK)
                _info.ComPort = comBuf[0];

            Debug.WriteLine($"[DPR500/SDK] Instrument 已打开: {_params.Model} COM{_info.ComPort}" +
                (instBuf.Count(h => h != 0) > 1
                    ? $"（共 {instBuf.Count(h => h != 0)} 台仪器在线，使用第一台）" : ""));

            // 6. 获取全部 Channel 句柄并读取通道字母（§2.2 #5/#8 双通道）。
            // P3-FIX3d（现场 20260826）：同 1001 的教训——先读元信息拿真实 elementCount，
            // 按台数请求。DPR500 双通道=2，单通道配置=1；硬编码请求 2 在单通道时同样
            // 触发 0x83F 整次拒绝。
            int chanFound = ReadElementCount(_instrumentHandle, JsrNative.JSR_ID_InstrumentChannelHandles, fallback: 2);
            var chanBuf = new int[chanFound];
            status = JsrNative.JSR_GetInt32(
                _instrumentHandle, JsrNative.JSR_ID_InstrumentChannelHandles,
                chanBuf.Length, chanBuf);

            if (!JsrNative.CheckStatus(status, "获取 Channel 句柄"))
            {
                LastConnectError = $"获取 Channel 句柄失败 (status=0x{status:X})，仪器已打开但无法获取通道";
                CleanupInstrument();
                return false;
            }

            _channelHandles = chanBuf.Where(h => h != JsrNative.JSR_INVALID_HANDLE).ToArray();
            _channelLetters = new string[_channelHandles.Length];
            for (int i = 0; i < _channelHandles.Length; i++)
            {
                ReadAscii(_channelHandles[i], JsrNative.JSR_ID_ChannelLetter, out var letter);
                _channelLetters[i] = string.IsNullOrEmpty(letter) ? ((char)('A' + i)).ToString() : letter;
            }

            // 选择目标通道（UI 1-based: 1=A, 2=B；越界时取最后一档）
            // L5-FIX（审查 20260828）：_channelHandles 可能为空（全被 INVALID_HANDLE 过滤），
            // Math.Clamp(0,-1) 抛 ArgumentException，被外层 catch 泛化为"连接异常"。
            if (_channelHandles.Length == 0)
            {
                LastConnectError = "获取 Channel 句柄为空（所有通道句柄无效），无法连接";
                CleanupInstrument();
                return false;
            }
            _selectedChannelIndex = Math.Clamp(_params.Channel - 1, 0, _channelHandles.Length - 1);
            if (!OpenChannelObjects(_selectedChannelIndex))
            {
                LastConnectError = $"打开 Channel 对象失败，CH{_channelLetters[_selectedChannelIndex]} 不可用";
                CleanupInstrument();
                return false;
            }

            _isConnected = true;
            _state = DprState.Ready;   // H-5：连接就绪

            // 7. 运行时能力探测（动态范围/阻尼表/滤波器表/SLAVE/BOTH 支持）
            LoadRuntimeCapabilities();

            // 8. 连接后的第一项安全动作：无条件关闭触发并确认 IsPulsing=false。
            // 设备可能保留上次会话的 Internal + TriggerEnable 状态，不能仅凭 UI 默认值假定未发射。
            if (!DisablePulsing())
            {
                _state = DprState.FaultedUnsafe;
                LastConnectError = "DPR500 已打开，但连接后无法确认脉冲输出已关闭；拒绝提交连接";
                return false;
            }
            _params.Enabled = false;

            // 9. 读取当前硬件参数到内部状态
            ReadParamsFromHardware();
            // P4-A：重连后复位应用跟踪，确保首次 Apply 完整下发（硬件可能在断连期间被外部改动）
            _appliedLpIndex = -1;
            _appliedHpIndex = -1;
            _appliedGainDb = float.NaN;
            return true;
        }
    }

    /// <summary>
    /// 断连后重连（§2.3 #8 错误恢复机制）：关闭全部对象后按最近一次配置重建。
    /// </summary>
    public Task<bool> ReconnectAsync(ConnectionConfig? config = null)
    {
        var cfg = config ?? _lastConfig;
        if (cfg is null)
        {
            Debug.WriteLine("[DPR500/SDK] ReconnectAsync: 无历史连接配置");
            return Task.FromResult(false);
        }
        CleanupAll();
        return ConnectAsync(cfg);
    }

    public Task DisconnectAsync()
    {
        // H-5 修复：先执行关断确认。失败时状态切换为 FaultedUnsafe，保留 SDK 句柄允许重试，
        // 抛 DprSafetyException，不得记录"已断开"，不得让 UI 点亮安全/断开状态。
        lock (_sdkLock)
        {
            if (_state == DprState.Disposed)
                return Task.CompletedTask;

            if (!DisablePulsing())
            {
                _state = DprState.FaultedUnsafe;
                Debug.WriteLine("[DPR500/SDK] ⚠ 关断确认失败，状态=FaultedUnsafe，SDK 对象保持打开以便重试");
                throw new DprSafetyException(
                    "DPR500 脉冲关断未确认（IsPulsing 仍为 true 或 TriggerEnable 写入失败），" +
                    "SDK 对象保持打开以便重试。最终安全必须依赖设备 fail-safe 或硬件联锁");
            }

            CloseObjectsInReverseOrderChecked();
            _isConnected = false;
            _state = DprState.Disconnected;
            _connectionKind = DprConnectionKind.Disconnected;
        }
        Debug.WriteLine("[DPR500/SDK] 已断开（关断已确认）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 切换工作通道（§2.2 #5/#8 多通道）。
    /// channel: 1=Channel A, 2=Channel B。切换后重新应用当前参数。
    /// </summary>
    public async Task<bool> SelectChannelAsync(int channel)
    {
        if (!_isConnected || _channelHandles.Length == 0)
        {
            _params.Channel = Math.Clamp(channel, 1, Math.Max(1, _channelHandles.Length));
            return false;
        }

        int idx = Math.Clamp(channel - 1, 0, _channelHandles.Length - 1);
        if (idx == _selectedChannelIndex)
            return true;

        // M-5 修复：切换通道前先关断脉冲并确认——禁止在发射状态下切换通道
        lock (_sdkLock)
        {
            if (!DisablePulsing())
                throw new DprSafetyException("当前通道脉冲未停止（IsPulsing 仍为 true 或关断写入失败），禁止切换通道");

            CleanupPulserReceiver();
            JsrNative.JSR_CloseObject(_channelHandle);
            _channelHandle = 0;

            if (!OpenChannelObjects(idx))
                // M-5：保持 Faulted/Disconnected，不得继续 ApplyParamsAsync
                return false;

            LoadRuntimeCapabilities();   // 各通道脉冲器/接收器模块可能不同
        }

        // M-5：只有新通道对象全部打开且参数全部应用成功后，才提交 _selectedChannelIndex
        try
        {
            await ApplyParamsAsync(_params);
        }
        catch
        {
            // 参数应用失败：新通道对象已打开但参数未就绪，回滚到原通道不安全（旧句柄已关闭）
            // 标记 FaultedUnsafe，UI 须重新连接
            lock (_sdkLock) { _state = DprState.FaultedUnsafe; }
            throw;
        }

        // 参数全部成功才提交通道切换
        lock (_sdkLock)
        {
            _selectedChannelIndex = idx;
            _params.Channel = idx + 1;
        }
        Debug.WriteLine($"[DPR500/SDK] 已切换到 CH{_channelLetters[idx]} 并重新应用参数");
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  运行时能力探测（§2.2 #2/#3 动态范围，§2.3 #4/#5/#6/#17）
    // ═══════════════════════════════════════════════════════════════

    private void LoadRuntimeCapabilities()
    {
        // ── 标量属性范围：JSR_GetAsciiInfo 的 limitLo/limitHi（Properties Reference 各属性页）──
        if (GetLimit(_receiverHandle, JsrNative.JSR_ID_ReceiverGainDB, out double gLo, out double gHi))
        {
            _gainMinDb = (float)gLo;
            _gainMaxDb = (float)gHi;
        }

        if (GetLimit(_pulserHandle, JsrNative.JSR_ID_PulserPRF, out double pLo, out double pHi))
        {
            _prfMinHz = (float)pLo;
            _prfMaxHz = (float)pHi;
        }

        if (GetLimit(_pulserHandle, JsrNative.JSR_ID_PulserVolts, out double vLo, out double vHi))
        {
            _voltMin = (float)vLo;
            _voltMax = (float)vHi;
        }

        // ── 触发源支持度：limitHi == JSR_TRIGGER_SLAVE 表示双通道级联可用（§11.7）──
        _info.SupportsSlaveTrigger = GetLimitInt(_pulserHandle,
            JsrNative.JSR_ID_PulserTriggerSource, out _, out int trigMax) && trigMax >= JsrNative.JSR_TRIGGER_SLAVE;

        // ── 信号选择支持度：limitHi == JSR_SIGNAL_SELECT_BOTH 表示双工可用 ──
        _info.SupportsBothSignalSelect = GetLimitInt(_receiverHandle,
            JsrNative.JSR_ID_ReceiverSignalSelect, out _, out int sigMax) && sigMax >= JsrNative.JSR_SIGNAL_SELECT_BOTH;

        // ── 列表属性：阻尼电阻/滤波器/外触发阻抗（替代硬编码 PL01/RL01 表，§2.3 #4/#5）──
        _info.DampingOhms = ReadDoubleList(_pulserHandle, JsrNative.JSR_ID_PulserDampResistorList);
        _info.LowPassMHz = ReadDoubleList(_receiverHandle, JsrNative.JSR_ID_ReceiverLPFilterList)
            .Select(v => v < 1000 ? v : v / 1e6).ToArray();     // SDK 单位为 MHz，防御性归一
        _info.HighPassMHz = ReadDoubleList(_receiverHandle, JsrNative.JSR_ID_ReceiverHPFilterList)
            .Select(v => v < 1000 ? v : v / 1e6).ToArray();
        _info.ExtTriggerZOhms = ReadIntList(_pulserHandle, JsrNative.JSR_ID_PulserExtTriggerZList);

        // ── 仪器识别信息（§2.3 #2/#13）──
        ReadAscii(_instrumentHandle, JsrNative.JSR_ID_InstrumentSerNum, out string serNum);
        _info.SerialNumber = serNum;
        ReadAscii(_pulserHandle, JsrNative.JSR_ID_PulserModelName, out string pulserModel);
        _info.PulserModelName = pulserModel;
        ReadAscii(_pulserHandle, JsrNative.JSR_ID_PulserHardwareRev, out string pulserRev);
        _info.PulserHardwareRev = pulserRev;
        ReadAscii(_receiverHandle, JsrNative.JSR_ID_ReceiverModelName, out string receiverModel);
        _info.ReceiverModelName = receiverModel;
        ReadAscii(_receiverHandle, JsrNative.JSR_ID_ReceiverHardwareRev, out string receiverRev);
        _info.ReceiverHardwareRev = receiverRev;

        var buf = new int[1];
        if (JsrNative.JSR_GetInt32(_instrumentHandle, JsrNative.JSR_ID_InstrumentSerialComPort, 1, buf) == JsrNative.JSR_OK)
            _info.ComPort = buf[0];
        if (JsrNative.JSR_GetInt32(_instrumentHandle, JsrNative.JSR_ID_InstrumentSerialChainAddress, 1, buf) == JsrNative.JSR_OK)
            _info.ChainAddress = buf[0];
        if (JsrNative.JSR_GetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverBandwidth, 1, buf) == JsrNative.JSR_OK)
            _info.ReceiverBandwidthMHz = buf[0];
    }

    /// <summary>读取属性元信息（limitLo/limitHi 为 double 类型时）</summary>
    private bool GetLimit(int handle, int propId, out double lo, out double hi)
    {
        lo = hi = 0;
        if (handle == 0) return false;
        var info = new JsrNative.JsrAsciiInfoStruct();
        if (JsrNative.JSR_GetAsciiInfo(handle, propId, ref info) != JsrNative.JSR_OK) return false;
        lo = info.limitLo.d;
        hi = info.limitHi.d;
        return true;
    }

    /// <summary>读取属性元信息（limitLo/limitHi 为 int/enum 类型时）</summary>
    private bool GetLimitInt(int handle, int propId, out int lo, out int hi)
    {
        lo = hi = 0;
        if (handle == 0) return false;
        var info = new JsrNative.JsrAsciiInfoStruct();
        if (JsrNative.JSR_GetAsciiInfo(handle, propId, ref info) != JsrNative.JSR_OK) return false;
        lo = info.limitLo.i;
        hi = info.limitHi.i;
        return true;
    }

    private double[] ReadDoubleList(int handle, int propId)
    {
        if (handle == 0) return Array.Empty<double>();
        var info = new JsrNative.JsrAsciiInfoStruct();
        if (JsrNative.JSR_GetAsciiInfo(handle, propId, ref info) != JsrNative.JSR_OK || info.elementCount <= 0)
            return Array.Empty<double>();
        var buf = new double[info.elementCount];
        return JsrNative.JSR_GetDouble(handle, propId, buf.Length, buf) == JsrNative.JSR_OK
            ? buf : Array.Empty<double>();
    }

    private int[] ReadIntList(int handle, int propId)
    {
        if (handle == 0) return Array.Empty<int>();
        var info = new JsrNative.JsrAsciiInfoStruct();
        if (JsrNative.JSR_GetAsciiInfo(handle, propId, ref info) != JsrNative.JSR_OK || info.elementCount <= 0)
            return Array.Empty<int>();
        var buf = new int[info.elementCount];
        return JsrNative.JSR_GetInt32(handle, propId, buf.Length, buf) == JsrNative.JSR_OK
            ? buf : Array.Empty<int>();
    }

    private void ReadAscii(int handle, int propId, out string value)
    {
        value = "";
        if (handle == 0) return;
        var buf = new JsrNative.JsrAscii[1];
        if (JsrNative.JSR_GetAscii(handle, propId, 1, buf) == JsrNative.JSR_OK)
            value = buf[0].Value?.TrimEnd('\0') ?? "";
    }

    // ═══════════════════════════════════════════════════════════════
    //  单参数设置（IPulseGenerator 接口）
    // ═══════════════════════════════════════════════════════════════

    public Task SetGainAsync(float gainDb)
    {
        _params.GainDb = Math.Clamp(gainDb, _gainMinDb, _gainMaxDb);
        SdkSetDouble(_receiverHandle, JsrNative.JSR_ID_ReceiverGainDB, _params.GainDb, $"SetGain({_params.GainDb}dB)");
        return Task.CompletedTask;
    }

    public Task SetPulseWidthAsync(float widthNs)
    {
        // §2.2 #4（已证实）：JSR_PropertyID.h 无 PulseWidth 属性；
        // DPR500 脉冲宽度由远程脉冲器型号（RP-L2/RP-H2）硬件决定，仅记录。
        _params.PulseWidthNs = Math.Max(0, widthNs);
        return Task.CompletedTask;
    }

    public Task SetPrfAsync(float prfHz)
    {
        // §2.2 #2：范围由设备 limitLo/limitHi 决定；未连接回退 PL01 0~5000 Hz（0=单次触发）
        _params.PrfHz = Math.Clamp(prfHz, _prfMinHz, _prfMaxHz);
        SdkSetDouble(_pulserHandle, JsrNative.JSR_ID_PulserPRF, _params.PrfHz, $"SetPRF({_params.PrfHz}Hz)");
        return Task.CompletedTask;
    }

    public Task SetModeAsync(PulseMode mode)
    {
        _params.Mode = mode;
        // H-1 修复：PulseEcho（自发自收）与 Through（一发一收）都需要 DPR500 自主 PRF 发射——
        // 触发源必须为 INTERNAL（TRIG/SYNC 输出同步脉冲给 Spectrum 专用 EXT0）。
        // 原映射 PulseEcho→External 是错误反转：External 使 TRIG/SYNC 变输入等待外部脉冲，
        // 而项目无外部脉冲源接 DPR500 → 永不发射 → 采集零数据（审查报告 H-1）。
        SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerSource,
            JsrNative.JSR_TRIGGER_INTERNAL,
            $"SetMode({mode}) → TriggerSource(INTERNAL)");

        SdkSetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverSignalSelect,
            mode == PulseMode.ThroughTransmission ? JsrNative.JSR_SIGNAL_SELECT_THROUGH : JsrNative.JSR_SIGNAL_SELECT_TR_ECHO,
            $"SetSignalSelect({mode})");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    //  扩展控制 API（§2.2 #6/#7、§2.3 #16/#17 整改新增）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 设置触发源。source: 0=Internal, 1=External, 2=Slave（双通道级联同步，
    /// 仅双脉冲器 DPR500 支持，运行时探测；两通道不可同时为 Slave —— §11.7）。
    /// </summary>
    public Task<bool> SetTriggerSourceAsync(int source)
    {
        if (source == JsrNative.JSR_TRIGGER_SLAVE && !_info.SupportsSlaveTrigger)
        {
            Debug.WriteLine("[DPR500/SDK] 本配置不支持 SLAVE 触发源（需双通道 DPR500）");
            return Task.FromResult(false);
        }
        bool ok = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerSource, source, $"TriggerSource({source})");
        return Task.FromResult(ok);
    }

    /// <summary>
    /// 设置接收器信号选择。select: 0=T/R Echo, 1=Through, 2=Both（双工模式，
    /// 仅部分仪器支持，运行时探测）。
    /// </summary>
    public Task<bool> SetSignalSelectAsync(int select)
    {
        if (select == JsrNative.JSR_SIGNAL_SELECT_BOTH && !_info.SupportsBothSignalSelect)
        {
            Debug.WriteLine("[DPR500/SDK] 本配置不支持 BOTH 信号选择（双工）");
            return Task.FromResult(false);
        }
        bool ok = SdkSetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverSignalSelect, select, $"SignalSelect({select})");
        return Task.FromResult(ok);
    }

    /// <summary>
    /// 设置外部触发边沿（§2.3 #16）。DPR500 说明书规定外触发为 3-5V 正脉冲上升沿，
    /// 部分仪器支持下降沿。
    /// </summary>
    public Task SetTriggerEdgeAsync(bool risingEdge)
    {
        SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerEdge,
            risingEdge ? JsrNative.JSR_TRIGGER_EDGE_RISING : JsrNative.JSR_TRIGGER_EDGE_FALLING,
            $"TriggerEdge({(risingEdge ? "上升沿" : "下降沿")})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 设置外部触发输入阻抗索引（§2.3 #16）。可用档位见 InstrumentInfo.ExtTriggerZOhms。
    /// </summary>
    public Task<bool> SetExternalTriggerImpedanceAsync(int index)
    {
        if (_info.ExtTriggerZOhms.Length > 0 && (index < 0 || index >= _info.ExtTriggerZOhms.Length))
        {
            Debug.WriteLine($"[DPR500/SDK] 外触发阻抗索引越界: {index}（可用 0~{_info.ExtTriggerZOhms.Length - 1}）");
            return Task.FromResult(false);
        }
        bool ok = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserExtTriggerZIndex, index, $"ExtTriggerZ(idx={index})");
        return Task.FromResult(ok);
    }

    /// <summary>
    /// 脉冲器 LED 识别模式（§2.3 #15）：identify=true 常亮用于机内识别板卡，
    /// false 恢复脉冲活动闪烁（默认）。
    /// </summary>
    public Task SetPulserLedIdentifyAsync(bool identify)
    {
        SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserLEDControl,
            identify ? JsrNative.JSR_LED_IDENTIFY_BOARD : JsrNative.JSR_LED_PULSE_ACTIVITY,
            $"PulserLED({(identify ? "常亮" : "闪烁")})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 电源 LED 闪烁速率（§2.3 #15）。rate: 0/25/200/254=闪烁速率档，255=常亮。
    /// </summary>
    public Task SetPowerLedBlinkRateAsync(int rate)
    {
        SdkSetInt32(_instrumentHandle, JsrNative.JSR_ID_InstrumentPowerLEDControl,
            Math.Clamp(rate, 0, 255), $"PowerLED({rate})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取诊断快照（§2.3 #10 自检/诊断 + #9 功率监测）。
    /// PowerLimitStatus 非 OK 表示电压/能量/PRF 组合超出高压电源功率极限
    /// （脉冲幅度会下垂，应降低参数或参考 PowerLimitMax* 建议值）。
    /// </summary>
    public Dpr500Diagnostics GetDiagnostics()
    {
        var d = new Dpr500Diagnostics();
        if (!_isConnected) return d;

        lock (_sdkLock)
        {
            var ibuf = new int[1];
            if (JsrNative.JSR_GetInt32(JsrNative.JSR_LIBRARY_HANDLE, JsrNative.JSR_ID_LibraryDriversStatus, 1, ibuf) == JsrNative.JSR_OK)
                d.LibraryDriversStatus = ibuf[0];
            if (JsrNative.JSR_GetInt32(_instrumentHandle, JsrNative.JSR_ID_InstrumentConnectStatus, 1, ibuf) == JsrNative.JSR_OK)
            {
                d.InstrumentConnectStatus = ibuf[0];
                d.ConnectionStatusValid = true;
            }
            if (JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserPowerLimitStatus, 1, ibuf) == JsrNative.JSR_OK)
            {
                d.PulserPowerLimitStatus = ibuf[0];
                d.PowerLimitStatusValid = true;
            }
            if (JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserIsPulsing, 1, ibuf) == JsrNative.JSR_OK)
            {
                d.IsPulsing = ibuf[0] == JsrNative.JSR_TRUE;
                d.PulsingStatusValid = true;
            }

            var dbuf = new double[1];
            if (JsrNative.JSR_GetDouble(_pulserHandle, JsrNative.JSR_ID_PulserEnergyPerPulse, 1, dbuf) == JsrNative.JSR_OK)
                d.EnergyPerPulseUj = dbuf[0];
            if (JsrNative.JSR_GetDouble(_pulserHandle, JsrNative.JSR_ID_PulserPowerLimitPRF, 1, dbuf) == JsrNative.JSR_OK)
                d.PowerLimitMaxPrfHz = dbuf[0];
            if (JsrNative.JSR_GetDouble(_pulserHandle, JsrNative.JSR_ID_PulserPowerLimitVolts, 1, dbuf) == JsrNative.JSR_OK)
                d.PowerLimitMaxVolts = dbuf[0];
            if (JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserPowerLimitEnergyIndex, 1, ibuf) == JsrNative.JSR_OK)
                d.PowerLimitMaxEnergyIndex = ibuf[0];
        }

        _params.EnergyPerPulseUj = (float)d.EnergyPerPulseUj;
        return d;
    }

    // ═══════════════════════════════════════════════════════════════
    //  批量参数下发
    // ═══════════════════════════════════════════════════════════════

    public Task ApplyParamsAsync(PulseParams p)
    {
        // P5-FIX：记录改动前的当前生效值（供前后对比；首次应用时 _appliedGainDb 为 NaN）
        LogEvent?.Invoke(this,
            $"[INFO] 参数应用前: 增益={(float.IsNaN(_appliedGainDb) ? "未设" : _appliedGainDb.ToString("F1"))}dB " +
            $"LP idx={(_appliedLpIndex < 0 ? "未设" : _appliedLpIndex.ToString())} " +
            $"HP idx={(_appliedHpIndex < 0 ? "未设" : _appliedHpIndex.ToString())} " +
            $"→ 目标: 增益={p.GainDb:F1}dB 低通={p.LowPassHz / 1e6f:F1}MHz 高通={p.HighPassHz / 1e6f:F1}MHz " +
            $"PRF={p.PrfHz:F0}Hz 电压={p.Voltage:F0}V 能量={p.EnergyLevel} 阻尼={p.Damping} 触发={(p.TriggerMode == TriggerMode.Internal ? "内" : "外")}");

        // 更新内部参数（含动态范围 clamp）
        CopyParams(p);

        if (!_isConnected)
        {
            Debug.WriteLine("[DPR500/SDK] 未连接，仅更新内部参数");
            return Task.CompletedTask;
        }

        lock (_sdkLock)
        {
            // RH-6 修复：安全顺序——先禁用输出并确认 IsPulsing=false，再修改高压参数。
            // 原实现先写 PRF/电压/能量，最后才写 TriggerEnable=FALSE——设备原本正在发射时
            // 参数在发射状态下变化，可能产生瞬态异常高压脉冲或探头冲击。
            if (_params.Enabled)
            {
                if (!DisablePulsing())
                {
                    throw new InvalidOperationException(
                        "DPR500 关断确认失败（IsPulsing 仍为 true 或 TriggerEnable 写入失败），" +
                        "无法安全修改参数。请检查连接后重试");
                }
            }

            // M-4 修复：Receiver + Pulser 全部写入纳入统一 ok 累积，任一失败即禁用脉冲并抛异常。
            // 收集失败属性名和 status，避免 ok &= 只得到总布尔值而丢失诊断。
            bool ok = true;
            var failures = new List<string>();

            // P4-A：仅在目标值真正变化时写滤波器/增益寄存器。
            // 关键缺陷修复——原实现每帧都重写 LP/HP 滤波器，导致"仅改增益"的应用也会
            // 无意触发滤波器写入；若设备列表与 UI 下拉的映射不一致（FindNearestListIndex
            // 就近选取），会改动模拟带宽 → 相位响应变化 → 波峰时域位置偏移（增益-位置耦合）。
            // 现在用记录值做变更检测，未变化时跳过写入，增益调节不再扰动滤波器/波峰位置。
            // 增益本身仍每次按需写入（增益是幅值调节的唯一来源，须精确生效）。

            // ── 接收器增益（幅值调节）──
            if (float.IsNaN(_appliedGainDb) || Math.Abs(_appliedGainDb - _params.GainDb) > 1e-3f)
            {
                bool receiverGain = SdkSetDouble(_receiverHandle, JsrNative.JSR_ID_ReceiverGainDB, _params.GainDb, $"ReceiverGain({_params.GainDb}dB)");
                ok &= receiverGain; if (!receiverGain) failures.Add("ReceiverGainDB");
                _appliedGainDb = _params.GainDb;
            }

            // ── 接收器低通滤波器（仅变化时写入）──
            int lpIndex = FindNearestListIndex(_params.LowPassHz / 1e6f, _info.LowPassMHz,
                i => Dpr500Protocol.LowPassMHz[Math.Clamp(i, 0, Dpr500Protocol.LowPassMHz.Length - 1)],
                Dpr500Protocol.LowPassMHz.Length, Dpr500Protocol.FindNearestLowPassIndex(_params.LowPassHz));
            if (lpIndex != _appliedLpIndex)
            {
                bool lp = SdkSetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverLPFilterIndex, lpIndex, $"LPFilter(idx={lpIndex})");
                ok &= lp; if (!lp) failures.Add("ReceiverLPFilterIndex");
                _appliedLpIndex = lpIndex;
            }

            // ── 接收器高通滤波器（仅变化时写入）──
            int hpIndex = FindNearestListIndex(_params.HighPassHz / 1e6f, _info.HighPassMHz,
                i => Dpr500Protocol.HighPassMHz[Math.Clamp(i, 0, Dpr500Protocol.HighPassMHz.Length - 1)],
                Dpr500Protocol.HighPassMHz.Length, Dpr500Protocol.FindNearestHighPassIndex(_params.HighPassHz));
            if (hpIndex != _appliedHpIndex)
            {
                bool hp = SdkSetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverHPFilterIndex, hpIndex, $"HPFilter(idx={hpIndex})");
                ok &= hp; if (!hp) failures.Add("ReceiverHPFilterIndex");
                _appliedHpIndex = hpIndex;
            }

            // 信号选择（Echo/Through，双工经 SetSignalSelectAsync(2) 单独设置）
            bool sig = SdkSetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverSignalSelect,
                _params.Mode == PulseMode.ThroughTransmission
                    ? JsrNative.JSR_SIGNAL_SELECT_THROUGH : JsrNative.JSR_SIGNAL_SELECT_TR_ECHO,
                "SignalSelect");
            ok &= sig; if (!sig) failures.Add("ReceiverSignalSelect");

            // ── Pulser 参数 ──
            bool prf = SdkSetDouble(_pulserHandle, JsrNative.JSR_ID_PulserPRF, _params.PrfHz, $"PulserPRF({_params.PrfHz}Hz)");
            ok &= prf; if (!prf) failures.Add("PulserPRF");
            bool volts = SdkSetDouble(_pulserHandle, JsrNative.JSR_ID_PulserVolts, _params.Voltage, $"PulserVolts({_params.Voltage}V)");
            ok &= volts; if (!volts) failures.Add("PulserVolts");
            bool damp = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserDampResistorIndex, (int)_params.Damping, $"DampResistor(idx={(int)_params.Damping})");
            ok &= damp; if (!damp) failures.Add("PulserDampResistorIndex");
            bool energy = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserEnergyIndex,
                Math.Clamp(_params.EnergyLevel - 1, 0, 3), $"EnergyIndex(idx={Math.Clamp(_params.EnergyLevel - 1, 0, 3)})");
            ok &= energy; if (!energy) failures.Add("PulserEnergyIndex");
            bool trigSrc = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerSource,
                _params.TriggerMode == TriggerMode.Internal ? JsrNative.JSR_TRIGGER_INTERNAL : JsrNative.JSR_TRIGGER_EXTERNAL,
                "TriggerSource");
            ok &= trigSrc; if (!trigSrc) failures.Add("PulserTriggerSource");
            // NH-3 修复：参数应用与脉冲启用分离——应用参数时**始终** TriggerEnable=FALSE。
            // 原实现按 _params.Enabled（默认 true）直接开启高压脉冲，用户仅点"应用参数"就可能发射。
            // 写入完成并经功率检查通过后，由独立的 SetOutputEnabledAsync(true) 显式启用输出。
            bool trigEn = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerEnable,
                JsrNative.JSR_FALSE, $"TriggerEnable(FALSE-应用参数不启动发射)");
            ok &= trigEn; if (!trigEn) failures.Add("PulserTriggerEnable");
            _params.Enabled = false;
            if (!ok)
            {
                // M-4/H-7：任一关键写入失败 → 立即禁用脉冲并抛异常（收集失败属性名供诊断）
                bool disabled = DisablePulsing();
                throw new DprConfigurationException(
                    disabled
                        ? $"DPR500 参数下发失败，输出已关闭。失败属性: {string.Join(", ", failures)}"
                        : $"DPR500 参数下发失败且输出关断未确认。失败属性: {string.Join(", ", failures)}");
            }
        }

        // 功率极限检查（§11.16：设置 Volts/Energy/PRF 后应复查）
        var diag = GetDiagnostics();
        if (!diag.CanEnableOutput)
        {
            // 功率超限或关键诊断读取失败都按 fail-closed 处理：保持输出关闭，不把默认 0 当成安全状态。
            if (!DisablePulsing())
                Debug.WriteLine("[DPR500/SDK] 诊断异常后的关断确认失败（继续抛出异常）");
            throw new InvalidOperationException(diag.IsPowerLimitExceeded
                ? $"DPR500 功率超限（{diag.Describe()}），已禁用脉冲输出。请降低电压/能量/PRF 后重试"
                : "DPR500 关键诊断读取不完整，无法确认连接/功率/发射状态；输出保持关闭");
        }

        Debug.WriteLine($"[DPR500/SDK] 批量参数已下发（未启用输出）: Gain={_params.GainDb}dB, PRF={_params.PrfHz}Hz, " +
                      $"Volts={_params.Voltage}V, Damping={_params.Damping}, Energy={_params.EnergyLevel}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// NH-3：独立启用/禁用脉冲输出（应用参数后需显式调用启用才开始发射）。
    /// 启用前检查功率限制，超限拒绝开启。
    /// </summary>
    public Task<bool> SetOutputEnabledAsync(bool enable)
    {
        if (!_isConnected || _pulserHandle == 0) return Task.FromResult(false);
        lock (_sdkLock)
        {
            if (enable)
            {
                var diag = GetDiagnostics();
                if (!diag.CanEnableOutput)
                {
                    Debug.WriteLine($"[DPR500/SDK] 拒绝启用输出：{diag.Describe()}");
                    return Task.FromResult(false);
                }
            }
            bool ok = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerEnable,
                enable ? JsrNative.JSR_TRUE : JsrNative.JSR_FALSE, $"OutputEnable({enable})");
            if (ok)
            {
                _params.Enabled = enable;
                _state = enable ? DprState.Running : DprState.Ready;   // H-5：状态机
            }
            return Task.FromResult(ok);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  H-1：单次触发语义
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// H-1：DPR500 是否支持严格单次触发。DPR500 自身无软件单发 API——严格一点一脉冲
    /// 需切换为 External 触发源，由 ZMC 数字输出口产生单个边沿驱动。本属性返回 true
    /// 表示已装备外触发模式，TriggerOnceAsync 由 ScanService 委派 ZMC.PulseTriggerOutputAsync。
    /// </summary>
    public bool SupportsSingleTrigger => _isConnected && _params.TriggerMode == TriggerMode.External;

    /// <summary>
    /// H-1：装备外触发模式——切换触发源为 External 并保持 TriggerEnable 使能，
    /// 实际发射由每个点位的单次外部边沿（ZMC 触发输出）决定。Spectrum 必须已进入外触发等待状态。
    /// </summary>
    public Task ArmExternalTriggerAsync(CancellationToken ct = default)
    {
        if (!_isConnected || _pulserHandle == 0)
            throw new InvalidOperationException("DPR500 未连接，无法装备外触发");

        lock (_sdkLock)
        {
            bool ok = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerSource,
                JsrNative.JSR_TRIGGER_EXTERNAL, "ArmExternalTrigger(TriggerSource=EXTERNAL)");
            if (!ok)
                throw new InvalidOperationException("DPR500 切换 External 触发源失败");
            _params.TriggerMode = TriggerMode.External;

            // TriggerEnable 保持使能——实际发射由外部边沿决定
            ok = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerEnable,
                JsrNative.JSR_TRUE, "ArmExternalTrigger(TriggerEnable=TRUE)");
            if (!ok)
                throw new InvalidOperationException("DPR500 TriggerEnable 使能失败");
            // L4-FIX（审查 20260828）：武装后发射实际已使能，必须同步 _params.Enabled/_state——
            // 否则扫查期间 UI"发射状态: 关闭"与实际高压发射相反（误导操作员）。
            _params.Enabled = true;
            _state = DprState.Running;
        }
        Debug.WriteLine("[DPR500/SDK] 已装备外触发模式（等单次外部边沿）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// H-1：产生一次硬件触发。DPR500 自身无软件单发 API——严格单发需由 ZMC 数字输出口产生边沿，
    /// 本方法抛 NotSupportedException 提示调用方应委派 IMotionController.PulseTriggerOutputAsync。
    /// 不得用软件延时包装 Internal PRF 后静默成功（无法保证脉冲数量）。
    /// </summary>
    public Task TriggerOnceAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "DPR500 无软件单发 API。严格单次触发需由 ZMC 数字输出口产生边沿——" +
            "ScanService 应调用 IMotionController.PulseTriggerOutputAsync，不得用软件延时包装 Internal PRF");

    /// <summary>
    /// H-1/H-5/H-6：禁用脉冲输出并确认 IsPulsing=false。比 SetOutputEnabledAsync(false) 更强：
    /// 写入 TriggerEnable=FALSE 后轮询回读 IsPulsing 直至确认关断或超时失败。
    /// L2-FIX（审查 20260828）：严格单次触发扫查结束时，若触发源仍为 External（扫查武装残留），
    /// 恢复 Internal 并同步 _params.TriggerMode——否则下一次"开始扫查"被 M-3 守卫拒绝，
    /// 且直接"启用发射"会停在 External 等待状态无自主 PRF。
    /// </summary>
    public Task<bool> DisableOutputAndConfirmAsync(CancellationToken ct = default)
    {
        if (!_isConnected || _pulserHandle == 0) return Task.FromResult(true);
        bool ok;
        lock (_sdkLock)
        {
            ok = DisablePulsing();
            // 恢复 Internal 触发源（仅当本次关断前为 External——扫查残留状态）
            if (ok && _params.TriggerMode == TriggerMode.External)
            {
                bool trigRestored = SdkSetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerSource,
                    JsrNative.JSR_TRIGGER_INTERNAL, "DisableOutputAndConfirm(TriggerSource=INTERNAL 恢复)");
                if (trigRestored)
                    _params.TriggerMode = TriggerMode.Internal;
                else
                    LogEvent?.Invoke(this, "[WARN] 触发源恢复 Internal 失败（下次扫查可能被触发拓扑守卫拒绝）");
            }
        }
        return Task.FromResult(ok);
    }

    /// <summary>优先用设备实际列表查找最接近索引；设备列表不可用时回退 RL01 静态表</summary>
    private static int FindNearestListIndex(float targetMHz, double[] deviceList,
        Func<int, float> fallbackTable, int fallbackLength, int fallbackIndex)
    {
        if (deviceList is { Length: > 0 })
        {
            int best = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < deviceList.Length; i++)
            {
                double diff = Math.Abs(deviceList[i] - targetMHz);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }
        return Math.Clamp(fallbackIndex, 0, fallbackLength - 1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  从硬件读取当前参数
    // ═══════════════════════════════════════════════════════════════

    public void ReadParamsFromHardware()
    {
        // 3-FIX：从硬件寄存器读取当前参数到 _params 缓存—— MainForm 回读按钮与
        // 应用参数后均先调此方法再刷新 UI，消除"UI 显示 200V 但硬件实际 275V"的缓存分叉。
        bool linkOk = true;
        lock (_sdkLock)
        {
            if (_receiverHandle != 0)
            {
                var gainBuf = new double[1];
                if (JsrNative.JSR_GetDouble(_receiverHandle, JsrNative.JSR_ID_ReceiverGainDB, 1, gainBuf) == JsrNative.JSR_OK)
                    _params.GainDb = (float)gainBuf[0];

                var lpBuf = new int[1];
                if (JsrNative.JSR_GetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverLPFilterIndex, 1, lpBuf) == JsrNative.JSR_OK)
                    _params.LowPassHz = ListValueAt(lpBuf[0], _info.LowPassMHz,
                        Dpr500Protocol.LowPassMHz) * 1e6f;

                var hpBuf = new int[1];
                if (JsrNative.JSR_GetInt32(_receiverHandle, JsrNative.JSR_ID_ReceiverHPFilterIndex, 1, hpBuf) == JsrNative.JSR_OK)
                    _params.HighPassHz = ListValueAt(hpBuf[0], _info.HighPassMHz,
                        Dpr500Protocol.HighPassMHz) * 1e6f;
            }

            if (_pulserHandle != 0)
            {
                var prfBuf = new double[1];
                if (JsrNative.JSR_GetDouble(_pulserHandle, JsrNative.JSR_ID_PulserPRF, 1, prfBuf) == JsrNative.JSR_OK)
                    _params.PrfHz = (float)prfBuf[0];

                var voltBuf = new double[1];
                if (JsrNative.JSR_GetDouble(_pulserHandle, JsrNative.JSR_ID_PulserVolts, 1, voltBuf) == JsrNative.JSR_OK)
                    _params.Voltage = (float)voltBuf[0];

                var dampBuf = new int[1];
                if (JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserDampResistorIndex, 1, dampBuf) == JsrNative.JSR_OK
                    && dampBuf[0] >= 0 && dampBuf[0] <= 3)
                    _params.Damping = (DampingSetting)dampBuf[0];

                var energyBuf = new int[1];
                if (JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserEnergyIndex, 1, energyBuf) == JsrNative.JSR_OK)
                    _params.EnergyLevel = energyBuf[0] + 1;

                var trigBuf = new int[1];
                if (JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserTriggerSource, 1, trigBuf) == JsrNative.JSR_OK)
                    _params.TriggerMode = trigBuf[0] == JsrNative.JSR_TRIGGER_INTERNAL
                        ? TriggerMode.Internal : TriggerMode.External;
                else
                    linkOk = false;

                var enabledBuf = new int[1];
                int enabledStatus = JsrNative.JSR_GetInt32(
                    _pulserHandle, JsrNative.JSR_ID_PulserTriggerEnable, 1, enabledBuf);
                var pulsingBuf = new int[1];
                int pulsingStatus = JsrNative.JSR_GetInt32(
                    _pulserHandle, JsrNative.JSR_ID_PulserIsPulsing, 1, pulsingBuf);
                if (enabledStatus == JsrNative.JSR_OK && pulsingStatus == JsrNative.JSR_OK)
                    _params.Enabled = enabledBuf[0] == JsrNative.JSR_TRUE
                        && pulsingBuf[0] == JsrNative.JSR_TRUE;
                else
                    linkOk = false;
            }

            // L-7：哨兵验证——链路异常时记录（原实现静默，现场难定位）
            if (!linkOk)
                Debug.WriteLine("[DPR500/SDK] 参数读回部分失败（通信可能不稳定），部分参数保持旧值");
        }
    }

    private static float ListValueAt(int index, double[] deviceList, float[] fallback)
    {
        if (deviceList is { Length: > 0 } && index >= 0 && index < deviceList.Length)
            return (float)deviceList[index];
        return fallback[Math.Clamp(index, 0, fallback.Length - 1)];
    }

    // ═══════════════════════════════════════════════════════════════
    //  内部辅助（含断连检测）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>SDK 写入 double 属性（带断连检测；未连接时静默跳过）</summary>
    private bool SdkSetDouble(int handle, int propId, double value, string op)
    {
        if (!_isConnected || handle == 0) return false;
        lock (_sdkLock)
        {
            int status = JsrNative.JSR_SetDouble(handle, propId, 1, new[] { value });
            return HandleStatus(status, op);
        }
    }

    /// <summary>SDK 写入 Int32 属性（带断连检测；未连接时静默跳过）</summary>
    private bool SdkSetInt32(int handle, int propId, int value, string op)
    {
        if (!_isConnected || handle == 0) return false;
        lock (_sdkLock)
        {
            int status = JsrNative.JSR_SetInt32(handle, propId, 1, new[] { value });
            return HandleStatus(status, op);
        }
    }

    /// <summary>状态处理：OK 返回真；断连类错误触发 ConnectionLost；其他错误记录</summary>
    private bool HandleStatus(int status, string op)
    {
        if (status == JsrNative.JSR_OK) return true;

        string errText = JsrNative.GetErrorString(status);
        LogEvent?.Invoke(this, $"[WARN] {op} 失败: {errText} (0x{status:X})");

        if (JsrNative.IsDisconnectError(status))
        {
            _isConnected = false;
            string msg = $"DPR500 通信中断: {errText}";
            LogEvent?.Invoke(this, $"[ERROR] ⚠ {msg}，可调用 ReconnectAsync 恢复");
            ConnectionLost?.Invoke(this, msg);
        }
        return false;
    }

    /// <summary>
    /// P3-FIX3d（现场 20260826）：读数组型属性的元信息，取真实元素数。
    /// JSR SDK 规则：JSR_GetInt32 的请求个数 &gt; 实际元素数时整次调用被拒
    /// （0x83F=JSR_FAIL_INCOUNT_TOO_HIGH）。任何句柄枚举调用前必须先按真实
    /// 元素数分配缓冲。读取失败返回 fallback（保持旧行为）。
    /// </summary>
    private static int ReadElementCount(int objectHandle, int propId, int fallback)
    {
        try
        {
            var info = new JsrNative.JsrAsciiInfoStruct();
            return JsrNative.JSR_GetAsciiInfo(objectHandle, propId, ref info) == JsrNative.JSR_OK
                && info.elementCount > 0
                    ? info.elementCount : fallback;
        }
        catch { return fallback; }
    }

    private bool OpenChannelObjects(int channelIndex)
    {
        _channelHandle = _channelHandles[channelIndex];
        int status = JsrNative.JSR_OpenObject(_channelHandle, JsrNative.JSR_CHANNEL_OPEN_DEFAULT);
        if (!JsrNative.CheckStatus(status, $"打开 Channel {_channelLetters[channelIndex]}"))
            return false;

        // Pulser——P3-FIX3d：每通道实际 1 个脉冲器，硬编码 2 同样触发 0x83F
        int pulserFound = ReadElementCount(_channelHandle, JsrNative.JSR_ID_ChannelPulserHandles, fallback: 1);
        var pulserBuf = new int[pulserFound];
        status = JsrNative.JSR_GetInt32(_channelHandle, JsrNative.JSR_ID_ChannelPulserHandles, pulserBuf.Length, pulserBuf);
        if (JsrNative.CheckStatus(status, "获取 Pulser 句柄") && pulserBuf[0] != JsrNative.JSR_INVALID_HANDLE)
        {
            _pulserHandle = pulserBuf[0];
            status = JsrNative.JSR_OpenObject(_pulserHandle, JsrNative.JSR_PULSER_OPEN_DEFAULT);
            // M-6 修复：Open 失败必须置句柄 0——否则最终 return 因句柄非零误判成功
            if (!JsrNative.CheckStatus(status, "打开 Pulser"))
                _pulserHandle = 0;
        }
        // Receiver——P3-FIX3d：同上，每通道实际 1 个接收器
        int recvFound = ReadElementCount(_channelHandle, JsrNative.JSR_ID_ChannelReceiverHandles, fallback: 1);
        var recvBuf = new int[recvFound];
        status = JsrNative.JSR_GetInt32(_channelHandle, JsrNative.JSR_ID_ChannelReceiverHandles, recvBuf.Length, recvBuf);
        if (JsrNative.CheckStatus(status, "获取 Receiver 句柄") && recvBuf[0] != JsrNative.JSR_INVALID_HANDLE)
        {
            _receiverHandle = recvBuf[0];
            status = JsrNative.JSR_OpenObject(_receiverHandle, JsrNative.JSR_RECEIVER_OPEN_DEFAULT);
            // M-6 修复：同上
            if (!JsrNative.CheckStatus(status, "打开 Receiver"))
                _receiverHandle = 0;
        }

        // NEW-M-4 修复：Pulser 或 Receiver 打开失败时，逆序回滚已成功打开的对象
        // （Channel → Pulser/Receiver → Instrument 顺序：先开 Channel，再开 Pulser/Receiver，
        //   失败时需按逆序关闭，否则下次连接可能遇到 "对象已打开" 错误或句柄泄漏）
        if (_pulserHandle == 0 || _receiverHandle == 0)
        {
            Debug.WriteLine($"[DPR500/SDK] 子对象打开不完整（Pulser={_pulserHandle}, Receiver={_receiverHandle}），执行回滚");
            if (_pulserHandle != 0)
            {
                try { JsrNative.JSR_CloseObject(_pulserHandle); } catch { }
                _pulserHandle = 0;
            }
            if (_receiverHandle != 0)
            {
                try { JsrNative.JSR_CloseObject(_receiverHandle); } catch { }
                _receiverHandle = 0;
            }
            // Channel 已打开，需关闭
            if (_channelHandle != 0)
            {
                try { JsrNative.JSR_CloseObject(_channelHandle); } catch { }
                _channelHandle = 0;
            }
            return false;
        }

        return true;
    }

    private void CopyParams(PulseParams src)
    {
        _params.Channel = Math.Clamp(src.Channel, 1, Math.Max(1, _channelHandles.Length));
        _params.PowerOn = src.PowerOn;
        _params.Mode = src.Mode;
        _params.GainDb = Math.Clamp(src.GainDb, _gainMinDb, _gainMaxDb);
        _params.PrfHz = Math.Clamp(src.PrfHz, _prfMinHz, _prfMaxHz);
        _params.Voltage = Math.Clamp(src.Voltage, _voltMin, _voltMax);
        _params.EnergyLevel = Math.Clamp(src.EnergyLevel, 1, 4);
        _params.TriggerMode = src.TriggerMode;
        _params.Damping = src.Damping;
        _params.Impedance = src.Impedance;
        _params.PulseWidthNs = Math.Max(0, src.PulseWidthNs);
        _params.LowPassHz = src.LowPassHz;
        _params.HighPassHz = src.HighPassHz;
        _params.Enabled = src.Enabled;
    }

    private void CleanupAll()
    {
        lock (_sdkLock)
        {
            // H-6 修复：关闭对象前先禁用 TriggerEnable 并确认 IsPulsing=false——
            // 防止 Close 仅释放软件对象而脉冲/高压仍在输出（探头持续激励风险）。
            if (!DisablePulsing())
            {
                Debug.WriteLine("[DPR500/SDK] 关断确认失败（继续关闭，高压状态可能未完全停止）");
                _state = DprState.FaultedUnsafe;   // H-5：尽力关闭但标记不安全
            }
            CleanupPulserReceiver();
            CleanupChannel();
            CleanupInstrument();
            CleanupLibrary();
            _channelHandles = Array.Empty<int>();
            _channelLetters = Array.Empty<string>();
            _isConnected = false;
            if (_state != DprState.FaultedUnsafe)
            {
                _state = DprState.Disconnected;
                _connectionKind = DprConnectionKind.Disconnected;
            }
        }
        Debug.WriteLine("[DPR500/SDK] 已断开");
    }

    /// <summary>H-5：逆序关闭 SDK 对象并检查每个 CloseObject 返回码（用于 DisconnectAsync 严格路径）。</summary>
    private void CloseObjectsInReverseOrderChecked()
    {
        CleanupPulserReceiver();
        CleanupChannel();
        CleanupInstrument();
        CleanupLibrary();
        _channelHandles = Array.Empty<int>();
        _channelLetters = Array.Empty<string>();
    }

    /// <summary>H-6/RH-7：显式禁用脉冲触发并轮询确认 IsPulsing=false（关断确认）。
    /// 写入失败或读到仍为 true 时返回 false（安全故障），调用方必须据此决定是否阻止后续操作。</summary>
    private bool DisablePulsing()
    {
        if (!_isConnected || _pulserHandle == 0) return true;
        try
        {
            // 1. 禁用触发（TriggerEnable=FALSE）——H-5：使用与厂商宏一致的 IsPass 判断
            int status = JsrNative.JSR_SetInt32(_pulserHandle,
                JsrNative.JSR_ID_PulserTriggerEnable, 1, new[] { JsrNative.JSR_FALSE });
            if (!JsrNative.IsPass(status))
            {
                // H-5：区分"通信已断，无法确认"与"设备明确回读仍在发射"——两者都不能标安全，但诊断信息不同
                bool commLost = JsrNative.IsDisconnectError(status);
                LogEvent?.Invoke(this, commLost
                    ? $"[WARN] 关断失败（通信已断，无法确认高压状态）: {JsrNative.GetErrorString(status)} (0x{status:X})"
                    : $"[WARN] 关闭脉冲触发失败: {JsrNative.GetErrorString(status)} (0x{status:X})");
                return false;
            }

            // 2. 轮询确认 IsPulsing=false（审查 2026-08-25 P2：窗口加宽至 10×100ms=1s，
            //    单次读取失败重试一次再判通信断——串口偶发超时不应立即误报安全故障）
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(100);
                var buf = new int[1];
                int readStatus = JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserIsPulsing, 1, buf);
                if (readStatus != JsrNative.JSR_OK)
                {
                    // P2：读取失败先重试一次（50ms 后），连续失败才判"通信断，无法确认"
                    Thread.Sleep(50);
                    readStatus = JsrNative.JSR_GetInt32(_pulserHandle, JsrNative.JSR_ID_PulserIsPulsing, 1, buf);
                    if (readStatus != JsrNative.JSR_OK)
                    {
                        Debug.WriteLine($"[DPR500/SDK] 关断确认：IsPulsing 连续读取失败（通信断，无法确认高压状态）: {JsrNative.GetErrorString(readStatus)}");
                        return false;
                    }
                }

                if (buf[0] == JsrNative.JSR_FALSE)
                {
                    // L4-FIX：关断确认后同步内部状态——否则扫查结束/暂停后 UI 仍显示"发射中"
                    // （与 L4 ArmExternalTrigger 同步为对称修复）。
                    _params.Enabled = false;
                    if (_state == DprState.Running) _state = DprState.Ready;
                    Debug.WriteLine("[DPR500/SDK] 关断确认: IsPulsing=false（脉冲已完全停止）");
                    return true;
                }
                // H-5：设备明确回读仍在发射
                LogEvent?.Invoke(this, $"[INFO] 关断确认: IsPulsing={buf[0]}（设备回读仍在发射，重试 {i + 1}/10）");
            }
            LogEvent?.Invoke(this, "[WARN] 关断确认超时：IsPulsing 仍为 true（安全故障，设备明确回读仍在发射）");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DPR500/SDK] 关断异常: {ex.Message}");
            return false;
        }
    }

    private void CleanupPulserReceiver()
    {
        if (_pulserHandle != 0)
        {
            // RM-7 修复：记录 CloseObject 返回码——关闭失败可能表示硬件仍在发射或句柄已损坏，
            // 诊断时需区分"正常关闭"与"关闭失败"（真机故障注入测试的关键证据）。
            int rc = JsrNative.JSR_CloseObject(_pulserHandle);
            if (rc != JsrNative.JSR_OK)
                Debug.WriteLine($"[DPR500/SDK] Close Pulser 返回: {JsrNative.GetErrorString(rc)} (0x{rc:X})");
            _pulserHandle = 0;
        }
        if (_receiverHandle != 0)
        {
            int rc = JsrNative.JSR_CloseObject(_receiverHandle);
            if (rc != JsrNative.JSR_OK)
                Debug.WriteLine($"[DPR500/SDK] Close Receiver 返回: {JsrNative.GetErrorString(rc)} (0x{rc:X})");
            _receiverHandle = 0;
        }
    }

    private void CleanupChannel()
    {
        if (_channelHandle != 0)
        {
            int rc = JsrNative.JSR_CloseObject(_channelHandle);
            if (rc != JsrNative.JSR_OK)
                Debug.WriteLine($"[DPR500/SDK] Close Channel 返回: {JsrNative.GetErrorString(rc)} (0x{rc:X})");
            _channelHandle = 0;
        }
    }

    private void CleanupInstrument()
    {
        if (_instrumentHandle != 0)
        {
            int rc = JsrNative.JSR_CloseObject(_instrumentHandle);
            if (rc != JsrNative.JSR_OK)
                Debug.WriteLine($"[DPR500/SDK] Close Instrument 返回: {JsrNative.GetErrorString(rc)} (0x{rc:X})");
            _instrumentHandle = 0;
        }
    }

    private void CleanupLibrary()
    {
        if (_libraryOpen)
        {
            int rc = JsrNative.JSR_CloseLibrary();
            if (rc != JsrNative.JSR_OK)
                Debug.WriteLine($"[DPR500/SDK] CloseLibrary 返回: {JsrNative.GetErrorString(rc)} (0x{rc:X})");
            _libraryOpen = false;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            // RM-6 修复：仅从 Dispose() 调用时执行完整 CleanupAll（含锁、脉冲关断、对象关闭）。
            // 终结器线程不执行这些——防死锁和与 SDK 线程竞争。
            CleanupAll();
            _state = DprState.Disposed;   // H-5：生命周期状态机
        }
        // RM-6：终结器（disposing=false）仅关闭已打开的 JSR 对象句柄（非托管资源）。
        // 不执行 TriggerEnable=FALSE、IsPulsing 轮询等托管同步操作。
        if (_instrumentHandle != 0)
        {
            try { JsrNative.JSR_CloseObject(_instrumentHandle); } catch { }
            _instrumentHandle = 0;
        }
        if (_pulserHandle != 0)
        {
            try { JsrNative.JSR_CloseObject(_pulserHandle); } catch { }
            _pulserHandle = 0;
        }
        if (_receiverHandle != 0)
        {
            try { JsrNative.JSR_CloseObject(_receiverHandle); } catch { }
            _receiverHandle = 0;
        }
        if (_channelHandle != 0)
        {
            try { JsrNative.JSR_CloseObject(_channelHandle); } catch { }
            _channelHandle = 0;
        }
    }

    // RM-6：Finalizer 仅兜底关闭 JSR 对象句柄（P/Invoke 终结器安全）。
    // 完整关闭（脉冲关断确认、对象逆序关闭）由 Dispose 负责。
    ~Dpr500Controller() => Dispose(false);
}
