using System.Diagnostics;
using System.Threading;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Hardware.Zmc;

/// <summary>
/// ZMC 运动控制器真实实现（众为兴 zauxdll.dll）。
/// 实现说明：
/// - 以太网连接（ZAux_OpenEth），IP 来自 ConnectionConfig.IpAddress。
/// - 轴使能通过说明书定义的输出口（X=OP0, Y1=OP3, Y2=OP4, Z=OP10, A=OP11, B=OP12），见 AxisTable.EnableOutputs。
/// - 位置通过 ZAux_Direct_GetMpos（编码器测量反馈，说明书 §5.2.2）轮询，50ms 定时器推送 PositionChanged。
/// - 运动：连接时已写 UNITS；运动仅写 Speed/Accel/Decel，再 Singl_MoveAbs / Singl_Move。
/// - 运动到位判断：GetIfIdle 输出非零（官方手册：运动中 0、结束非零）。
///
/// 未连接语义约定（审查报告 M-3，测试锁定）：
/// - EnableAxisAsync/DisableAxisAsync：返回 Task&lt;bool&gt;（false=未连接/失败，bool 契约天然表达尝试结果）
/// - StopAsync/EmergencyStopAsync：安全操作，未连接静默成功（防误报，测试锁定）
/// - MoveAbsoluteAsync/MoveRelativeAsync/HomeAsync：影响数据正确性，未连接抛 ZmcException（P1-1 锁定）
/// </summary>
public sealed class ZmcMotionController : IMotionController
{
    // H-2 修复：非线程安全 DLL 的串行化边界——所有原生调用必须通过此锁。
    // Timer 回调、UI 手动运动、扫查运动、关闭流程并发访问 _handle 的竞态由此消除。
    private readonly object _nativeLock = new();

    private IntPtr _handle = IntPtr.Zero;
    private bool _isConnected;
    private readonly bool[] _axisEnabled = new bool[5];
    // 每轴 MPOS（编码器测量位置）缓存，用于轮询事件；单位=该轴工程单位（mm/°）
    private readonly float[] _positions = new float[5];
    private readonly int[] _lastAxisStatus = new int[5];
    private readonly System.Timers.Timer _pollTimer;
    // M-2：轮询回调重入标志（Timer 回调可能重入，避免并发轮询同一句柄）
    private volatile bool _polling;
    // L-1：状态查询失败限频记录的时间戳（避免轮询路径日志爆炸）
    private long _lastStatusErrTick;
    // 关闭中标志：阻止关闭后新调用进入原生层
    private volatile bool _closing;

    /// <summary>
    /// 每轴硬件参数表（唯一权威来源）。<para/>
    /// 说明书固定值（§5.2.1 UNITS、§5.1.2 OP 使能口）已按说明书填写；
    /// 标「现场确认」的字段（物理轴号、软限位行程、原点/限位输入号、回零方向）在真机联调前必须由
    /// 电气点表/百分表实测确认，当前仅使用保守默认值，不能视为最终行程。
    /// </summary>
    private sealed record AxisHardware(
        AxisId LogicalAxis,
        int ControllerAxis,      // 现场确认：逻辑轴 → ZMC206 物理轴号（当前按序号假定）
        float Units,             // 说明书 §5.2.1：脉冲/单位
        string DisplayUnit,      // "mm" / "°"
        float ForwardSoftLimit,  // 现场确认：正向软限位（工程单位）
        float ReverseSoftLimit,  // 现场确认：负向软限位（工程单位）
        int DatumIn,             // 现场确认：原点输入号（-1 未配置）
        int FwdIn,               // 现场确认：正限位输入号（-1 未配置）
        int RevIn,               // 现场确认：负限位输入号（-1 未配置）
        int HomeMode,            // 3/4：普通原点开关回零（3=SPEED正向找开关、4=反向，方向现场确认）
        int[] EnableOutputs);    // 说明书 §5.1.2 OP 使能口映射

    private static readonly AxisHardware[] AxisTable =
    {
        // 逻辑轴 物理轴 Units         单位  +软限  -软限  原点 正限 负限 回零模式  使能OP
        new(AxisId.X,  0, 2000f,        "mm",  300f, -300f,  2,  1,  2,  3, new[] { 0 }),    // X=OP0
        new(AxisId.Y,  1, 2000f,        "mm",  300f, -300f,  5,  4,  5,  3, new[] { 3, 4 }), // Y1=OP3 + Y2=OP4 双驱
        new(AxisId.Z,  2, 1000f,        "mm",  150f, -150f,  8,  7,  8,  3, new[] { 10 }),   // Z=OP10
        new(AxisId.W1, 3, 10000f/360f,  "°",   180f, -180f, -1, -1, -1, 3, new[] { 11 }),   // A=OP11
        new(AxisId.W2, 4, 10000f/360f,  "°",   180f, -180f, -1, -1, -1, 3, new[] { 12 }),   // B=OP12
    };

