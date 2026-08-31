using System.Drawing;
using System.IO;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.PulseGen;
using UTscan.Mock;
using UTscan.Services;
using UTscan.Services.Imaging;
using UTscan.Services.SignalProcessing;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// Phase 4 功能测试：B 扫成像、.adtx 二进制格式、编码器触发扫查、JSR SDK 参数映射。
/// </summary>
public class Phase4Tests
{
    // ═══════════════════════════════════════════════════════════════
    //  B 扫成像 (BScanImageService)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BScan_Render_ProducesNonEmptyBitmap()
    {
        var svc = new BScanImageService();
        var ascans = new float[][]
        {
            new float[] { 0.1f, 0.5f, 0.8f, 0.3f },
            new float[] { 0.2f, 0.6f, 0.9f, 0.4f },
            new float[] { 0.3f, 0.7f, 1.0f, 0.5f }
        };
        var positions = new float[] { 0f, 1f, 2f };

        using var bmp = svc.Render(ascans, positions, 1e6f, 6000f, 0f, Colormap.Jet);

        Assert.True(bmp.Width > 0);
        Assert.True(bmp.Height > 0);
    }

    [Fact]
    public void BScan_Render_EmptyInput_Returns1x1Bitmap()
    {
        var svc = new BScanImageService();
        using var bmp = svc.Render(Array.Empty<float[]>(), Array.Empty<float>(), 1e6f, 6000f, 0f, Colormap.Jet);
        Assert.Equal(1, bmp.Width);
        Assert.Equal(1, bmp.Height);
    }

    [Fact]
    public void BScan_Render_MaxDepthLimitsRows()
    {
        var svc = new BScanImageService();
        // 100 samples @ 1MHz, v=6000 m/s → depth = 100 * 6000/2000 * 1us = 0.3mm
        var scan = new float[100];
        scan[50] = 0.9f;
        var ascans = new float[][] { scan };
        var positions = new float[] { 0f };

        // maxDepthMm = 0.1mm → should limit rows to ~33 samples
        using var bmp = svc.Render(ascans, positions, 1e6f, 6000f, 0f, Colormap.Jet, 0.1f);
        // Bitmap height should correspond to limited depth, not full 100 samples
        Assert.True(bmp.Height < 100 * 10, "Max depth should limit image height");
    }

    [Fact]
    public void BScan_GetDepthAxis_CalculatesCorrectDepths()
    {
        var svc = new BScanImageService();
        // 1 MHz sample rate, 6000 m/s sound velocity
        // dt = 1us, mmPerSample = 6000/2000 * 1us = 3mm/sample (one-way depth)
        var axis = svc.GetDepthAxis(5, 1e6f, 6000f, 0f);

        Assert.Equal(0f, axis[0], 5);
        Assert.Equal(3f, axis[1], 4);
        Assert.Equal(6f, axis[2], 4);
        Assert.Equal(12f, axis[4], 4);
    }

    [Fact]
    public void BScan_GetDepthAxis_WithZeroOffset_ShiftsAxis()
    {
        var svc = new BScanImageService();
        // zeroOffset = 2us → depth shift = 2 * 6000/2000 = 6mm
        var axis = svc.GetDepthAxis(3, 1e6f, 6000f, 2f);

        Assert.Equal(-6f, axis[0], 3);
        Assert.Equal(-3f, axis[1], 3);
        Assert.Equal(0f, axis[2], 3);
    }

    [Fact]
    public void BScan_ExtractDepthSlice_ReturnsAmplitudes()
    {
        var svc = new BScanImageService();
        var ascans = new float[][]
        {
            new float[] { 0.1f, 0.5f, 0.8f },
            new float[] { 0.2f, 0.6f, 0.9f },
            new float[] { 0.3f, 0.7f, 1.0f }
        };

        var slice = svc.ExtractDepthSlice(ascans, 1);

        Assert.Equal(3, slice.Length);
        Assert.Equal(0.5f, slice[0], 4);
        Assert.Equal(0.6f, slice[1], 4);
        Assert.Equal(0.7f, slice[2], 4);
    }

    [Fact]
    public void BScan_ExtractDepthSlice_OutOfRange_ReturnsZero()
    {
        var svc = new BScanImageService();
        var ascans = new float[][]
        {
            new float[] { 0.1f, 0.5f },
            new float[] { 0.2f }
        };

        var slice = svc.ExtractDepthSlice(ascans, 5);

        Assert.Equal(2, slice.Length);
        Assert.Equal(0f, slice[0], 4);
        Assert.Equal(0f, slice[1], 4);
    }

