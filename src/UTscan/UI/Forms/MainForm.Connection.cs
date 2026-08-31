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
/// 主窗体 partial：连接/断开与 ConnectionOrchestrator 事件订阅。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  连接 / 断开
    // ════════════════════════════════════════════════════════════════

    private void OnConnectClick(object? sender, EventArgs e)
    {
        ConnectionConfig edited;
        using (var dlg = new ConnectionForm(_config) { ShowMockSwitch = false })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            edited = dlg.Config;
        }
        _config.IpAddress = edited.IpAddress;
        _config.Port = edited.Port;
        _config.TimeoutMs = edited.TimeoutMs;   // H-A：对话框超时真实回传（DPR 连接消费）
        _config.TriggerIo = edited.TriggerIo;               // D2-FIX：触发 IO 回传（严格单次触发消费）
        _config.TriggerPulseWidthMs = edited.TriggerPulseWidthMs;
        BeginConnectSequence();
    }

    /// <summary>
    /// 启动硬件连接序列（UI 线程调用）：捕获 DAQ 参数快照后于后台线程执行连接核心。
    /// 防重入；手动“连接”菜单与程序启动自动连接共用，避免并发连接导致状态错乱。
    /// P2：连接编排已下沉至 ConnectionOrchestrator，此处只做快照捕获与任务调度。
    /// </summary>
    private void BeginConnectSequence()
    {
        if (_connectRunning) return;
        _connectRunning = true;
        SetConnectMenuItemEnabled(false);

        // NM-3：UI 线程捕获 DAQ 参数快照（后台线程不读控件）
        DaqSnapshot snapshot = CaptureDaqParamsSnapshot();

        _connectTask = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.ConnectAsync(snapshot);
            }
            finally
            {
                _connectRunning = false;
                SetConnectMenuItemEnabled(true);
            }
        });
    }

    private void SetConnectMenuItemEnabled(bool enabled)
    {
        if (_connectMenuItem is null) return;
        if (InvokeRequired)
            BeginInvoke(new Action(() => { if (_connectMenuItem is not null) _connectMenuItem.Enabled = enabled; }));
        else
            _connectMenuItem.Enabled = enabled;
    }

    private void OnDisconnectClick(object? sender, EventArgs e)
    {
        Task.Run(async () =>
        {
            var errors = new List<string>();
            LogI("系统", "开始断开连接...");

            if (_connectTask is { IsCompleted: false })
            {
                LogI("系统", "等待正在进行的硬件连接结束后再断开...");
                try { await _connectTask; }
                catch (Exception ex) { errors.Add($"连接任务: {ex.Message}"); }
            }

            // 高压优先；任一设备失败不能阻止其余设备进入安全状态。
            // P2：断开编排已下沉至 ConnectionOrchestrator。
            await _orchestrator.DisconnectAsync();

            try
            {
                BeginInvoke(new Action(() =>
                {
                    _lblConn.Text = errors.Count == 0 ? "状态：已断开" : "状态：断开不完整，请检查日志";
                    _lblConn.ForeColor = errors.Count == 0
                        ? System.Drawing.SystemColors.ControlText
                        : System.Drawing.Color.Red;
                    _btnPulseApply.Enabled = false;
                    _btnPulseReadback.Enabled = false;
                    if (_btnPulseLed != null) _btnPulseLed.Enabled = false;
                    _btnPulseOutput.Enabled = false;
                    RefreshPulseOutputUi(false);
                    _btnDaqApply.Enabled = false;
                    _btnDaqStart.Enabled = false;
                    _btnDaqStop.Enabled = false;
                    _btnMoveTo.Enabled = false;
                    _axisMonitor.Stop();
                    _axisAlarm = false;
                    UpdateDeviceLeds(false, false, false);
                }));
                if (errors.Count == 0) LogS("系统", "所有已启用设备已断开");
                else LogE("系统", $"硬件断开不完整: {string.Join(" | ", errors)}");
            }
            catch (Exception ex)
            {
                LogE("系统", $"断开界面更新失败: {ex.Message}");
                BeginInvoke(new Action(() =>
                    MessageBox.Show(this, $"断开结果显示失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)));
            }
        });
    }

    // ════════════════════════════════════════════════════════════════
    //  ConnectionOrchestrator 事件订阅（P2：连接编排逻辑下沉，UI 只做更新）
    // ════════════════════════════════════════════════════════════════

    private void SubscribeOrchestrator()
    {
        _orchestrator.LogEvent += (module, level, msg) =>
        {
            var mapped = level switch
            {
                "ERROR" => LogLevel.Error,
                "WARNING" => LogLevel.Warning,
                "SUCCESS" => LogLevel.Success,
                "DEBUG" => LogLevel.Debug,
                _ => LogLevel.Info
            };
            Log(module, msg, mapped);
        };

        _orchestrator.DeviceConnected += (device, color, simulated) =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => OnDeviceConnected(device, color, simulated)));
            else OnDeviceConnected(device, color, simulated);
        };

        _orchestrator.DeviceDisconnected += (device, color) =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => OnDeviceDisconnected(device, color)));
            else OnDeviceDisconnected(device, color);
        };

        _orchestrator.StatusText += (text, color) =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => { _lblConn.Text = text; _lblConn.ForeColor = ParseColor(color); }));
            else { _lblConn.Text = text; _lblConn.ForeColor = ParseColor(color); }
        };

        _orchestrator.FatalError += ex =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => MessageBox.Show(this, $"连接异常：{ex.Message}", "连接异常", MessageBoxButtons.OK, MessageBoxIcon.Error)));
            else MessageBox.Show(this, $"连接异常：{ex.Message}", "连接异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        _orchestrator.DaqControlsApplied += _ =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => { _btnDaqApply.Enabled = true; _btnDaqStart.Enabled = false; _btnDaqStop.Enabled = true; }));
            else { _btnDaqApply.Enabled = true; _btnDaqStart.Enabled = false; _btnDaqStop.Enabled = true; }
        };

        _orchestrator.DaqReadbackRequested += () =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke(new Action(ReadbackDaqParams));
            else ReadbackDaqParams();
        };

        _orchestrator.PulseControlsApplied += _ =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => { _btnPulseApply.Enabled = true; if (_btnPulseLed != null) _btnPulseLed.Enabled = true; _btnPulseReadback.Enabled = true; _btnPulseOutput.Enabled = true; }));
            else { _btnPulseApply.Enabled = true; if (_btnPulseLed != null) _btnPulseLed.Enabled = true; _btnPulseReadback.Enabled = true; _btnPulseOutput.Enabled = true; }
        };

        _orchestrator.PulseOutputUiRefresh += simulated =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke(new Action(() => RefreshPulseOutputUi(simulated)));
            else RefreshPulseOutputUi(simulated);
        };

        _orchestrator.PulseReadbackRequested += () =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke(new Action(ReadbackPulseParams));
            else ReadbackPulseParams();
        };

        _orchestrator.MotionControlsApplied += () =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
                BeginInvoke(new Action(() => { SetLed(_lblLedMotion, _lblMotionStatus, true); _btnMoveTo.Enabled = true; _axisMonitor.Start(); }));
            else { SetLed(_lblLedMotion, _lblMotionStatus, true); _btnMoveTo.Enabled = true; _axisMonitor.Start(); }
        };
    }

    private void OnDeviceConnected(string device, string color, bool simulated)
    {
        switch (device)
        {
            case "DAQ": SetLed(_lblLedDaq, _lblDaqStatus, true, simulated: simulated); break;
            case "DPR": SetLed(_lblLedPulse, _lblPulseStatus, true, simulated: simulated); break;
            case "ZMC": SetLed(_lblLedMotion, _lblMotionStatus, true, simulated: simulated); break;
        }
    }

    private void OnDeviceDisconnected(string device, string color)
    {
        switch (device)
        {
            case "DAQ": SetLed(_lblLedDaq, _lblDaqStatus, false); break;
            case "DPR": SetLed(_lblLedPulse, _lblPulseStatus, false); break;
            case "ZMC": SetLed(_lblLedMotion, _lblMotionStatus, false); break;
        }
    }

    private static System.Drawing.Color ParseColor(string color) => color switch
    {
        "Red" => System.Drawing.Color.Red,
        "DarkOrange" => System.Drawing.Color.DarkOrange,
        "Green" => System.Drawing.Color.Green,
        "Gray" => System.Drawing.Color.Gray,
        _ => System.Drawing.SystemColors.ControlText
    };
}