    // 连接默认速度/加速度（工程单位，保守值，仅作安全兜底；实际运动由 ScanParams 覆盖）
    private const float ConnectDefaultSpeed = 100f;
    private const float ConnectDefaultAccel = 500f;

    // 回零速度（工程单位：mm/s 或 °/s，现场确认）
    private const float HomeSpeed = 30f;
    private const float CreepSpeed = 5f;

    public bool IsConnected => _isConnected;

    /// <summary>最后一次连接失败的详细错误（供 UI 诊断日志显示）</summary>
    public string? LastConnectError { get; private set; }

    public event EventHandler<AxisPositionChangedEventArgs>? PositionChanged;

    /// <summary>5-FIX（审查 20260828）：ZMC 通信中断时触发（UI 据此显示"通信中断"而非"未连接"）。</summary>
    public event EventHandler<string>? ConnectionLost;

    /// <summary>触发 ConnectionLost（仅当状态由连接→断开翻转一次，避免每帧重复触发）。</summary>
    private void RaiseConnectionLost(string reason)
    {
        if (!_isConnected) return;   // 已在断开态，不重复触发
        _isConnected = false;
        ConnectionLost?.Invoke(this, reason);
    }

    public ZmcMotionController()
    {
        _pollTimer = new System.Timers.Timer(50);
        // M-2：AutoReset + 重入标志——慢轮询时避免 Timer 回调重入并发访问句柄
        _pollTimer.AutoReset = true;
        _pollTimer.Elapsed += (_, _) =>
        {
            if (_polling) return;
            _polling = true;
            try { PollPositions(); }
            finally { _polling = false; }
        };
    }

