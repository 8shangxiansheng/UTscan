using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UTscan.Core.Enums;
using UTscan.Core.Exceptions;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.Daq;
using UTscan.Hardware.PulseGen;
using UTscan.Hardware.Zmc;
using UTscan.Services;

namespace UTscan.Services.Connection;

/// <summary>连接编排器（P2）：承载三硬件连接核心逻辑，通过事件通知 UI 更新。</summary>
public sealed class ConnectionOrchestrator
{
    private readonly IMotionController _motion;
    private readonly IDataAcquisition _daq;
    private readonly IPulseGenerator _pulse;
    private readonly ConnectionConfig _config;

    public ConnectionOrchestrator(IMotionController motion, IDataAcquisition daq, IPulseGenerator pulse, ConnectionConfig config)
    {
        _motion = motion;
        _daq = daq;
        _pulse = pulse;
        _config = config;
    }

    // ── 事件 ──
    public event Action<string, string, string>? LogEvent;       // (module, level, message)
    public event Action<string, string, bool>? DeviceConnected;  // (device, colorName, simulated)
    public event Action<string, string>? DeviceDisconnected;     // (device, colorName)
    public event Action<string, string>? StatusText;             // (text, colorName)
    public event Action<Exception>? FatalError;                  // 需要弹窗的错误
    public event Action<string>? DaqControlsApplied;             // DAQ 按钮使能
    public event Action<string>? PulseControlsApplied;           // 脉冲按钮使能
    public event Action<bool>? PulseOutputUiRefresh;             // RefreshPulseOutputUi(simulated)
    public event Action? DaqReadbackRequested;                   // 触发回读 DAQ 参数到控件
    public event Action? PulseReadbackRequested;                 // 触发回读脉冲参数到控件
    public event Action? MotionControlsApplied;                  // 运动面板使能 + 启动轴监控

    private void Log(string module, string level, string msg)
    {
        LogFile.Write($"[{module}] {msg}", level);
        LogEvent?.Invoke(module, level, msg);
    }

