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
/// 主窗体 partial：视图菜单、保存/加载设置、数据导出/导入、链路诊断/关于/更新。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  视图菜单
    // ════════════════════════════════════════════════════════════════

    private void OnOpenAscan(object? sender, EventArgs e)
    {
        var ascan = Application.OpenForms.OfType<AscanForm>().FirstOrDefault();
        if (ascan == null)
        {
            ascan = new AscanForm(_daq, _pulse) { MdiParent = this };
            ascan.Show();
        }
        else ascan.Activate();
    }

    /// <summary>P3：DAQ 采集参数（采样率/采样长度/量程）应用完成后，通知所有已打开的 A 扫显示窗口重置展示。
    /// 2/3-FIX（审查 20260828）：用采集配置显式计算总时长传入——不依赖可能为空的当前帧
    /// （重初始化后 _currentData 为空帧），消除采样长 10µs 时波形"消失"。</summary>
    private void ResetOpenAscanDisplays()
    {
        // 用实际配置换算总时长（对齐后的点数 × 实际采样间隔）
        float rate = _config.SampleRate > 0 ? _config.SampleRate : 100e6f;
        int points = _config.SampleCount > 0 ? _config.SampleCount : ConnectionConfig.DefaultSampleCount;
        float totalUs = points / rate * 1e6f;
        foreach (var ascan in Application.OpenForms.OfType<AscanForm>())
            ascan.SyncSampleLengthToAcquisition(totalUs);
    }

    private void OnOpenScan(object? sender, EventArgs e)
    {
        var scan = Application.OpenForms.OfType<ScanForm>().FirstOrDefault();
        if (scan == null)
        {
            scan = OpenScanForm();
            scan.ScanDataUpdated += (_, _) => PushScanDataToBscan(scan);
        }
        else scan.Activate();
    }

    private void PushScanDataToBscan(ScanForm scan)
    {
        var bscan = Application.OpenForms.OfType<BScanForm>().FirstOrDefault();
        if (bscan == null || scan == null || IsDisposed || Disposing) return;

        var (ascans, positions, sampleRate) = scan.GetScanData();
        if (ascans.Length == 0) return;
        if (bscan.InvokeRequired) bscan.BeginInvoke(() => bscan.UpdateData(ascans, positions, sampleRate));
        else bscan.UpdateData(ascans, positions, sampleRate);
    }

    private void OnOpenBscan(object? sender, EventArgs e)
    {
        var bscan = Application.OpenForms.OfType<BScanForm>().FirstOrDefault();
        if (bscan == null)
        {
            bscan = new BScanForm { MdiParent = this };
            bscan.SubscribeToScan(_scanEngine);
            bscan.Show();
        }
        else bscan.Activate();
    }

    /// <summary>P0-C：打开 FFT 频谱窗体（确认探头频率/滤波范围）</summary>
    private void OnOpenFft(object? sender, EventArgs e)
    {
        var fft = Application.OpenForms.OfType<FftForm>().FirstOrDefault();
        if (fft == null)
        {
            fft = new FftForm(_daq) { MdiParent = this };
            fft.Show();
        }
        else fft.Activate();
    }

    /// <summary>TCG 深度补偿曲线统一入口：复用或打开扫查窗，编辑其 TCG 曲线（随深度提升接收增益，厚件定量）。</summary>
    private void OnOpenTcgEditor(object? sender, EventArgs e)
    {
        var scan = CurrentScanForm;
        if (scan == null)
        {
            scan = OpenScanForm();
            scan.ScanDataUpdated += (_, _) => PushScanDataToBscan(scan);
        }
        scan.Activate();
        using var dlg = new TcgCurveEditorForm(scan.TcgCurve);
        dlg.ShowDialog(this);
    }

    // ════════════════════════════════════════════════════════════════
    //  保存/加载设置
    // ════════════════════════════════════════════════════════════════

    private ScanForm? CurrentScanForm => Application.OpenForms.OfType<ScanForm>().FirstOrDefault();

    private async void OnSaveSettings(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存扫查设置",
            Filter = "扫查设置文件 (*.acf)|*.acf|JSON 文件 (*.json)|*.json",
            DefaultExt = "acf",
            FileName = "scan-settings.acf",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ApplySystemParams();

            var cfg = new ScanSessionConfig
            {
                Pulse = new PulseParams
                {
                    Channel = _cmbPulseChannel.SelectedIndex + 1,
                    GainDb = (float)_numGain.Value,
                    PrfHz = (float)_numPrf.Value,
                    Mode = _cmbPulseMode.SelectedIndex == 0 ? PulseMode.PulseEcho : PulseMode.ThroughTransmission,
                    Voltage = (float)_numVoltage.Value,
                    EnergyLevel = (int)_numEnergyLevel.Value,
                    Damping = (DampingSetting)_cmbDamping.SelectedIndex,
                    PulseWidthNs = (float)_numWidth.Value,
                },
                Daq = new DaqParams
                {
                    SampleRate = (float)((double)_numSampleRateMHz.Value * 1e6),
                    SampleLengthUs = (float)_numSampleLengthUs.Value,
                    Channel = _cmbDaqChannel.SelectedIndex + 1,
                    WaveformType = WaveformType.RF,
                },
                System = _systemParams,
                ColormapName = "Jet"
            };
            CurrentScanForm?.BuildSessionConfig(cfg);

            await new ConfigService().SaveAsync(dlg.FileName, cfg);
            LogS("系统", $"设置已保存: {dlg.FileName}");
            // D5-FIX（审查 20260828）：保存的是面板当前值（可能未下发硬件）——明示防误操作，
            // 与 D3 加载提示构成对称闭环（加载→警示未应用；保存→警示可能是未生效值）。
            MessageBox.Show(this,
                $"设置已保存到：{dlg.FileName}\n\n" +
                "⚠ 提示：保存的是面板当前值。若修改后未点击【应用参数】，\n" +
                "落盘数值与硬件实际配置可能不一致。",
                "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogE("系统", $"保存设置失败: {ex.Message}");
            MessageBox.Show(this, $"保存设置失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnLoadSettings(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "加载扫查设置",
            Filter = "扫查设置文件 (*.acf)|*.acf|JSON 文件 (*.json)|*.json",
            DefaultExt = "acf"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var cfg = await new ConfigService().LoadAsync(dlg.FileName);

            // 回填脉冲面板
            _cmbPulseChannel.SelectedIndex = Math.Clamp(cfg.Pulse.Channel - 1, 0, 1);
            _numGain.Value = Math.Clamp((decimal)cfg.Pulse.GainDb, _numGain.Minimum, _numGain.Maximum);
            _numWidth.Value = Math.Clamp((decimal)cfg.Pulse.PulseWidthNs, _numWidth.Minimum, _numWidth.Maximum);
            _numPrf.Value = Math.Clamp((decimal)cfg.Pulse.PrfHz, _numPrf.Minimum, _numPrf.Maximum);
            _cmbPulseMode.SelectedIndex = cfg.Pulse.Mode == PulseMode.ThroughTransmission ? 1 : 0;
            _numVoltage.Value = Math.Clamp((decimal)cfg.Pulse.Voltage, _numVoltage.Minimum, _numVoltage.Maximum);
            _numEnergyLevel.Value = Math.Clamp(cfg.Pulse.EnergyLevel, _numEnergyLevel.Minimum, _numEnergyLevel.Maximum);
            _cmbDamping.SelectedIndex = Math.Clamp((int)cfg.Pulse.Damping, 0, 3);

            // 回填采集面板
            if (cfg.Daq.SampleRate > 0)
                _numSampleRateMHz.Value = Math.Clamp((decimal)(cfg.Daq.SampleRate / 1e6), _numSampleRateMHz.Minimum, _numSampleRateMHz.Maximum);
            if (cfg.Daq.SampleLengthUs > 0)
                _numSampleLengthUs.Value = Math.Clamp((decimal)cfg.Daq.SampleLengthUs, _numSampleLengthUs.Minimum, _numSampleLengthUs.Maximum);
            _cmbDaqChannel.SelectedIndex = Math.Clamp(cfg.Daq.Channel - 1, 0, 2);

            // 回填系统参数
            _numSoundVelocity.Value = Math.Clamp((decimal)cfg.System.SoundVelocity, _numSoundVelocity.Minimum, _numSoundVelocity.Maximum);
            _numFocalLength.Value = Math.Clamp((decimal)cfg.System.FocalLength, _numFocalLength.Minimum, _numFocalLength.Maximum);
            _numZeroOffset.Value = Math.Clamp((decimal)cfg.System.ZeroOffsetUs, _numZeroOffset.Minimum, _numZeroOffset.Maximum);
            // B3-FIX（审查 20260828）：逐字段拷贝而非引用替换——原 `_systemParams = cfg.System`
            // 使加载后的对象与后续面板改动分叉（旧引用仍被扫描/成像消费）。
            _systemParams.RulerUnit = cfg.System.RulerUnit;
            _systemParams.SoundVelocity = cfg.System.SoundVelocity;
            _systemParams.FocalLength = cfg.System.FocalLength;
            _systemParams.ZeroOffsetUs = cfg.System.ZeroOffsetUs;

            // 回填扫查窗体
            CurrentScanForm?.ApplySessionConfig(cfg);

            LogS("系统", $"设置已加载: {dlg.FileName}");
            MessageBox.Show(this,
                $"设置已从 {dlg.FileName} 加载。\n\n" +
                "⚠ 注意：参数仅回填到面板，尚未下发至硬件。\n" +
                "请分别在『脉冲』页与『采集』页点击【应用参数】使其生效，\n" +
                "否则扫描将按硬件旧参数采集（数据与显示不一致）。",
                "加载设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            LogE("系统", $"加载设置失败: {ex.Message}");
            MessageBox.Show(this, $"加载设置失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  数据导出/导入
    // ════════════════════════════════════════════════════════════════

    private async void OnExportCsv(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出 A 扫数据",
            Filter = "CSV 文件 (*.csv)|*.csv",
            DefaultExt = "csv",
            FileName = $"ascan-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            // 默认保存到系统文档目录（用户可见位置）；对话框会显示完整路径供确认
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // 先确认采集链路已启动且有数据（Mock 需先"连接"触发 StartContinuousAsync）
            var data = _daq.GetCurrentData();
            if (data.PointCount == 0)
            {
                MessageBox.Show(this, "当前无 A 扫数据（请先执行 文件→连接 并开始采集后重试）", "导出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await new CsvExportService().ExportAsync(dlg.FileName, data);
            LogS("系统", $"A 扫数据已导出: {dlg.FileName}");
            MessageBox.Show(this, $"A 扫数据已导出到：{dlg.FileName}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogE("系统", $"A扫导出失败: {ex.Message}");
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnExportAdtx(object? sender, EventArgs e)
    {
        var scan = CurrentScanForm;
        if (scan == null || !scan.HasScanData)
        {
            MessageBox.Show(this, "尚无扫查数据（请先打开扫查成像窗体并执行一次扫查）", "导出 .adtx", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "导出扫查数据 (.adtx)",
            Filter = "超声扫查数据 (*.adtx)|*.adtx",
            DefaultExt = "adtx",
            FileName = $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.adtx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            await Task.Run(() => scan.ExportScanDataToAdtx(dlg.FileName));
            LogS("系统", $"扫查数据已导出: {dlg.FileName}");
            MessageBox.Show(this, $"扫查数据已导出到：{dlg.FileName}", "导出 .adtx", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogE("系统", $"ADTX导出失败: {ex.Message}");
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnImportAdtx(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "导入扫查数据 (.adtx)",
            Filter = "超声扫查数据 (*.adtx)|*.adtx",
            DefaultExt = "adtx"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var loaded = await Task.Run(() => new AdtxDataService().Load(dlg.FileName));

            var scan = CurrentScanForm ?? OpenScanForm();
            scan.LoadAdtxData(loaded);

            var bscan = Application.OpenForms.OfType<BScanForm>().FirstOrDefault();
            if (bscan == null)
            {
                bscan = new BScanForm { MdiParent = this };
                bscan.SubscribeToScan(_scanEngine);
                bscan.Show();
            }
            else bscan.Activate();
            bscan.UpdateData(loaded.Ascans, loaded.Positions, loaded.SampleRate,
                loaded.SystemParams.SoundVelocity, loaded.SystemParams.ZeroOffsetUs);

            LogS("系统", $"已导入 {loaded.ColumnCount} 条 A 扫 × {loaded.SampleCount} 点");
            MessageBox.Show(this, $"已导入 {loaded.ColumnCount} 条 A 扫 × {loaded.SampleCount} 点\n（{dlg.FileName}）", "导入 .adtx", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogE("系统", $"导入失败: {ex.Message}");
            MessageBox.Show(this, $"导入失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private ScanForm OpenScanForm()
    {
        var scan = new ScanForm(_scanEngine, _motion, _daq, _config) { MdiParent = this };
        scan.Show();
        return scan;
    }

    private async void OnLinkDiagnostics(object? sender, EventArgs e)
    {
        LogI("系统", "开始通信链路诊断...");
        var svc = new Services.LinkDiagnosticsService(_pulse, _daq);
        var report = await svc.RunFullDiagnosticsAsync(_config);
        string reportText = report.ToReportString();
        if (report.IsAllOk) LogI("系统", $"链路诊断完成: {reportText}");
        else LogW("系统", $"链路诊断完成: {reportText}");

        // 生成简明摘要弹窗
        string summary = report.IsAllOk
            ? "通信链路全部正常"
            : report.FailCount > 0 || report.WarnCount > 0
                ? $"发现 {report.FailCount} 项故障、{report.WarnCount} 项警告、{report.SkippedCount} 项跳过"
                : $"诊断未完成（{report.SkippedCount} 项跳过）";
        string detail = string.Join("\n", report.Steps
            .Where(s => s.Status is UTscan.Services.Status.Fail
                or UTscan.Services.Status.Warn
                or UTscan.Services.Status.Skipped)
            .Select(s => $"[{s.Status}] {s.Name}: {s.Detail}"));
        string fullMsg = $"{summary}\n\n{(string.IsNullOrEmpty(detail) ? "未发现可显示的诊断明细" : detail)}";

        MessageBox.Show(this, fullMsg, "链路诊断结果",
            MessageBoxButtons.OK,
            report.FailCount > 0
                ? MessageBoxIcon.Error
                : report.WarnCount > 0 || report.SkippedCount > 0
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
    }

    private void OnAboutClick(object? sender, EventArgs e)
    {
        MessageBox.Show(this,
            $"超声显微扫查系统 {Program.FullVersionText}\n基于 .NET 8 + WinForms\n\n硬件支持:\n• ZMC 运动控制器 (以太网/串口)\n• Spectrum M3i.3242-exp 采集卡\n• JSR DPR500 脉冲收发仪",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 检查更新（软件更新方案，决策 A：仅程序内手动触发）。
    /// 流程：UpdateService 校验 _update\ 包（完整性+版本链）→ 用户确认 →
    /// staging + 生成 UpdateSwap.cmd → 拉起脚本后立即退出本进程，由脚本完成交换并重启新版。
    /// </summary>
    private void OnCheckForUpdate(object? sender, EventArgs e)
    {
        try
        {
            var svc = new Services.UpdateService();
            var current = Program.VersionText.TrimStart('v');
            // VersionText 可能带 " (20260818)" 构建号后缀——取主版本部分做链比较
            int sp = current.IndexOf(' ');
            if (sp > 0) current = current[..sp];

            LogI("更新", $"开始检查更新（当前 v{current}）...");
            var outcome = svc.CheckForUpdate(current);
            switch (outcome.Result)
            {
                case Services.UpdateCheckResult.InvalidPackage:
                    LogW("更新", outcome.Message);
                    MessageBox.Show(this, outcome.Message, "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                case Services.UpdateCheckResult.AlreadyUpToDate:
                    LogI("更新", outcome.Message);
                    MessageBox.Show(this, outcome.Message, "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                case Services.UpdateCheckResult.ChainMismatch:
                    LogE("更新", outcome.Message);
                    MessageBox.Show(this, outcome.Message, "版本链校验失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
            }

            // Available：展示摘要并请求确认
            var m = outcome.Manifest!;
            string detail = $"{outcome.Message}\n\n" +
                            $"目标版本: v{m.Version}（构建 {m.Build}，{m.Date}）\n" +
                            $"文件数: {m.Files.Count}\n\n" +
                            "确认后程序将:\n" +
                            $"1. 暂存新文件到 .update\\staging\\（绝不覆盖 hardware.json）\n" +
                            "2. 生成 UpdateSwap.cmd 并退出本程序\n" +
                            "3. 脚本自动备份当前版本、完成替换并重启新版\n\n" +
                            "是否继续？";
            LogI("更新", outcome.Message);
            if (MessageBox.Show(this, detail, "发现新版本",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            {
                LogI("更新", "用户取消更新");
                return;
            }

            svc.PrepareUpdate(m, current);

            // 落盘回滚脚本（供更新失败时手动恢复 .update\backup）
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "RollbackUpdate.cmd"),
                    Services.UpdateService.BuildRestoreScriptContent(),
                    new System.Text.UTF8Encoding(false));
            }
            catch { /* 回滚脚本写失败不阻塞升级 */ }

            LogS("更新", $"已就绪升级到 v{m.Version}，正在退出并执行交换...");
            MessageBox.Show(this, "更新已就绪。程序即将退出并自动完成替换、重启。",
                "正在更新", MessageBoxButtons.OK, MessageBoxIcon.Information);

            // 拉起交换脚本（等待本进程退出→备份→覆盖→重启），然后关闭主窗体结束进程。
            // FormClosing 会先走正常硬件关断流程（DPR 关断→DAQ 停止→ZMC 断开）。
            var swap = Path.Combine(AppContext.BaseDirectory, Services.UpdateService.SwapScriptName);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + swap + "\"",
                UseShellExecute = true,
                CreateNoWindow = false
            });
            BeginInvoke(() => Close());
        }
        catch (Exception ex)
        {
            LogE("更新", $"检查/应用更新异常: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(this, $"更新失败: {ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
