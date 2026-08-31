using System.Diagnostics;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.PulseGen;
using UTscan.Hardware.Zmc;

namespace UTscan.Services;

/// <summary>
/// 启动时硬件探测服务：检测三个设备（ZMC/DPR500/Spectrum）的 DLL 可用性。
/// 轻量级——仅检查 DLL 文件存在性，不执行实际连接/断开（避免与后续 ConnectHardwareCoreAsync 双连）。
/// 注意：探测阶段不打开采集卡设备（spcm_hOpen）——启动阶段这里打开设备会和紧随其后的
/// 正式连接 ConnectHardwareCoreAsync 再次 spcm_hOpen 产生驱动层竞态（Close 后快速重开可能
/// 返回空句柄，导致正式连接报"打开设备 spc0 失败"）。设备可用性完全交由正式连接判定。
/// </summary>
public sealed class HardwareProbeService
{
    private readonly IMotionController _motion;
    private readonly IDataAcquisition _daq;
    private readonly IPulseGenerator? _pulse;
    private readonly ConnectionConfig _config;

    public HardwareProbeService(IMotionController motion, IDataAcquisition daq, IPulseGenerator? pulse, ConnectionConfig config)
    {
        _motion = motion;
        _daq = daq;
        _pulse = pulse;
        _config = config;
    }

    /// <summary>运行全量探测，返回探测报告</summary>
    public Task<ProbeReport> ProbeAllAsync()
    {
        var report = new ProbeReport();
        var sw = Stopwatch.StartNew();

        if (_config.UseMock)
        {
            report.Items.Add(new ProbeItem("运动控制器", ProbeStatus.Mock, "Mock 模式，跳过真机探测"));
            report.Items.Add(new ProbeItem("采集卡", ProbeStatus.Mock, "Mock 模式，跳过真机探测"));
            report.Items.Add(new ProbeItem("脉冲收发仪", ProbeStatus.Mock, "Mock 模式，跳过真机探测"));
            report.ElapsedMs = sw.ElapsedMilliseconds;
            return Task.FromResult(report);
        }

        // 1. ZMC 运动控制器。两设备联调阶段可通过配置完整跳过，避免仅为探测加载/访问运动系统。
        report.Items.Add(_config.EnableMotionController
            ? ProbeZmcDll()
            : new ProbeItem("运动控制器", ProbeStatus.Mock, "配置已禁用，保留接口但跳过探测和连接"));

        // 2. Spectrum DAQ 采集卡（DLL 检查）。
        //    注意：不在此打开设备（spcm_hOpen）——启动阶段这里打开设备会和紧随其后的
        //    正式连接 ConnectHardwareCoreAsync 再次 spcm_hOpen 产生驱动层竞态（Close 后
        //    快速重开可能返回空句柄，导致正式连接报"打开设备 spc0 失败"）。设备可用性
        //    完全交由正式连接判定，正式连接本身会给出清晰的设备/驱动错误信息。
        report.Items.Add(ProbeDaqDll());

        // 3. DPR500 脉冲收发仪（仅 DLL 检查——连接由 ConnectHardwareCoreAsync 负责）
        report.Items.Add(ProbeDprDll());
        report.Items.Add(ProbeDprSerialConfig());

        report.ElapsedMs = sw.ElapsedMilliseconds;
        return Task.FromResult(report);
    }

    // ═══════════════════════════════════════════════════════
    //  ZMC 运动控制器（仅 DLL 检查）
    // ═══════════════════════════════════════════════════════

    private ProbeItem ProbeZmcDll()
    {
        bool zmotion = File.Exists(Path.Combine(AppContext.BaseDirectory, "zmotion.dll"));
        bool zaux = File.Exists(Path.Combine(AppContext.BaseDirectory, "zauxdll.dll"));

        if (!zmotion && !zaux)
            return new ProbeItem("运动控制器", ProbeStatus.Fail,
                "zmotion.dll 和 zauxdll.dll 均未找到——确认 ZMC SDK 已部署到程序目录");
        if (!zmotion)
            return new ProbeItem("运动控制器", ProbeStatus.Warn,
                "zauxdll.dll 已就位，但缺少 zmotion.dll（核心运动库）");
        if (!zaux)
            return new ProbeItem("运动控制器", ProbeStatus.Warn,
                "zmotion.dll 已就位，但缺少 zauxdll.dll（辅助 API 库）");

        return new ProbeItem("运动控制器", ProbeStatus.Ok,
            $"ZMC SDK 已就位（zmotion.dll + zauxdll.dll），连接目标={_config.IpAddress}:{_config.Port}");
    }

