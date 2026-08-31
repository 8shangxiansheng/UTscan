using System.IO;
using System.Text.Json;
using UTscan.Core.Models;

namespace UTscan.Services;

/// <summary>
/// 扫查会话配置（说明书“保存设置/加载设置”）：聚合闸门、采集卡、脉冲收发仪、
/// 系统参数、扫查参数、扫查区域。对应 .acf 配置文件（此处用 JSON 实现）。
/// </summary>
public class ScanSessionConfig
{
    public GateSet Gates { get; set; } = new();
    public DaqParams Daq { get; set; } = new();
    public PulseParams Pulse { get; set; } = new();
    public SystemParams System { get; set; } = new();
    public ScanParams Scan { get; set; } = new();
    public ScanRegion Region { get; set; } = new();
    public Core.Enums.CScanImagingMode ImagingMode { get; set; } = Core.Enums.CScanImagingMode.PeakPeak;
    public Core.Enums.WaveformType WaveformType { get; set; } = Core.Enums.WaveformType.RF;
    public string? ColormapName { get; set; } = "Jet";

    /// <summary>C 扫手动显示下限（null = 自动范围）。批次2 新增。</summary>
    public float? DisplayMin { get; set; }

    /// <summary>C 扫手动显示上限（null = 自动范围）。批次2 新增。</summary>
    public float? DisplayMax { get; set; }
}

/// <summary>
/// 配置服务：保存/加载 ScanSessionConfig 为 .acf（JSON）。
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>保存到文件</summary>
    public async Task SaveAsync(string path, ScanSessionConfig config)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, config, s_opts);
    }

    /// <summary>从文件加载</summary>
    public async Task<ScanSessionConfig> LoadAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        var cfg = await JsonSerializer.DeserializeAsync<ScanSessionConfig>(fs, s_opts);
        return cfg ?? new ScanSessionConfig();
    }

    /// <summary>保存默认配置到 %AppData%/UTscan/default.acf</summary>
    public async Task SaveDefaultAsync(ScanSessionConfig config)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UTscan");
        await SaveAsync(Path.Combine(dir, "default.acf"), config);
    }

    /// <summary>加载默认配置；不存在则返回新实例</summary>
    public async Task<ScanSessionConfig> LoadDefaultAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UTscan", "default.acf");
        return File.Exists(path) ? await LoadAsync(path) : new ScanSessionConfig();
    }
}
