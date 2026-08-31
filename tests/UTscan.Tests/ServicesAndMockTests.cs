using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Mock;
using UTscan.Services;
using UTscan.Services.SignalProcessing;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// 服务层 + Mock 硬件行为测试（不涉及 WinForms 控件）。
/// </summary>
public class ServicesAndMockTests
{
    // ---------- AuthService ----------

    [Theory]
    [InlineData("", "x", false)]                                   // 用户名空
    [InlineData("user", "", false)]                                // 未知用户
    [InlineData(" ", "x", false)]                                  // 用户名空白
    [InlineData("admin", "123", true)]               // 管理员正确密码（P0-4 后真实校验）
    [InlineData("operator", "123", true)]            // 操作员正确密码
    [InlineData("admin", "wrong-password", false)]              // 密码错误（跨账号密码）
    [InlineData("operator", "any", false)]                         // 密码错误（任意密码不再放行）
    [InlineData("ADMIN", "123", true)]               // 用户名大小写不敏感
    public async Task AuthService_Validation(string user, string pass, bool expectedOk)
    {
        var auth = new AuthService();
        var ok = await auth.LoginAsync(user, pass);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedOk, auth.IsLoggedIn);

        if (expectedOk)
        {
            Assert.Equal(user, auth.CurrentUser!.Username, ignoreCase: true);
            if (string.Equals(user, "admin", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(UserRole.Admin, auth.CurrentUser.Role);
            else
                Assert.Equal(UserRole.Operator, auth.CurrentUser.Role);
        }
    }

    [Fact]
    public async Task AuthService_Logout_ClearsState()
    {
        var auth = new AuthService();
        Assert.True(await auth.LoginAsync("admin", "123"));
        Assert.True(auth.IsLoggedIn);
        auth.Logout();
        Assert.False(auth.IsLoggedIn);
    }

    [Fact]
    public async Task AuthService_LoginAndLoginAsync_UseSameValidation()
    {
        // P0-4：两个入口必须同一校验路径（旧版 LoginAsync 任意非空密码放行）
        var auth = new AuthService();
        var a = new AuthService();

        Assert.True(await auth.LoginAsync("operator", "123"));
        Assert.Null(a.Login("operator", "wrong-password"));
        Assert.False(await auth.LoginAsync("operator", "wrong-password"));
        Assert.Null(a.Login("", ""));
    }

    // ---------- MockMotionController ----------

    [Fact]
    public async Task MockMotion_ConnectEnableMove_PositionConverges()
    {
        using var motion = new MockMotionController();
        Assert.False(motion.IsConnected);

        Assert.True(await motion.ConnectAsync(new ConnectionConfig()));
        Assert.True(motion.IsConnected);

        await motion.EnableAxisAsync(AxisId.X);
        await motion.EnableAxisAsync(AxisId.Y);

        await motion.MoveAbsoluteAsync(AxisId.X, 10f, new ScanParams());
        await motion.MoveAbsoluteAsync(AxisId.Y, 5f, new ScanParams());

        // Mock 定时器 50ms 一拍、每拍走 0.5mm → 10mm 需 20 拍 ≈ 1s+裕量
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (MathF.Abs(motion.GetPosition(AxisId.X) - 10f) < 0.01f &&
                MathF.Abs(motion.GetPosition(AxisId.Y) - 5f) < 0.01f)
                break;
            await Task.Delay(50);
        }

        Assert.InRange(motion.GetPosition(AxisId.X), 9.99f, 10.01f);
        Assert.InRange(motion.GetPosition(AxisId.Y), 4.99f, 5.01f);
    }

    [Fact]
    public async Task MockMotion_Stop_FreezesPosition()
    {
        using var motion = new MockMotionController();
        await motion.ConnectAsync(new ConnectionConfig());
        await motion.EnableAxisAsync(AxisId.X);
        await motion.MoveAbsoluteAsync(AxisId.X, 100f, new ScanParams());
        await Task.Delay(200);

        await motion.StopAsync(AxisId.X);
        var frozen = motion.GetPosition(AxisId.X);
        await Task.Delay(200);

        Assert.Equal(frozen, motion.GetPosition(AxisId.X));
    }

    // ---------- MockDaqCard ----------

    [Fact]
    public async Task MockDaq_Start_GeneratesData()
    {
        using var daq = new MockDaqCard();
        Assert.True(await daq.InitializeAsync(new ConnectionConfig { SampleCount = 512, SampleRate = 1000f }));
        Assert.False(daq.IsRunning);

        await daq.StartContinuousAsync();
        Assert.True(daq.IsRunning);

        await Task.Delay(300);   // 100ms 定时器至少触发 2 次
        var data = daq.GetCurrentData();

        Assert.NotNull(data);
        Assert.Equal(512, data.PointCount);
        Assert.Equal(1000f, data.SampleRate);
        Assert.Contains(data.Samples, v => v != 0f);   // 非全零

        await daq.StopAsync();
        Assert.False(daq.IsRunning);
    }