    // ═══════════════════════════════════════════════════════════════
    //  .adtx 二进制格式 (AdtxDataService)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Adtx_SaveLoad_RoundTrip_PreservesData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            var svc = new AdtxDataService();
            var ascans = new float[][]
            {
                new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new float[] { 0.5f, 0.6f, 0.7f, 0.8f },
                new float[] { 0.9f, 1.0f, -0.1f, -0.2f }
            };
            var positions = new float[] { 0f, 1.5f, 3.0f };
            var region = new ScanRegion { StartX = 0, StartY = 0, Width = 3f, Height = 1f, StepX = 1.5f, StepY = 1f };
            var sysParams = new SystemParams { SoundVelocity = 6000f, FocalLength = 25f, ZeroOffsetUs = 2.5f, RulerUnit = "mm" };

            svc.Save(path, ascans, positions, region, sysParams, 100e6f);

            var loaded = svc.Load(path);

            Assert.Equal((ushort)1, loaded.Version);
            Assert.Equal(3, loaded.ColumnCount);
            Assert.Equal(4, loaded.SampleCount);
            Assert.Equal(100e6f, loaded.SampleRate);
            Assert.Equal(6000f, loaded.SystemParams.SoundVelocity);
            Assert.Equal(25f, loaded.SystemParams.FocalLength);
            Assert.Equal(2.5f, loaded.SystemParams.ZeroOffsetUs);

            // Verify position array
            Assert.Equal(0f, loaded.Positions[0], 4);
            Assert.Equal(1.5f, loaded.Positions[1], 4);
            Assert.Equal(3.0f, loaded.Positions[2], 4);

            // Verify waveform data
            Assert.Equal(0.1f, loaded.Ascans[0][0], 5);
            Assert.Equal(0.8f, loaded.Ascans[1][3], 5);
            Assert.Equal(-0.2f, loaded.Ascans[2][3], 5);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Adtx_Save_EmptyData_Throws()
    {
        var svc = new AdtxDataService();
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            Assert.Throws<ArgumentException>(() =>
                svc.Save(path, Array.Empty<float[]>(), Array.Empty<float>(),
                    new ScanRegion(), new SystemParams(), 100e6f));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Adtx_Save_PositionCountMismatch_Throws()
    {
        var svc = new AdtxDataService();
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            var ascans = new float[][] { new float[] { 0.1f, 0.2f } };
            var positions = new float[] { 0f, 1f }; // 2 positions for 1 ascan

            Assert.Throws<ArgumentException>(() =>
                svc.Save(path, ascans, positions, new ScanRegion(), new SystemParams(), 100f));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Adtx_Load_InvalidMagic_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x42, 0x41, 0x44, 0x21, 0, 0, 0, 0 });
            var svc = new AdtxDataService();
            Assert.Throws<InvalidDataException>(() => svc.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Adtx_Load_HeaderSizeTooSmall_Throws()
    {
        // M-6：headerSize 声明小于最小头（256）必须拒绝（原实现会 fs.Position=headerSize 读错位）
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write("ADTX"u8.ToArray());   // 魔数
                bw.Write((ushort)1);             // 版本
                bw.Write((ushort)8);             // 非法 headerSize=8 < 256
                for (int i = 0; i < 64; i++) bw.Write(0f);  // 填充
            }
            var svc = new AdtxDataService();
            Assert.Throws<InvalidDataException>(() => svc.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Adtx_Load_HeaderSizeExceedsFileLength_Throws()
    {
        // M-6：headerSize 声明大于文件长度必须拒绝（文件被截断场景）
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write("ADTX"u8.ToArray());
                bw.Write((ushort)1);
                bw.Write((ushort)0xFFFF);        // 非法 headerSize=65535 远超文件长度
            }
            var svc = new AdtxDataService();
            Assert.Throws<InvalidDataException>(() => svc.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Adtx_SaveLoad_SingleAscan_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utscan_p4_{Guid.NewGuid():N}.adtx");
        try
        {
            var svc = new AdtxDataService();
            var ascans = new float[][] { new float[] { 1f, -1f, 0.5f, -0.5f } };
            var positions = new float[] { 0f };

            svc.Save(path, ascans, positions, new ScanRegion(), new SystemParams(), 50e6f);
            var loaded = svc.Load(path);

            Assert.Single(loaded.Ascans);
            Assert.Equal(4, loaded.Ascans[0].Length);
            Assert.Equal(1f, loaded.Ascans[0][0], 5);
            Assert.Equal(-0.5f, loaded.Ascans[0][3], 5);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  编码器触发扫查 (ScanService with EncoderTriggered strategy)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ScanService_EncoderTriggered_FiresLineScanComplete()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 64, SampleRate = 1000f });
        await daq.StartContinuousAsync();
        await Task.Delay(200); // Let mock generate some data

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 2f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f, Strategy = ScanStrategy.EncoderTriggered, SampleRate = 1000f };

        LineScanCompleteEventArgs? lineArgs = null;
        int pointCount = 0;
        engine.LineScanComplete += (_, e) => lineArgs = e;
        engine.PointDataReady += (_, _) => Interlocked.Increment(ref pointCount);

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.NotNull(lineArgs);
        Assert.True(pointCount > 0, "Should have acquired at least one point");
        Assert.Equal(1000f, lineArgs!.SampleRate);
    }

