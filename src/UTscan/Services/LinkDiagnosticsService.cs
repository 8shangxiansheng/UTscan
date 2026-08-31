using System.Diagnostics;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Hardware.Daq;
using UTscan.Hardware.PulseGen;

namespace UTscan.Services;

/// <summary>
/// 链路诊断服务：检测 DPR500 脉冲收发器与 Spectrum DAQ 采集卡的通信链路。
/// 输出逐级诊断报告，定位具体故障环节。
/// </summary>
public sealed class LinkDiagnosticsService
{
    private readonly IPulseGenerator _pulse;
    private readonly IDataAcquisition _daq;
    // 保留含运动参数的构造签名，后续恢复三设备严格单触发时无需破坏调用接口。

    public LinkDiagnosticsService(IPulseGenerator pulse, IDataAcquisition daq)
        : this(pulse, daq, null, -1, 5)
    {
    }

    public LinkDiagnosticsService(
        IPulseGenerator pulse,
        IDataAcquisition daq,
        Core.Interfaces.IMotionController? motion,
        int triggerIo,
        int triggerPulseWidthMs)
    {
        _pulse = pulse;
        _daq = daq;
        _ = motion;
        _ = triggerIo;
        _ = triggerPulseWidthMs;
    }

    public async Task<LinkReport> RunFullDiagnosticsAsync(Core.Models.ConnectionConfig config)
    {
        var report = new LinkReport();
        var sw = Stopwatch.StartNew();

        // 两设备联调：诊断不得主动修改 PRF、触发源或自动开启高压。
        // 触发链仅在操作员已经安全启用发射时被动观察 Spectrum 是否出现新帧。
        report.Steps.Add(CheckDprDll());
        report.Steps.Add(CheckDprConnection());
        report.Steps.Add(CheckDaqDll());
        report.Steps.Add(CheckDaqConnection());
        SpectrumDaqCard? sc = _daq as SpectrumDaqCard;
        if (_pulse.IsConnected && _pulse is Dpr500Controller dpr)
        {
            report.Steps.Add(CheckDprParameterReadback(dpr));
            report.Steps.Add(CheckDprDiagnostics(dpr));
            if (sc is not null && !sc.NeedsReinitialize && sc.IsRunning)
                report.Steps.Add(await CheckTwoDeviceTriggerLinkAsync(dpr, sc));
            else
                report.Steps.Add(new StepResult("触发链路自检", Status.Skipped,
                    "采集卡未运行（需先连接并启动连续采集）"));
        }
        else
        {
            report.Steps.Add(new StepResult("DPR500 参数回读", Status.Skipped, "设备未连接，跳过参数回读验证"));
            report.Steps.Add(new StepResult("DPR500 健康状态", Status.Skipped, "设备未连接，跳过健康检查"));
            report.Steps.Add(new StepResult("触发链路自检", Status.Skipped, "脉冲收发器未连接，跳过触发链验证"));
        }

        if (sc is not null && !sc.NeedsReinitialize)
        {
            report.Steps.Add(CheckDaqCapabilities(sc));
            report.Steps.Add(await CheckDaqReadbackAsync(sc, config));
        }
        else
        {
            report.Steps.Add(new StepResult("DAQ 能力探测", Status.Skipped, "设备未连接，跳过能力探测"));
            report.Steps.Add(new StepResult("DAQ 数据回传", Status.Skipped, "设备未连接，跳过数据回传验证"));
        }

        report.ElapsedMs = sw.ElapsedMilliseconds;
        return report;
    }

    // ═══════════════════════════════════════════════════════
    //  DPR500 脉冲收发器诊断
    // ═══════════════════════════════════════════════════════

    private static StepResult CheckDprDll()
    {
        bool available = JsrNative.IsDllAvailable();
        return new StepResult(
            "DPR500 DLL 探测",
            available ? Status.Ok : Status.Fail,
            available
                ? "JSR_Common3264.dll 已定位（JSR Common API SDK）"
                : "JSR_Common3264.dll 未找到——确认 DLL 在程序目录或 SysWOW64 中");
    }

    private StepResult CheckDprConnection()
    {
        if (_pulse.IsConnected)
        {
            string info = "已连接";
            if (_pulse is Dpr500Controller dpr)
            {
                try
                {
                    var diag = dpr.GetDiagnostics();
                    info += $" | 驱动: {(diag.LibraryDriversStatus == 0 ? "正常" : $"状态码={diag.LibraryDriversStatus}")}";
                    info += $" | 仪器: {(diag.InstrumentConnectStatus == 0 ? "已连接" : $"状态码={diag.InstrumentConnectStatus}")}";
                    info += $" | 脉冲: {(diag.IsPulsing ? "发射中" : "停止")}";
                }
                catch { info += " | 诊断读取失败"; }
            }
            return new StepResult("DPR500 连接状态", Status.Ok, info);
        }
        return new StepResult("DPR500 连接状态", Status.Fail, "未连接——请先点击「连接」建立链路");
    }

