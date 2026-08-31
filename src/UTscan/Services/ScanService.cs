using System.Diagnostics;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Services;

/// <summary>
/// 扫查调度服务
/// </summary>
public class ScanService : IScanEngine
{
    private readonly IMotionController _motion;
    private readonly IDataAcquisition _daq;
    private readonly IPulseGenerator? _pulse;   // H-8：脉冲发生器（可选注入，统一故障复位用）
    private CancellationTokenSource? _cts;
    private volatile bool _isPaused;

    private volatile bool _isScanning;
    private readonly object _scanLock = new();

    // ── 断点续扫（20260828）：手动/异常停止后从中断点恢复，数据不重复不丢失 ──
    private ScanRegion? _lastRegion;
    private ScanParams? _lastParams;
    private long _lastFrameBaseline;
    private float _lastSampleRate;
    private int _lastSampleCount;
    private int _lastResumeRow, _lastResumeCol;
    private bool _hadBreakpoint;
    public bool HasBreakpoint => _hadBreakpoint && !_isScanning;
    public float BreakpointPercent { get; private set; }

    /// <summary>到位判定：位置误差带（mm），超出视为运动被截断（限位/堵转）</summary>
    private const float PositionToleranceMm = 0.05f;

    /// <summary>到位判定：跟随误差带 |DPOS-MPOS|（mm），超出视为堵转/失步（现场确认）</summary>
    private const float FollowingErrorToleranceMm = 0.5f;

    /// <summary>到位判定：MPOS 到位后位置收敛等待窗口（ms）</summary>
    private const int PositionSettleMs = 200;

    /// <summary>伺服使能保留输出口（说明书 §5.1.2：X=OP0,Y1=OP3,Y2=OP4,Z=OP10,A=OP11,B=OP12），禁止用作触发输出（H-07）</summary>
    private static readonly int[] ReservedOutputs = { 0, 3, 4, 10, 11, 12 };

    public bool IsScanning => _isScanning;
    public event EventHandler<ScanProgressEventArgs>? ProgressChanged;
    public event EventHandler<PointDataReadyEventArgs>? PointDataReady;
    public event EventHandler<LineScanCompleteEventArgs>? LineScanComplete;

    public ScanService(IMotionController motion, IDataAcquisition daq, IPulseGenerator? pulse = null)
    {
        _motion = motion;
        _daq = daq;
        _pulse = pulse;

        // M-2：FIFO 溢出联动——采集卡连续溢出时中止当前扫查（防丢帧污染 C 扫成像）。
        // 真机 SpectrumDaqCard 触发；Mock 无此事件，不影响开发。
        if (daq is UTscan.Hardware.Daq.SpectrumDaqCard spectrum)
        {
            spectrum.OverrunDetected += (_, msg) =>
            {
                System.Diagnostics.Debug.WriteLine($"[ScanService] {msg}");
                StopAsync();   // 取消当前扫查（触发安全复位路径）
            };
        }
    }

