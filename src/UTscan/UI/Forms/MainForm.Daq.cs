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
/// 主窗体 partial：信号采集面板：DAQ 参数快照/联动/应用/启停/回读。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  采集参数应用
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// NM-3：在 UI 线程捕获 DAQ 控件参数（必须在 Task.Run 之前调用）。M-2：返回不可变快照供后台传值使用。
    /// P2：DaqSnapshot 已提升至共享模型 Core.Models。
    /// </summary>
    private DaqSnapshot CaptureDaqParamsSnapshot()
    {
        var snap = new DaqSnapshot
        {
            AcquisitionMode = (SpectrumAcquisitionMode)_cmbAcqMode.SelectedIndex,
            ChannelMask = _cmbDaqChannel.SelectedIndex switch { 0 => SpectrumNative.CHANNEL0, 1 => SpectrumNative.CHANNEL1, _ => 0x3 },
            InputRangeMv = int.Parse((string)_cmbInputRange.SelectedItem!),
            InputFiftyOhm = _cmbImpedance.SelectedIndex == 0,
            Averages = (int)_numAverages.Value,
            EnableTimestamp = _chkTimestamp.Checked,
            ExternalTriggerLevelMv = (int)_numTrigLevelMv.Value,
            TriggerDelayUs = (float)_numTrigDelayUs.Value,
        };
        float sampleRate = (float)((double)_numSampleRateMHz.Value * 1e6);
        float sampleLengthUs = (float)_numSampleLengthUs.Value;
        snap = snap with { SampleRate = sampleRate };
        // 采样点数：优先用采样点数控件值（用户直接配置）；否则由长度×采样率换算
        snap = snap with { SampleCount = Math.Max(16, (int)(sampleLengthUs * sampleRate / 1e6)) };
        return snap;
    }

    /// <summary>采样点数 ↔ 采样长度双向联动：改点数 → 长度 = 点数×1e6/采样率；改长度 → 点数 = 长度×采样率/1e6。</summary>
    private void SyncSampleCountToLength()
    {
        if (_updatingSampleSync || _numSampleCount == null) return;
        _updatingSampleSync = true;
        try
        {
            float rate = (float)((double)_numSampleRateMHz.Value * 1e6);
            if (rate <= 0) return;
            // 改点数 → 更新长度
            float lenUs = (float)_numSampleCount.Value * 1e6f / rate;
            _numSampleLengthUs.Value = Math.Clamp((decimal)Math.Max(lenUs, 0.1f), _numSampleLengthUs.Minimum, _numSampleLengthUs.Maximum);
        }
        finally { _updatingSampleSync = false; }
    }

    /// <summary>采样长度/采样率变化时反向同步采样点数（改长度或采样率 → 点数刷新）。</summary>
    private void SyncSampleLengthToCount()
    {
        if (_updatingSampleSync || _numSampleCount == null) return;
        _updatingSampleSync = true;
        try
        {
            float rate = (float)((double)_numSampleRateMHz.Value * 1e6);
            float lenUs = (float)_numSampleLengthUs.Value;
            if (rate <= 0) return;
            _numSampleCount.Value = Math.Clamp((decimal)Math.Max(16, (int)(lenUs * rate / 1e6)), _numSampleCount.Minimum, _numSampleCount.Maximum);
        }
        finally { _updatingSampleSync = false; }
    }

    private async Task ApplyDaqParamsAsync()
    {
        // M-2 修复：每次点击应用时在 UI 线程创建不可变快照，作为值参数传入后台方法，不再依赖共享字段。
        // 成功日志只能使用 snapshot 或硬件回读值，不能再次读取可能已改变的控件值。
        DaqSnapshot snapshot = CaptureDaqParamsSnapshot();
        try
        {
            LogI("DAQ", "应用采集参数...");
            bool wasRunning = _daq.IsRunning;
            if (wasRunning) await _daq.StopAsync();

            _orchestrator.ApplyDaqParams(snapshot);
            bool ok = await _daq.InitializeAsync(_config);

            if (ok && wasRunning)
                await _daq.StartContinuousAsync();
            // M-2：初始化失败时不要自动恢复"运行中"UI

            ReadbackDaqParams();
            ResetOpenAscanDisplays();   // P3：采样窗口变更后，重置已打开的 A 扫显示窗口/纵轴

            // H1-FIX：应用成功后把采样参数回写 hardware.json（持久化，重启后保持）
            if (ok)
                _configService.SaveSampleParams((int)_config.SampleRate, _config.SampleCount);

            // M-2：成功日志使用 snapshot 值（不读控件）
            if (ok) LogS("DAQ", $"采集参数已应用: 采样率={snapshot.SampleRate / 1e6:F1}MHz, 采样长度={snapshot.SampleCount / (snapshot.SampleRate / 1e6f):F1}µs, " +
                $"量程=±{snapshot.InputRangeMv}mV, 通道=0x{snapshot.ChannelMask:X}, 模式={snapshot.AcquisitionMode}, " +
                $"阻抗={(snapshot.InputFiftyOhm ? "50Ω" : "1MΩ")}, 平均={snapshot.Averages}, 时间戳={(snapshot.EnableTimestamp ? "开" : "关")}, " +
                $"触发电平={snapshot.ExternalTriggerLevelMv}mV");
            else LogE("DAQ", $"采集参数应用失败: 采样率={snapshot.SampleRate / 1e6:F1}MHz, 量程=±{snapshot.InputRangeMv}mV, 通道=0x{snapshot.ChannelMask:X}");
        }
        catch (Exception ex)
        {
            LogE("DAQ", $"采集参数应用失败: {ex.Message}");
            MessageBox.Show(this, $"采集参数应用失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task DaqStartAsync()
    {
        // 诊断（停止→开始失败定位）：入口记录 UI 按钮态 + 硬件全链路状态
        string daqState = _daq.DescribeState();
        LogD("DAQ", $"[诊断] 点击开始采集：btnStart={_btnDaqStart.Enabled}, btnStop={_btnDaqStop.Enabled}, daq={daqState}");
        try
        {
            // 5-FIX（审查 20260828）：启动前检查是否需重新初始化（Stop 超时/故障复位后句柄已释放）——
            // 原实现直接 StartContinuousAsync 抛"采集卡未初始化"被 catch 吞掉，按钮无恢复、无提示，表现为"无响应"。
            if (_daq.NeedsReinitialize)
            {
                // A-FIX（恢复采集）：Stop 超时/故障复位后资源已释放，自动重初始化并重启，
                // 无需用户手动"文件→连接"整机重连。InitializeAsync 幂等（内部先 CleanupForReinitialize 释放旧资源）。
                LogW("DAQ", $"采集卡需重新初始化（needsReinit=true），自动重初始化并重启... {_daq.DescribeState()}");
                try
                {
                    bool reinitOk = await _daq.InitializeAsync(_config);
                    if (!reinitOk)
                    {
                        _btnDaqStart.Enabled = false;
                        _btnDaqStop.Enabled = false;
                        LogE("DAQ", $"[诊断] 自动重初始化失败（InitializeAsync 返回 false）：{_daq.DescribeState()}");
                        MessageBox.Show(this, "采集卡自动重初始化失败，请执行 文件→连接 重新连接。",
                            "采集启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    await _daq.StartContinuousAsync();
                    _btnDaqStart.Enabled = false;
                    _btnDaqStop.Enabled = true;
                    LogS("DAQ", $"采集卡已自动重初始化并恢复采集（{_daq.DescribeState()}");
                    return;
                }
                catch (Exception reinitEx)
                {
                    _btnDaqStart.Enabled = false;
                    _btnDaqStop.Enabled = false;
                    LogE("DAQ", $"采集卡自动重初始化失败: {reinitEx.Message}");
                    MessageBox.Show(this, $"采集卡自动重初始化失败：{reinitEx.Message}\n请执行 文件→连接 重新连接。",
                        "采集启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            await _daq.StartContinuousAsync();
            _btnDaqStart.Enabled = false;
            _btnDaqStop.Enabled = true;
            LogS("DAQ", $"采集已启动（{_daq.DescribeState()}");
        }
        catch (Exception ex)
        {
            LogE("DAQ", $"采集启动失败: {ex.Message} | {_daq.DescribeState()}");
            // 5-FIX：失败时恢复按钮可用（允许重试），而非静默保持禁用
            _btnDaqStart.Enabled = !_daq.IsRunning && !_daq.NeedsReinitialize;
            _btnDaqStop.Enabled = _daq.IsRunning;
            MessageBox.Show(this, $"采集启动失败：{ex.Message}", "采集启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task DaqStopAsync()
    {
        try
        {
            await _daq.StopAsync();
            // 诊断（停止→开始失败定位）：记录停止后硬件状态，判断是否进入需重初始化分支
            LogD("DAQ", $"[诊断] 停止后：{_daq.DescribeState()}");
            // RM-1 修复：Stop 超时后 deferred 清理会释放句柄和缓冲，
            // 此时不能允许再次 Start——UI 必须提示用户重新连接/初始化。
            if (_daq.NeedsReinitialize)
            {
                _btnDaqStart.Enabled = false;
                _btnDaqStop.Enabled = false;
                LogW("DAQ", "采集已停止，但资源已释放（Stop 超时），需重新连接才能启动");
            }
            else
            {
                _btnDaqStart.Enabled = true;
                _btnDaqStop.Enabled = false;
                LogS("DAQ", "采集已停止");
            }
        }
        catch (Exception ex)
        {
            LogE("DAQ", $"采集停止失败: {ex.Message} | {_daq.DescribeState()}");
        }
    }

    private void ReadbackDaqParams()
    {
        try
        {
            string text;
            // RM-2 修复：后台线程调用时 _numSampleRateMHz.Value 需通过 Invoke 读取，
            // 避免 WinForms 非法跨线程访问（开启 CheckForIllegalCrossThreadCalls 时会抛异常）。
            double rateMhz;
            if (_txtDaqReadback.InvokeRequired)
                rateMhz = (double)_txtDaqReadback.Invoke(() => _numSampleRateMHz.Value);
            else
                rateMhz = (double)_numSampleRateMHz.Value;

            if (_daq is SpectrumDaqCard s)
            {
                int chCount = s.EnabledChannelCount;
                float actualRate = SpectrumDaqCard.ClampSampleRate((float)(rateMhz * 1e6), chCount);
                // D4-FIX（审查 20260828）：显示硬件实际对齐后的点数（与 A 扫帧 PointCount 一致），
                // 而非 UI 原始值——消除"回读区 1020 vs A 扫 1024"的两处不一致。
                int aligned = SpectrumDaqCard.AlignSegmentSize(_config.SampleCount > 0 ? _config.SampleCount : ConnectionConfig.DefaultSampleCount);
                text = $"采样率: {actualRate / 1e6:F1} MHz\r\n" +
                       $"采样点数: {aligned}（对齐后，UI 输入 {_config.SampleCount}）\r\n" +
                       $"采样长度: {aligned / actualRate * 1e6:F1} µs\r\n" +
                       $"输入量程: ±{s.InputRangeMv} mV\r\n" +
                       $"通道掩码: 0x{s.ChannelMask:X}\r\n" +
                       $"通道数: {chCount}\r\n" +
                       $"输入阻抗: {(s.InputFiftyOhm ? "50Ω HF" : "1MΩ 缓冲")}\r\n" +
                       $"采集模式: {s.AcquisitionMode}\r\n" +
                       $"平均次数: {s.Averages}\r\n" +
                       $"时间戳: {(s.EnableTimestamp ? "开" : "关")}\r\n" +
                       $"触发电平: {s.ExternalTriggerLevelMv} mV\r\n" +
                       $"能力: {(s.Capabilities?.Describe() ?? "未初始化")}";
            }
            else
            {
                text = $"采样率: {rateMhz:F1} MHz\r\n" +
                       $"采样点数: {_config.SampleCount}\r\n" +
                       $"模式: Mock（模拟）\r\n" +
                       $"采集状态: {(_daq.IsRunning ? "运行中" : "已停止")}";
            }

            if (_txtDaqReadback.InvokeRequired)
                _txtDaqReadback.BeginInvoke(() => _txtDaqReadback.Text = text);
            else
                _txtDaqReadback.Text = text;
        }
        catch (Exception ex) { LogE("DAQ", $"采集参数回读失败: {ex.Message}"); }
    }
}