    public Task<bool> ConnectAsync(ConnectionConfig config)
    {
        IntPtr newHandle = IntPtr.Zero;
        LastConnectError = null;
        try
        {
            // RH-1：旧句柄关闭必须与全部原生调用共用同一把锁（原锁外关闭与轮询并发）
            lock (_nativeLock)
            {
                // M-1：重复连接先关闭旧句柄并停轮询，避免句柄泄漏
                _pollTimer.Stop();
                if (_handle != IntPtr.Zero)
                {
                    try { ZmcNative.Close(_handle); } catch { /* 忽略旧句柄关闭失败 */ }
                    _handle = IntPtr.Zero;
                }
            }

            lock (_nativeLock)
            {
                // 连接方式：优先以太网（IP 非空）；IP 为空则回退串口（COM 口号取自 SerialPort，如 "COM3"→3）
                if (!string.IsNullOrWhiteSpace(config.IpAddress))
                {
                    int ret = ZmcNative.OpenEth(config.IpAddress, out newHandle);
                    ZmcNative.CheckError(ret, $"OpenEth({config.IpAddress})");
                }
                else
                {
                    int comId = ParseComNumber(config.SerialPort);
                    int ret = ZmcNative.OpenCom((uint)comId, out newHandle);
                    ZmcNative.CheckError(ret, $"OpenCom({config.SerialPort})");
                }

                // L-1：句柄有效性校验——返回码 0 但句柄无效（0/-1）时拒绝继续
                if (newHandle == IntPtr.Zero || newHandle == new IntPtr(-1))
                    throw new ZmcException($"连接失败：控制器返回无效句柄（{config.IpAddress ?? config.SerialPort}）");

                _handle = newHandle;

                // H-3 修复：安全初始化失败时不再提交连接状态——
                // 单位/限位映射/极性/软限位/轴类型任一失败都禁止使能与运动。
                // 先断开全部轴使能（按说明书 OP 映射，见 AxisTable）
                foreach (var hw in AxisTable)
                    foreach (var op in hw.EnableOutputs)
                        ZmcNative.CheckError(
                            ZmcNative.DirectSetOp(_handle, op, 0),
                            $"SetOp(OP{op}, DISABLE_ALL)");

                ApplySafetyInitialization();
                RestorePositionsFromVrf();

                _isConnected = true;
                // NEW-M-1：连接成功后复位关闭标志——断开后仍允许重新连接
                // （原实现 _closing 只置位不复位，DisconnectAsync 后任何重连都
                //  被 EnsureConnected 提前拒绝，且轮询不再运行）
                _closing = false;
            }
            _pollTimer.Start();
            Debug.WriteLine($"[ZMC] 已连接 {config.IpAddress}（安全初始化完成）");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LastConnectError = $"{ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine($"[ZMC] 连接失败: {ex.Message}");
            // M-1：连接提交前失败必须关闭新句柄，防句柄泄漏
            if (newHandle != IntPtr.Zero)
            {
                try { ZmcNative.Close(newHandle); } catch { }
                newHandle = IntPtr.Zero;
            }
            _handle = IntPtr.Zero;
            _isConnected = false;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 安全初始化序列（连接时执行一次）。
    /// 顺序（说明书 §9.5）：ATYPE → 每轴 UNITS（写+回读）→ 动态参数 → 原点/限位映射 → 软限位（工程单位）。
    /// 未执行此序列时 DATUM/FWD/REV 输入口未绑定、UNITS 未生效，回零与限位保护全部失效，有撞机风险。
    /// </summary>
    private void ApplySafetyInitialization()
    {
        for (int i = 0; i < AxisTable.Length; i++)
        {
            var hw = AxisTable[i];
            int ax = hw.ControllerAxis;

            ZmcNative.CheckError(ZmcNative.DirectSetAtype(_handle, ax, 1), $"ATYPE({ax})"); // 1 = 脉冲方向型

            // H-02：每轴独立 UNITS（说明书 §5.2.1），写入并回读校验
            WriteUnitsAndVerify(ax, hw.Units);

            ZmcNative.CheckError(ZmcNative.DirectSetDecelAngle(_handle, ax, 0.2618f), $"DECEL_ANGLE({ax})"); // 15°
            ZmcNative.CheckError(ZmcNative.DirectSetStopAngle(_handle, ax, 0.768f), $"STOP_ANGLE({ax})");     // 44°
            ZmcNative.CheckError(ZmcNative.DirectSetLspeed(_handle, ax, 0f), $"LSPEED({ax})");
            ZmcNative.CheckError(ZmcNative.DirectSetCornerMode(_handle, ax, 0), $"CORNER_MODE({ax})");

            ZmcNative.CheckError(ZmcNative.DirectSetSpeed(_handle, ax, ConnectDefaultSpeed), $"SPEED({ax})");
            ZmcNative.CheckError(ZmcNative.DirectSetAccel(_handle, ax, ConnectDefaultAccel), $"ACCEL({ax})");
            ZmcNative.CheckError(ZmcNative.DirectSetDecel(_handle, ax, ConnectDefaultAccel), $"DECEL({ax})");

            // 原点/限位输入映射（现场确认；-1 表示该轴未配置对应输入，跳过）
            if (hw.DatumIn >= 0) ZmcNative.CheckError(ZmcNative.DirectSetDatumIn(_handle, ax, hw.DatumIn), $"DATUM_IN({ax})");
            if (hw.FwdIn >= 0)   ZmcNative.CheckError(ZmcNative.DirectSetFwdIn(_handle, ax, hw.FwdIn), $"FWD_IN({ax})");
            if (hw.RevIn >= 0)   ZmcNative.CheckError(ZmcNative.DirectSetRevIn(_handle, ax, hw.RevIn), $"REV_IN({ax})");

            // H-03：软限位以工程单位（mm/°）写入，不再按脉冲数解释
            ZmcNative.CheckError(ZmcNative.DirectSetFsLimit(_handle, ax, hw.ForwardSoftLimit), $"FS_LIMIT({ax})");
            ZmcNative.CheckError(ZmcNative.DirectSetRsLimit(_handle, ax, hw.ReverseSoftLimit), $"RS_LIMIT({ax})");
        }

        // 输入极性：仅对已分配的输入设置（现场确认；示例批量 IN0~IN8 不反相不可照抄，见 H-08）
        for (int io = 0; io <= 8; io++)
            ZmcNative.CheckError(ZmcNative.DirectSetInvertIn(_handle, io, 0), $"INVERT_IN({io})");
    }

    /// <summary>写 UNITS 并回读校验，防止单位未生效导致速度/坐标错位（H-02）。</summary>
    private void WriteUnitsAndVerify(int controllerAxis, float units)
    {
        ZmcNative.CheckError(ZmcNative.DirectSetUnits(_handle, controllerAxis, units), $"UNITS({controllerAxis})");
        float readback = 0f;
        ZmcNative.CheckError(ZmcNative.DirectGetUnits(_handle, controllerAxis, ref readback), $"GetUnits({controllerAxis})");
        if (Math.Abs(readback - units) > 0.01f)
            throw new ZmcException($"UNITS({controllerAxis}) 回读 {readback} 与期望 {units} 不一致，连接中止");
    }

    /// <summary>
    /// 从 VR 寄存器（VRF 断电保持区）恢复 X/Y/Z/A/B 掉电前「需求位置」DPOS。
    /// H-05 修复：只恢复 DPOS，绝不写 MPOS——MPOS 是编码器测量反馈，必须反映真实机械位置，
    /// 不能把未经回零的历史坐标当作测量反馈（否则堵转/断电漂移后数据标到错误坐标）。
    /// 未掉电前保存过则恢复，否则跳过。
    /// </summary>
    private void RestorePositionsFromVrf()
    {
        for (int i = 0; i < AxisTable.Length; i++)
        {
            var hw = AxisTable[i];
            float vrfValue = 0f;
            int ret = ZmcNative.DirectGetVrf(_handle, i, 1, ref vrfValue);
            // 以工程单位软限位校验（H-03：不再用脉冲限 600000 比较）
            if (ret != 0 || Math.Abs(vrfValue) > Math.Abs(hw.ForwardSoftLimit) || vrfValue == 0f)
                continue; // VRF 从未写入或数值非法，跳过

            // 只写 DPOS；MPOS 由编码器反馈决定，不回写
            ZmcNative.CheckError(ZmcNative.DirectSetDpos(_handle, hw.ControllerAxis, vrfValue), $"Restore DPOS({hw.ControllerAxis})");
            Debug.WriteLine($"[ZMC] 轴{i} 需求位置自 VRF 恢复: {vrfValue}（MPOS 不回写，请回零建立基准）");
        }
    }

    /// <summary>将各轴当前「需求位置」DPOS 写入 VR0..4（断电后可恢复）。</summary>
    public void SavePositionsToVrf()
    {
        if (!_isConnected || _handle == IntPtr.Zero) return;
        lock (_nativeLock)
        {
            if (!_isConnected || _handle == IntPtr.Zero) return;
            for (int i = 0; i < AxisTable.Length; i++)
            {
                var hw = AxisTable[i];
                float dpos = 0f;
                int getRc = ZmcNative.DirectGetDpos(_handle, hw.ControllerAxis, ref dpos);
                if (getRc != 0)
                {
                    // M-7：某轴读取失败记录具体轴和返回码，不输出全局成功日志
                    Debug.WriteLine($"[ZMC] SavePositionsToVrf: GetDpos({hw.ControllerAxis}) 返回 {getRc}");
                    continue;
                }
                float v = dpos;
                ZmcNative.CheckError(ZmcNative.DirectSetVrf(_handle, i, 1, ref v), $"SaveVRF({i})");
            }
            Debug.WriteLine("[ZMC] 需求位置已写入 VRF（断电保持）");
        }
    }

    public Task DisconnectAsync()
    {
        // M-2/H-2：先置关闭标志阻止新调用进入，再在锁内执行关闭
        _closing = true;
        _pollTimer.Stop();
        lock (_nativeLock)
        {
            if (_handle != IntPtr.Zero)
            {
                // H-06：安全关机序列——急停并等待停止 → 保存 DPOS → 关闭全部使能 → 关闭句柄
                SafeShutdown();
                try { SavePositionsToVrf(); }
                catch { /* 忽略保存失败 */ }

                try
                {
                    int rc = ZmcNative.Close(_handle);
                    if (rc != 0) Debug.WriteLine($"[ZMC] 断开: ZAux_Close 返回 {rc}");
                }
                catch { /* 忽略 */ }
                _handle = IntPtr.Zero;
            }
            _isConnected = false;
        }
        // 5-FIX：断连后触发 ConnectionLost（UI 据此显示"通信中断"而非"未连接"）
        ConnectionLost?.Invoke(this, "ZMC 通信已断开");
        Debug.WriteLine("[ZMC] 已断开");
        return Task.CompletedTask;
    }

    public Task<bool> EnableAxisAsync(AxisId axis)
    {
        if (!EnsureConnected()) return Task.FromResult(false);
        lock (_nativeLock)
        {
            if (!EnsureConnected()) return Task.FromResult(false);   // RH-1：锁内复查，防断开竞态
            var hw = AxisTable[(int)axis];
            // H-01：多使能口（Y=OP3+OP4）必须原子化——任一口失败立即关闭已开的全部口并返回失败
            var opened = new List<int>(hw.EnableOutputs.Length);
            foreach (var op in hw.EnableOutputs)
            {
                try
                {
                    ZmcNative.CheckError(
                        ZmcNative.DirectSetOp(_handle, op, 1),
                        $"SetOp(OP{op}, ENABLE)");
                    opened.Add(op);
                }
                catch
                {
                    foreach (var o in opened)
                        try { ZmcNative.DirectSetOp(_handle, o, 0); } catch { }
                    _axisEnabled[(int)axis] = false;
                    Debug.WriteLine($"[ZMC] 轴 {axis} 使能失败，已回滚（OP{op}）");
                    return Task.FromResult(false);
                }
            }
            _axisEnabled[(int)axis] = true;
            Debug.WriteLine($"[ZMC] 轴 {axis} 已使能 ({string.Join(",", hw.EnableOutputs.Select(o => $"OP{o}"))})");
            return Task.FromResult(true);
        }
    }

    public Task<bool> DisableAxisAsync(AxisId axis)
    {
        if (!EnsureConnected()) return Task.FromResult(false);
        lock (_nativeLock)
        {
            if (!EnsureConnected()) return Task.FromResult(false);
            var hw = AxisTable[(int)axis];
            foreach (var op in hw.EnableOutputs)
            {
                try
                {
                    ZmcNative.CheckError(
                        ZmcNative.DirectSetOp(_handle, op, 0),
                        $"SetOp(OP{op}, DISABLE)");
                }
                catch { /* 关闭某路失败不阻止关闭其余路 */ }
            }
            _axisEnabled[(int)axis] = false;
            Debug.WriteLine($"[ZMC] 轴 {axis} 已禁用 ({string.Join(",", hw.EnableOutputs.Select(o => $"OP{o}"))})");
            return Task.FromResult(true);
        }
    }

    public Task MoveAbsoluteAsync(AxisId axis, float position, ScanParams parameters)
    {
        EnsureConnectedOrThrow($"MoveAbsolute(axis={axis}, pos={position})");
        lock (_nativeLock)
        {
            EnsureConnectedOrThrow($"MoveAbsolute(axis={axis}, pos={position})");   // RH-1：锁内复查
            int ax = AxisTable[(int)axis].ControllerAxis;
            ApplyMotionParams(ax, parameters);
            ZmcNative.CheckError(
                ZmcNative.DirectSinglMoveAbs(_handle, ax, position),
                $"Singl_MoveAbs(axis={ax}, pos={position})");
            Debug.WriteLine($"[ZMC] 绝对运动: 轴{axis} → {position} @ {parameters.Velocity}/s");
            return Task.CompletedTask;
        }
    }

    public Task MoveRelativeAsync(AxisId axis, float distance, ScanParams parameters)
    {
        EnsureConnectedOrThrow($"MoveRelative(axis={axis}, dist={distance})");
        lock (_nativeLock)
        {
            EnsureConnectedOrThrow($"MoveRelative(axis={axis}, dist={distance})");
            int ax = AxisTable[(int)axis].ControllerAxis;
            ApplyMotionParams(ax, parameters);
            ZmcNative.CheckError(
                ZmcNative.DirectSinglMove(_handle, ax, distance),
                $"Singl_Move(axis={ax}, dist={distance})");
            Debug.WriteLine($"[ZMC] 相对运动: 轴{axis} += {distance} @ {parameters.Velocity}/s");
            return Task.CompletedTask;
        }
    }

    public Task HomeAsync(AxisId axis)
    {
        EnsureConnectedOrThrow($"Home(axis={axis})");
        lock (_nativeLock)
        {
            EnsureConnectedOrThrow($"Home(axis={axis})");
            var hw = AxisTable[(int)axis];
            int ax = hw.ControllerAxis;

            // H-02/H-04：回零前确保该轴 UNITS 已写入（连接时已写，此处幂等兜底，防止直接回零时单位未初始化）
            WriteUnitsAndVerify(ax, hw.Units);

            // 回零速度：快速趋近 SPEED、慢速精找 CREEP（工程单位，现场确认）
            ZmcNative.CheckError(ZmcNative.DirectSetSpeed(_handle, ax, HomeSpeed), $"HOME_SPEED({axis})");
            // CREEP 是每轴参数，正确 BASIC 语法为 CREEP(axis)=value（旧实现 CREEP(ax,500) 语法错误，H-04）
            ZmcNative.ExecuteCommand(_handle, $"CREEP({ax})={CreepSpeed:F3}");

            // H-04：DATUM 命令格式修正为 DATUM(mode) AXIS(axis)（旧实现 DATUM(ax,2) 参数顺序错误）。
            // 普通原点开关 + ATYPE=1 使用 mode 3/4（方向现场确认）；mode 2 需编码器 Z 信号，禁止用于当前 ATYPE。
            string cmd = $"DATUM({hw.HomeMode}) AXIS({ax})";
            string response = ZmcNative.ExecuteCommand(_handle, cmd);
            if (response.StartsWith("?", StringComparison.Ordinal))
                throw new ZmcException($"DATUM(axis={axis}) 回零命令被控制器拒绝: {response}");

            // 等待回零完成（DATUM 是异步命令）：IDLE 非零 + 无报警；超时/报警则急停
            WaitHomeComplete(ax, axis);
            Debug.WriteLine($"[ZMC] 回零完成: 轴{axis}（mode={hw.HomeMode}, SPEED={HomeSpeed}, CREEP={CreepSpeed}）");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 等待回零完成：轮询 IDLE + AXISSTATUS，超时或出现故障位则急停并抛异常（H-04）。
    /// </summary>
    private void WaitHomeComplete(int controllerAxis, AxisId axis, int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            int idle = 0;
            ZmcNative.CheckError(ZmcNative.DirectGetIfIdle(_handle, controllerAxis, ref idle), $"GetIfIdle({axis})");

            int status = 0;
            if (ZmcNative.DirectGetAxisStatus(_handle, controllerAxis, ref status) == 0 && ZmcAxisStatus.IsFault(status))
            {
                ZmcNative.RapidStop(_handle, 2);
                throw new ZmcException($"轴{axis} 回零中检测到报警 (status=0x{status:X})，已急停");
            }

            if (idle != 0) return;
            Thread.Sleep(20);
        }
        ZmcNative.RapidStop(_handle, 2);
        throw new ZmcException($"轴{axis} 回零超时（{timeoutMs}ms）未完成，已急停");
    }

    public Task StopAsync(AxisId axis)
    {
        if (!EnsureConnected()) return Task.CompletedTask;
        lock (_nativeLock)
        {
            if (!EnsureConnected()) return Task.CompletedTask;   // RH-1：锁内复查
            int ax = AxisTable[(int)axis].ControllerAxis;
            // mode=2: 减速停止
            ZmcNative.CheckError(
                ZmcNative.DirectSinglCancel(_handle, ax, 2),
                $"Singl_Cancel(axis={ax})");
            Debug.WriteLine($"[ZMC] 停止: 轴{axis}");
            return Task.CompletedTask;
        }
    }

    public Task EmergencyStopAsync()
    {
        if (!EnsureConnected()) return Task.CompletedTask;
        lock (_nativeLock)
        {
            if (!EnsureConnected()) return Task.CompletedTask;   // RH-1：锁内复查
            // mode=2: 减速停止所有轴
            ZmcNative.CheckError(
                ZmcNative.RapidStop(_handle, 2),
                "RapidStop");
            // L-2 修复：急停后保存当前需求位置到 VRF（断电保持）。
            // 注：SavePositionsToVrf 内部再取 _nativeLock（Monitor 可重入），锁内调用安全。
            try { SavePositionsToVrf(); }
            catch { /* 保存失败不掩盖急停结果 */ }
            Debug.WriteLine("[ZMC] 急停（所有轴）");
            return Task.CompletedTask;
        }
    }

    /// <summary>当前位置 = 编码器测量反馈 MPOS（说明书 §5.2.2/§4(1)，H-05）。</summary>
    public float GetPosition(AxisId axis)
    {
        if (!_isConnected || _handle == IntPtr.Zero) return 0f;
        lock (_nativeLock)
        {
            if (!_isConnected || _handle == IntPtr.Zero) return 0f;   // RH-1：锁内复查
            return GetMeasuredPosition(axis);
        }
    }

    /// <summary>读取需求位置 DPOS（用于跟随误差/诊断，H-05）。</summary>
    public float GetDemandPosition(AxisId axis)
    {
        if (!_isConnected || _handle == IntPtr.Zero) return 0f;
        lock (_nativeLock)
        {
            if (!_isConnected || _handle == IntPtr.Zero) return 0f;
            int ax = AxisTable[(int)axis].ControllerAxis;
            float pos = 0f;
            if (ZmcNative.DirectGetDpos(_handle, ax, ref pos) == 0) return pos;
            return 0f;
        }
    }

    /// <summary>正向软限位（工程单位 mm/°）——扫查区域校验单一来源（H-03）。</summary>
    public float GetForwardSoftLimit(AxisId axis) => AxisTable[(int)axis].ForwardSoftLimit;

    /// <summary>负向软限位（工程单位 mm/°）。</summary>
    public float GetReverseSoftLimit(AxisId axis) => AxisTable[(int)axis].ReverseSoftLimit;

    /// <summary>读取测量反馈 MPOS（无连接检查，供内部在持锁/已连接路径调用，H-05）。</summary>
    private float GetMeasuredPosition(AxisId axis)
    {
        int ax = AxisTable[(int)axis].ControllerAxis;
        float pos = 0f;
        if (ZmcNative.DirectGetMpos(_handle, ax, ref pos) == 0)
        {
            _positions[(int)axis] = pos;   // 同步刷新缓存，供轮询事件使用
            return pos;
        }
        return _positions[(int)axis];
    }

    /// <summary>轴是否运动完毕（空闲）。H-12：官方手册运动结束返回非零，判 idle != 0。</summary>
    public bool IsAxisIdle(AxisId axis)
    {
        if (!_isConnected || _handle == IntPtr.Zero) return true;
        lock (_nativeLock)
        {
            if (!_isConnected || _handle == IntPtr.Zero) return true;   // RH-1：锁内复查
            int ax = AxisTable[(int)axis].ControllerAxis;
            int idle = 0;
            // P0-1：查询失败（通信断开等）必须抛异常而非默认返回，
            // 否则扫描服务的到位判定会把"查询失败"误判为"已到位"继续采数
            ZmcNative.CheckError(ZmcNative.DirectGetIfIdle(_handle, ax, ref idle), $"GetIfIdle({axis})");
            return idle != 0;
        }
    }

    /// <summary>
    /// 设置连续插补（MERGE=1）：相邻运动指令平滑衔接不停减速。
    /// 扫描开始前对 X/Y 轴开启，结束后关闭（与旧项目扫描逻辑一致）。
    /// </summary>
    public void SetContinuousInterpolation(AxisId axis, bool enable)
    {
        if (!EnsureConnected()) return;
        lock (_nativeLock)
        {
            if (!EnsureConnected()) return;   // RH-1：锁内复查
            int ax = AxisTable[(int)axis].ControllerAxis;
            ZmcNative.CheckError(
                ZmcNative.DirectSetMerge(_handle, ax, enable ? 1 : 0),
                $"SetMerge(axis={ax}, {enable})");
        }
    }

    /// <summary>P0-D：轴置零（DPOS/MPOS 同步写 0，说明书 4.5 定位起始点）</summary>
    public void SetPositionZero(AxisId axis)
    {
        if (!EnsureConnected()) return;
        lock (_nativeLock)
        {
            if (!EnsureConnected()) return;   // RH-1：锁内复查
            int ax = AxisTable[(int)axis].ControllerAxis;
            ZmcNative.CheckError(ZmcNative.DirectSetDpos(_handle, ax, 0f), $"SetDpos({ax},0)");
            ZmcNative.CheckError(ZmcNative.DirectSetMpos(_handle, ax, 0f), $"SetMpos({ax},0)");
            _positions[(int)axis] = 0f;
            Debug.WriteLine($"[ZMC] 轴{axis} 已置零");
        }
    }

    /// <summary>
    /// H-1：单次触发输出——从指定数字输出口产生单个高电平脉冲，驱动 DPR500 External Trigger Input。
    /// 高电平保持期间不持有 _nativeLock（否则阻塞急停），拉低操作在 finally 中确保释放。
    /// 两次 ZAux_Direct_SetOp 返回码均用 CheckError 检查。
    /// </summary>
    public async Task PulseTriggerOutputAsync(int io, int pulseWidthMs, CancellationToken ct = default)
    {
        if (pulseWidthMs < 1) pulseWidthMs = 1;

        IntPtr handle;
        lock (_nativeLock)
        {
            EnsureConnectedOrThrow($"PulseTriggerOutput(IO{io})");
            handle = _handle;
            ZmcNative.CheckError(ZmcNative.DirectSetOp(handle, io, 1), $"SetOp(IO{io},1)");
        }

        try
        {
            await Task.Delay(pulseWidthMs, ct);
        }
        finally
        {
            // L6-FIX（审查 20260828）：原实现检查 _handle（断连后已置零）→ 跳过拉低 → IO 残留高电平。
            // 改用延迟前捕获的 handle（仍有效），写失败忽略（断连后 IO 状态由硬件复位）。
            lock (_nativeLock)
            {
                if (handle != IntPtr.Zero)
                    ZmcNative.DirectSetOp(handle, io, 0);
            }
        }
    }

    /// <summary>读取轴状态原始字（ZAux_Direct_GetAxisStatus）。L-1：检查返回码，非 0 限频记录日志。</summary>
    public int GetAxisStatus(AxisId axis)
    {
        if (!_isConnected || _handle == IntPtr.Zero) return 0;
        lock (_nativeLock)
        {
            if (!_isConnected || _handle == IntPtr.Zero) return 0;   // RH-1：锁内复查
            int ax = AxisTable[(int)axis].ControllerAxis;
            int status = 0;
            int rc = ZmcNative.DirectGetAxisStatus(_handle, ax, ref status);
            if (rc != 0)
            {
                // L-1：轮询路径不适频繁抛异常，限频记录通信失败（不把失败与合法值 0 混在一起）
                if (Environment.TickCount64 - _lastStatusErrTick > 1000)
                {
                    Debug.WriteLine($"[ZMC] GetAxisStatus({axis}) 返回 {rc}（通信失败）");
                    _lastStatusErrTick = Environment.TickCount64;
                }
                return 0;
            }
            return status;
        }
    }

    /// <summary>读取插补运动缓冲区剩余空间（ZAux_Direct_GetRemain_LineBuffer，监控下位机缓冲占用）。L-1：检查返回码。</summary>
    public int GetRemainLineBuffer(AxisId axis)
    {
        if (!_isConnected || _handle == IntPtr.Zero) return 0;
        lock (_nativeLock)
        {
            if (!_isConnected || _handle == IntPtr.Zero) return 0;
            int ax = AxisTable[(int)axis].ControllerAxis;
            int remain = 0;
            int rc = ZmcNative.DirectGetRemainLineBuffer(_handle, ax, ref remain);
            if (rc != 0)
            {
                Debug.WriteLine($"[ZMC] GetRemainLineBuffer({axis}) 返回 {rc}（通信失败）");
                return 0;
            }
            return remain;
        }
    }

    // ── 内部方法 ──

    /// <summary>解析 "COM3" 形式串口名为数字 3</summary>
    private static int ParseComNumber(string serialPort)
    {
        // 审查 P2-11：解析失败回退 COM1 有连错串口风险，改为显式抛异常要求正确配置
        string digits = new string(serialPort.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out int com) || com <= 0)
            throw new ZmcException($"串口配置 '{serialPort}' 无法解析出 COM 号（示例：COM3）");
        return com;
    }

    /// <summary>
    /// H-02：运动参数只更新该轴 SPEED/ACCEL/DECEL，不再覆盖 UNITS（UNITS 已在连接时写入并回读）。
    /// </summary>
    private void ApplyMotionParams(int controllerAxis, ScanParams parameters)
    {
        ZmcNative.CheckError(ZmcNative.DirectSetSpeed(_handle, controllerAxis, parameters.Velocity), $"SPEED({controllerAxis})");
        ZmcNative.CheckError(ZmcNative.DirectSetAccel(_handle, controllerAxis, parameters.Acceleration), $"ACCEL({controllerAxis})");
        ZmcNative.CheckError(ZmcNative.DirectSetDecel(_handle, controllerAxis, parameters.Acceleration), $"DECEL({controllerAxis})");
    }

    private void PollPositions()
    {
        if (!_isConnected || _handle == IntPtr.Zero || _closing) return;
        lock (_nativeLock)   // H-2/M-2：轮询与所有原生调用共用串行化边界
        {
            if (!_isConnected || _handle == IntPtr.Zero || _closing) return;

            for (int i = 0; i < AxisTable.Length; i++)
            {
                int ax = AxisTable[i].ControllerAxis;

                // 轴状态监控（软限位超程报警，状态变化时输出）
                int status = 0;
                if (ZmcNative.DirectGetAxisStatus(_handle, ax, ref status) == 0 && status != _lastAxisStatus[i])
                {
                    _lastAxisStatus[i] = status;
                    if (ZmcAxisStatus.IsOverForwardSoftLimit(status))
                        Debug.WriteLine($"[ZMC] ⚠ 轴{i} 超出正向软限位 (status=0x{status:X})");
                    if (ZmcAxisStatus.IsOverReverseSoftLimit(status))
                        Debug.WriteLine($"[ZMC] ⚠ 轴{i} 超出负向软限位 (status=0x{status:X})");
                    if (ZmcAxisStatus.IsPaused(status))
                        Debug.WriteLine($"[ZMC] 轴{i} 进入暂停状态 (status=0x{status:X})");
                }

                // H-05：位置轮询读取 MPOS（编码器测量反馈），不再用 DPOS
                float pos = 0f;
                int ret = ZmcNative.DirectGetMpos(_handle, ax, ref pos);
                if (ret != 0) continue;

                if (Math.Abs(pos - _positions[i]) > 0.001f)
                {
                    _positions[i] = pos;
                    PositionChanged?.Invoke(this, new AxisPositionChangedEventArgs
                    {
                        Axis = (AxisId)i,
                        Position = pos,
                        Velocity = 0f // ZAux_GetCurSpeed 可扩展
                    });
                }
            }
        }
    }

    private bool EnsureConnected()
    {
        if (_isConnected && _handle != IntPtr.Zero && !_closing) return true;
        Debug.WriteLine("[ZMC] 操作失败：未连接");
        return false;
    }

    /// <summary>运动类操作前置守卫：未连接直接抛异常。
    /// 静默返回"成功"会让上层（ScanService）把数据标到从未到达的坐标（审查 P1-1）。</summary>
    private void EnsureConnectedOrThrow(string operation)
    {
        if (!_isConnected || _handle == IntPtr.Zero || _closing)
            throw new ZmcException($"{operation} 失败：运动控制器未连接");
    }

    /// <summary>
    /// H-06：安全关机序列——RAPIDSTOP(2) → 有界等待各轴 IDLE → 关闭全部使能输出。
    /// 每步独立容错，某路失败不阻止关闭其余输出。关闭句柄由调用方负责。
    /// </summary>
    private void SafeShutdown()
    {
        try { ZmcNative.RapidStop(_handle, 2); } catch { /* 急停失败不阻止继续关闭 */ }

        // 有界等待各物理轴停止（单轴 2s 上限，避免关闭被卡死）
        foreach (var hw in AxisTable)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000)
            {
                int idle = 0;
                if (ZmcNative.DirectGetIfIdle(_handle, hw.ControllerAxis, ref idle) != 0) break;
                if (idle != 0) break;
                Thread.Sleep(10);
            }
        }

        // 关闭全部使能输出（OP0/3/4/10/11/12）
        foreach (var hw in AxisTable)
            foreach (var op in hw.EnableOutputs)
                try { ZmcNative.DirectSetOp(_handle, op, 0); } catch { /* 忽略单路关闭失败 */ }
    }