    [Fact]
    public async Task ScanService_PointByPoint_DoesNotFireLineScanComplete()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 64, SampleRate = 1000f });
        await daq.StartContinuousAsync();   // NH-8：扫查前置要求采集卡运行

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { Width = 1f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f, Strategy = ScanStrategy.PointByPoint };

        bool lineScanFired = false;
        engine.LineScanComplete += (_, _) => lineScanFired = true;

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.False(lineScanFired, "PointByPoint strategy should not fire LineScanComplete");
    }

    [Fact]
    public async Task ScanService_EncoderTriggered_LineScanContainsPositions()
    {
        using var motion = new MockMotionController();
        using var daq = new MockDaqCard();
        await motion.ConnectAsync(new ConnectionConfig());
        await daq.InitializeAsync(new ConnectionConfig { SampleCount = 32, SampleRate = 1000f });
        await daq.StartContinuousAsync();
        await Task.Delay(200);

        var engine = new ScanService(motion, daq);

        var region = new ScanRegion { StartX = 0, StartY = 0, Width = 3f, Height = 1f, StepX = 1f, StepY = 1f };
        var parameters = new ScanParams { Velocity = 10f, Strategy = ScanStrategy.EncoderTriggered };

        LineScanCompleteEventArgs? lineArgs = null;
        engine.LineScanComplete += (_, e) => lineArgs = e;

        await engine.StartScanAsync(region, parameters, CancellationToken.None);

        Assert.NotNull(lineArgs);
        Assert.True(lineArgs!.Positions.Length > 0, "Should have position data");
        Assert.True(lineArgs.Waveforms.Length > 0, "Should have waveform data");
        // Each waveform should have samples
        Assert.True(lineArgs.Waveforms[0].Length > 0, "Waveform should have samples");
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSR SDK 参数映射 (Dpr500Protocol helpers)
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(3e6f, 0)]      // 3 MHz → index 0
    [InlineData(7.5e6f, 1)]    // 7.5 MHz → index 1
    [InlineData(10e6f, 2)]     // 10 MHz → index 2
    [InlineData(15e6f, 3)]     // 15 MHz → index 3
    [InlineData(22.5e6f, 4)]   // 22.5 MHz → index 4
    [InlineData(50e6f, 5)]     // 50 MHz → index 5
    public void Dpr500_FindNearestLowPassIndex_ExactMatch(float hz, int expectedIndex)
    {
        int idx = Dpr500Protocol.FindNearestLowPassIndex(hz);
        Assert.Equal(expectedIndex, idx);
    }

    [Theory]
    [InlineData(0f, 0)]        // 0 MHz → index 0
    [InlineData(1e6f, 1)]      // 1 MHz → index 1
    [InlineData(2.5e6f, 2)]    // 2.5 MHz → index 2
    [InlineData(5e6f, 3)]      // 5 MHz → index 3
    [InlineData(7.5e6f, 4)]    // 7.5 MHz → index 4
    [InlineData(12.5e6f, 5)]   // 12.5 MHz → index 5
    public void Dpr500_FindNearestHighPassIndex_ExactMatch(float hz, int expectedIndex)
    {
        int idx = Dpr500Protocol.FindNearestHighPassIndex(hz);
        Assert.Equal(expectedIndex, idx);
    }

    [Theory]
    [InlineData(4e6f, 0)]      // 4 MHz is closer to 3 MHz than 7.5 MHz → index 0
    [InlineData(8e6f, 1)]      // 8 MHz is closer to 7.5 MHz than 10 MHz → index 1
    [InlineData(12e6f, 2)]     // 12 MHz is closer to 10 MHz than 15 MHz → index 2
    public void Dpr500_FindNearestLowPassIndex_NearestMatch(float hz, int expectedIndex)
    {
        int idx = Dpr500Protocol.FindNearestLowPassIndex(hz);
        Assert.Equal(expectedIndex, idx);
    }

    [Theory]
    [InlineData(0.6e6f, 1)]    // 0.6 MHz is closer to 1 MHz than 0 MHz → index 1
    [InlineData(3e6f, 2)]      // 3 MHz is closer to 2.5 MHz → index 2
    [InlineData(10.5e6f, 5)]   // 10.5 MHz is closer to 12.5 MHz than 7.5 MHz → index 5
    public void Dpr500_FindNearestHighPassIndex_NearestMatch(float hz, int expectedIndex)
    {
        int idx = Dpr500Protocol.FindNearestHighPassIndex(hz);
        Assert.Equal(expectedIndex, idx);
    }

    [Fact]
    public void ScanStrategy_DefaultIsPointByPoint()
    {
        var p = new ScanParams();
        Assert.Equal(ScanStrategy.PointByPoint, p.Strategy);
    }

    [Fact]
    public void ScanParams_CanSetEncoderTriggered()
    {
        var p = new ScanParams { Strategy = ScanStrategy.EncoderTriggered };
        Assert.Equal(ScanStrategy.EncoderTriggered, p.Strategy);
    }
}
