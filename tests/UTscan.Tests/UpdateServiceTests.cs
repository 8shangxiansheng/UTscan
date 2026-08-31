using System.IO;
using System.Text;
using UTscan.Services;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// 软件更新服务测试（2026-08-25 部署落地）。
/// 覆盖可单元验证的契约：
///   1. ParseVersion 解析与容错
///   2. CompareVersions 排序语义（顺序升级规则的基础）
///   3. IsProtected 红线：hardware.json 永不经更新通道触碰
///   4. CheckForUpdate 全部分支：无包/损坏/已最新/链不匹配/校验失败/可用
///   5. PrepareUpdate：staging 复制、保护文件排除、pending.json 落盘
///   6. BuildSwapScriptContent：纯 ASCII、保护排除、备份与恢复路径
///   7. HasPendingUpdate 启动检测
/// </summary>
public class UpdateServiceTests : IDisposable
{
    private readonly string _root;

    public UpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "utscan-updtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>在临时应用目录里构造一个已安装的"当前版本"和 _update\ 中的"新版本包"。</summary>
    private UpdateService MakeInstall(out string updateDir)
    {
        updateDir = Path.Combine(_root, UpdateService.UpdateDirName);
        Directory.CreateDirectory(updateDir);
        return new UpdateService(_root);
    }

    /// <summary>与 UpdateService 读取端一致的选项（大小写不敏感），模拟真实发布包由 gen-manifest.ps1 写出的小写字段。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions SerReadOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };


    /// <summary>写入一个最小合法更新包（两个文件）并返回其清单。</summary>
    private UpdateManifest WritePackage(string fileA, string fileB, string version = "2.0.0", string previous = "1.0.0")
    {
        var updateDir = Path.Combine(_root, UpdateService.UpdateDirName);
        WriteFile(Path.Combine(updateDir, "newlib.dll"), fileA);
        WriteFile(Path.Combine(updateDir, "sub/dir/app.pdb"), fileB);
        var manifest = new UpdateManifest
        {
            Version = version,
            Build = "20260901",
            Date = "2026-09-01",
            Previous = previous,
            Files = new List<UpdateFileEntry>
            {
                new() { Path = "newlib.dll", Sha256 = UpdateService.ComputeSha256(Path.Combine(updateDir, "newlib.dll")), Size = Encoding.UTF8.GetByteCount(fileA) },
                new() { Path = "sub/dir/app.pdb", Sha256 = UpdateService.ComputeSha256(Path.Combine(updateDir, "sub/dir/app.pdb")), Size = Encoding.UTF8.GetByteCount(fileB) },
            }
        };
        File.WriteAllText(Path.Combine(updateDir, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, SerReadOpts));
        return manifest;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── ParseVersion ──

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v2.0.0", 2, 0, 0)]
    [InlineData("V10.20.30", 10, 20, 30)]
    [InlineData("3.4.5-beta", 3, 4, 5)]      // 预发布后缀剥离
    [InlineData("", 0, 0, 0)]
    [InlineData(null, 0, 0, 0)]
    [InlineData("garbage", 0, 0, 0)]
    [InlineData("1.2", 1, 2, 0)]             // 缺段补零
    public void ParseVersion_ParsesCoreSemver(string? input, int major, int minor, int patch)
    {
        Assert.Equal((major, minor, patch), UpdateService.ParseVersion(input));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("2.0.1", "2.0.0", 1)]
    [InlineData("1.9.9", "2.0.0", -1)]
    public void CompareVersions_OrdersByMajorMinorPatch(string a, string b, int expectedSign)
    {
        int actual = Math.Sign(UpdateService.CompareVersions(UpdateService.ParseVersion(a), UpdateService.ParseVersion(b)));
        Assert.Equal(expectedSign, actual);
    }

    // ── IsProtected 红线 ──

    [Theory]
    [InlineData("hardware.json", true)]
    [InlineData("HARDWARE.JSON", true)]          // 大小写不敏感
    [InlineData("/hardware.json", true)]
    [InlineData("drivers/hardware.json.bak", false)]  // 仅精确文件名匹配
    [InlineData("UTscan.exe", false)]
    [InlineData("sub/dir/file.dll", false)]
    public void IsProtected_GuardsFieldConfigOnly(string path, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsProtected(path));
    }

    // ── CheckForUpdate 分支 ──

    [Fact]
    public void Check_NoPackage_ReturnsInvalid()
    {
        var svc = MakeInstall(out _);
        var outcome = svc.CheckForUpdate("1.0.0");
        Assert.Equal(UpdateCheckResult.InvalidPackage, outcome.Result);
        Assert.Contains("_update", outcome.Message);
    }

    [Fact]
    public void Check_CorruptManifest_ReturnsInvalid()
    {
        var svc = MakeInstall(out var upd);
        File.WriteAllText(Path.Combine(upd, "manifest.json"), "{ not json");
        Assert.Equal(UpdateCheckResult.InvalidPackage, svc.CheckForUpdate("1.0.0").Result);
    }

    [Fact]
    public void Check_SameOrOlderVersion_AlreadyUpToDate()
    {
        var svc = MakeInstall(out _);
        WritePackage("a", "b", version: "1.0.0");
        Assert.Equal(UpdateCheckResult.AlreadyUpToDate, svc.CheckForUpdate("1.0.0").Result);

        WritePackage("a", "b", version: "0.9.0");
        Assert.Equal(UpdateCheckResult.AlreadyUpToDate, svc.CheckForUpdate("1.0.0").Result);
    }

