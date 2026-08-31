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
/// 主窗体 partial：脉冲收发仪面板：DPR500 参数应用/发射开关/回读。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  脉冲参数应用
    // ════════════════════════════════════════════════════════════════

    private async Task ApplyPulseParamsAsync()
    {
        try
        {
            // 保存应用前的输出状态（ApplyParamsAsync 内部无条件禁用输出以确保安全）
            bool wasOutputEnabled = _pulse.Params.Enabled;

            LogI("DPR", "应用脉冲参数...");
            int channel = _cmbPulseChannel.SelectedIndex + 1;
            var prm = new PulseParams
            {
                Channel = channel,
                GainDb = (float)_numGain.Value,
                PrfHz = (float)_numPrf.Value,
                Mode = _cmbPulseMode.SelectedIndex == 0 ? PulseMode.PulseEcho : PulseMode.ThroughTransmission,
                Voltage = (float)_numVoltage.Value,
                EnergyLevel = (int)_numEnergyLevel.Value,
                Damping = (DampingSetting)_cmbDamping.SelectedIndex,
                PulseWidthNs = (float)_numWidth.Value,
                // H-1：触发源与下拉同步（索引 0=Internal 1=External 2=Slave），保持内部状态与下发一致
                TriggerMode = _cmbTriggerSource.SelectedIndex == 0 ? TriggerMode.Internal : TriggerMode.External,
            };

            // 低通/高通滤波
            int lpIdx = _cmbLowPass.SelectedIndex;
            prm.LowPassHz = lpIdx < 6 ? float.Parse((string)_cmbLowPass.SelectedItem!) * 1e6f : 0f;
            int hpIdx = _cmbHighPass.SelectedIndex;
            prm.HighPassHz = float.Parse((string)_cmbHighPass.SelectedItem!) * 1e6f;

            // 3.2-FIX：PRF × 采样点数乘积上限守护——与 ScanService DMA 带宽校验同源，
            // 在参数应用阶段即拦截高负载组合，避免采集中途 FIFO 溢出/线程退出。
            float prfNow = (float)_numPrf.Value;
            int samplesNow = _config.SampleCount > 0 ? _config.SampleCount : ConnectionConfig.DefaultSampleCount;
            long dmaRate = (long)(prfNow * samplesNow * 4L);
            const long safeDmaLimit = 500_000_000L;   // 500 MB/s（与 ScanService 一致）
            if (dmaRate > safeDmaLimit)
            {
                string err = $"PRF({prfNow:F0}Hz) × 采样点数({samplesNow}) × 4B/帧 = {dmaRate / 1e6:F0} MB/s " +
                             $"超过 DMA 安全带宽 {safeDmaLimit / 1e6:F0} MB/s。请降低 PRF 或减少采样点数";
                LogE("DPR", err);
                MessageBox.Show(this, err, "参数超限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 应用基础参数（DPR500 ApplyParamsAsync 内部无条件 TriggerEnable=FALSE）
            await _pulse.ApplyParamsAsync(prm);
            await _pulse.SetPulseWidthAsync((float)_numWidth.Value);
            // L-4：SDK 无 PulseWidth 属性，脉宽仅记录（由脉冲器硬件决定），日志明示避免误判
            LogI("DPR", $"脉宽 {_numWidth.Value} ns 仅记录（DPR500 脉宽由脉冲器型号硬件决定）");

            // 应用扩展参数（M-3：接口统一，Mock/真机一致；真机 Dpr500Controller 实际下发硬件）
            // SelectChannelAsync 内部也可能调 ApplyParamsAsync（通道切换时重写全部参数）
            await _pulse.SelectChannelAsync(channel);
            // IO3-FIX（审查 20260828）：返回 false 不再静默——Slave 触发源不支持/写入失败须明示，
            // 避免 UI 显示已设置但硬件未生效。
            bool trigOk = await _pulse.SetTriggerSourceAsync(_cmbTriggerSource.SelectedIndex);
            if (!trigOk)
                LogW("DPR", $"触发源 {_cmbTriggerSource.SelectedItem} 设置失败（本配置可能不支持），请核对");
            await _pulse.SetSignalSelectAsync(_cmbSignalSelect.SelectedIndex);
            LogI("DPR", $"通道 {channel} 触发源={_cmbTriggerSource.SelectedItem} 信号选择={_cmbSignalSelect.SelectedItem}");

            // 仪器信息（P5：接口成员）
            LogI("DPR", $"DPR500 型号: {_pulse.InstrumentInfo.ModelName}");

            ReadbackPulseParams();

            // 恢复应用前的输出状态（所有参数操作完成后统一恢复，避免中间状态被覆盖）
            if (wasOutputEnabled)
            {
                bool restored = await _pulse.SetOutputEnabledAsync(true);
                if (restored)
                    LogI("DPR", "参数应用后已恢复脉冲发射");
                else
                    LogW("DPR", "参数应用后恢复发射失败（可能功率超限），请手动启用发射");
            }

            // 同步发射按钮文字（确保 UI 状态与硬件一致）
            RefreshPulseOutputUi(_pulse.Params.Enabled);
            LogS("DPR", $"脉冲参数已应用: 增益={prm.GainDb:F1}dB PRF={prm.PrfHz:F0}Hz 电压={prm.Voltage:F0}V");
        }
        catch (Exception ex)
        {
            LogE("DPR", $"脉冲参数应用失败: {ex.Message}");
            // 异常时也同步按钮状态（ApplyParamsAsync 已禁用输出，按钮应显示"启用发射"）
            RefreshPulseOutputUi(_pulse.Params.Enabled);
            MessageBox.Show(this, $"脉冲参数下发失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 脉冲发射开关（对标 JSR Control Panel 的 Enable/Disable Pulser）。
    /// 参数应用后需显式启用才开始发射；启用时 DPR500 经 TRIG/SYNC 输出同步脉冲驱动 Spectrum EXT0，
    /// 闭合"参数配置→触发→采集→显示"全流程。启用前检查功率限制（超限拒绝）。
    /// </summary>
    private async Task TogglePulseOutputAsync()
    {
        try
        {
            bool currentlyOn = _pulse.Params.Enabled;
            bool target = !currentlyOn;

            // H-D-FIX：启用发射前联动校验采集链路就绪——否则探头将在无采集监控下持续受激
            // （线缆断开/DAQ 未运行时发射=无监控激励，安全隐患）。
            if (target && _daq != null && !_daq.IsRunning)
            {
                LogW("DPR", "采集卡未运行，拒绝启用脉冲发射（防无监控受激）——请先启动采集");
                MessageBox.Show(this,
                    "采集卡未在采集，禁止启用脉冲发射（否则探头将在无采集监控下持续受激）。\n请先『开始采集』后再启用发射。",
                    "安全限制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool ok = await _pulse.SetOutputEnabledAsync(target);
            if (!ok && target)
            {
                    LogE("DPR", "脉冲发射启用失败（可能功率超限或未连接）");
                return;
            }

            // 刷新 UI 并更新回读区（含功率/脉冲中状态）
                if (target) LogS("DPR", "脉冲发射已启用");
                else LogI("DPR", "脉冲发射已停止");
            RefreshPulseOutputUi(target);
            ReadbackPulseParams();
        }
        catch (Exception ex)
        {
            LogE("DPR", $"脉冲发射切换失败: {ex.Message}");
            MessageBox.Show(this, $"脉冲发射切换失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>刷新发射开关按钮与状态标签（connect/disconnect 与切换后共用）。</summary>
    private void RefreshPulseOutputUi(bool enabled)
    {
        if (_btnPulseOutput == null || _lblPulseOutput == null) return;
        void Apply()
        {
            _btnPulseOutput!.Text = enabled ? "停止发射" : "启用发射";
            _btnPulseOutput!.BackColor = enabled ? System.Drawing.Color.IndianRed : System.Drawing.Color.ForestGreen;
            _lblPulseOutput!.Text = $"发射状态: {(enabled ? "发射中" : "关闭")}";
            _lblPulseOutput!.ForeColor = enabled ? System.Drawing.Color.ForestGreen : System.Drawing.Color.DimGray;
        }
        if (InvokeRequired) BeginInvoke(new Action(Apply));
        else Apply();
    }

    private void ReadbackPulseParams()
    {
        try
        {
            // 3-FIX（审查 20260828）：先触发硬件寄存器回读，再显示——消除"UI 显示软件缓存值
            // ≠ 硬件实际值"的分叉（如 UI 200V vs 硬件 275V）。回读失败时保留缓存并显示失败态。
            if (_pulse.IsConnected)
                _pulse.ReadParamsFromHardware();

            var p = _pulse.Params;
            // 3-FIX：硬件回读后同步刷新 UI 输入框（显示真实硬件值而非用户上次输入）
            if (_numGain != null && _numGain.Enabled)
                _numGain.Value = Math.Clamp((decimal)p.GainDb, _numGain.Minimum, _numGain.Maximum);
            if (_numVoltage != null && _numVoltage.Enabled)
                _numVoltage.Value = Math.Clamp((decimal)p.Voltage, _numVoltage.Minimum, _numVoltage.Maximum);
            if (_numPrf != null && _numPrf.Enabled)
                _numPrf.Value = Math.Clamp((decimal)p.PrfHz, _numPrf.Minimum, _numPrf.Maximum);
            if (_cmbDamping != null && p.Damping >= 0 && (int)p.Damping < _cmbDamping.Items.Count)
                _cmbDamping.SelectedIndex = (int)p.Damping;
            if (_numEnergyLevel != null && _numEnergyLevel.Enabled)
                _numEnergyLevel.Value = Math.Clamp(p.EnergyLevel, _numEnergyLevel.Minimum, _numEnergyLevel.Maximum);

            // 占空比 = 脉宽 × PRF（派生值；DPR500 脉宽由脉冲器型号硬件决定，不可软件设置）
            float dutyCyclePct = p.PulseWidthNs * p.PrfHz * 1e-7f; // (ns→s)=1e-9, ×PRF×100 → ×1e-7
            string text =
                $"通道: {p.Channel}\r\n" +
                $"增益: {p.GainDb:F1} dB\r\n" +
                $"脉宽: {p.PulseWidthNs:F0} ns（硬件固定）\r\n" +
                $"PRF: {p.PrfHz:F0} Hz（每秒脉冲数）\r\n" +
                $"占空比: {dutyCyclePct:F4} %\r\n" +
                $"模式: {(p.Mode == PulseMode.PulseEcho ? "自发自收" : "一发一收")}\r\n" +
                $"电压: {p.Voltage:F0} V\r\n" +
                $"能量挡位: {p.EnergyLevel}\r\n" +
                $"阻尼: {p.Damping}\r\n" +
                $"低通: {(p.LowPassHz > 0 ? $"{p.LowPassHz / 1e6:F1} MHz" : "全通")}\r\n" +
                $"高通: {(p.HighPassHz > 0 ? $"{p.HighPassHz / 1e6:F1} MHz" : "直流")}\r\n" +
                $"触发模式: {p.TriggerMode}\r\n" +
                $"发射: {(p.Enabled ? "开启" : "关闭")}\r\n" +
                $"已连接: {_pulse.IsConnected}";

            // DPR500 扩展信息（P5：接口成员）
            if (_pulse.IsConnected)
            {
                var info = _pulse.InstrumentInfo;
                text += $"\r\n── 仪器信息 ──\r\n" +
                        $"型号: {info.ModelName}\r\n" +
                        $"序列号: {info.SerialNumber}\r\n" +
                        $"脉冲器: {info.PulserModelName}\r\n" +
                        $"接收器: {info.ReceiverModelName} ({info.ReceiverBandwidthMHz}MHz)\r\n" +
                        $"SLAVE支持: {info.SupportsSlaveTrigger}\r\n" +
                        $"BOTH支持: {info.SupportsBothSignalSelect}";
            }

            if (_txtPulseReadback.InvokeRequired)
                _txtPulseReadback.BeginInvoke(() => _txtPulseReadback.Text = text);
            else
                _txtPulseReadback.Text = text;
        }
        catch (Exception ex) { LogE("DPR", $"脉冲参数回读失败: {ex.Message}"); }
    }
}