    [Fact]
    public async Task MockDaq_Initialize_NoSampleRate_DefaultsTo100MHz()
    {
        // M-9：默认采样率与硬件一致（100 MHz）——原默认 100 Hz 与真实卡差 6 个数量级，
        // 缺 sampleRate 时 Mock 波形时间轴会错误（dt=10000μs）
        using var daq = new MockDaqCard();
        Assert.True(await daq.InitializeAsync(new ConnectionConfig { SampleCount = 128 }));

        var data = daq.GetCurrentData();
        Assert.Equal(100e6f, data.SampleRate);
    }

    // ---------- ScanService ----------

    [Fact]
    public async Task ScanService_SmallRegion_CompletesAllPoints()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());   // P1 守卫：未连接拒绝启动扫描
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();   // NH-8：扫查前置要求采集卡运行
        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f };

        int points = 0, lastProgress = 0;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref points);
        engine.ProgressChanged += (_, e) => lastProgress = e.CompletedPoints;

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.Equal(4, points);                     // 2x2 网格
        Assert.Equal(4, lastProgress);
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task ScanService_Stop_CancelsScan()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { Width = 50f, Height = 1f, StepX = 0.1f, StepY = 1f };  // 501 点

        var task = engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);
        await Task.Delay(100);
        await engine.StopAsync();
        await task;                                   // 应正常结束（不抛，用户取消不外抛）

        Assert.False(engine.IsScanning);
    }

    // ── 断点续扫（20260828）──

    [Fact]
    public async Task ScanService_Stop_CreatesBreakpoint_ResumeSkipsCompletedRows()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        var engine = new ScanService(motion, daq);

        // 小网格：3×3 区域 Step=1 → 每轴 ceil(3)+1=4 点（含起点）→ 4×4=16 点
        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 3f, Height = 3f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f };
        const int totalPoints = 16;   // PointCountX=4, PointCountY=4

        var firstPointDone = new ManualResetEventSlim(false);
        int points = 0;
        engine.PointDataReady += (_, _) =>
        {
            Interlocked.Increment(ref points);
            firstPointDone.Set();
        };

        // 启动后等第一点完成再停（确保有断点数据）
        var task = engine.StartScanAsync(region, parameters, CancellationToken.None);
        Assert.True(firstPointDone.Wait(5000), "第一点应在 5 秒内完成");
        await engine.StopAsync();
        await task;
        int pointsBeforeResume = points;

        // 应产生断点
        Assert.True(pointsBeforeResume > 0, "停止前应有至少 1 个点完成");
        Assert.True(engine.HasBreakpoint, "停止后应存在可恢复断点");
        Assert.True(engine.BreakpointPercent > 0 && engine.BreakpointPercent < 100, "断点进度应在 0~100 之间");

        // 续扫：数据不重复
        bool resumed = await engine.ResumeFromBreakpointAsync(CancellationToken.None);
        Assert.True(resumed, "存在断点时续扫应成功");
        Assert.True(points > pointsBeforeResume, $"续扫应产生更多数据（停止时{pointsBeforeResume}，续扫后{points}）");
        Assert.True(points <= totalPoints, $"数据不重复：总点数 {points} 不得超过 {totalPoints}（停止时 {pointsBeforeResume}，断点续扫跳过已扫行/列）");
        Assert.False(engine.HasBreakpoint, "续扫完成后应清除断点");
    }

    [Fact]
    public async Task ScanService_NoBreakpoint_ResumeReturnsFalse()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        var engine = new ScanService(motion, daq);

        // 从未扫描 → 无断点 → 续扫返回 false
        Assert.False(engine.HasBreakpoint);
        Assert.False(await engine.ResumeFromBreakpointAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScanService_NotConnected_RejectsStart()
    {
        // P1 守卫：运动控制器未连接时拒绝启动扫查（未连接的 Move 会静默成功导致位置错标）
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        var engine = new ScanService(motion, daq);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StartScanAsync(new ScanRegion { Width = 1f, Height = 1f, StepX = 1f, StepY = 1f },
                new ScanParams(), CancellationToken.None));
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task ScanService_InvalidRegion_RejectsAndUnlocks()
    {
        // L1-FIX 回归（审查 20260828）：ValidateScanRegion 抛异常路径必须复位 _isScanning，
        // 否则扫描功能被输入错误永久锁死直至重启（原缺陷）。
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        var engine = new ScanService(motion, daq);

        // 非法步距（StepX < 0.1 下限）触发 ValidateScanRegion 抛异常
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StartScanAsync(new ScanRegion { Width = 1f, Height = 1f, StepX = 0.01f, StepY = 1f },
                new ScanParams(), CancellationToken.None));
        Assert.False(engine.IsScanning, "ValidateScanRegion 异常后 _isScanning 必须复位（防锁死）");

        // 异常后可再次正常启动（不锁死）
        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        await engine.StartScanAsync(region, new ScanParams { Velocity = 10f }, CancellationToken.None);
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task ScanService_TooMuchData_RejectsAndUnlocks()
    {
        // L1-FIX 回归（审查 20260828）：ValidateScanDataSize（数据量超预算）抛异常路径必须复位，
        // 防止用户常见输入（区域过大）锁死扫描。
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        var engine = new ScanService(motion, daq);

        // 巨大区域触发 ValidateScanDataSize 抛异常
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.StartScanAsync(
                new ScanRegion { Width = 5000f, Height = 5000f, StepX = 0.001f, StepY = 0.001f },
                new ScanParams(), CancellationToken.None));
        Assert.False(engine.IsScanning, "ValidateScanDataSize 异常后 _isScanning 必须复位（防锁死）");

        // 异常后仍可正常启动
        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        await engine.StartScanAsync(region, new ScanParams { Velocity = 10f }, CancellationToken.None);
        Assert.False(engine.IsScanning);
    }

    [Fact]
    public async Task ScanService_StartWhileScanning_IsIgnored()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { Width = 5f, Height = 1f, StepX = 0.1f, StepY = 1f };
        var t1 = engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);
        await engine.StartScanAsync(region, new ScanParams(), CancellationToken.None);   // 应直接返回
        await t1;

        Assert.False(engine.IsScanning);
    }

    // ═══════════════════════════════════════════════════════════════
    //  L-6：MockMotionController 按 Velocity 运动（审查报告 2026-08-18-v2 L-6）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MockMotion_MoveDistance_DependsOnVelocity()
    {
        using var motion = new MockMotionController();
        await motion.ConnectAsync(new ConnectionConfig());

        // 高速度（100 mm/s）：50ms tick → 每 tick 5mm，1mm 目标 1 tick 即到
        await motion.MoveAbsoluteAsync(AxisId.X, 1f, new ScanParams { Velocity = 100f });
        await Task.Delay(120);
        Assert.True(motion.IsAxisIdle(AxisId.X));
        Assert.Equal(1f, motion.GetPosition(AxisId.X), 2);

        // 低速度（1 mm/s）：50ms tick → 每 tick 0.05mm，从 1mm 走回 0 需 20 tick（约 1s）
        await motion.MoveAbsoluteAsync(AxisId.X, 0f, new ScanParams { Velocity = 1f });
        await Task.Delay(150);
        // 150ms 只走了约 0.15mm，位置应仍在 ~0.85（离目标 0 尚远；
        // 原固定 0.5mm/tick 实现 150ms 即可走完 1mm 全程）
        Assert.True(motion.GetPosition(AxisId.X) > 0.5f, $"低速 150ms 应从 1.0 走至 ~0.85，实际 {motion.GetPosition(AxisId.X)}");
        Assert.True(motion.GetPosition(AxisId.X) < 1.0f);
        Assert.False(motion.IsAxisIdle(AxisId.X));

        await motion.StopAsync(AxisId.X);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mock 数据链路端到端验证：连接→扫查→数据→成像值→导出
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MockScan_FullPipeline_DataReadyToExport()
    {
        // 覆盖"数据加载→显示→交互"完整链路：扫查产生有效 A 扫数据（非空、含信号），
        // 每点可计算成像值，闸门分析可用，且结果可导出为 .adtx（可回读）
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 256, SampleRate = 100e6f });
        await daq.StartContinuousAsync();
        await Task.Delay(150);   // 让 Mock 定时器生成首帧数据

        var engine = new ScanService(motion, daq);
        // Width=1,Step=1 → 2×2 网格 = 4 点
        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f, SampleRate = 100e6f };

        var samples = new List<float[]>();
        var positions = new List<float>();
        engine.PointDataReady += (_, e) =>
        {
            samples.Add(e.Data.Samples);
            positions.Add(e.X);
        };

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        // 1) 扫查完整：4 个点全部采集
        Assert.Equal(4, samples.Count);
        Assert.Equal(4, positions.Count);

        // 2) 每帧数据有效：非空、采样点数正确、含非零信号（Mock 生成正弦+噪声）
        foreach (var s in samples)
        {
            Assert.Equal(256, s.Length);
            Assert.Contains(s, v => v != 0f);
        }

        // 3) 成像值可计算（GateAnalyzer 在 Mock 数据上正常出值）
        var analyzer = new GateAnalyzer();
        foreach (var s in samples)
        {
            var data = new AScanData { Samples = s, SampleRate = 100e6f };
            var gate = new GateConfig { StartUs = 1f, WidthUs = 2f, ThresholdV = 0.05f };
            float v = analyzer.ComputeImagingValue(data, gate, CScanImagingMode.MaxPeak, WaveformType.Detected);
            Assert.True(float.IsFinite(v));
        }

        // 4) 可导出为 .adtx 并回读（数据交互闭环）
        string path = Path.Combine(Path.GetTempPath(), $"utscan-e2e-{Guid.NewGuid():N}.adtx");
        try
        {
            new AdtxDataService().Save(path, samples.ToArray(), positions.ToArray(), region, new SystemParams(), 100e6f);
            var loaded = new AdtxDataService().Load(path);
            Assert.Equal(4, loaded.ColumnCount);
            Assert.Equal(256, loaded.SampleCount);
            Assert.Equal(samples[0][0], loaded.Ascans[0][0], 5);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

}