    [Fact]
    public void Check_ChainMismatch_RejectsJumpAndDowngrade()
    {
        var svc = MakeInstall(out _);
        // 包要求从 1.5.0 升级，当前是 1.0.0 → 禁止跳级
        WritePackage("a", "b", version: "2.0.0", previous: "1.5.0");
        Assert.Equal(UpdateCheckResult.ChainMismatch, svc.CheckForUpdate("1.0.0").Result);
    }

    [Fact]
    public void Check_TamperedFile_ReturnsInvalidWithHashFailure()
    {
        var svc = MakeInstall(out _);
        WritePackage("a", "b");
        // 校验通过后篡改文件内容（模拟 U 盘拷贝损坏）
        WriteFile(Path.Combine(_root, UpdateService.UpdateDirName, "newlib.dll"), "tampered!");
        var outcome = svc.CheckForUpdate("1.0.0");
        Assert.Equal(UpdateCheckResult.InvalidPackage, outcome.Result);
        Assert.Contains("newlib.dll", outcome.Message);
    }

    [Fact]
    public void Check_ManifestListsProtectedFile_Rejected()
    {
        var svc = MakeInstall(out var upd);
        var manifest = WritePackage("a", "b");
        manifest.Files.Add(new UpdateFileEntry
        {
            Path = "hardware.json",
            Sha256 = new string('0', 64),
            Size = 1,
        });
        File.WriteAllText(Path.Combine(upd, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
        var outcome = svc.CheckForUpdate("1.0.0");
        Assert.Equal(UpdateCheckResult.InvalidPackage, outcome.Result);
        Assert.Contains("hardware.json", outcome.Message);   // 明确指出受保护文件
    }

    [Fact]
    public void Check_ValidSequentialUpgrade_Available()
    {
        var svc = MakeInstall(out _);
        WritePackage("payload-a", "payload-b", version: "2.0.0", previous: "1.0.0");
        var outcome = svc.CheckForUpdate("1.0.0");
        Assert.Equal(UpdateCheckResult.Available, outcome.Result);
        Assert.NotNull(outcome.Manifest);
        Assert.Equal(2, outcome.Manifest!.Files.Count);
    }

    // ── PrepareUpdate：staging 与保护排除 ──

    [Fact]
    public void Prepare_StagesFiles_WritesPending_ExcludesProtected()
    {
        var svc = MakeInstall(out _);
        var manifest = WritePackage("payload-a", "payload-b");

        svc.PrepareUpdate(manifest, "1.0.0");

        var staging = Path.Combine(_root, UpdateService.StagingDirName, " staging".Trim());
        staging = Path.Combine(_root, UpdateService.StagingDirName, "staging");
        Assert.True(File.Exists(Path.Combine(staging, "newlib.dll")));
        Assert.True(File.Exists(Path.Combine(staging, "sub", "dir", "app.pdb")));

        // pending.json 存在且目标版本正确 → 启动时 HasPendingUpdate 命中
        Assert.True(UpdateService.HasPendingUpdate(_root));

        // exclude.txt 含保护文件名；swap 脚本落盘且为 ASCII
        Assert.Equal(UpdateService.ProtectedConfigFile,
            File.ReadAllLines(Path.Combine(_root, UpdateService.StagingDirName, "exclude.txt"))[0]);
        string script = File.ReadAllText(Path.Combine(_root, UpdateService.SwapScriptName));
        Assert.All(script, c => Assert.True(c < 128, $"非 ASCII 字符: U+{(int)c:X4}"));
    }

    [Fact]
    public void Prepare_ReRunReplacesStaleStaging()
    {
        var svc = MakeInstall(out _);
        var m1 = WritePackage("old-a", "old-b");
        svc.PrepareUpdate(m1, "1.0.0");
        var m2 = WritePackage("new-a", "new-b");
        svc.PrepareUpdate(m2, "1.0.0");   // 上次中断后重新准备 → 旧 staging 必须被清掉

        string content = File.ReadAllText(
            Path.Combine(_root, UpdateService.StagingDirName, "staging", "newlib.dll"));
        Assert.Equal("new-a", content);
    }

    // ── 交换脚本内容契约 ──

    [Fact]
    public void SwapScript_ContainsBackupCopyRestoreAndProtectedExclusion()
    {
        string script = UpdateService.BuildSwapScriptContent("2.0.0");

        // 纯 ASCII（工控机 ANSI 代码页安全）
        Assert.All(script, c => Assert.True(c < 128));

        // 等待退出 + 备份 + 排除 hardware.json + 恢复路径 + 重启
        Assert.Contains("tasklist", script);
        Assert.Contains(".update\\backup", script);
        Assert.Contains("hardware.json", script);
        Assert.Contains(":fail_backup", script);
        Assert.Contains(":fail_copy", script);
        Assert.Contains("start \"\"", script);
        Assert.DoesNotContain("%s", script);   // 无未替换占位符
    }

    [Fact]
    public void RestoreScript_IsAsciiAndRollsBackFromBackup()
    {
        string script = UpdateService.BuildRestoreScriptContent();
        Assert.All(script, c => Assert.True(c < 128));
        Assert.Contains(".update\\backup", script);
        Assert.Contains("robocopy", script);
    }

    // ── ComputeSha256 已知向量 ──

    [Fact]
    public void Sha256_KnownVector()
    {
        // SHA256("") = e3b0c442...
        var empty = Path.Combine(_root, "empty.bin");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.StartsWith("e3b0c44298fc1c14", UpdateService.ComputeSha256(empty));

        // SHA256("abc") = ba7816bf...
        var abc = Path.Combine(_root, "abc.bin");
        File.WriteAllText(abc, "abc");
        Assert.StartsWith("ba7816bf8f01cfea", UpdateService.ComputeSha256(abc));
    }
}