    /// <summary>连接核心序列（原 MainForm.ConnectHardwareCoreAsync）。</summary>
    public async Task<ConnectionResult> ConnectAsync(DaqSnapshot snapshot)
    {
        bool motionOpened = false, daqOpened = false, pulseOpened = false;
        bool motionOk = false, daqOk = false, pulseOk = false;

        try
        {
            Log("系统", "INFO", $"开始连接... IP={_config.IpAddress}:{_config.Port}  模式={(_config.UseMock ? "Mock" : "真机")}");

            // ── 0. 应用 DAQ 参数 ──
            ApplyDaqParams(snapshot);
            Log("系统", "DEBUG", "DAQ 参数已应用到硬件对象");

            // ══════════════════════════════════════════════════════
            //  1. 运动控制器
            // ══════════════════════════════════════════════════════
            if (_config.EnableMotionController)
            {
                Log("ZMC", "INFO", "正在连接运动控制器...");
                try
                {
                    motionOk = await _motion.ConnectAsync(_config);
                    motionOpened = motionOk;
                    if (motionOk)
                    {
                        var xEnabled = await _motion.EnableAxisAsync(AxisId.X);
                        var yEnabled = await _motion.EnableAxisAsync(AxisId.Y);
                        var zEnabled = await _motion.EnableAxisAsync(AxisId.Z);
                        motionOk = xEnabled && yEnabled && zEnabled;
                        if (!motionOk)
                            throw new HardwareConnectionException("运动控制器已连接，但 XYZ 轴未全部使能成功");
                        Log("ZMC", "SUCCESS", "运动控制器已连接，XYZ 轴已使能");
                    }
                    else
                    {
                        Log("ZMC", "WARNING", "运动控制器连接失败（检查 IP/串口与上电状态），运动控制功能不可用");
                        if (!string.IsNullOrEmpty(_motion.LastConnectError))
                            Log("ZMC", "WARNING", $"  错误: {_motion.LastConnectError}");
                    }
                }
                catch (Exception ex)
                {
                    motionOk = false;
                    Log("ZMC", "WARNING", $"运动控制器连接异常: {ex.Message}");
                }
            }
            else
            {
                Log("ZMC", "INFO", "运动控制器已由 hardware.json 禁用；本阶段仅联调 DPR500 + Spectrum，接口保留");
            }

            // ══════════════════════════════════════════════════════
            //  2. Spectrum DAQ 采集卡
            // ══════════════════════════════════════════════════════
            Log("DAQ", "INFO", "正在初始化 Spectrum M3i.3242 采集卡...");
            try
            {
                // 2a. 检查驱动 DLL
                var dllPath = Path.Combine(AppContext.BaseDirectory, "spcm_win32.dll");
                bool dllExists = File.Exists(dllPath);
                Log("DAQ", "DEBUG", $"spcm_win32.dll: {(dllExists ? $"已就位 ({new FileInfo(dllPath).Length / 1024} KB)" : "未找到")}");
                if (!dllExists)
                {
                    Log("DAQ", "ERROR", "spcm_win32.dll 未找到——请安装 Spectrum 驱动 (CD_SPCM)");
                    Log("DAQ", "ERROR", "  排查: 1) 安装 Spectrum Driver CD 2) 确认 spcm_win32.dll 在程序目录 3) 以 x86 模式运行");
                }

                // 2b. 初始化
                Log("DAQ", "DEBUG", "调用 InitializeAsync (spcm_hOpen + 寄存器配置 + DMA 缓冲)...");
                daqOk = await _daq.InitializeAsync(_config);
                daqOpened = daqOk;

                if (daqOk)
                {
                    Log("DAQ", "DEBUG", "  初始化成功: Handle 已打开");

                    // 2d. 启动连续采集
                    Log("DAQ", "DEBUG", "调用 StartContinuousAsync (WAITDMA 线程)...");
                    await _daq.StartContinuousAsync();
                    Log("DAQ", "SUCCESS", "采集卡已连接，连续采集已启动");

                    // 显示卡参数（Capabilities 为 Spectrum 专有能力描述，保留具体类型分支）
                    if (_daq is SpectrumDaqCard spectrum && spectrum.Capabilities != null)
                        Log("DAQ", "DEBUG", $"  能力: {spectrum.Capabilities.Describe()}");
                    Log("DAQ", "DEBUG", $"  采样率: {_config.SampleRate / 1e6:F1} MHz, 点数: {_config.SampleCount}");

                    DeviceConnected?.Invoke("DAQ", "Green", _config.UseMock);
                    DaqControlsApplied?.Invoke("DAQ");
                    DaqReadbackRequested?.Invoke();
                }
                else
                {
                    Log("DAQ", "WARNING", "采集卡初始化失败——采集功能不可用");
                    if (!string.IsNullOrEmpty(_daq.LastConnectError))
                        Log("DAQ", "WARNING", $"  SDK 错误: {_daq.LastConnectError}");
                    Log("DAQ", "WARNING", "  排查: 1) 采集卡是否插入 PCIe 插槽 2) Spectrum 驱动已安装 3) 设备管理器无黄色感叹号 4) 无其他程序占用");
                    DeviceDisconnected?.Invoke("DAQ", "Gray");
                }
            }
            catch (SpectrumDaqException ex)
            {
                daqOk = false;
                if (daqOpened) { try { await _daq.StopAsync(); } catch { } daqOpened = false; }
                Log("DAQ", "ERROR", $"采集卡异常: {ex.Message}");
                Log("DAQ", "WARNING", "  排查: 1) PCIe 卡是否松动 2) 驱动版本是否匹配 3) 其他 Spectrum 程序是否已关闭");
                DeviceDisconnected?.Invoke("DAQ", "Gray");
            }
            catch (Exception ex)
            {
                daqOk = false;
                if (daqOpened) { try { await _daq.StopAsync(); } catch { } daqOpened = false; }
                Log("DAQ", "ERROR", $"采集卡连接异常: {ex.GetType().Name}: {ex.Message}");
                DeviceDisconnected?.Invoke("DAQ", "Gray");
            }

            // ══════════════════════════════════════════════════════
            //  3. DPR500 脉冲收发仪
            // ══════════════════════════════════════════════════════
            Log("DPR", "INFO", $"正在连接脉冲收发仪 (JSR Common SDK, 超时={Math.Max(_config.TimeoutMs, 5000)}ms)...");
            try
            {
                bool jsrDll = JsrNative.IsDllAvailable();
                Log("DPR", "DEBUG", $"JSR_Common3264.dll: {(jsrDll ? "已就位" : "未找到")}");
                if (!jsrDll)
                {
                    Log("DPR", "ERROR", "JSR_Common3264.dll 未找到——请安装 JSR Control Panel SDK");
                    Log("DPR", "ERROR", "  排查: 1) 运行 JSRControlPanelInstaller 2) 确认 DLL 在程序目录 3) 确认 JSR_Common3264.dll 非零字节");
                }

                Log("DPR", "DEBUG", "调用 JSR_OpenLibrary (扫描 USB/COM 端口)...");
                pulseOk = await _pulse.ConnectAsync(_config);
                pulseOpened = pulseOk;

                if (pulseOk)
                {
                    if (!_config.UseMock && _pulse.ConnectionKind != DprConnectionKind.Physical)
                    {
                        throw new HardwareConnectionException($"DPR500 连接种类为 {_pulse.ConnectionKind}，真机模式要求 Physical");
                    }

                    if (_pulse.IsConnected)
                    {
                        var info = _pulse.InstrumentInfo;
                        Log("DPR", "SUCCESS", "脉冲收发仪已连接");
                        Log("DPR", "DEBUG", $"  型号: {info.ModelName}, 序列号: {info.SerialNumber}");
                        Log("DPR", "DEBUG", $"  COM 端口: COM{info.ComPort}, 脉冲器: {info.PulserModelName}");
                        Log("DPR", "DEBUG", $"  接收器: {info.ReceiverModelName} ({info.ReceiverBandwidthMHz}MHz)");
                    }
                    else
                    {
                        Log("DPR", "SUCCESS", "脉冲收发仪已连接（仿真模式）");
                    }

                    bool simulated = _pulse.ConnectionKind == DprConnectionKind.Simulation;
                    DeviceConnected?.Invoke("DPR", "Green", simulated);
                    PulseControlsApplied?.Invoke("DPR");
                    PulseOutputUiRefresh?.Invoke(false);
                    PulseReadbackRequested?.Invoke();
                }
                else
                {
                    Log("DPR", "WARNING", "脉冲收发仪连接失败——脉冲功能不可用");
                    if (!string.IsNullOrEmpty(_pulse.LastConnectError))
                        Log("DPR", "WARNING", $"  SDK 错误: {_pulse.LastConnectError}");
                    Log("DPR", "WARNING", "  排查: 1) DPR500 已上电 2) USB/串口线已连接");
                    Log("DPR", "WARNING", "  3) 串口参数 4800,8,N,1 4) JSR SDK 已安装 5) 设备管理器 COM 端口正常");
                    Log("DPR", "WARNING", $"  6) 超时={Math.Max(_config.TimeoutMs, 5000)}ms（设备未上电扫描较慢建议≥8000ms）");
                    DeviceDisconnected?.Invoke("DPR", "Gray");
                }
            }
            catch (Exception ex)
            {
                pulseOk = false;
                if (pulseOpened) { try { await _pulse.DisconnectAsync(); } catch { } pulseOpened = false; }
                Log("DPR", "ERROR", $"脉冲收发仪连接异常: {ex.GetType().Name}: {ex.Message}");
                DeviceDisconnected?.Invoke("DPR", "Gray");
            }

            // ══════════════════════════════════════════════════════
            //  4. 运动控制器 LED + 控件使能
            // ══════════════════════════════════════════════════════
            if (motionOk)
                MotionControlsApplied?.Invoke();
            else
                DeviceDisconnected?.Invoke("ZMC", "Gray");

            // ══════════════════════════════════════════════════════
            //  5. 全局状态汇总
            // ══════════════════════════════════════════════════════
            int requiredCount = _config.EnableMotionController ? 3 : 2;
            int connectedCount = (daqOk ? 1 : 0) + (pulseOk ? 1 : 0)
                + (_config.EnableMotionController && motionOk ? 1 : 0);

            string statusText, statusColor;
            if (connectedCount == requiredCount)
            {
                statusText = _config.UseMock ? "状态：Mock 已连接"
                    : _config.EnableMotionController ? "状态：全部已连接"
                    : "状态：DPR500 + Spectrum 已连接（运动已禁用）";
                statusColor = "ControlText";
            }
            else if (connectedCount > 0)
            {
                var parts = new List<string>();
                if (_config.EnableMotionController && !motionOk) parts.Add("运动控制器");
                if (!daqOk) parts.Add("采集卡");
                if (!pulseOk) parts.Add("脉冲收发仪");
                statusText = $"状态：部分已连接（{string.Join("、", parts)} 未连接）";
                statusColor = "DarkOrange";
            }
            else
            {
                statusText = "状态：全部连接失败";
                statusColor = "Red";
            }
            StatusText?.Invoke(statusText, statusColor);

            // ── 6. 连接汇总日志 ──
            string motionStr = _config.EnableMotionController ? (motionOk ? "OK" : "FAIL") : "SKIP";
            string daqStr = daqOk ? "OK" : "FAIL";
            string pulseStr = pulseOk ? "OK" : "FAIL";
            string summary = $"连接完成: 运动={motionStr}, 采集卡={daqStr}, 脉冲={pulseStr}";
            if (connectedCount == requiredCount) Log("系统", "SUCCESS", summary);
            else if (connectedCount > 0) Log("系统", "WARNING", summary);
            else Log("系统", "ERROR", summary);

            if (daqOk && pulseOk)
            {
                Log("系统", "INFO", "脉冲→采集链路已初始化: DPR500 TRIG/SYNC → Spectrum EXT0；是否实时以A扫新帧指示为准");
                Log("系统", "INFO", "请在『脉冲』页点击【启用发射】以开始发射（默认不发射，安全设计）");
            }
            if (_config.EnableMotionController && !motionOk)
                Log("ZMC", "WARNING", "运动控制器不可用——扫描/定位/回零功能暂停，待排查后重连");

            return new ConnectionResult { MotionOk = motionOk, DaqOk = daqOk, PulseOk = pulseOk,
                MotionOpened = motionOpened, DaqOpened = daqOpened, PulseOpened = pulseOpened,
                RequiredCount = requiredCount, ConnectedCount = connectedCount };
        }
        catch (Exception ex)
        {
            await RollbackAsync(pulseOpened, daqOpened, motionOpened);
            DeviceDisconnected?.Invoke("ZMC", "Gray");
            DeviceDisconnected?.Invoke("DAQ", "Gray");
            DeviceDisconnected?.Invoke("DPR", "Gray");
            Log("系统", "ERROR", $"连接异常: {ex.GetType().Name}: {ex.Message}");
            FatalError?.Invoke(ex);
            return new ConnectionResult { MotionOk = false, DaqOk = false, PulseOk = false, MotionOpened = motionOpened, DaqOpened = daqOpened, PulseOpened = pulseOpened };
        }
    }

