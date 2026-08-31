using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace UTscan.Services;

/// <summary>
/// 发布包清单（manifest.json，随更新包提供）。
/// 与 version.json 的关系：version.json 描述当前安装版本；manifest.json 描述"新版本包"，
/// 含全部文件哈希清单——覆盖安装时逐文件跳过未变化文件（决策：完整目录覆盖+单文件差异跳过）。
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>新版本号（语义化 1.2.3）</summary>
    public string Version { get; set; } = "";

    /// <summary>构建号 YYYYMMDD</summary>
    public string Build { get; set; } = "";

    /// <summary>发布日期</summary>
    public string Date { get; set; } = "";

    /// <summary>允许升级的来源版本（顺序升级约束：非空时必须等于当前版本才放行）</summary>
    public string Previous { get; set; } = "";

    /// <summary>文件清单（相对应用目录路径 + SHA256 + 字节数）。不含 hardware.json 等现场配置。</summary>
    public List<UpdateFileEntry> Files { get; set; } = new();
}

/// <summary>清单中的单文件条目</summary>
public sealed class UpdateFileEntry
{
    /// <summary>相对应用目录的路径（正斜杠分隔，如 "UTscan.exe"、"drivers/xxx.exe"）</summary>
    public string Path { get; set; } = "";

    /// <summary>SHA256（小写十六进制）</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>字节数（快速预检）</summary>
    public long Size { get; set; }
}

/// <summary>检查更新结果</summary>
public enum UpdateCheckResult
{
    /// <summary>发现可用的新版本（满足顺序升级规则且包校验通过）</summary>
    Available,
    /// <summary>包版本不高于当前版本</summary>
    AlreadyUpToDate,
    /// <summary>违反顺序升级：manifest.Previous 与当前版本不符（禁止跳级/降级）</summary>
    ChainMismatch,
    /// <summary>包缺失/损坏（无 manifest、文件缺失或哈希不符）</summary>
    InvalidPackage,
}

/// <summary>检查更新结果详情</summary>
public sealed record UpdateCheckOutcome(UpdateCheckResult Result, string Message, UpdateManifest? Manifest);

/// <summary>
/// 应用内手动「检查更新」服务（决策 A：仅程序内按钮触发，不做自动检测）。
///
/// 设计要点（docs/软件更新方案.md + 2026-08-25 决策）：
/// - 更新包 = 完整多文件发布目录 + manifest.json（逐文件 SHA256），U 盘拷贝到应用目录 _update\ 子目录；
/// - 增量粒度：完整目录覆盖 + 单文件 sha256 差异跳过；
/// - 版本链规则：只允许顺序升级——manifest.Previous 必须等于当前运行版本（非空时），且新版本 &gt; 当前；
///   禁止跳级、禁止降级（降级走回滚脚本恢复 .update\backup）；
/// - 绝不覆盖 hardware.json（现场配置）：staging 复制与交换阶段均显式排除（双重防线）；
/// - 应用方式：swap-on-exit 分离脚本模式——主程序把新版复制到 .update\staging\ 并写 pending.json，
///   退出前拉起 ASCII 安全编码的 UpdateSwap.cmd；脚本等待进程退出后完成备份→覆盖→重启
///   （运行中 exe 无法自我覆盖，独立批处理是唯一可靠交换者；工控机 ANSI 代码页下必须纯 ASCII）。
/// </summary>
public sealed class UpdateService
{
    private static readonly JsonSerializerOptions s_jsonRead = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions s_jsonWrite = new()
    {
        WriteIndented = true,
    };

    /// <summary>更新包放置目录（相对应用根）</summary>
    public const string UpdateDirName = "_update";

    /// <summary>待定升级工作目录（相对应用根，含 staging/backup/pending.json/exclude.txt）</summary>
    public const string StagingDirName = ".update";

    /// <summary>现场配置文件名——任何更新操作绝不覆盖</summary>
    public const string ProtectedConfigFile = "hardware.json";

    /// <summary>生成的交换脚本名（应用根）</summary>
    public const string SwapScriptName = "UpdateSwap.cmd";

    private readonly string _appDir;
    private readonly string _updateDir;
    private readonly string _workDir;

    public UpdateService(string? appDir = null)
    {
        _appDir = Path.GetFullPath(appDir ?? AppContext.BaseDirectory);
        _updateDir = Path.Combine(_appDir, UpdateDirName);
        _workDir = Path.Combine(_appDir, StagingDirName);
    }