    private static StepResult CheckDprParameterReadback(Dpr500Controller dpr)
    {
        try
        {
            var diag = dpr.GetDiagnostics();
            bool valid = diag.ConnectionStatusValid && diag.PowerLimitStatusValid && diag.PulsingStatusValid;
            return new StepResult("DPR500 参数回读", valid ? Status.Ok : Status.Fail,
                valid
                    ? $"只读检查通过：PRF={dpr.Params.PrfHz:F0}Hz，发射中={diag.IsPulsing}"
                    : "连接/功率/发射状态读取不完整；为安全起见未执行参数写入测试");
        }
        catch (Exception ex)
        {
            return new StepResult("DPR500 参数回读", Status.Fail, $"参数回读失败: {ex.Message}");
        }
    }

    private static StepResult CheckDprDiagnostics(Dpr500Controller dpr)
    {
        try
        {
            var diag = dpr.GetDiagnostics();
            string desc = diag.Describe();
            var status = !diag.ConnectionStatusValid || !diag.PowerLimitStatusValid || !diag.PulsingStatusValid
                ? Status.Fail
                : diag.IsPowerLimitExceeded ? Status.Warn : Status.Ok;
            return new StepResult("DPR500 健康状态", status, desc);
        }
        catch (Exception ex)
        {
            return new StepResult("DPR500 健康状态", Status.Fail, $"诊断读取异常: {ex.Message}");
        }
    }
    // ═══════════════════════════════════════════════════════
    //  触发链路在线自检（审查 2026-08-25 P0）
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 两设备被动触发链验证：不改变 DPR500 参数、不自动开启高压；仅当操作员已经启用
    /// Internal 发射时，观察 DPR500 TRIG/SYNC → Spectrum EXT0 是否推动帧计数。
    /// </summary>
    private static async Task<StepResult> CheckTwoDeviceTriggerLinkAsync(
        Dpr500Controller dpr, SpectrumDaqCard sc)
    {
        const int frameTimeoutMs = 1000;
        try
        {
            var diag = dpr.GetDiagnostics();
            if (!diag.PulsingStatusValid)
                return new StepResult("触发链路自检", Status.Fail, "无法读取 DPR500 发射状态，拒绝判断链路");
            if (!diag.IsPulsing)
                return new StepResult("触发链路自检", Status.Skipped,
                    "DPR500 当前未发射；完成安全接线后手动启用 Internal 发射，再运行诊断");
            if (dpr.Params.TriggerMode != TriggerMode.Internal)
                return new StepResult("触发链路自检", Status.Fail,
                    $"DPR500 当前为 {dpr.Params.TriggerMode}，两设备联动要求 Internal + TRIG/SYNC 输出");

            long baseline = sc.GetCurrentFrameCount();
            bool framed = await sc.WaitForFrameAfterAsync(baseline, frameTimeoutMs);
            if (!framed)
            {
                return new StepResult("触发链路自检", Status.Fail,
                    $"DPR500 正在发射，但 {frameTimeoutMs}ms 内 Spectrum 无新帧。检查 TRIG/SYNC → EXT0 接线、触发电平、端接和耦合");
            }

            return new StepResult("触发链路自检", Status.Ok,
                $"DPR500 Internal/TRIG-SYNC → Spectrum EXT0 链路通过，帧计数 {baseline} → {sc.GetCurrentFrameCount()}");
        }
        catch (Exception ex)
        {
            return new StepResult("触发链路自检", Status.Fail, $"自检异常: {ex.Message}");
        }
    }





    // ═══════════════════════════════════════════════════════
    //  Spectrum DAQ 采集卡诊断
    // ═══════════════════════════════════════════════════════

    private static StepResult CheckDaqDll()
    {
        string dllPath = Path.Combine(AppContext.BaseDirectory, "spcm_win32.dll");
        bool exists = File.Exists(dllPath);
        return new StepResult(
            "DAQ DLL 探测",
            exists ? Status.Ok : Status.Fail,
            exists
                ? $"spcm_win32.dll 已定位（{new FileInfo(dllPath).Length / 1024} KB）"
                : "spcm_win32.dll 未找到——确认 Spectrum 驱动已安装");
    }

    private StepResult CheckDaqConnection()
    {
        if (_daq is SpectrumDaqCard spectrum)
        {
            if (spectrum.NeedsReinitialize)
                return new StepResult("DAQ 连接状态", Status.Warn, "采集卡句柄已释放，需重新初始化");
            if (spectrum.IsRunning)
                return new StepResult("DAQ 连接状态", Status.Ok, "Spectrum M3i 采集卡已连接（运行中）");
            return new StepResult("DAQ 连接状态", Status.Ok, "Spectrum M3i 采集卡已连接");
        }
        return new StepResult("DAQ 连接状态", Status.Fail, "未连接——请先点击「连接」建立链路");
    }