    /// <summary>M-1：连接事务回滚——顺序 DPR → DAQ → ZMC，每步独立 try/catch。</summary>
    private async Task RollbackAsync(bool pulseOpened, bool daqOpened, bool motionOpened)
    {
        if (pulseOpened) { try { await _pulse.DisconnectAsync(); } catch (Exception ex) { Log("DPR", "WARNING", $"回滚断开失败: {ex.Message}"); } }
        if (daqOpened) { try { await _daq.StopAsync(); } catch (Exception ex) { Log("DAQ", "WARNING", $"回滚停止失败: {ex.Message}"); } }
        if (motionOpened) { try { await _motion.EmergencyStopAsync(); } catch (Exception ex) { Log("ZMC", "WARNING", $"回滚急停失败: {ex.Message}"); } try { await _motion.DisconnectAsync(); } catch (Exception ex) { Log("ZMC", "WARNING", $"回滚断开失败: {ex.Message}"); } }
    }

    /// <summary>断开所有已连接设备（原 OnDisconnectClick 逻辑）。</summary>
    public async Task DisconnectAsync()
    {
        var errors = new List<string>();
        Log("系统", "INFO", "开始断开连接...");

        try { await _pulse.DisconnectAsync(); }
        catch (Exception ex) { errors.Add($"DPR: {ex.Message}"); Log("DPR", "ERROR", $"断开失败: {ex.Message}"); }
        try { await _daq.StopAsync(); }
        catch (Exception ex) { errors.Add($"DAQ: {ex.Message}"); Log("DAQ", "ERROR", $"停止失败: {ex.Message}"); }
        if (_config.EnableMotionController)
        {
            try { await _motion.DisconnectAsync(); }
            catch (Exception ex) { errors.Add($"ZMC: {ex.Message}"); Log("ZMC", "ERROR", $"断开失败: {ex.Message}"); }
        }

        if (errors.Count == 0) Log("系统", "SUCCESS", "所有已启用设备已断开");
        else Log("系统", "ERROR", $"硬件断开不完整: {string.Join(" | ", errors)}");
    }

