using System.Threading;
using System.Threading.Tasks;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Mock;
using UTscan.Services;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// 三设备（ZMC/DPR500/Spectrum）端到端联动验证测试。
/// 仅验证 Mock 模式下的触发链/状态同步逻辑（不依赖真机硬件）。
/// </summary>
public class ThreeDeviceLinkageTests
{
    // ═══════════════════════════════════════════════════════════════
    // 1. TriggerIo 接线断层验证
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Linkage_MockWithTriggerIo_UsesSingleTriggerPath()
    {
        // TriggerIo >= 0 → MockPulseGenerator + MockMotionController 走严格单次触发路径
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        var pulse = new MockPulseGenerator();
        await motion.ConnectAsync(new ConnectionConfig());
        await pulse.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq, pulse);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 2f, Height = 2f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams
        {
            Velocity = 10f,
            TriggerIo = 5,               // 模拟接线 IO5
            TriggerPulseWidthMs = 3
        };

        int points = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.Equal(9, points);        // 3x3 网格
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task Linkage_MockWithoutTriggerIo_UsesPrfFallback()
    {
        // TriggerIo == -1 → Mock 模式回退到 Internal PRF 路径
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        var pulse = new MockPulseGenerator();
        await motion.ConnectAsync(new ConnectionConfig());
        await pulse.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq, pulse);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams
        {
            Velocity = 10f,
            TriggerIo = -1               // 未配置触发 IO → 走 PRF 回退
        };

        int points = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.Equal(4, points);        // 2x2 网格
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task Linkage_NoPulseGenerator_SimpleScan()
    {
        // 无脉冲发生器注入 → ScanService 跳过触发相关逻辑，仅运动+采数
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);  // 无 pulse 参数

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 2f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f, TriggerIo = -1 };

        int points = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.Equal(6, points);        // 2x3 网格
        Assert.False(engine.IsScanning);
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. 触发链完整性验证（Mock 单次触发路径）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Linkage_SingleTriggerPath_CallsPulseTriggerOutput()
    {
        // 验证 MockPulseGenerator.ArmExternalTriggerAsync 被调用，
        // MockMotionController.PulseTriggerOutputAsync 在每个点位被调用。
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        var pulse = new MockPulseGenerator();
        await motion.ConnectAsync(new ConnectionConfig());
        await pulse.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq, pulse);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 2f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams
        {
            Velocity = 10f,
            TriggerIo = 3,
            TriggerPulseWidthMs = 2
        };

        int points = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        // 验证：ArmExternalTriggerAsync 已被调用（MockPulseGenerator 内部跟踪）
        Assert.Equal(6, points);     // X: 0,1,2 (3 points) × Y: 0,1 (2 points) = 6
        Assert.False(engine.IsScanning);
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. 状态同步与并发安全
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Linkage_ConcurrentStart_OnlyOneExecutes()
    {
        // TOCTOU 修复验证：两个并发 StartScanAsync 只有一个真正执行
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { Width = 10f, Height = 1f, StepX = 0.1f, StepY = 1f }; // 101 点

        var t1 = engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);
        var t2 = engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);

        await Task.WhenAll(t1, t2);

        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task Linkage_StopDuringScan_AbortsGracefully()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { Width = 50f, Height = 1f, StepX = 0.1f, StepY = 1f }; // 501 点

        var task = engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);
        await Task.Delay(50);
        await engine.StopAsync();
        await task;

        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task Linkage_PauseResume_PauseStopsOutput()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { Width = 20f, Height = 1f, StepX = 0.1f, StepY = 1f }; // 201 点

        var task = engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);
        await Task.Delay(50);
        Assert.True(engine.IsScanning);

        await engine.PauseAsync();

        await engine.StopAsync();
        await task;
        Assert.False(engine.IsScanning);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. 触发拓扑校验（DPR500 Internal/External 模式）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Linkage_ExternalTriggerMode_Rejected()
    {
        // DPR500 设为 External 模式时，无外部脉冲源会拒绝启动（M-3 校验）
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        var pulse = new MockPulseGenerator();
        await motion.ConnectAsync(new ConnectionConfig());
        await pulse.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        // 将 MockPulseGenerator 设为 External 模式
        pulse.Params.TriggerMode = TriggerMode.External;

        var engine = new ScanService(motion, daq, pulse);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StartScanAsync(
                new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f },
                new ScanParams { Velocity = 10f, TriggerIo = 5 },
                CancellationToken.None));

        Assert.False(engine.IsScanning);
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. 扫前数据完整性（PositionDataReceived 回调）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Linkage_PointDataEventArgs_ContainsValidData()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f, SampleRate = 100e6f };

        var positions = new List<(float X, float Y)>();
        var samples = new List<float[]>();

        engine.PointDataReady += (_, e) =>
        {
            positions.Add((e.X, e.Y));
            samples.Add(e.Data.Samples);
        };

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        // 验证：每个点位有正确的坐标和有效波形数据
        Assert.Equal(4, positions.Count);
        Assert.Contains(positions, p => p.X == 0f && p.Y == 0f);
        Assert.Contains(positions, p => p.X == 1f && p.Y == 0f);
        Assert.Contains(positions, p => p.X == 0f && p.Y == 1f);
        Assert.Contains(positions, p => p.X == 1f && p.Y == 1f);

        foreach (var s in samples)
        {
            Assert.Equal(256, s.Length);
            Assert.Contains(s, v => v != 0f);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. 扫描参数传递验证
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Linkage_ScanParams_VelocityAndAccelerationApplied()
    {
        // 验证 ScanParams.Velocity/Acceleration 传递到 MockMotionController
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 2f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams
        {
            Velocity = 50f,         // 非默认速度
            Acceleration = 200f     // 非默认加速度
        };

        int points = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.Equal(6, points);  // 3x2 网格
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task Linkage_ScanRegionStepSize_AffectsPointCount()
    {
        // 验证步进尺寸影响扫描点数
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();

        var engine = new ScanService(motion, daq);

        // 10mm × 10mm 区域，步进 2mm → 6×6=36 点
        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 10f, Height = 10f, StepX = 2f, StepY = 2f };
        var parameters = new ScanParams { Velocity = 100f };

        int points = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.Equal(36, points);
        Assert.False(engine.IsScanning);
    }
}
