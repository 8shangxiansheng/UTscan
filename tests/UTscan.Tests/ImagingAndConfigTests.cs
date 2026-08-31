using System.Drawing;
using System.IO;
using UTscan.Core.Enums;
using UTscan.Core.Models;
using UTscan.Mock;
using UTscan.Services;
using UTscan.Services.SignalProcessing;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// 成像算法 / 同步闸门跟踪 / 色图 / C 扫渲染 / 配置服务 / CSV 导出 测试。
/// </summary>
public class ImagingAndConfigTests
{
    // 采样率 1 MHz → 1 sample = 1 μs，便于手算
    private const float Rate = 1e6f;

    private static AScanData MakeData(float[] samples) => new() { Samples = samples, SampleRate = Rate };

    // ---------- 成像模式 ----------

    [Fact]
    public void Imaging_PeakPeak_ReturnsRange()
    {
        var s = new float[20];
        s[7] = 0.8f; s[9] = -0.3f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.1f };
        var an = new GateAnalyzer();
        float v = an.ComputeImagingValue(data, gate, CScanImagingMode.PeakPeak, WaveformType.RF);
        Assert.Equal(1.1f, v, 4);
    }

    [Fact]
    public void Imaging_PositiveAndNegativePeak()
    {
        var s = new float[20];
        s[7] = 0.8f; s[9] = -0.3f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.1f };
        var an = new GateAnalyzer();
        Assert.Equal(0.8f, an.ComputeImagingValue(data, gate, CScanImagingMode.PositivePeak, WaveformType.RF), 4);
        Assert.Equal(-0.3f, an.ComputeImagingValue(data, gate, CScanImagingMode.NegativePeak, WaveformType.RF), 4);
        Assert.Equal(0.8f, an.ComputeImagingValue(data, gate, CScanImagingMode.MaxPeak, WaveformType.RF), 4);
    }

    [Fact]
    public void Imaging_TofPositivePeak_RelativeToGateStart()
    {
        var s = new float[20];
        s[8] = 0.9f;   // 闸门起点 5 → TOF = 3 μs
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.1f };
        var an = new GateAnalyzer();
        float v = an.ComputeImagingValue(data, gate, CScanImagingMode.TofPositivePeak, WaveformType.RF);
        Assert.Equal(3f, v, 4);
    }

    [Fact]
    public void Imaging_TofPositiveThreshold_FirstUpwardCrossing()
    {
        // 闸门 5..14；阈值 0.5；idx6:0.4→idx7:0.7 首次上穿
        var s = new float[20];
        s[6] = 0.4f; s[7] = 0.7f; s[10] = 0.9f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.5f };
        var an = new GateAnalyzer();
        float v = an.ComputeImagingValue(data, gate, CScanImagingMode.TofPositiveThreshold, WaveformType.RF);
        // 穿越时间 = 7 μs（绝对），相对闸门起点 = 2 μs
        Assert.Equal(2f, v, 4);
    }

    [Fact]
    public void Imaging_PhaseReversal_NegatesPeak()
    {
        var s = new float[20];
        s[7] = 0.8f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.1f };
        var an = new GateAnalyzer();
        Assert.Equal(-0.8f, an.ComputeImagingValue(data, gate, CScanImagingMode.PhaseReversal, WaveformType.RF), 4);
    }

    [Fact]
    public void Imaging_Mean_AveragesGateWindow()
    {
        // P0-F：Mean 成像模式——闸门内采样点算术平均（说明书 2.6）
        var s = new float[20];
        for (int i = 5; i <= 15; i++) s[i] = 0.2f;   // 闸门内 11 点全部 0.2 → 均值 0.2
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.1f };
        var an = new GateAnalyzer();
        Assert.Equal(0.2f, an.ComputeImagingValue(data, gate, CScanImagingMode.Mean, WaveformType.RF), 4);

        // 混合值：闸门内 11 点 = 9×0.2 + s[14]=1.0 + s[15]=0.2 → 均值 3.0/11
        s[14] = 1.0f;
        Assert.Equal(3.0f / 11f, an.ComputeImagingValue(data, gate, CScanImagingMode.Mean, WaveformType.RF), 4);
    }

    // ---------- 同步闸门跟踪 ----------

    [Fact]
    public void SyncGate_FirstCrossOffset_FeedsDataGateStart()
    {
        // 同步闸门 0..4（idx0..4），阈值 0.5；idx2=0.9 首次越过
        var s = new float[20];
        s[2] = 0.9f;
        var data = MakeData(s);
        var sync = new GateConfig { Name = "Sync", Role = GateRole.Sync, StartUs = 0, WidthUs = 4, ThresholdV = 0.5f };
        var an = new GateAnalyzer();
        var r = an.Analyze(data, sync);
        Assert.Equal(2f, r.SyncFirstCrossOffsetUs, 4);   // 偏移 = 2 μs

        var dataGate = new GateConfig { StartUs = 3, WidthUs = 5 };
        float start = an.ComputeDataGateStart(r, dataGate);
        Assert.Equal(5f, start, 4);   // ft(0) + Δt(2) + dataGate.StartUs(3) = 5
    }

    [Fact]
    public void SyncGate_NoCross_KeepsDataGateStart()
    {
        var s = new float[20];
        var data = MakeData(s);
        var sync = new GateConfig { Role = GateRole.Sync, StartUs = 0, WidthUs = 4, ThresholdV = 0.5f };
        var an = new GateAnalyzer();
        var r = an.Analyze(data, sync);
        Assert.Equal(-1f, r.SyncFirstCrossOffsetUs, 4);

        var dataGate = new GateConfig { StartUs = 3, WidthUs = 5 };
        Assert.Equal(3f, an.ComputeDataGateStart(r, dataGate), 4);
    }

    // ---------- 波形预处理 ----------

    [Fact]
    public void Preprocess_Detected_TakesAbsoluteValue()
    {
        var r = GateAnalyzer.Preprocess(new float[] { -0.5f, 0.3f, -0.2f }, WaveformType.Detected);
        Assert.Equal(0.5f, r[0], 4);
        Assert.Equal(0.3f, r[1], 4);
    }

    [Fact]
    public void Preprocess_PositiveHalf_ZerosNegatives()
    {
        var r = GateAnalyzer.Preprocess(new float[] { -0.5f, 0.3f }, WaveformType.PositiveHalf);
        Assert.Equal(0f, r[0], 4);
        Assert.Equal(0.3f, r[1], 4);
    }

    // ---------- D2：成像波形类型一致性 ----------

    [Fact]
    public void Imaging_WaveType_NegativeHalf_SuppressesPositiveOnlySignal()
    {
        // 纯正信号 + NegativeHalf 预处理 → 幅值成像应为 0（负半波把正值置零）
        var s = new float[20];
        s[7] = 0.8f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.05f };
        var an = new GateAnalyzer();
        Assert.Equal(0f, an.ComputeImagingValue(data, gate, CScanImagingMode.PositivePeak, WaveformType.NegativeHalf), 4);
        Assert.Equal(0f, an.ComputeImagingValue(data, gate, CScanImagingMode.MaxPeak, WaveformType.NegativeHalf), 4);
    }

    [Fact]
    public void Imaging_WaveType_Detected_FlipsNegativeToPositive()
    {
        // 纯负信号 + Detected（绝对值）→ 正峰成像应取 absolute（正值），MaxPeak 为正
        var s = new float[20];
        s[7] = -0.8f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.05f };
        var an = new GateAnalyzer();
        Assert.Equal(0.8f, an.ComputeImagingValue(data, gate, CScanImagingMode.PositivePeak, WaveformType.Detected), 4);
        Assert.Equal(0.8f, an.ComputeImagingValue(data, gate, CScanImagingMode.MaxPeak, WaveformType.Detected), 4);
    }

    [Fact]
    public void Imaging_WaveType_Rf_PreservesPolarity()
    {
        // RF 原样：纯负信号 + PositivePeak 为 0；MaxPeak 取绝对值较大者 → -0.8
        var s = new float[20];
        s[7] = -0.8f;
        var data = MakeData(s);
        var gate = new GateConfig { StartUs = 5, WidthUs = 10, ThresholdV = 0.05f };
        var an = new GateAnalyzer();
        Assert.Equal(0f, an.ComputeImagingValue(data, gate, CScanImagingMode.PositivePeak, WaveformType.RF), 4);
        Assert.Equal(-0.8f, an.ComputeImagingValue(data, gate, CScanImagingMode.MaxPeak, WaveformType.RF), 4);
    }

    // ---------- 色图 ----------

    [Fact]
    public void Colormap_BoundsMapToEndpoints()
    {
        var cmap = Colormap.Jet;
        Assert.Equal(Color.FromArgb(0, 0, 128), cmap.Map(0f));
        Assert.Equal(Color.FromArgb(255, 0, 0), cmap.Map(1f));
    }

    [Fact]
    public void Colormap_FromName_FallbackJet()
    {
        Assert.Same(Colormap.Jet, Colormap.FromName("不存在"));
        Assert.Same(Colormap.Viridis, Colormap.FromName("Viridis"));
    }

    // ---------- C 扫渲染 ----------

    [Fact]
    public void CScan_Render_ProducesNonEmptyBitmap()
    {
        var vals = new float[,] { { 0f, 1f }, { 2f, 3f } };
        var svc = new CScanImageService();
        var (min, max) = CScanImageService.Range(vals);
        Assert.Equal(0f, min);
        Assert.Equal(3f, max);
        using var bmp = svc.Render(vals, Colormap.Jet, min, max, 2, 2);
        Assert.Equal(4, bmp.Width);
        Assert.Equal(4, bmp.Height);
    }

    [Fact]
    public void CScan_RenderColorBar_HasCorrectSize()
    {
        var svc = new CScanImageService();
        using var bar = svc.RenderColorBar(Colormap.Hot, 0f, 1f, 20, 100);
        Assert.Equal(20, bar.Width);
        Assert.Equal(100, bar.Height);
    }

    // ---------- 配置服务 ----------

    [Fact]
    public async Task ConfigService_SaveLoad_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_{Guid.NewGuid():N}.acf");
        try
        {
            var svc = new ConfigService();
            var cfg = new ScanSessionConfig
            {
                Daq = new DaqParams { SampleRate = 200e6f, DelayUs = 1.5f, SampleLengthUs = 8f },
                Pulse = new PulseParams { GainDb = 12f, PrfHz = 2000f, Voltage = 250f },
                ImagingMode = CScanImagingMode.MaxPeak,
                ColormapName = "Viridis"
            };
            await svc.SaveAsync(path, cfg);
            var loaded = await svc.LoadAsync(path);
            Assert.Equal(200e6f, loaded.Daq.SampleRate);
            Assert.Equal(1.5f, loaded.Daq.DelayUs);
            Assert.Equal(12f, loaded.Pulse.GainDb);
            Assert.Equal(CScanImagingMode.MaxPeak, loaded.ImagingMode);
            Assert.Equal("Viridis", loaded.ColormapName);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------- Mock 脉冲收发仪 ----------

    [Fact]
    public async Task MockPulse_ApplyParams_ClampsRanges()
    {
        var gen = new MockPulseGenerator();
        await gen.ApplyParamsAsync(new PulseParams { GainDb = 999f, PrfHz = 1f, Voltage = 99999f });
        // L-5：增益范围与真机 DPR500 一致（-13~66），原 50 是 Mock 旧范围
        Assert.Equal(66f, gen.Params.GainDb);
        Assert.Equal(100f, gen.Params.PrfHz);
        Assert.Equal(330f, gen.Params.Voltage);
    }

    // ---------- CSV 导出 ----------

    [Fact]
    public async Task CsvExport_AScan_WritesHeaderAndRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_{Guid.NewGuid():N}.csv");
        try
        {
            var svc = new CsvExportService();
            var data = MakeData(new float[] { 0.1f, 0.2f, 0.3f });
            await svc.ExportAsync(path, data);
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("index,time_us,voltage_(V)", lines[0]);   // IO4-FIX：头行标注单位
            Assert.Equal(4, lines.Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CsvExport_Matrix_WritesGrid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_{Guid.NewGuid():N}.csv");
        try
        {
            var svc = new CsvExportService();
            var m = new float[,] { { 1f, 2f }, { 3f, 4f } };
            await svc.ExportMatrixAsync(path, m);
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal("1.000000,2.000000", lines[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------- GateSet ----------

    [Fact]
    public void GateSet_AddRemoveDataGate_RespectsLimit()
    {
        var gs = new GateSet();
        Assert.Single(gs.DataGates);
        Assert.True(gs.TryAddDataGate(new GateConfig { Name = "G2" }));
        Assert.Equal(2, gs.DataGates.Count);
        Assert.True(gs.RemoveDataGate(gs.DataGates[1]));
        Assert.Single(gs.DataGates);
    }

    // ═══════════════════════════════════════════════════════════════
    //  L-4：GateConfig.GateColor 转换属性（审查报告 2026-08-18-v2 L-4）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GateConfig_GateColor_IntColorRoundTrip()
    {
        var gate = new GateConfig { Color = unchecked((int)0xFF00FF00) };  // 绿色

        var c = gate.GateColor;
        Assert.Equal(255, c.A);
        Assert.Equal(0, c.R);
        Assert.Equal(255, c.G);
        Assert.Equal(0, c.B);

        // 反向：设置 Color 后写回 int
        gate.GateColor = System.Drawing.Color.Blue;
        Assert.Equal(unchecked((int)0xFF0000FF), gate.Color);
    }

    // ═══════════════════════════════════════════════════════════════
    //  H-1：DPR500 触发源默认 Internal（审查报告 H-1）
    //  ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PulseParams_TriggerMode_DefaultsToInternal()
    {
        // H-1：默认 Internal（DPR500 自主 PRF 发射，TRIG/SYNC 输出给 Spectrum 专用 EXT0）。
        // 原默认 External 使 DPR500 等待外部脉冲但不发射 → 真机采集零数据。
        Assert.Equal(TriggerMode.Internal, new PulseParams().TriggerMode);
    }

    [Fact]
    public async Task MockPulse_SetMode_KeepsInternalTrigger()
    {
        // H-1：SetModeAsync 无论 PulseEcho/Through 均保持 Internal 触发（自发自收/一发一收都需自主 PRF）
        var gen = new MockPulseGenerator();
        await gen.SetModeAsync(PulseMode.PulseEcho);
        await gen.SetModeAsync(PulseMode.ThroughTransmission);
        // Mock 实现应记录触发源为 Internal（见 MockPulseGenerator 扩展实现）
        Assert.Equal(TriggerMode.Internal, gen.Params.TriggerMode);
    }
}