    /// <summary>应用 DAQ 参数快照到硬件（原 MainForm.ApplyDaqParamsToHardware）。供连接序列与手动应用参数复用。</summary>
    public void ApplyDaqParams(DaqSnapshot snapshot)
    {
        if (_daq is SpectrumDaqCard spectrum)
        {
            spectrum.AcquisitionMode = snapshot.AcquisitionMode;
            spectrum.ChannelMask = snapshot.ChannelMask;
            spectrum.InputRangeMv = snapshot.InputRangeMv;
            spectrum.InputFiftyOhm = snapshot.InputFiftyOhm;
            spectrum.Averages = snapshot.Averages;
            spectrum.EnableTimestamp = snapshot.EnableTimestamp;
            spectrum.ExternalTriggerLevelMv = snapshot.ExternalTriggerLevelMv;
            spectrum.TriggerDelayUs = snapshot.TriggerDelayUs;
        }
        _config.SampleRate = snapshot.SampleRate;
        _config.SampleCount = snapshot.SampleCount;
    }
}

/// <summary>连接结果（P2）</summary>
public sealed class ConnectionResult
{
    public bool MotionOk, DaqOk, PulseOk;
    public bool MotionOpened, DaqOpened, PulseOpened;
    public int RequiredCount, ConnectedCount;
}