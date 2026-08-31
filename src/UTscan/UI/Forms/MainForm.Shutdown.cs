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
/// 主窗体 partial：关闭流程（H-7/H-8）：唯一关闭所有者 + DPR 优先关断顺序。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  关闭流程（H-7/H-8：唯一关闭所有者 + DPR 优先顺序）
    // ════════════════════════════════════════════════════════════════

    private bool _shutdownStarted;
    private Task? _shutdownTask;

    /// <summary>H-8：唯一关闭 Task。任何调用方（OnFormClosing / Program.Dispose）都等待同一个 Task，避免并发 Dispose 同一硬件对象。</summary>
    public Task ShutdownCompletion => _shutdownTask ?? Task.CompletedTask;

    /// <summary>H-8：确保关闭只启动一次，返回唯一的关闭 Task。</summary>
    private Task EnsureShutdownStarted()
        => _shutdownTask ??= ShutdownHardwareAsync();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_shutdownStarted)
        {
            _shutdownStarted = true;
            e.Cancel = true;   // 取消本次关闭，等异步关闭完成后再 Close
            _ = ShutdownAndCloseAsync();
        }
        base.OnFormClosing(e);
    }

    private async Task ShutdownAndCloseAsync()
    {
        bool closeWindow = false;
        try
        {
            LogI("系统", "正在关闭系统...");
            Task shutdown = EnsureShutdownStarted();

            // H-8：超时只能提示"硬件仍在关闭，暂不退出"，不能继续 Close 后再启动另一套 Dispose。
            Task completed = await Task.WhenAny(shutdown, Task.Delay(5000));
            if (completed != shutdown)
            {
                Debug.WriteLine("[MainForm] 硬件关闭仍在执行（5s），暂不退出窗体——请稍后重试关闭");
                ShowShutdownPendingWarning();
                _shutdownStarted = false;   // 允许用户稍后重试关闭
                return;                     // 不 Close
            }

            await shutdown;   // 传播关闭结果（异常会进 catch）
            LogS("系统", "硬件资源已释放");
            closeWindow = true;
        }
        catch (Exception ex)
        {
            LogW("系统", $"硬件关闭未全部成功: {ex.Message}");
            _shutdownTask = null;
            _shutdownStarted = false;
            ShowShutdownPendingWarning();
        }
        finally
        {
            if (closeWindow && !IsDisposed && !Disposing)
            {
                try { Close(); } catch { /* 关闭失败不阻止进程结束 */ }
            }
        }
    }

    private void ShowShutdownPendingWarning()
    {
        try
        {
            if (InvokeRequired)
                BeginInvoke(new Action(() =>
                    MessageBox.Show(this, "硬件正在关闭中，暂不退出。请稍后再次关闭窗口。",
                        "关闭进行中", MessageBoxButtons.OK, MessageBoxIcon.Information)));
            else
                MessageBox.Show(this, "硬件正在关闭中，暂不退出。请稍后再次关闭窗口。",
                    "关闭进行中", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { /* UI 不可用时忽略 */ }
    }

    /// <summary>
    /// H-7：硬件关闭顺序——DPR 关断并确认 → Spectrum Stop/Reset → ZMC 急停/断开。
    /// 每一步独立执行，前一步失败不能跳过后续安全动作；最终聚合为 AggregateException。
    /// DPR 始终优先关断（高压输出必须立即关闭）。
    /// </summary>
    private async Task ShutdownHardwareAsync()
    {
        List<Exception> errors = new();

        // 连接与关闭必须串行，避免 Open/Start 与 Disconnect/Stop 竞争同一原生句柄。
        if (_connectTask is { IsCompleted: false })
        {
            try { await _connectTask; }
            catch (Exception ex) { errors.Add(ex); }
        }

        // 1. DPR500 关断确认（高压优先）
        try { await _pulse.DisconnectAsync(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainForm] DPR 关闭失败: {ex.Message}");
            errors.Add(ex);
        }

        // 2. Spectrum 停止采集（常规停机路径；故障复位场景由 SafeResetAll 另走 ResetAsync）
        try { await _daq.StopAsync(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainForm] DAQ 停止失败: {ex.Message}");
            errors.Add(ex);
        }

        // 3. ZMC 急停 + 断开。两设备联调阶段配置禁用时不访问运动控制器。
        if (_config.EnableMotionController)
        {
            try { await _motion.EmergencyStopAsync(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainForm] ZMC 急停失败: {ex.Message}");
                errors.Add(ex);
            }
            try { await _motion.DisconnectAsync(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainForm] ZMC 断开失败: {ex.Message}");
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
            throw new AggregateException("硬件关闭未全部成功", errors);
    }
}