    public async Task StartScanAsync(ScanRegion region, ScanParams parameters, CancellationToken ct)
    {
        // L1-FIX（审查 20260828）：原实现 `_isScanning = true` 后，前置守卫（Validate/ScanDataSize）
        // 与 ArmExternalTriggerAsync 抛异常时不复位 → 扫描永久锁死直至重启。
        // 现改为：入口即进入 try/finally 保护区，finally 按 ownsScan 判定本调用是否持有扫描后统一复位。
        // ownsScan=false（并发第二调用提前返回）时不得复位共享字段（属于在跑的扫描）。
        bool ownsScan = false;
        try
        {
            lock (_scanLock)
            {
                if (_isScanning) return;
                _isScanning = true;
                ownsScan = true;
            }

            // 前置守卫（审查 P1）：未连接的运动控制器 Move 会静默返回成功，
            // 扫描会在原地反复采数并把数据标到错误坐标——直接拒绝启动
            if (!_motion.IsConnected)
            {
                throw new InvalidOperationException("运动控制器未连接，无法开始扫查（请先在主界面建立连接）");
            }

            // NH-8 修复：完整硬件就绪检查——ZMC/DAQ/DPR 三设备就绪才允许下发第一条运动指令。
            // 原实现仅查 ZMC——采集卡未就绪/脉冲仪未连接时仍会运动，产生空数据或整段无效数据。
            if (!_daq.IsRunning)
            {
                throw new InvalidOperationException("采集卡未在采集（请先连接并启动连续采集），无法开始扫查");
            }
            if (_pulse != null && !_pulse.IsConnected)
            {
                throw new InvalidOperationException("DPR500 未连接（请先建立连接），无法开始扫查");
            }

            // P0-E（说明书 4.10 报警 1/2）：扫前防呆校验——超行程 / 超 16G 数据量
            ValidateScanRegion(region);
            ValidateScanDataSize(region, parameters);

            // M-3 修复：扫前触发拓扑校验——当前接线为 DPR500 Internal（自主 PRF，TRIG/SYNC 输出）→ Spectrum EXT0。
            // 若用户把 DPR500 设为 External（等外部脉冲），而现场无外部脉冲源，会相互等待导致无数据。
            if (_pulse != null && _pulse.Params.TriggerMode != TriggerMode.Internal)
            {
                throw new InvalidOperationException(
                    $"DPR500 触发模式为 {_pulse.Params.TriggerMode}，与当前接线（Internal 自主 PRF → Spectrum EXT0）不匹配。" +
                    $"请在脉冲面板将触发源设为'内部'后再开始扫查");
            }

            // 联动前置校验：PRF × 采样点数 vs DMA 带宽——PRF 过高或采样点数过多会导致 DMA 溢出丢帧。
            if (_pulse != null && _pulse.IsConnected)
            {
                float prf = _pulse.Params.PrfHz;
                int samples = _daq.GetCurrentData().PointCount > 0 ? _daq.GetCurrentData().PointCount : ConnectionConfig.DefaultSampleCount;
                // DMA 带宽估算：每帧 = samples × 2 字节/采样 × 通道数（保守取 2）。
                // PCIe x8 理论带宽 ~2 GB/s，实际可用约 800 MB/s；安全阈值取 500 MB/s。
                long frameBytes = (long)samples * 2 * 2;
                long dmaRateBytes = (long)(prf * frameBytes);
                const long safeDmaLimitBytes = 500_000_000L; // 500 MB/s
                if (dmaRateBytes > safeDmaLimitBytes)
                {
                    throw new InvalidOperationException(
                        $"DPR500 PRF({prf:F0}Hz) × 采样点数({samples}) × 4B/帧 = {dmaRateBytes / 1e6:F0} MB/s " +
                        $"超过 DMA 安全带宽 {safeDmaLimitBytes / 1e6:F0} MB/s。请降低 PRF 或减少采样点数");
                }
            }

            // H-1 修复：严格一点一脉冲。扫描启动时不再统一打开 Internal PRF（启用窗口内脉冲数量不确定）。
            // 改为判断是否具备严格单次触发能力：
            //   - 真机 + TriggerIo 已配置 → ArmExternalTriggerAsync（DPR500 切 External，每点由 ZMC 单次边沿触发）；
            //   - Mock 模式（无 ZMC 边沿能力）→ 回退 Internal PRF（开发演示，不满足严格时序，明确标记）；
            //   - 真机但 TriggerIo 未配置 → 拒绝启动（不能用软件延时包装 Internal PRF 冒充单发）。
            bool useStrictSingleTrigger = false;
            bool useInternalPrfFallback = false;
            if (_pulse != null)
            {
                bool isMock = _pulse is UTscan.Mock.MockPulseGenerator;
                // H-07：触发输出不得复用任何伺服使能保留口（OP0/3/4/10/11/12，说明书 §5.1.2）
                if (parameters.TriggerIo >= 0 && !isMock && ReservedOutputs.Contains(parameters.TriggerIo))
                {
                    throw new InvalidOperationException(
                        $"触发输出 IO{parameters.TriggerIo} 是伺服使能保留口，禁止复用（否则扫描中会反复开关使能导致失步/撞机）。" +
                        $"请按现场 I/O 点表配置专用触发输出。");
                }
                if (parameters.TriggerIo >= 0 && !isMock)
                {
                    // 真机严格单次触发：DPR500 装备 External 模式，由 ZMC 每点产生单次边沿
                    await _pulse.ArmExternalTriggerAsync(ct);
                    useStrictSingleTrigger = true;
                    // 验证 Spectrum 已进入外触发等待状态
                    if (!_daq.IsRunning)
                    {
                        throw new InvalidOperationException("Spectrum 未处于外触发等待状态，禁止启动严格单次触发扫描");
                    }
                }
                else if (isMock)
                {
                    // Mock 模式无 ZMC 边沿能力，回退 Internal PRF（仅开发演示，不满足严格一点一脉冲）
                    bool enabled = await _pulse.SetOutputEnabledAsync(true);
                    if (!enabled)
                    {
                        throw new InvalidOperationException("DPR500 脉冲输出启用失败（可能功率超限），扫查已拒绝启动");
                    }
                    useInternalPrfFallback = true;
                }
                else
                {
                    throw new InvalidOperationException(
                        "真机模式未配置单次触发 IO（ScanParams.TriggerIo）。严格一点一脉冲需由 ZMC 数字输出口产生" +
                        "单次边沿驱动 DPR500 External Trigger。请按现场接线配置 TriggerIo 后再启动（不得用软件延时包装 Internal PRF）");
                }
            }

        _isPaused = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // 记录本次扫查参数供断点续扫使用
            _lastRegion = region;
            _lastParams = parameters;
            _lastFrameBaseline = _daq.GetCurrentFrameCount();
            // 记录 DAQ 采样配置（续扫恢复硬件时用）
            var cur = _daq.GetCurrentData();
            if (cur is { SampleRate: > 0 })
            {
                _lastSampleRate = cur.SampleRate;
                _lastSampleCount = cur.PointCount;
            }

            // 连续插补（MERGE=1）：光栅扫描换行平滑衔接，避免每步减速停顿
            _motion.SetContinuousInterpolation(AxisId.X, true);
            _motion.SetContinuousInterpolation(AxisId.Y, true);

            int totalX = region.PointCountX;
            int totalY = region.PointCountY;
            int totalPoints = totalX * totalY;
            int completed = 0;

            bool encoderTriggered = parameters.Strategy == ScanStrategy.EncoderTriggered;

            // 断点续扫：从 _lastResumeRow 行开始（跳过已扫行），列内跳过已扫点位。
            // isResuming 用 _hadBreakpoint（而非列>0）——行完成时 col 归 0，若此时停止
            // 列>0 判 false 会导致从头重扫（数据重复）。新开始扫查会 ClearBreakpoint。
            bool isResuming = _hadBreakpoint;
            int startRow = Math.Clamp(_lastResumeRow, 0, totalY - 1);

            for (int yi = startRow; yi < totalY; yi++)
            {
                if (_cts.Token.IsCancellationRequested) break;
                await WaitIfPausedAsync(_cts.Token);

                float y = region.StartY + yi * region.StepY;
                await _motion.MoveAbsoluteAsync(AxisId.Y, y, parameters);
                await WaitAxisSettledAsync(AxisId.Y, y, parameters, _cts.Token);

                // 编码器触发策略：整行波形成帧缓存，行完成时一次性发布
                var linePositions = encoderTriggered ? new List<float>(totalX) : null!;
                var lineWaveforms = encoderTriggered ? new List<float[]>(totalX) : null!;

                // 断点续扫：此行的起始列
                int startCol = (yi == startRow && isResuming) ? Math.Clamp(_lastResumeCol, 0, totalX - 1) : 0;
                // 断点已扫点计数（跳过已扫点时不同步计数，仅用于进度百分比）
                int skipped = isResuming ? (yi - startRow) * totalX + startCol : 0;

                for (int xi = startCol; xi < totalX; xi++)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    await WaitIfPausedAsync(_cts.Token);

                    float x = region.StartX + xi * region.StepX;
                    await _motion.MoveAbsoluteAsync(AxisId.X, x, parameters);
                    await WaitAxisSettledAsync(AxisId.X, x, parameters, _cts.Token);

                    // 帧同步：等待产生新帧再取数
                    bool newFrame;
                    if (useStrictSingleTrigger)
                    {
                        long frameBeforeTrigger = _daq.GetCurrentFrameCount();
                        await _motion.PulseTriggerOutputAsync(parameters.TriggerIo, parameters.TriggerPulseWidthMs, _cts.Token);
                        newFrame = await _daq.WaitForFrameAfterAsync(frameBeforeTrigger, 500, _cts.Token);
                        if (!newFrame)
                            throw new TimeoutException(
                                $"单次触发超时：点位 X={x:F3}, Y={y:F3} 等待新帧失败。" +
                                $"请检查 ZMC 触发输出 IO{parameters.TriggerIo} → DPR500 External Trigger 接线");
                    }
                    else
                    {
                        newFrame = await _daq.WaitForNewFrameAsync(500, _cts.Token);
                        if (!newFrame)
                            throw new TimeoutException(
                                $"外触发缺失：等待新帧超时（位置 X={x:F3}, Y={y:F3}）。请检查 DPR500 TRIG/SYNC → Spectrum EXT0 触发线");
                    }
                    var data = _daq.GetCurrentData();
                    completed++;

                    if (encoderTriggered)
                    {
                        linePositions.Add(x);
                        lineWaveforms.Add((float[])data.Samples.Clone());
                    }

                    PointDataReady?.Invoke(this, new PointDataReadyEventArgs { X = x, Y = y, Data = data });

                    // 断点（列粒度）：每点完成后记录，停止时从未完成点继续（含首行中途）
                    _lastResumeRow = yi;
                    _lastResumeCol = xi + 1;
                    _lastFrameBaseline = _daq.GetCurrentFrameCount();

                    ProgressChanged?.Invoke(this, new ScanProgressEventArgs
                    {
                        ProgressPercent = (float)(completed + skipped) / totalPoints * 100f,
                        CurrentX = x,
                        CurrentY = y,
                        TotalPoints = totalPoints,
                        CompletedPoints = completed + skipped
                    });
                }

                // 行完成
                if (encoderTriggered && lineWaveforms.Count > 0)
                {
                    float actualRate = _daq.GetCurrentData().SampleRate > 0
                        ? _daq.GetCurrentData().SampleRate
                        : parameters.SampleRate;
                    LineScanComplete?.Invoke(this, new LineScanCompleteEventArgs
                    {
                        LineIndex = yi,
                        Y = y,
                        SampleRate = actualRate,
                        Positions = linePositions.ToArray(),
                        Waveforms = lineWaveforms.ToArray()
                    });
                }

                // 断点：记录最后完成行
                _lastResumeRow = yi + 1;
                _lastResumeCol = 0;
                _lastFrameBaseline = _daq.GetCurrentFrameCount();
            }

            // 取消检测：循环内 break（取消）不抛异常，此处显式区分"正常完成"与"被停止"
            if (_cts.Token.IsCancellationRequested)
            {
                // 被停止：保存断点（列粒度已在点级记录，此处行级兜底）
                SaveBreakpoint(region, parameters);
            }
            else
            {
                // 正常完成：清除断点
                _hadBreakpoint = false;
                _lastResumeRow = 0;
                _lastResumeCol = 0;
            }
        }
        catch (OperationCanceledException)
        {
            // 用户主动停止：H-8 修复——必须停轴（防止轴完成原指令继续运动），并复位三设备
            await SafeResetAllAsync();
            SaveBreakpoint(region, parameters);
        }
        catch (Exception)
        {
            // H-8 修复：统一故障复位——运动/采集/脉冲任一异常，三设备全部进入安全状态
            // （急停轴 + 关断 DPR 脉冲 + 停止并复位 DAQ）
            await SafeResetAllAsync();
            SaveBreakpoint(region, parameters);
            throw;
        }
        finally
        {
            // NH-3/H-1：扫查结束（正常/异常/停止）禁用 DPR500 输出——防止高压脉冲持续发射。
            // H-1：严格单次触发模式用 DisableOutputAndConfirmAsync 确认关断；Internal PRF 回退用 SetOutputEnabledAsync(false)。
            if (_pulse != null)
            {
                try
                {
                    if (useStrictSingleTrigger)
                        _ = await _pulse.DisableOutputAndConfirmAsync();
                    else if (useInternalPrfFallback)
                        _ = _pulse.SetOutputEnabledAsync(false);
                }
                catch { /* 关断失败不掩盖 */ }
            }
            // 关闭连续插补，恢复单步运动语义
            try
            {
                _motion.SetContinuousInterpolation(AxisId.X, false);
                _motion.SetContinuousInterpolation(AxisId.Y, false);
            }
            catch { /* 断开时忽略 */ }

            _isScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
        }
        finally
        {
            // L1-FIX（审查 20260828）：外层 finally 兜底——前置守卫（Validate/ScanDataSize/
            // ArmExternalTriggerAsync 等）在内部 try 之外抛异常时，也必须复位 _isScanning。
            // 正常路径下内部 finally 已复位，此处幂等；异常路径靠此保证扫描不锁死。
            // ownsScan=false（并发第二调用提前返回）时跳过——不得复位在跑扫描的共享状态。
            if (ownsScan)
            {
                _isScanning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    /// <summary>
    /// H-8：统一安全复位（故障/停止共用）——ZMC 停轴 → DPR 关断脉冲 → DAQ 停止。
    /// 每步独立 try/catch：任一步失败不掩盖其他设备的复位。
    /// </summary>
    private async Task SafeResetAllAsync()
    {
        // 1. 运动：急停所有轴（含用户停止场景——防止轴完成原指令继续运动）
        try { await _motion.EmergencyStopAsync(); }
        catch { /* 急停失败不掩盖 */ }
        // 2. 脉冲：断开 DPR500（CleanupAll 内含 TriggerEnable=FALSE + IsPulsing 确认）
        if (_pulse != null)
        {
            try { await _pulse.DisconnectAsync(); }
            catch { /* 关断失败不掩盖 */ }
        }
        // 3. RH-8 修复：DAQ 使用 ResetAsync（CARD_RESET 清除板卡残留错误状态），
        //    原实现仅调 StopAsync，溢出/DMA 错误残留在板卡寄存器中，下次 InitializeAsync 仍会失败。
        try { await _daq.ResetAsync(); }
        catch { /* 复位失败不掩盖 */ }
        // 4. 急停后短暂等待让控制器处理停止指令
        try { await Task.Delay(100); } catch { }
    }

    public async Task PauseAsync()
    {
        if (!_isScanning) return;

        // H-6 修复：先进入"正在进入安全暂停"状态，阻止下一点开始，不要先向 UI 宣布暂停完成。
        _isPaused = true;
        try
        {
            // 检查关断布尔结果；失败时升级为扫描故障并执行统一复位。
            if (_pulse is { IsConnected: true })
            {
                bool disabled = await _pulse.SetOutputEnabledAsync(false);
                if (!disabled)
                    throw new InvalidOperationException("暂停失败：DPR500 输出关断未确认");
            }
        }
        catch
        {
            // 关断失败：统一故障复位，保持停止状态，不得把 _isPaused=false 误导为可恢复
            await SafeResetAllAsync();
            _isPaused = false;
            _isScanning = false;
            throw;
        }
    }

    public async Task ResumeAsync()
    {
        // H-6 修复：只能在 DAQ 仍运行、DPR 状态安全、ZMC 无报警时恢复；任一失败都保持停止状态。
        if (!_isScanning) return;
        if (!_daq.IsRunning)
        {
            // P4：发布版可追溯（Debug.WriteLine 被 Release 裁剪）
            LogFile.Write("[ScanService] 恢复失败：采集卡未运行，保持停止状态", "WARNING");
            await StopAsync();
            return;
        }
        // L14-FIX（审查 20260828）：DPR 断连时原实现跳过使能直接恢复 → 后续每点超时报
        // "单次触发超时"误导排查方向。现在保持暂停并抛真实诊断（UI 显示"继续失败：DPR500 已断连"）。
        if (_pulse != null && !_pulse.IsConnected)
        {
            LogFile.Write("[ScanService] 恢复失败：DPR500 已断连，保持暂停状态", "WARNING");
            throw new InvalidOperationException("DPR500 已断连，请重连后再继续扫查");
        }
        if (_pulse != null && _pulse.IsConnected)
        {
            bool enabled = await _pulse.SetOutputEnabledAsync(true);
            if (!enabled)
            {
                LogFile.Write("[ScanService] 恢复时 DPR 启用失败（功率超限或连接异常），保持停止状态", "WARNING");
                await StopAsync();
                return;
            }
        }
        _isPaused = false;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _isPaused = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 断点续扫（20260828）：手动/异常停止后记录断点，供 <see cref="ResumeFromBreakpointAsync"/> 恢复。
    /// 仅当确实产生点位数据时记录（防止前置守卫失败也误存断点）。
    /// </summary>
    private void SaveBreakpoint(ScanRegion region, ScanParams parameters)
    {
        lock (_scanLock)
        {
            // 断点判定：行完成时 col 归 0（row>0），列级完成时 col>0——两者任一即存在可恢复断点
            if (_lastResumeRow <= 0 && _lastResumeCol <= 0)
            {
                _hadBreakpoint = false;   // 未完成任何列/行，无可恢复断点
                return;
            }
            _hadBreakpoint = true;
            _lastRegion = region;
            _lastParams = parameters;
            int totalPoints = region.PointCountX * region.PointCountY;
            int completed = _lastResumeRow * region.PointCountX + _lastResumeCol;
            BreakpointPercent = totalPoints > 0 ? (float)completed / totalPoints * 100f : 0f;
        }
    }

    /// <summary>
    /// 断点续扫：从上次停止位置恢复扫查（同参数、跳过已扫行/列）。
    /// 数据不重复（跳过已完成点位）、不丢失（ScanForm 已累积数据保留）。
    /// 无断点/断点已完成时返回 false。
    /// </summary>
    public async Task<bool> ResumeFromBreakpointAsync(CancellationToken ct = default)
    {
        ScanRegion? region;
        ScanParams? parameters;
        lock (_scanLock)
        {
            if (!_hadBreakpoint || _isScanning) return false;
            region = _lastRegion;
            parameters = _lastParams;
        }
        if (region is null || parameters is null) return false;

        // 断点续扫需硬件恢复：停止/异常路径 SafeResetAllAsync 已复位 DAQ/DPR。
        // 先恢复采集卡运行（若已复位），否则 StartScanAsync 的 DAQ 守卫拒绝启动。
        try
        {
            if (!_daq.IsRunning)
            {
                await _daq.InitializeAsync(new ConnectionConfig
                {
                    SampleRate = _lastSampleRate > 0 ? _lastSampleRate : 100e6f,
                    SampleCount = _lastSampleCount > 0 ? _lastSampleCount : ConnectionConfig.DefaultSampleCount
                });
                await _daq.StartContinuousAsync();
            }
        }
        catch (Exception)
        {
            return false;   // 硬件恢复失败：无法续扫
        }

        try
        {
            await StartScanAsync(region, parameters, ct);
            // 正常完成时 StartScanAsync 内部清除断点
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>清除断点（新扫查开始/用户明确放弃续扫时调用）</summary>
    public void ClearBreakpoint()
    {
        lock (_scanLock)
        {
            _hadBreakpoint = false;
            _lastResumeRow = 0;
            _lastResumeCol = 0;
            _lastRegion = null;
            _lastParams = null;
            BreakpointPercent = 0f;
        }
    }

    /// <summary>
    /// P0-E：扫查区域行程校验——起点+行程不得超出软限位（软限位值取自运动控制器，单一来源，H-03）。
    /// 超行程运动有撞机风险，直接拒绝启动（说明书 4.10 报警 1）。
    /// </summary>
    private void ValidateScanRegion(ScanRegion region)
    {
        float xLimit = _motion.GetForwardSoftLimit(AxisId.X);
        float yLimit = _motion.GetForwardSoftLimit(AxisId.Y);
        float xNeg = _motion.GetReverseSoftLimit(AxisId.X);
        float yNeg = _motion.GetReverseSoftLimit(AxisId.Y);
        float xEnd = region.StartX + region.Width;
        float yEnd = region.StartY + region.Height;
        if (region.StartX < xNeg || xEnd > xLimit || region.StartY < yNeg || yEnd > yLimit)
        {
            throw new InvalidOperationException(
                $"扫查行程超出软限位 X[{xNeg:F1}~{xLimit:F1}]mm Y[{yNeg:F1}~{yLimit:F1}]mm：" +
                $"X[{region.StartX:F1}~{xEnd:F1}] Y[{region.StartY:F1}~{yEnd:F1}]。" +
                $"请调整起点/尺寸（防撞机保护）");
        }
        // #31/#32 修复：代码级步距校验（说明书 §5.3 扫查参数范围）。
        // StepX 0.1~1000mm，StepY 0.001~100mm；步距为 0 或负值会导致除零或反向运动。
        if (!float.IsFinite(region.StepX) || region.StepX < 0.1f || region.StepX > 1000f)
            throw new InvalidOperationException($"X步距 {region.StepX}mm 超出有效范围 [0.1, 1000]mm");
        if (!float.IsFinite(region.StepY) || region.StepY < 0.001f || region.StepY > 100f)
            throw new InvalidOperationException($"Y步距 {region.StepY}mm 超出有效范围 [0.001, 100]mm");
    }

    /// <summary>
    /// P0-E：扫查数据量校验——估算 .adtx 导出体积，超过 16G 拒绝启动（说明书 4.10 报警 2，防爆盘）。
    /// 估算：点数 × 采样点 × 4 字节（float32）+ 位置 4 字节/点；采样点数取采集卡当前配置。
    /// </summary>
    private void ValidateScanDataSize(ScanRegion region, ScanParams parameters)
    {
        // M-8 修复：扫描开始前一次总预算，避免 ScanService 和 ScanForm 各自校验局部对象。
        // 预算含波形、波形对象开销、波形引用、C 扫矩阵、点位映射、图像（工作图+显示图）、DMA 环形缓冲。
        // 用 checked 防整数溢出；x86 保守上限（为 WinForms/原生 DLL/GC 碎片留数百 MB）。
        long rows = region.PointCountY;
        long cols = region.PointCountX;
        long points = checked(rows * cols);
        int samples = _daq.GetCurrentData().PointCount > 0 ? _daq.GetCurrentData().PointCount : ConnectionConfig.DefaultSampleCount;

        long waveformBytes;
        long waveformObjectOverhead;
        long waveformReferenceBytes;
        long matrixBytes;
        long pointMapBytes;
        long imageBytes;
        long daqBytes;
        try
        {
            waveformBytes = checked(points * samples * sizeof(float));
            waveformObjectOverhead = checked(points * 32L);
            waveformReferenceBytes = checked(points * IntPtr.Size);
            matrixBytes = checked(points * sizeof(float));
            pointMapBytes = checked(points * 16L);
            imageBytes = checked(cols * rows * 4L * 2L);   // 工作图 + 显示图
            // DAQ 环形缓冲估算（8 段 × samples × ch × 2 字节，保守取双通道）
            daqBytes = checked((long)samples * 8 * 2 * BytesPerSampleEstimate);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("扫查数据量计算发生整数溢出，请减小区域/步距/采样点数", ex);
        }

        long total = checked(waveformBytes + waveformObjectOverhead + waveformReferenceBytes
            + matrixBytes + pointMapBytes + imageBytes + daqBytes);

        // x86 保守上限 768 MiB（为 WinForms、原生 DLL、GC/LOH 碎片留数百 MB）；x64 8 GiB
        long limitBytes = IntPtr.Size == 4
            ? 768L * 1024 * 1024
            : 8L * 1024 * 1024 * 1024;

        if (total > limitBytes)
        {
            throw new InvalidOperationException(
                $"扫查总内存约 {total / 1024.0 / 1024:F1} MiB，超过 {IntPtr.Size * 8}-bit 进程安全上限 {limitBytes / 1024 / 1024} MiB" +
                $"（{points:N0} 点 × {samples} 采样点；波形 {waveformBytes / 1024.0 / 1024:F1} MiB + 矩阵 {matrixBytes / 1024.0 / 1024:F1} MiB + 图像/映射 {imageBytes / 1024.0 / 1024:F1} MiB）。" +
                $"请减小扫查区域、增大步距或减少采样点数（防爆盘保护）");
        }
    }

    // M-8：DAQ 每采样字节数估算（12-bit 以 16-bit 字传输 = 2 字节/采样/通道）
    private const int BytesPerSampleEstimate = 2;

    /// <summary>
    /// 暂停等待（审查报告 M-1）：暂停期间每 100ms 轮询一次，取消/恢复后继续。
    /// 原实现 Y/X 两层各有一份相同 while 循环，提取为单一方法。
    /// </summary>
    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (_isPaused && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// P0-1 运动到位判定：轮询 <see cref="IMotionController.IsAxisIdle"/> 等待轴空闲，
    /// 随后按位置误差带校验是否真正到达目标（防止限位截断后"空闲但未到位"继续采数）。
    /// 超时按行程/速度自适应估算（2 倍理论时间 + 3s 余量，5s~120s 钳位）。
    /// </summary>
    private async Task WaitAxisSettledAsync(AxisId axis, float target, ScanParams parameters, CancellationToken ct)
    {
        // 自适应超时：按当前速度走完行程的理论时间估算
        float velocity = Math.Max(parameters.Velocity, 0.1f);
        float distance = Math.Abs(target - _motion.GetPosition(axis));
        double timeoutMs = Math.Clamp(distance / velocity * 1000f * 2f + 3000f, 5000f, 120_000f);

        var sw = Stopwatch.StartNew();
        while (!_motion.IsAxisIdle(axis))
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed.TotalMilliseconds > timeoutMs)
                throw new TimeoutException(
                    $"轴 {axis} 在 {timeoutMs / 1000:0.#} s 内未到位（目标 {target:F3} mm），扫查中止");
            await Task.Delay(10, ct);
        }

        // 位置误差带校验：GetPosition 为 MPOS（测量反馈），空闲后留收敛窗口再判定（H-05）
        var swPos = Stopwatch.StartNew();
        while (swPos.Elapsed.TotalMilliseconds < PositionSettleMs)
        {
            float mpos = _motion.GetPosition(axis);
            float dpos = _motion.GetDemandPosition(axis);
            bool inPosition = Math.Abs(mpos - target) <= PositionToleranceMm;
            bool followingOk = Math.Abs(dpos - mpos) <= FollowingErrorToleranceMm;
            if (inPosition && followingOk)
                return;
            await Task.Delay(20, ct);
        }
        float m = _motion.GetPosition(axis);
        float d = _motion.GetDemandPosition(axis);
        if (Math.Abs(d - m) > FollowingErrorToleranceMm)
            throw new InvalidOperationException(
                $"轴 {axis} 跟随误差超限（DPOS {d:F3} vs MPOS {m:F3} 偏差 {Math.Abs(d - m):F3} > {FollowingErrorToleranceMm}mm），" +
                $"可能存在堵转/失步，扫查中止");
        throw new InvalidOperationException(
            $"轴 {axis} 已停止但位置偏离目标超过 {PositionToleranceMm} mm" +
            $"（当前 {m:F3}，目标 {target:F3}，运动可能被限位截断），扫查中止");
    }
}
