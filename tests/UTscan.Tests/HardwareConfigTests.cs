using System.IO;
using UTscan;
using UTscan.Core.Models;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// 硬件装配配置加载测试（审查报告 C-1）。
/// 锁定：带 // 注释的 hardware.json 必须能正确解析（不再静默回退 Mock），
/// 且 useMock=false 必须真实生效（真机模式可达）。
/// </summary>
public class HardwareConfigTests
{
    private static string WriteTempConfig(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"utscan-hw-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LoadHardwareConfig_WithComments_ParsesUseMockFalse()
    {
        // 历史版本格式：含 // 注释 + useMock=false（审查报告 C-1 复现场景）
        string path = WriteTempConfig(
            """
            {
              // 硬件装配配置
              // useMock=false 使用真实硬件
              "ipAddress": "192.168.0.11",
              "port": 502,
              "sampleRate": 100000000,
              "sampleCount": 1024,
              "useMock": false
            }
            """);

        try
        {
            var cfg = Program.TryLoadHardwareConfigFile(path);

            Assert.NotNull(cfg);
            Assert.False(cfg!.UseMock);                       // 真机模式必须可达
            Assert.Equal("192.168.0.11", cfg.IpAddress);
            Assert.Equal(100_000_000f, cfg.SampleRate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadHardwareConfig_PureJson_ParsesUseMockTrue()
    {
        string path = WriteTempConfig(
            """
            {
              "ipAddress": "192.168.0.11",
              "useMock": true
            }
            """);

        try
        {
            var cfg = Program.TryLoadHardwareConfigFile(path);
            Assert.NotNull(cfg);
            Assert.True(cfg!.UseMock);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadHardwareConfig_InvalidJson_ReturnsNull()
    {
        string path = WriteTempConfig("{ this is not valid json !!!");

        try
        {
            Assert.Null(Program.TryLoadHardwareConfigFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadHardwareConfig_MissingFile_ReturnsNull()
    {
        Assert.Null(Program.TryLoadHardwareConfigFile(Path.Combine(Path.GetTempPath(), "does-not-exist.json")));
    }

    [Fact]
    public void LoadHardwareConfig_UnitCaseInsensitive_UseMockParses()
    {
        // 字段名大小写不敏感（PropertyNameCaseInsensitive）
        string path = WriteTempConfig(
            """
            {
              "UseMock": false,
              "IPAddress": "10.0.0.5",
              "Port": 503
            }
            """);
        var cfg = Program.TryLoadHardwareConfigFile(path);
        Assert.NotNull(cfg);
        Assert.False(cfg!.UseMock);
        Assert.Equal("10.0.0.5", cfg.IpAddress);
        Assert.Equal(503, cfg.Port);
    }

    // ── H1-FIX：采样参数回写 hardware.json（最小化更新）──

    [Fact]
    public void SaveSampleParams_UpdatesOnlySampleKeys_PreservesOthers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_cfg_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                "{\n  \"ipAddress\": \"192.168.0.11\",\n  \"sampleRate\": 100000000,\n  \"sampleCount\": 1024,\n  \"triggerIo\": -1\n}");
            Program.SaveHardwareConfigSampleParams(50_000_000, 2048, path);

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(50_000_000, root.GetProperty("sampleRate").GetInt32());
            Assert.Equal(2048, root.GetProperty("sampleCount").GetInt32());
            // 非采样键原样保留
            Assert.Equal("192.168.0.11", root.GetProperty("ipAddress").GetString());
            Assert.Equal(-1, root.GetProperty("triggerIo").GetInt32());
            Assert.Equal(4, root.EnumerateObject().Count());   // 键数不变
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveSampleParams_MissingFile_NoThrow()
    {
        // 文件不存在/路径空 → 静默返回，不抛异常
        Program.SaveHardwareConfigSampleParams(50_000_000, 2048, Path.Combine(Path.GetTempPath(), "nope-absent.json"));
        Program.SaveHardwareConfigSampleParams(50_000_000, 2048, "");
    }

    // ═══════════════════════════════════════════════════════════════
    //  版本信息读取（软件更新方案：version.json）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void LoadVersionInfo_ValidJson_ParsesVersion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"utscan-ver-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"version":"1.2.0","build":"20260818","date":"2026-08-18","exeSha256":"abc"}""");
        try
        {
            var v = Program.LoadVersionInfo(path);
            Assert.Equal("1.2.0", v.Version);
            Assert.Equal("20260818", v.Build);
            Assert.Equal("2026-08-18", v.Date);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadVersionInfo_MissingFile_ReturnsDefault()
    {
        var v = Program.LoadVersionInfo(Path.Combine(Path.GetTempPath(), "does-not-exist-version.json"));
        Assert.Equal("1.0.0", v.Version);
        Assert.Equal("", v.Build);
    }

    [Fact]
    public void LoadVersionInfo_InvalidJson_ReturnsDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), $"utscan-ver-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not valid !!!");
        try
        {
            var v = Program.LoadVersionInfo(path);
            Assert.Equal("1.0.0", v.Version);
        }
        finally { File.Delete(path); }
    }
}