    public void Dispose()
    {
        _closing = true;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        lock (_nativeLock)   // M-2：与轮询/运动调用共用锁，防关闭与原生调用并发
        {
            if (_handle != IntPtr.Zero)
            {
                // H-06：Dispose 时先安全关机（急停+关使能），再保存位置、关闭句柄
                try { if (_isConnected) SafeShutdown(); } catch { /* 关闭失败不阻止清理 */ }
                try { if (_isConnected) SavePositionsToVrf(); } catch { /* 保存失败不阻止清理 */ }
                try
                {
                    int rc = ZmcNative.Close(_handle);
                    if (rc != 0) Debug.WriteLine($"[ZMC] Dispose: ZAux_Close 返回 {rc}");
                }
                catch (Exception ex) { Debug.WriteLine($"[ZMC] Dispose: ZAux_Close 异常 {ex.Message}"); }
                _handle = IntPtr.Zero;
            }
            _isConnected = false;
        }
        GC.SuppressFinalize(this);
    }

    // RM-6：Finalizer 收窄——终结线程上不再执行锁/Timer/VRF/状态机等托管操作，
    // 只兜底关闭非托管句柄（P/Invoke 终结器安全）。完整关闭路径由 Dispose 负责。
    ~ZmcMotionController()
    {
        if (_handle != IntPtr.Zero)
        {
            try { ZmcNative.Close(_handle); } catch { /* 终结线程尽力而为 */ }
            _handle = IntPtr.Zero;
        }
    }
}