    // ═══════════════════════════════════════════════════════
    //  Spectrum DAQ 采集卡（仅 DLL 检查，不打开设备）
    // ═══════════════════════════════════════════════════════

    private static ProbeItem ProbeDaqDll()
    {
        string dllPath = Path.Combine(AppContext.BaseDirectory, "spcm_win32.dll");
        bool exists = File.Exists(dllPath);
        return new ProbeItem("采集卡 DLL", exists ? ProbeStatus.Ok : ProbeStatus.Fail,
            exists
                ? $"spcm_win32.dll 已就位（{new FileInfo(dllPath).Length / 1024} KB）"
                : "spcm_win32.dll 未找到——确认 Spectrum 驱动已安装");
    }

    // ═══════════════════════════════════════════════════════
    //  DPR500 脉冲收发仪（仅 DLL + 串口配置检查）
    // ═══════════════════════════════════════════════════════

    private static ProbeItem ProbeDprDll()
    {
        bool available = JsrNative.IsDllAvailable();
        return new ProbeItem("脉冲收发仪 DLL", available ? ProbeStatus.Ok : ProbeStatus.Fail,
            available
                ? "JSR_Common3264.dll 已就位"
                : "JSR_Common3264.dll 未找到——确认 JSR SDK 已部署");
    }

    /// <summary>
    /// 检查 DPR500 串口配置合规性（Manual Page 20: 4800,8,N,1）。
    /// JSR Common SDK 内部管理串口——应用层无法直接指定 COM 端口号，
    /// SDK 通过 USB vendor/product ID 或扫描默认串口自动发现设备。
    /// </summary>
    private ProbeItem ProbeDprSerialConfig()
    {
        var issues = new List<string>();

        if (_config.BaudRate != 4800)
            issues.Add($"波特率={_config.BaudRate}（DPR500 标准为 4800）");

        if (string.IsNullOrWhiteSpace(_config.SerialPort))
            issues.Add("串口名称为空");
        else if (!_config.SerialPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            issues.Add($"串口名称='{_config.SerialPort}'（应以 COM 开头，如 COM1）");

        if (_config.TimeoutMs < 1000)
            issues.Add($"超时={_config.TimeoutMs}ms（建议≥5000ms 以避免设备未上电时误判）");

        if (issues.Count > 0)
            return new ProbeItem("脉冲收发仪配置", ProbeStatus.Warn,
                $"串口参数异常: {string.Join("; ", issues)}。" +
                "JSR SDK 使用默认串口参数(4800,8,N,1)，配置不一致可能导致连接失败");

        return new ProbeItem("脉冲收发仪配置", ProbeStatus.Ok,
            $"串口={_config.SerialPort} 波特率={_config.BaudRate} " +
            $"超时={_config.TimeoutMs}ms（符合 DPR500 Manual Page 20 规格）");
    }
}

// ═══════════════════════════════════════════════════════
//  探测数据模型
// ═══════════════════════════════════════════════════════

public enum ProbeStatus { Ok, Warn, Fail, Mock }

public sealed class ProbeItem
{
    public string Device { get; }
    public ProbeStatus Status { get; }
    public string Message { get; }

    public ProbeItem(string device, ProbeStatus status, string message)
    {
        Device = device;
        Status = status;
        Message = message;
    }

    public string StatusIcon => Status switch
    {
        ProbeStatus.Ok => "OK",
        ProbeStatus.Warn => "!!",
        ProbeStatus.Fail => "XX",
        ProbeStatus.Mock => "--",
        _ => "??"
    };
}

public sealed class ProbeReport
{
    public List<ProbeItem> Items { get; } = new();
    public long ElapsedMs { get; set; }

    public bool AllOk => Items.All(i => i.Status == ProbeStatus.Ok || i.Status == ProbeStatus.Mock);
    public bool HasFail => Items.Any(i => i.Status == ProbeStatus.Fail);

    /// <summary>生成单行摘要（供状态栏显示）</summary>
    public string Summary =>
        $"硬件探测({ElapsedMs}ms): " +
        string.Join(" | ", Items.Select(i => $"{i.Device}={i.StatusIcon}"));
}
