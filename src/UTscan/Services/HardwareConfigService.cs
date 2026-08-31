using System.IO;
using System.Text.Json;
using UTscan.Core.Models;

namespace UTscan.Services;

/// <summary>
/// 硬件配置服务接口（R5：配置注入解耦）。
/// 承载 hardware.json 的加载/校验/采样参数回写，替代 Program.cs 静态方法。
/// </summary>
public interface IHardwareConfigService
{
    /// <summary>实际加载的 hardware.json 绝对路径（供 UI 显示配置来源）</summary>
    string ConfigSourcePath { get; }

    /// <summary>
    /// 加载硬件配置。缺失/损坏时 fail closed——真机程序拒绝启动不自动回退 Mock。
    /// </summary>
    ConnectionConfig LoadHardwareConfig();

    /// <summary>解析单个配置文件；失败/不存在返回 null（容忍 // 注释）。</summary>
    ConnectionConfig? TryLoadConfigFile(string path);

    /// <summary>
    /// 运行期修改采样参数后回写 hardware.json（最小化更新：只改 sampleRate/sampleCount）。
    /// 回写失败静默。
    /// </summary>
    void SaveSampleParams(int sampleRate, int sampleCount);

    /// <summary>回写实现（path 显式传入，便于测试验证最小化更新）。</summary>
    void SaveSampleParams(int sampleRate, int sampleCount, string path);
}

/// <summary>
/// 硬件配置服务（R5）：hardware.json 加载/校验/采样参数回写。
/// 逻辑自 Program.cs 静态方法迁出，保持行为一致（fail-closed、容忍注释、最小化回写）。
/// </summary>
public class HardwareConfigService : IHardwareConfigService
{
    /// <summary>硬件装配配置文件名（放在可执行文件目录或工作目录）</summary>
    private const string HardwareConfigFile = "hardware.json";

    public string ConfigSourcePath { get; private set; } = "";

    /// <summary>
    /// M-10/L-3 修复：硬件配置只从 AppContext.BaseDirectory 加载（去掉 GetCurrentDirectory 回退）。
    /// 配置缺失/损坏时 fail closed——真机程序拒绝启动不自动回退 Mock；仅 useMock:true 才进 Mock。
    /// </summary>
    public ConnectionConfig LoadHardwareConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, HardwareConfigFile);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"未找到硬件配置文件 {path}。程序不会自动进入 Mock——请在可执行文件目录放置 hardware.json。", path);

        ConfigSourcePath = Path.GetFullPath(path);
        var cfg = TryLoadConfigFile(path);
        if (cfg == null)
            throw new InvalidOperationException(
                $"硬件配置文件解析失败：{path}。程序不会自动进入 Mock——请修复配置后重试。");
        return cfg;
    }

    /// <summary>
    /// 解析单个硬件配置文件；失败/不存在返回 null。
    /// 容忍 // 注释（审查报告 C-1：历史版本带注释的 hardware.json 曾导致解析失败静默回退 Mock）。
    /// </summary>
    public ConnectionConfig? TryLoadConfigFile(string path)
    {
        if (!File.Exists(path))
        {
            System.Diagnostics.Debug.WriteLine($"[装配] 未找到 {path}");
            return null;
        }

        try
        {
            using var fs = File.OpenRead(path);
            // ReadCommentHandling.Skip：容忍配置文件中的 // 注释（审查报告 C-1）。
            // 历史版本 hardware.json 带注释，System.Text.Json 默认拒绝注释导致
            // 反序列化必然失败并回退 Mock——真机模式永远无法启用（已实测复现）。
            var cfg = JsonSerializer.Deserialize<ConnectionConfig>(fs, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
            System.Diagnostics.Debug.WriteLine($"[装配] 已加载 {path}（UseMock={cfg?.UseMock}）");
            return cfg;
        }
        catch (Exception ex)
        {
            // 配置损坏不阻止启动：回退 Mock，避免真机参数半加载
            System.Diagnostics.Debug.WriteLine($"[装配] {path} 解析失败（{ex.Message}），回退 Mock 默认装配");
            return null;
        }
    }

    /// <summary>
    /// H1-FIX（审查 20260828）：运行期修改采样参数后回写 hardware.json。
    /// 最小化更新——用 JsonDocument 只改写 sampleRate/sampleCount 两个键，其余键
    /// （IP/串口/触发IO 等）与顺序原样保留，避免破坏现场配置其他内容。
    /// 回写失败静默（不阻断采集流程）；仅当文件存在且可解析时执行。
    /// </summary>
    public void SaveSampleParams(int sampleRate, int sampleCount)
        => SaveSampleParams(sampleRate, sampleCount, ConfigSourcePath);

    /// <summary>回写实现（path 可显式传入，便于单元测试用临时文件验证最小化更新）。</summary>
    public void SaveSampleParams(int sampleRate, int sampleCount, string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("sampleRate")) w.WriteNumber("sampleRate", sampleRate);
                    else if (prop.NameEquals("sampleCount")) w.WriteNumber("sampleCount", sampleCount);
                    else prop.WriteTo(w);
                }
                w.WriteEndObject();
            }
            File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            System.Diagnostics.Debug.WriteLine($"[装配] hardware.json 采样参数已回写: SR={sampleRate} 点数={sampleCount} ({path})");
        }
        catch (Exception ex)
        {
            // 回写失败不阻断：现场配置可能只读/占用，静默保留内存值
            System.Diagnostics.Debug.WriteLine($"[装配] hardware.json 回写失败: {ex.Message}");
        }
    }
}