    private static StepResult CheckDaqCapabilities(SpectrumDaqCard spectrum)
    {
        try
        {
            var caps = spectrum.Capabilities;
            if (caps is null)
                return new StepResult("DAQ 能力探测", Status.Warn, "能力位图未初始化");
            string desc = caps.Describe();
            return new StepResult("DAQ 能力探测", Status.Ok, desc);
        }
        catch (Exception ex)
        {
            return new StepResult("DAQ 能力探测", Status.Fail, $"能力探测异常: {ex.Message}");
        }
    }

    private static async Task<StepResult> CheckDaqReadbackAsync(SpectrumDaqCard spectrum, Core.Models.ConnectionConfig config)
    {
        _ = config; // 保留接口；两设备被动诊断不改变采集参数或运行状态
        bool wasRunning = spectrum.IsRunning;
        long baselineFrame = spectrum.GetCurrentFrameCount();

        try
        {
            // 如果已在运行，直接检查帧计数
            if (wasRunning)
            {
                bool received = await spectrum.WaitForFrameAfterAsync(baselineFrame, 1000);
                long frames = spectrum.GetCurrentFrameCount();
                long delta = frames - baselineFrame;
                return new StepResult(
                    "DAQ 数据回传",
                    received ? Status.Ok : Status.Warn,
                    received
                        ? $"采集运行中，帧计数 {frames}（+{delta}），数据回传正常"
                        : $"采集运行中，帧计数 {frames}（未递增），可能未收到触发信号");
            }

            return new StepResult(
                "DAQ 数据回传",
                Status.Skipped,
                "采集未运行；为避免诊断改变采集参数或触发状态，未自动启动板卡");
        }
        catch (Exception ex)
        {
            try { await spectrum.StopAsync(); } catch { }
            return new StepResult("DAQ 数据回传", Status.Fail, $"数据回传测试异常: {ex.Message}");
        }
    }
}

// ═══════════════════════════════════════════════════════
//  诊断报告数据模型
// ═══════════════════════════════════════════════════════

public enum Status { Ok, Warn, Fail, Skipped }

public sealed class StepResult
{
    public string Name { get; }
    public Status Status { get; }
    public string Detail { get; }

    public StepResult(string name, Status status, string detail)
    {
        Name = name;
        Status = status;
        Detail = detail;
    }
}

public sealed class LinkReport
{
    public List<StepResult> Steps { get; } = new();
    public long ElapsedMs { get; set; }

    public int OkCount => Steps.Count(s => s.Status == Status.Ok);
    public int WarnCount => Steps.Count(s => s.Status == Status.Warn);
    public int FailCount => Steps.Count(s => s.Status == Status.Fail);
    public int SkippedCount => Steps.Count(s => s.Status == Status.Skipped);

    public bool IsAllOk => Steps.Count > 0 && Steps.All(s => s.Status == Status.Ok);

    public string Summary => IsAllOk
        ? $"链路正常（{OkCount} 项通过）"
        : FailCount > 0 || WarnCount > 0
            ? $"发现 {FailCount} 项故障、{WarnCount} 项警告、{SkippedCount} 项跳过"
            : $"诊断未完成（{SkippedCount} 项跳过）";

    public string ToReportString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine("  UTscan 通信链路诊断报告");
        sb.AppendLine($"  时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  耗时: {ElapsedMs}ms");
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine();

        // DPR500 区段
        sb.AppendLine("── DPR500 脉冲收发器 ──────────────────────────────");
        foreach (var step in Steps.Where(s => s.Name.StartsWith("DPR")))
            AppendStep(sb, step);
        sb.AppendLine();

        // Spectrum 区段
        sb.AppendLine("── Spectrum DAQ 采集卡 ────────────────────────────");
        foreach (var step in Steps.Where(s => s.Name.StartsWith("DAQ")))
            AppendStep(sb, step);
        sb.AppendLine();

        sb.AppendLine("── DPR500 → Spectrum 触发链路 ─────────────────────");
        foreach (var step in Steps.Where(s => s.Name.StartsWith("触发")))
            AppendStep(sb, step);
        sb.AppendLine();

        // 汇总
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine($"  总结: {Summary}");
        sb.AppendLine("═══════════════════════════════════════════════════");

        return sb.ToString();
    }

    private static void AppendStep(System.Text.StringBuilder sb, StepResult step)
    {
        string icon = step.Status switch
        {
            Status.Ok => "[OK] ",
            Status.Warn => "[!!] ",
            Status.Fail => "[XX] ",
            Status.Skipped => "[--] ",
            _ => "[??] "
        };
        sb.AppendLine($"  {icon}{step.Name}");
        sb.AppendLine($"        {step.Detail}");
    }
}