    // ═══════════════════════════════════════════════════════════
    //  检查更新（同步即可：本地文件 IO，无网络）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 检查 _update\ 目录中是否有合法且允许安装的新版本。
    /// 校验链：manifest 可解析 → 版本链规则 → 包内文件齐全且大小/SHA256 匹配。
    /// </summary>
    public UpdateCheckOutcome CheckForUpdate(string currentVersion)
    {
        var manifestPath = Path.Combine(_updateDir, "manifest.json");
        if (!File.Exists(manifestPath))
            return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                $"未找到更新包。请将新版发布目录（含 manifest.json）解压到 {_updateDir} 后重试", null);

        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), s_jsonRead)
                       ?? throw new InvalidDataException("manifest 为空");
        }
        catch (Exception ex)
        {
            return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                $"manifest.json 解析失败: {ex.Message}", null);
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files.Count == 0)
            return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                "manifest.json 无效（缺 version 或 files 清单为空）", null);

        // ── 版本链规则：只允许顺序升级 ──
        var current = ParseVersion(currentVersion);
        var next = ParseVersion(manifest.Version);
        if (CompareVersions(next, current) <= 0)
            return new UpdateCheckOutcome(UpdateCheckResult.AlreadyUpToDate,
                $"更新包版本 v{manifest.Version} 不高于当前版本 v{currentVersion}，无需更新", null);

        if (!string.IsNullOrWhiteSpace(manifest.Previous)
            && !string.Equals(manifest.Previous.TrimStart('v', 'V'), currentVersion.TrimStart('v', 'V'),
                StringComparison.OrdinalIgnoreCase))
            return new UpdateCheckOutcome(UpdateCheckResult.ChainMismatch,
                $"版本链不匹配：此更新包要求从 v{manifest.Previous} 升级，当前为 v{currentVersion}。" +
                "请按发布记录顺序先安装中间版本（禁止跳级）；如需退回旧版请使用回滚", null);

        // ── 包完整性：逐文件存在性 + 大小 + SHA256 ──
        foreach (var f in manifest.Files)
        {
            if (IsProtected(f.Path))
                return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                    $"清单含受保护文件 {f.Path}——更新包不得携带现场配置，请联系发布方修正", null);

            var full = SafeJoin(Path.Combine(UpdateDirName, f.Path));
            if (full is null)
                return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                    $"清单含非法路径: {f.Path}", null);
            if (!File.Exists(full))
                return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                    $"更新包缺少文件: {f.Path}", null);

            var fi = new FileInfo(full);
            if (fi.Length != f.Size)
                return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                    $"文件大小不符: {f.Path}（期望 {f.Size} 字节，实际 {fi.Length} 字节）", null);

            var actual = ComputeSha256(full);
            if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return new UpdateCheckOutcome(UpdateCheckResult.InvalidPackage,
                    $"SHA256 校验失败: {f.Path}（U 盘拷贝可能损坏，请重新拷贝更新包）", null);
        }

        string chainNote = string.IsNullOrWhiteSpace(manifest.Previous) ? "" : $"（自 v{manifest.Previous} 顺序升级）";
        return new UpdateCheckOutcome(UpdateCheckResult.Available,
            $"发现新版本 v{manifest.Version}{chainNote}，{manifest.Files.Count} 个文件全部校验通过", manifest);
    }

    // ═══════════════════════════════════════════════════════════
    //  准备升级（staging + swap-on-exit）
    // ═══════════════════════════════════════════════════════════

    /// <summary>pending.json 内容（供启动检测与交换脚本判断）</summary>
    internal sealed record PendingUpdate(string TargetVersion, int FileCount);

    /// <summary>
    /// 准备升级：把 _update\ 中通过校验的文件复制到 .update\staging\（排除保护配置），
    /// 写 exclude.txt / UpdateSwap.cmd / pending.json。
    /// 返回后调用方提示用户"即将退出完成更新"，然后拉起 UpdateSwap.cmd 并退出进程。
    /// </summary>
    public void PrepareUpdate(UpdateManifest manifest, string currentVersion)
    {
        // 清掉上次残留的 staging（pending 存在但未消费=上次中断，重新准备即放弃旧暂存）
        var staging = Path.Combine(_workDir, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        int copied = 0, skipped = 0;
        foreach (var f in manifest.Files)
        {
            // 保护文件红线：hardware.json 绝不经更新通道进入 staging（第二道防线，第一道在 CheckForUpdate）
            if (IsProtected(f.Path)) { skipped++; continue; }

            var src = SafeJoin(Path.Combine(UpdateDirName, f.Path))
                      ?? throw new InvalidOperationException($"非法路径拒绝复制: {f.Path}");
            var dst = SafeJoinUnder(staging, f.Path)
                      ?? throw new InvalidOperationException($"非法路径拒绝写入: {f.Path}");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            // 单文件差异跳过：目标已在 staging 且哈希一致则不复制（本次全新 staging 下主要防重复条目）
            File.Copy(src, dst, overwrite: true);
            copied++;
        }

        // xcopy 排除清单（substring 匹配全路径，列出保护文件名即可）
        File.WriteAllText(Path.Combine(_workDir, "exclude.txt"), ProtectedConfigFile + "\n");

        // 交换脚本：ASCII 安全编码（工控机 ANSI 代码页），每次准备升级时重写
        var scriptPath = Path.Combine(_appDir, SwapScriptName);
        File.WriteAllText(scriptPath, BuildSwapScriptContent(manifest.Version),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var pending = new PendingUpdate(manifest.Version, copied);
        File.WriteAllText(
            Path.Combine(_workDir, "pending.json"),
            JsonSerializer.Serialize(pending, s_jsonWrite),
            new System.Text.UTF8Encoding(false));

        System.Diagnostics.Debug.WriteLine(
            $"[Update] 升级到 v{manifest.Version} 已就绪: staging {copied} 个文件（保护排除 {skipped}）。退出后由 {SwapScriptName} 完成交换。");
    }

    /// <summary>是否有待完成的升级（启动时检测用）</summary>
    public static bool HasPendingUpdate(string? appDir = null)
        => File.Exists(GetPendingPath(appDir));

    /// <summary>pending.json 路径</summary>
    internal static string GetPendingPath(string? appDir = null)
        => Path.Combine(appDir ?? AppContext.BaseDirectory, StagingDirName, "pending.json");

    /// <summary>
    /// 生成交换脚本内容（纯 ASCII）。职责：
    /// 1) 等待 UTscan.exe 退出（最多 30s）；2) robocopy 镜像当前应用到 .update\backup（排除保护配置与运行辅助目录）；
    /// 3) xcopy 用 staging 覆盖应用目录（exclude.txt 保证 hardware.json 不被触碰）；
    /// 4) 删 pending.json → 重启程序 → 写日志 → 自删除。
    /// </summary>
    public static string BuildSwapScriptContent(string targetVersion)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem UTscan update swap script - generated by UTscan (ASCII only)");
        sb.AppendLine("setlocal");
        sb.AppendLine("set \"APP=%~dp0\"");
        sb.AppendLine($"rem target version: {targetVersion}");
        sb.AppendLine("set \"STAGE=%APP%.update\\staging\"");
        sb.AppendLine("set \"BAK=%APP%.update\\backup\"");
        sb.AppendLine("set \"PENDING=%APP%.update\\pending.json\"");
        sb.AppendLine("set \"LOG=%APP%update.log\"");
        sb.AppendLine();
        sb.AppendLine("rem -- wait up to 30s for UTscan.exe to exit");
        sb.AppendLine("for /l %%i in (1,1,30) do (");
        sb.AppendLine("  tasklist /FI \"IMAGENAME eq UTscan.exe\" 2>nul | find /I \"UTscan.exe\" >nul || goto :ready");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine(")");
        sb.AppendLine(":ready");
        sb.AppendLine("if not exist \"%STAGE%\\*\" exit /b 1");
        sb.AppendLine();
        sb.AppendLine("rem -- backup current app (mirror). excludes: protected config, runtime helpers");
        sb.AppendLine("if exist \"%BAK%\" rd /s /q \"%BAK%\"");
        sb.AppendLine("mkdir \"%BAK%\"");
        sb.AppendLine("robocopy \"%APP%\" \"%BAK%\" /mir /xf hardware.json update.log /xd .update _update >nul");
        sb.AppendLine("if errorlevel 8 goto :fail_backup");
        sb.AppendLine();
        sb.AppendLine("rem -- overlay staged files; hardware.json is protected via exclude.txt");
        sb.AppendLine("xcopy \"%STAGE%\\*\" \"%APP%\" /e /y /i /q /exclude:%APP%.update\\exclude.txt >nul");
        sb.AppendLine("if errorlevel 4 goto :fail_copy");
        sb.AppendLine();
        sb.AppendLine("rem -- finish: clear pending flag, restart app, log, self-remove");
        sb.AppendLine("del \"%PENDING%\" >nul 2>&1");
        sb.AppendLine("rd /s /q \"%STAGE%\" >nul 2>&1");
        sb.AppendLine("start \"\" \"%APP%UTscan.exe\"");
        sb.AppendLine("echo [%date% %time%] update applied: v" + targetVersion + " >>\"%LOG%\"");
        sb.AppendLine("del \"%~f0\" >nul 2>&1");
        sb.AppendLine("exit /b 0");
        sb.AppendLine();
        sb.AppendLine(":fail_backup");
        sb.AppendLine("echo [%date% %time%] update FAILED at backup stage, app untouched >>\"%LOG%\"");
        sb.AppendLine("exit /b 1");
        sb.AppendLine(":fail_copy");
        sb.AppendLine("echo [%date% %time%] update FAILED at copy stage, restoring from backup >>\"%LOG%\"");
        // B1-FIX（审查 20260828）：原 /xo 排除"源比目标旧"的文件——xcopy 部分覆盖后
        // backup 文件比应用目录旧，全部被排除 → 回滚实际不恢复任何文件。
        // 改用 /e /y 强制覆盖恢复。
        sb.AppendLine("robocopy \"%BAK%\" \"%APP%\" /e /y >nul");
        sb.AppendLine("start \"\" \"%APP%UTscan.exe\"");
        sb.AppendLine("exit /b 1");
        return sb.ToString();
    }

    /// <summary>
    /// 回滚脚本内容（纯 ASCII）：从 .update\backup 恢复上一版（/e 覆盖而非镜像，
    /// 不删除新增文件；backup 不含 hardware.json，故现场配置天然不受影响）。
    /// 由「检查更新」对话框在需要时落盘为 RollbackUpdate.cmd。
    /// </summary>
    public static string BuildRestoreScriptContent()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem UTscan rollback script - generated by UTscan (ASCII only)");
        sb.AppendLine("setlocal");
        sb.AppendLine("set \"APP=%~dp0\"");
        sb.AppendLine("set \"BAK=%APP%.update\\backup\"");
        sb.AppendLine("set \"LOG=%APP%update.log\"");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq UTscan.exe\" 2>nul | find /I \"UTscan.exe\" >nul && (");
        sb.AppendLine("  echo Close UTscan first, then re-run this script.");
        sb.AppendLine("  pause & exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("if not exist \"%BAK%\\*\" (");
        sb.AppendLine("  echo No backup found under .update\\backup - cannot rollback.");
        sb.AppendLine("  pause & exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("robocopy \"%BAK%\" \"%APP%\" /e /y /q >nul");
        sb.AppendLine("if errorlevel 8 (");
        sb.AppendLine("  echo [%date% %time%] rollback FAILED >>\"%LOG%\"");
        sb.AppendLine("  echo Rollback failed. & pause & exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("echo [%date% %time%] rolled back to backup >>\"%LOG%\"");
        sb.AppendLine("echo Rolled back. Starting previous version...");
        sb.AppendLine("start \"\" \"%APP%UTscan.exe\"");
        sb.AppendLine("del \"%~f0\" >nul 2>&1");
        sb.AppendLine("exit /b 0");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    //  工具
    // ═══════════════════════════════════════════════════════════

    /// <summary>计算文件 SHA256（小写十六进制）</summary>
    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>解析语义化版本号为可比较元组；解析失败按 0.0.0</summary>
    internal static (int Major, int Minor, int Patch) ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return (0, 0, 0);
        var core = v.Trim().TrimStart('v', 'V').Split('-')[0];
        var parts = core.Split('.');
        try
        {
            return (
                parts.Length > 0 ? int.Parse(parts[0]) : 0,
                parts.Length > 1 ? int.Parse(parts[1]) : 0,
                parts.Length > 2 ? int.Parse(parts[2]) : 0);
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>语义化版本元组比较（Major→Minor→Patch）</summary>
    internal static int CompareVersions((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        int c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        return a.Patch.CompareTo(b.Patch);
    }

    /// <summary>是否受保护的现场文件（更新通道永不触碰）</summary>
    internal static bool IsProtected(string relativePath)
        => string.Equals(relativePath.Replace('\\', '/').TrimStart('/'),
                         ProtectedConfigFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>安全拼接：拒绝越出应用目录的相对路径（防清单注入 "..\\"）</summary>
    private string? SafeJoin(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_appDir, relativePath));
        var root = Path.GetFullPath(_appDir);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>安全拼接（自定义基目录，供 staging 使用）</summary>
    private static string? SafeJoinUnder(string baseDir, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(baseDir, relativePath));
        var root = Path.GetFullPath(baseDir);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
