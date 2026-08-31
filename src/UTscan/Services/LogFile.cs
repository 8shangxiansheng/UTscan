using System;
using System.IO;

namespace UTscan.Services;

/// <summary>
/// 文件日志门面（R4/P2）：统一发布版落盘通道 %APPDATA%\UTscan\utscan.log。
/// 与 MainForm.Log 的文件落盘行为一致（追加写、失败静默）。
/// 连接编排等非 UI 服务直接用此门面，不依赖窗体线程。
/// </summary>
public static class LogFile
{
    /// <summary>日志文件路径（%APPDATA%\UTscan\utscan.log）</summary>
    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UTscan", "utscan.log");

    /// <summary>写一行日志（级别标签 + 时间戳）。失败静默不影响主流程。</summary>
    public static void Write(string message, string level = "INFO")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
