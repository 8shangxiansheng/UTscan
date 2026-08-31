using System.Diagnostics;
using System.Windows.Forms;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.Daq;
using UTscan.Hardware.PulseGen;
using UTscan.Hardware.Zmc;
using UTscan.Services;
using UTscan.Services.Connection;

namespace UTscan.UI.Forms;

/// <summary>
/// 主窗体 partial：日志与状态：Log 系列 + 设备状态指示灯 + 状态栏更新。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  日志与状态
    // ════════════════════════════════════════════════════════════════

    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        // P4：文件落盘统一委托 LogFile 门面（消除重复实现）
        string levelStr = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Success => "SUCCESS",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };
        LogFile.Write(message, levelStr);

        if (IsDisposed || Disposing) return;
        if (_txtLog == null) return;
        if (_txtLog.InvokeRequired)
            _txtLog.BeginInvoke(() => AppendLog(message, level));
        else
            AppendLog(message, level);
    }

    private void AppendLog(string message, LogLevel level)
    {
        var color = level switch
        {
            LogLevel.Error => System.Drawing.Color.Red,
            LogLevel.Warning => System.Drawing.Color.DarkOrange,
            LogLevel.Success => System.Drawing.Color.Green,
            LogLevel.Debug => System.Drawing.Color.Gray,
            _ => System.Drawing.Color.Black
        };

        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.SelectionLength = 0;
        _txtLog.SelectionColor = color;
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _txtLog.ScrollToCaret();
    }

    // ════════════════════════════════════════════════════════════════
    //  日志模块（带模块标签的结构化日志）
    // ════════════════════════════════════════════════════════════════

    /// <summary>结构化日志：[模块] 消息，同时写 UI + 文件 + Debug 输出。</summary>
    private void Log(string module, string message, LogLevel level = LogLevel.Info)
        => Log($"[{module}] {message}", level);

    private void LogD(string module, string msg) => Log(module, msg, LogLevel.Debug);
    private void LogI(string module, string msg) => Log(module, msg, LogLevel.Info);
    private void LogS(string module, string msg) => Log(module, msg, LogLevel.Success);
    private void LogW(string module, string msg) => Log(module, msg, LogLevel.Warning);
    private void LogE(string module, string msg) => Log(module, msg, LogLevel.Error);

    private void UpdateDeviceLeds(bool motion, bool daq, bool pulse)
    {
        SetLed(_lblLedMotion, _lblMotionStatus, motion);
        SetLed(_lblLedDaq, _lblDaqStatus, daq);
        SetLed(_lblLedPulse, _lblPulseStatus, pulse);
    }

    private static void SetLed(Label led, Label status, bool connected, bool simulated = false)
    {
        if (connected && simulated)
        {
            // 审查 2026-08-25 P2：仿真连接用黄色警示——与真机绿色区分，防止误当物理设备在测
            led.BackColor = System.Drawing.Color.Gold;
            status.Text = "仿真模式";
            status.ForeColor = System.Drawing.Color.DarkGoldenrod;
        }
        else
        {
            led.BackColor = connected ? System.Drawing.Color.LimeGreen : System.Drawing.Color.DimGray;
            status.Text = connected ? "已连接" : "未连接";
            status.ForeColor = connected ? System.Drawing.Color.Green : System.Drawing.Color.Gray;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  状态栏更新
    // ════════════════════════════════════════════════════════════════

    private void UpdateUserLabel() =>
        _lblUser.Text = _auth.IsLoggedIn
            ? $"用户：{_auth.CurrentUser!.DisplayName} ({_auth.CurrentUser.Role})"
            : "未登录";

        private void UpdateConnLabel() => _lblConn.Text = _config.UseMock
        ? $"状态：Mock 模式（配置 {_configService.ConfigSourcePath}）"
        : $"状态：已配置 ({_config.IpAddress}:{_config.Port})（配置 {_configService.ConfigSourcePath}）";
}
