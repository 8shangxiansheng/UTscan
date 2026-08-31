using Microsoft.Extensions.DependencyInjection;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.Daq;
using UTscan.Hardware.PulseGen;
using UTscan.Hardware.Zmc;
using UTscan.Mock;
using UTscan.Services;
using UTscan.Services.SignalProcessing;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// Phase 3 硬件层单元测试。
/// 全部测试不依赖真实硬件——验证协议命令格式化、参数验证、
/// DI 切换逻辑和断连安全行为。
///
/// DPR500 协议基于 JSR DPR500 Operator Manual v2.2.0 + JSR Common SDK API v1.3。
/// </summary>
public class HardwareTests
{
    // ══════════════════════════════════════════════════════════════
    //  DPR500 协议常量验证
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dpr500Protocol_DefaultBaudRate_Is4800()
    {
        // JSR DPR500 Manual Page 20: 4800 baud
        Assert.Equal(4800, Dpr500Protocol.DefaultBaudRate);
    }

    // ══════════════════════════════════════════════════════════════
    //  DPR500 协议命令构建测试（二进制格式）
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dpr500Protocol_SetGain_ZeroDb_BuildsCorrectBinary()
    {
        // 0 dB → index = 0 - (-13) = 13
        byte[] cmd = Dpr500Protocol.BuildSetGain(0f);
        Assert.Equal(2, cmd.Length);
        Assert.Equal((byte)'g', cmd[0]);
        Assert.Equal(13, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetGain_MaxDb_BuildsCorrectBinary()
    {
        // 66 dB → index = 66 - (-13) = 79
        byte[] cmd = Dpr500Protocol.BuildSetGain(66f);
        Assert.Equal((byte)'g', cmd[0]);
        Assert.Equal(79, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetGain_MinDb_BuildsCorrectBinary()
    {
        // -13 dB → index = -13 - (-13) = 0
        byte[] cmd = Dpr500Protocol.BuildSetGain(-13f);
        Assert.Equal((byte)'g', cmd[0]);
        Assert.Equal(0, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetGain_ClampsOutOfRange()
    {
        // 100 dB → clamped to 66 → index 79
        byte[] cmd = Dpr500Protocol.BuildSetGain(100f);
        Assert.Equal(79, cmd[1]);

        // -100 dB → clamped to -13 → index 0
        cmd = Dpr500Protocol.BuildSetGain(-100f);
        Assert.Equal(0, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetGain_TenDb_RoundsCorrectly()
    {
        // 10 dB → index = 10 + 13 = 23
        byte[] cmd = Dpr500Protocol.BuildSetGain(10f);
        Assert.Equal(23, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetLowPass_50MHz_BuildsCorrectIndex()
    {
        // 50 MHz = 50e6 Hz → index 5 (last in table)
        byte[] cmd = Dpr500Protocol.BuildSetLowPass(50e6f);
        Assert.Equal((byte)'l', cmd[0]);
        Assert.Equal(5, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetLowPass_10MHz_BuildsCorrectIndex()
    {
        // 10 MHz → index 2
        byte[] cmd = Dpr500Protocol.BuildSetLowPass(10e6f);
        Assert.Equal(2, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetLowPass_3MHz_BuildsCorrectIndex()
    {
        // 3 MHz → index 0
        byte[] cmd = Dpr500Protocol.BuildSetLowPass(3e6f);
        Assert.Equal(0, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetHighPass_1MHz_BuildsCorrectIndex()
    {
        // 1 MHz → index 1
        byte[] cmd = Dpr500Protocol.BuildSetHighPass(1e6f);
        Assert.Equal((byte)'h', cmd[0]);
        Assert.Equal(1, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetHighPass_5MHz_BuildsCorrectIndex()
    {
        // 5 MHz → index 3
        byte[] cmd = Dpr500Protocol.BuildSetHighPass(5e6f);
        Assert.Equal(3, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetHighPass_ZeroHz_BuildsCorrectIndex()
    {
        // 0 Hz → index 0
        byte[] cmd = Dpr500Protocol.BuildSetHighPass(0f);
        Assert.Equal(0, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetPulserConfig_PulseEcho_LowEnergy_LowDamping()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPulserConfig(
            PulseMode.PulseEcho, energyIndex: 0, dampingIndex: 0);
        Assert.Equal((byte)'f', cmd[0]);
        // bit7=0 (energy), bit6=0 (echo), bits5-4=0 (damping) → 0x00
        Assert.Equal(0x00, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetPulserConfig_ThroughTransmission_HighEnergy_MaxDamping()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPulserConfig(
            PulseMode.ThroughTransmission, energyIndex: 1, dampingIndex: 3);
        // bit7=1 (energy), bit6=1 (thru), bits5-4=11 (damping=3) → 0xF0
        Assert.Equal(0xF0, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetPulserConfig_PulseEcho_HighEnergy_MidDamping()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPulserConfig(
            PulseMode.PulseEcho, energyIndex: 1, dampingIndex: 1);
        // bit7=1, bit6=0, bits5-4=01 → 0x90
        Assert.Equal(0x90, cmd[1]);
    }

    [Fact]
    public void Dpr500Protocol_SetPrf_1000Hz_BuildsCorrectBinary()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPrf(1000f);
        Assert.Equal(3, cmd.Length);
        Assert.Equal((byte)'p', cmd[0]);
        // 1000 = 0x03E8 → LE: E8 03
        Assert.Equal(0xE8, cmd[1]);
        Assert.Equal(0x03, cmd[2]);
    }

    [Fact]
    public void Dpr500Protocol_SetPrf_5000Hz_BuildsCorrectBinary()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPrf(5000f);
        // 5000 = 0x1388 → LE: 88 13
        Assert.Equal(0x88, cmd[1]);
        Assert.Equal(0x13, cmd[2]);
    }

    [Fact]
    public void Dpr500Protocol_SetPrf_ClampsToMax()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPrf(99999f);
        // Should clamp to 5000
        Assert.Equal(0x88, cmd[1]);
        Assert.Equal(0x13, cmd[2]);
    }

    [Fact]
    public void Dpr500Protocol_SetPrf_ZeroHz_BuildsZero()
    {
        byte[] cmd = Dpr500Protocol.BuildSetPrf(0f);
        Assert.Equal(0x00, cmd[1]);
        Assert.Equal(0x00, cmd[2]);
    }

    [Fact]
    public void Dpr500Protocol_SetChannel_Channel1_BuildsZero()
    {
        byte[] cmd = Dpr500Protocol.BuildSetChannel(1);
        Assert.Equal((byte)'c', cmd[0]);
        Assert.Equal(0, cmd[1]);  // Channel 1 → 0 (A)
    }

    [Fact]
    public void Dpr500Protocol_SetChannel_Channel2_BuildsOne()
    {
        byte[] cmd = Dpr500Protocol.BuildSetChannel(2);
        Assert.Equal(1, cmd[1]);  // Channel 2 → 1 (B)
    }

    [Fact]
    public void Dpr500Protocol_QueryModel_BuildsSingleByte()
    {
        byte[] cmd = Dpr500Protocol.BuildQueryModel();
        Assert.Single(cmd);
        Assert.Equal((byte)'n', cmd[0]);
    }

    // ══════════════════════════════════════════════════════════════
    //  DPR500 批量命令测试
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dpr500Protocol_BuildApplyAllParams_Returns6Commands()
    {
        var p = new PulseParams();
        var commands = Dpr500Protocol.BuildApplyAllParams(p);

        // 6 条命令: Channel + Gain + LowPass + HighPass + PulserConfig + PRF
        Assert.Equal(6, commands.Count);

        // 验证命令字符序列
        Assert.Equal((byte)'c', commands[0][0]);  // Channel
        Assert.Equal((byte)'g', commands[1][0]);  // Gain
        Assert.Equal((byte)'l', commands[2][0]);  // LowPass
        Assert.Equal((byte)'h', commands[3][0]);  // HighPass
        Assert.Equal((byte)'f', commands[4][0]);  // PulserConfig
        Assert.Equal((byte)'p', commands[5][0]);  // PRF
    }

    [Fact]
    public void Dpr500Protocol_BuildApplyAllParams_ChannelMapping()
    {
        var p = new PulseParams { Channel = 2 };
        var commands = Dpr500Protocol.BuildApplyAllParams(p);
        // Channel 2 → index 1
        Assert.Equal(1, commands[0][1]);
    }

    // ══════════════════════════════════════════════════════════════
    //  DPR500 协议响应解析测试
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dpr500Protocol_ParseResponse_Ack_ReturnsTrue()
    {
        bool ok = Dpr500Protocol.ParseResponse(Dpr500Protocol.Ack, out string? error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Dpr500Protocol_ParseResponse_Nak_ReturnsFalseWithError()
    {
        bool ok = Dpr500Protocol.ParseResponse(Dpr500Protocol.Nak, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("NAK", error);
    }

    [Fact]
    public void Dpr500Protocol_ParseResponse_UnknownByte_ReturnsFalse()
    {
        bool ok = Dpr500Protocol.ParseResponse(0xFF, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Dpr500Protocol_ParseModelResponse_ValidAscii_ReturnsString()
    {
        byte[] resp = { (byte)'D', (byte)'P', (byte)'R', (byte)'5', (byte)'0', (byte)'0' };
        string model = Dpr500Protocol.ParseModelResponse(resp);
        Assert.Equal("DPR500", model);
    }

    [Fact]
    public void Dpr500Protocol_ParseModelResponse_WithAckByte_FiltersAck()
    {
        byte[] resp = { Dpr500Protocol.Ack, (byte)'D', (byte)'P', (byte)'R', (byte)'5', (byte)'0', (byte)'0' };
        string model = Dpr500Protocol.ParseModelResponse(resp);
        Assert.Equal("DPR500", model);
    }

    [Fact]
    public void Dpr500Protocol_ParseModelResponse_Empty_ReturnsUnknown()
    {
        string model = Dpr500Protocol.ParseModelResponse(Array.Empty<byte>());
        Assert.Equal("Unknown", model);
    }

    // ══════════════════════════════════════════════════════════════
    //  DPR500 增益转换辅助测试
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dpr500Protocol_GainIndexToDb_CorrectMapping()
    {
        Assert.Equal(-13f, Dpr500Protocol.GainIndexToDb(0));
        Assert.Equal(0f, Dpr500Protocol.GainIndexToDb(13));
        Assert.Equal(66f, Dpr500Protocol.GainIndexToDb(79));
    }

    [Fact]
    public void Dpr500Protocol_GainDbToIndex_CorrectMapping()
    {
        Assert.Equal(0, Dpr500Protocol.GainDbToIndex(-13f));
        Assert.Equal(13, Dpr500Protocol.GainDbToIndex(0f));
        Assert.Equal(79, Dpr500Protocol.GainDbToIndex(66f));
    }

    [Fact]
    public void Dpr500Protocol_GainConversion_RoundTrip()
    {
        for (float db = -13f; db <= 66f; db += 5f)
        {
            int idx = Dpr500Protocol.GainDbToIndex(db);
            float resultDb = Dpr500Protocol.GainIndexToDb(idx);
            Assert.True(Math.Abs(resultDb - db) <= 1f, $"Round-trip failed for {db}dB");
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  DPR500 控制器参数验证测试（不连接硬件）
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Dpr500Controller_NotConnected_SetGainStillClampsParam()
    {
        using var ctrl = new Dpr500Controller();
        // DPR500 gain range: -13 to 66 dB
        await ctrl.SetGainAsync(100f);
        Assert.Equal(66f, ctrl.Params.GainDb);  // clamped to max 66

        await ctrl.SetGainAsync(-100f);
        Assert.Equal(-13f, ctrl.Params.GainDb); // clamped to min -13
    }

    [Fact]
    public async Task Dpr500Controller_NotConnected_SetPrfStillClampsParam()
    {
        using var ctrl = new Dpr500Controller();
        await ctrl.SetPrfAsync(10000f);
        Assert.Equal(5000f, ctrl.Params.PrfHz);

        // PL01 手册：PRF 范围 0~5000 Hz（0=单次触发），50Hz 不再被钳位到 100
        await ctrl.SetPrfAsync(50f);
        Assert.Equal(50f, ctrl.Params.PrfHz);

        await ctrl.SetPrfAsync(-10f);
        Assert.Equal(0f, ctrl.Params.PrfHz);   // 下限 0
    }

    [Fact]
    public async Task Dpr500Controller_NotConnected_SetPulseWidthClampsNegative()
    {
        using var ctrl = new Dpr500Controller();
        await ctrl.SetPulseWidthAsync(-50f);
        Assert.Equal(0f, ctrl.Params.PulseWidthNs);
    }

    [Fact]
    public async Task Dpr500Controller_NotConnected_SetModeUpdatesParam()
    {
        using var ctrl = new Dpr500Controller();
        await ctrl.SetModeAsync(PulseMode.ThroughTransmission);
        Assert.Equal(PulseMode.ThroughTransmission, ctrl.Params.Mode);

        await ctrl.SetModeAsync(PulseMode.PulseEcho);
        Assert.Equal(PulseMode.PulseEcho, ctrl.Params.Mode);
    }

    [Fact]
    public async Task Dpr500Controller_ApplyParams_NotConnected_ClampsAllValues()
    {
        using var ctrl = new Dpr500Controller();
        var p = new PulseParams
        {
            GainDb = 100f,
            PrfHz = 10000f,
            Voltage = 500f,
            EnergyLevel = 10,
            PulseWidthNs = -10f
        };
        await ctrl.ApplyParamsAsync(p);

        Assert.Equal(66f, ctrl.Params.GainDb);     // clamped to 66
        Assert.Equal(5000f, ctrl.Params.PrfHz);
        Assert.Equal(330f, ctrl.Params.Voltage);
        Assert.Equal(4, ctrl.Params.EnergyLevel);
        Assert.Equal(0f, ctrl.Params.PulseWidthNs);
    }

    [Fact]
    public async Task Dpr500Controller_ConnectInvalidPort_ReturnsFalse()
    {
        using var ctrl = new Dpr500Controller();
        var config = new ConnectionConfig { SerialPort = "COM999", BaudRate = 4800 };
        bool ok = await ctrl.ConnectAsync(config);
        Assert.False(ok);
        Assert.False(ctrl.IsConnected);
    }

    // ══════════════════════════════════════════════════════════════
    //  JSR SDK 常量与断连状态码（§2.3 #1 头文件吸收核对）
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void JsrNative_PropertyIds_MatchHeaderFile()
    {
        // 基准：JSR_PropertyID.h（JSR Common API 3.3.0）
        Assert.Equal(514, JsrNative.JSR_ID_ReferenceHighLowNamesInv);
        Assert.Equal(1001, JsrNative.JSR_ID_LibraryInstrumentHandles);
        Assert.Equal(2001, JsrNative.JSR_ID_InstrumentModelName);
        Assert.Equal(2003, JsrNative.JSR_ID_InstrumentSerNum);
        Assert.Equal(2500, JsrNative.JSR_ID_InstrumentPCISlot);      // 仅 PRC50
        Assert.Equal(2520, JsrNative.JSR_ID_InstrumentSerialComPort);   // DPR500 扩展
        Assert.Equal(2521, JsrNative.JSR_ID_InstrumentSerialChainAddress);
        Assert.Equal(2522, JsrNative.JSR_ID_InstrumentPowerLEDControl);
        Assert.Equal(3001, JsrNative.JSR_ID_ChannelLetter);
        Assert.Equal(4002, JsrNative.JSR_ID_PulserTriggerSource);
        Assert.Equal(4003, JsrNative.JSR_ID_PulserPRF);
        Assert.Equal(4004, JsrNative.JSR_ID_PulserVolts);
        Assert.Equal(4008, JsrNative.JSR_ID_PulserDampResistorIndex);
        Assert.Equal(4010, JsrNative.JSR_ID_PulserIsPulsing);
        Assert.Equal(4012, JsrNative.JSR_ID_PulserExtTriggerZIndex);
        Assert.Equal(4013, JsrNative.JSR_ID_PulserTriggerEdge);
        Assert.Equal(4014, JsrNative.JSR_ID_PulserPowerLimitStatus);
        Assert.Equal(5001, JsrNative.JSR_ID_ReceiverSignalSelect);
        Assert.Equal(5002, JsrNative.JSR_ID_ReceiverGainDB);
        Assert.Equal(5004, JsrNative.JSR_ID_ReceiverLPFilterIndex);
        Assert.Equal(5006, JsrNative.JSR_ID_ReceiverHPFilterIndex);
    }

    [Fact]
    public void JsrNative_LedAndTriggerEnumValues_MatchHeaderFile()
    {
        // 触发源/信号选择/边沿（JSR_Types.h）
        Assert.Equal(0, JsrNative.JSR_TRIGGER_INTERNAL);
        Assert.Equal(1, JsrNative.JSR_TRIGGER_EXTERNAL);
        Assert.Equal(2, JsrNative.JSR_TRIGGER_SLAVE);
        Assert.Equal(0, JsrNative.JSR_SIGNAL_SELECT_TR_ECHO);
        Assert.Equal(1, JsrNative.JSR_SIGNAL_SELECT_THROUGH);
        Assert.Equal(2, JsrNative.JSR_SIGNAL_SELECT_BOTH);
        Assert.Equal(0, JsrNative.JSR_TRIGGER_EDGE_RISING);
        Assert.Equal(1, JsrNative.JSR_TRIGGER_EDGE_FALLING);

        // LED 枚举（Properties Reference §11.7 / §11.20）
        Assert.Equal(0, JsrNative.JSR_LED_PULSE_ACTIVITY);
        Assert.Equal(1, JsrNative.JSR_LED_IDENTIFY_BOARD);
        Assert.Equal(0, JsrNative.JSR_POWER_LED_BLINK_VERY_SLOW);
        Assert.Equal(25, JsrNative.JSR_POWER_LED_BLINK_SLOW);
        Assert.Equal(200, JsrNative.JSR_POWER_LED_BLINK_FAST);
        Assert.Equal(254, JsrNative.JSR_POWER_LED_BLINK_VERY_FAST);
        Assert.Equal(255, JsrNative.JSR_POWER_LED_ON);
    }

    [Fact]
    public void JsrNative_IsDisconnectError_ClassifiesDisconnectCodes()
    {
        // 断连类（JSR_Status.h）：触发 ConnectionLost
        Assert.True(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_INSTRUMENT_DISCONNECTED));   // 2223
        Assert.True(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_INSTRUMENT_POWER_CYCLED));   // 2226
        Assert.True(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_PULSER_RECONNECTED));        // 2266
        Assert.True(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_PULSER_DISCONNECTED));       // 2267
        Assert.True(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_PULSER_HARDWARE_FAILED));    // 2269
        Assert.True(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_DPR_COMMO_FAILURE));         // 2703

        // 非断连类：OK / WARN / 其他 FAIL
        Assert.False(JsrNative.IsDisconnectError(JsrNative.JSR_OK));
        Assert.False(JsrNative.IsDisconnectError(JsrNative.JSR_WARN_NO_INSTRUMENT_FOUND));      // 1036
        Assert.False(JsrNative.IsDisconnectError(JsrNative.JSR_FAIL_INSTRUMENT_STILL_OPEN));    // 2222
        Assert.False(JsrNative.IsDisconnectError(9999));
    }

    [Fact]
    public void JsrNative_IsPass_AcceptsOkAndWarn()
    {
        Assert.True(JsrNative.IsPass(JsrNative.JSR_OK));        // 0: OK
        Assert.True(JsrNative.IsPass(1024));                     // JSR_WARN_GENERAL
        Assert.True(JsrNative.IsPass(1500));                     // WARN 区间 1024~2047
        Assert.False(JsrNative.IsPass(2048));                    // FAIL 区间 >= 2048
        Assert.False(JsrNative.IsPass(2223));                    // FAIL 区间 >= 2048
        // NEW-M-5：应用状态码（1-1023）不属于 OK 也不属于 WARN，IsPass 应返回 false
        Assert.False(JsrNative.IsPass(500));                     // APPLICATION_STATUS
    }

    [Fact]
    public void JsrNative_IsApplicationStatus_DetectsInfoRange()
    {
        Assert.False(JsrNative.IsApplicationStatus(0));          // OK
        Assert.True(JsrNative.IsApplicationStatus(500));         // APPLICATION_STATUS
        Assert.True(JsrNative.IsApplicationStatus(1023));        // APPLICATION_STATUS 上界
        Assert.False(JsrNative.IsApplicationStatus(1024));       // WARN_GENERAL
    }

    // ══════════════════════════════════════════════════════════════
    //  DPR500 扩展 API 未连接安全行为（§2.2/§2.3 整改新增）
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Dpr500Controller_NotConnected_InstrumentInfoAndDiagnostics_Defaults()
    {
        using var ctrl = new Dpr500Controller();
        var info = ctrl.InstrumentInfo;
        Assert.Equal(string.Empty, info.ModelName);
        Assert.Equal(string.Empty, info.SerialNumber);
        Assert.Equal(0, info.ComPort);
        Assert.Equal(0, info.ChainAddress);
        Assert.Empty(info.DampingOhms);
        Assert.Empty(info.LowPassMHz);
        Assert.Empty(info.HighPassMHz);
        Assert.False(info.SupportsSlaveTrigger);
        Assert.False(info.SupportsBothSignalSelect);

        var diag = ctrl.GetDiagnostics();
        Assert.Equal(JsrNative.JSR_OK, diag.LibraryDriversStatus);
        Assert.Equal(JsrNative.JSR_OK, diag.InstrumentConnectStatus);
        Assert.Equal(JsrNative.JSR_OK, diag.PulserPowerLimitStatus);
        Assert.False(diag.IsPulsing);
        Assert.False(diag.IsPowerLimitExceeded);
        Assert.NotNull(diag.Describe());
    }

    [Fact]
    public async Task Dpr500Controller_NotConnected_ExtendedApis_ReturnFalseOrNoThrow()
    {
        using var ctrl = new Dpr500Controller();

        // 未连接：SDK 写入被静默跳过，返回 false 但不抛异常
        Assert.False(await ctrl.SetTriggerSourceAsync(JsrNative.JSR_TRIGGER_INTERNAL));
        Assert.False(await ctrl.SetSignalSelectAsync(JsrNative.JSR_SIGNAL_SELECT_TR_ECHO));

        // 不支持的能力即使连接也应拒绝：SLAVE 需双通道 DPR500、BOTH 需双工支持
        Assert.False(await ctrl.SetTriggerSourceAsync(JsrNative.JSR_TRIGGER_SLAVE));
        Assert.False(await ctrl.SetSignalSelectAsync(JsrNative.JSR_SIGNAL_SELECT_BOTH));

        // 静默安全型 API：不抛异常即可
        await ctrl.SetTriggerEdgeAsync(true);
        await ctrl.SetTriggerEdgeAsync(false);
        await ctrl.SetPulserLedIdentifyAsync(true);
        await ctrl.SetPulserLedIdentifyAsync(false);
        await ctrl.SetPowerLedBlinkRateAsync(JsrNative.JSR_POWER_LED_ON);
        Assert.False(await ctrl.SetExternalTriggerImpedanceAsync(0));

        // 重连未连接 → false 不抛
        Assert.False(await ctrl.ReconnectAsync());
    }

    [Fact]
    public async Task Dpr500Controller_NotConnected_SelectChannel_UpdatesParamOnly()
    {
        using var ctrl = new Dpr500Controller();
        // 未连接且通道表为空：仅钳位记录目标通道，返回 false
        Assert.False(await ctrl.SelectChannelAsync(2));
        Assert.Equal(1, ctrl.Params.Channel);   // 无通道表时上限 1，2 被钳位

        Assert.False(await ctrl.SelectChannelAsync(1));
        Assert.Equal(1, ctrl.Params.Channel);
    }

    [Fact]
    public async Task Dpr500Controller_NotConnected_ApplyParamsDoesNotThrow()
    {
        using var ctrl = new Dpr500Controller();
        var p = new PulseParams { GainDb = 30f, PrfHz = 2000f };
        // Should not throw even when not connected
        await ctrl.ApplyParamsAsync(p);
        Assert.Equal(30f, ctrl.Params.GainDb);
        Assert.Equal(2000f, ctrl.Params.PrfHz);
    }

    // ══════════════════════════════════════════════════════════════
    //  ZMC 运动控制器断连安全测试
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void ZmcMotionController_NotConnected_GetPositionReturnsZero()
    {
        using var ctrl = new ZmcMotionController();
        Assert.False(ctrl.IsConnected);
        Assert.Equal(0f, ctrl.GetPosition(AxisId.X));
        Assert.Equal(0f, ctrl.GetPosition(AxisId.Y));
    }

    [Fact]
    public async Task ZmcMotionController_NotConnected_EnableAxisReturnsFalse()
    {
        using var ctrl = new ZmcMotionController();
        bool ok = await ctrl.EnableAxisAsync(AxisId.X);
        Assert.False(ok);
    }

    [Fact]
    public async Task ZmcMotionController_NotConnected_MoveThrowsZmcException()
    {
        // P1-1 整改后语义：未连接时运动指令必须抛 ZmcException（原为静默返回成功）。
        using var ctrl = new ZmcMotionController();
        await Assert.ThrowsAsync<UTscan.Hardware.Zmc.ZmcException>(() => ctrl.MoveAbsoluteAsync(AxisId.X, 10f, new ScanParams()));
        await Assert.ThrowsAsync<UTscan.Hardware.Zmc.ZmcException>(() => ctrl.MoveRelativeAsync(AxisId.Y, 5f, new ScanParams()));
        // Stop/EStop 属安全操作，未连接时保持静默成功语义。
        await ctrl.StopAsync(AxisId.Z);
        await ctrl.EmergencyStopAsync();
    }

    [Fact]
    public void ZmcMotionController_NotConnected_IsAxisIdleReturnsTrue()
    {
        using var ctrl = new ZmcMotionController();
        Assert.True(ctrl.IsAxisIdle(AxisId.X));
    }

    [Fact]
    public async Task ZmcMotionController_ConnectInvalidIp_ReturnsFalse()
    {
        using var ctrl = new ZmcMotionController();
        var config = new ConnectionConfig { IpAddress = "999.999.999.999" };
        bool ok = await ctrl.ConnectAsync(config);
        Assert.False(ok);
        Assert.False(ctrl.IsConnected);
    }

    // ══════════════════════════════════════════════════════════════
    //  Spectrum M3i.3242 DAQ 采集卡断连安全测试
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void SpectrumDaqCard_NotInitialized_IsRunningFalse()
    {
        using var card = new SpectrumDaqCard();
        Assert.False(card.IsRunning);
    }

    [Fact]
    public void SpectrumDaqCard_NotInitialized_GetCurrentDataReturnsEmpty()
    {
        using var card = new SpectrumDaqCard();
        var data = card.GetCurrentData();
        Assert.NotNull(data);
        Assert.Empty(data.Samples);
    }

    [Fact]
    public void SpectrumDaqCard_GetCurrentDataByChannel_OutOfRangeFallsBack()
    {
        // L-3：按通道取帧；越界通道回退到 CH0 缓存（不抛异常）
        using var card = new SpectrumDaqCard();
        var ch0 = card.GetCurrentData(0);
        var ch1 = card.GetCurrentData(1);
        var oob = card.GetCurrentData(99);
        Assert.NotNull(ch0);
        Assert.NotNull(ch1);
        Assert.NotNull(oob);
        Assert.Empty(oob.Samples);   // 未采集时为空帧，不抛异常
    }

    [Fact]
    public async Task SpectrumDaqCard_NotInitialized_StopAsyncDoesNotThrow()
    {
        using var card = new SpectrumDaqCard();
        await card.StopAsync();
        Assert.False(card.IsRunning);
    }

    [Theory]
    [InlineData(200, 200)]
    [InlineData(500, 500)]
    [InlineData(1000, 1000)]
    [InlineData(2000, 2000)]
    [InlineData(5000, 5000)]
    [InlineData(10000, 10000)]   // M3i.32xx 12-bit 共 6 档量程
    [InlineData(300, 2000)]      // 非法档位回退默认 ±2000 mV
    [InlineData(0, 2000)]
    public void SpectrumDaqCard_InputRange_ClampsToValidSteps(int requested, int expected)
    {
        using var card = new SpectrumDaqCard { InputRangeMv = requested };
        Assert.Equal(expected, card.InputRangeMv);
    }

    // ══════════════════════════════════════════════════════════════
    //  Spectrum §3.2 能力吸收测试（2026-08-18：寄存器锁定 + 能力解码 + 行为）
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void SpectrumNative_AcquisitionModeRegisters_MatchHeaderFile()
    {
        // 基准：CD_SPCM_348a/Driver/c_header/regs.h 1135-1152（卡模式）
        Assert.Equal(0x00000002, SpectrumNative.SPC_REC_STD_MULTI);
        Assert.Equal(0x00000004, SpectrumNative.SPC_REC_STD_GATE);
        Assert.Equal(0x00000008, SpectrumNative.SPC_REC_STD_ABA);
        Assert.Equal(0x00020000, SpectrumNative.SPC_REC_STD_AVERAGE);
        Assert.Equal(0x00800000, SpectrumNative.SPC_REC_STD_BOXCAR);
        Assert.Equal(0x00000010, SpectrumNative.SPC_REC_FIFO_SINGLE);
        Assert.Equal(0x00000020, SpectrumNative.SPC_REC_FIFO_MULTI);
        Assert.Equal(0x00000040, SpectrumNative.SPC_REC_FIFO_GATE);
        Assert.Equal(0x00000080, SpectrumNative.SPC_REC_FIFO_ABA);
        Assert.Equal(0x00200000, SpectrumNative.SPC_REC_FIFO_AVERAGE);
        Assert.Equal(0x01000000, SpectrumNative.SPC_REC_FIFO_BOXCAR);
    }

    [Fact]
    public void SpectrumNative_SegmentClockFeatureRegisters_MatchHeaderFile()
    {
        // 段参数（regs.h 10010-10100）
        Assert.Equal(10010, SpectrumNative.SPC_SEGMENTSIZE);
        Assert.Equal(10020, SpectrumNative.SPC_LOOPS);
        Assert.Equal(10030, SpectrumNative.SPC_PRETRIGGER);
        Assert.Equal(10100, SpectrumNative.SPC_POSTTRIGGER);
        Assert.Equal(10040, SpectrumNative.SPC_ABADIVIDER);
        Assert.Equal(10050, SpectrumNative.SPC_AVERAGES);
        Assert.Equal(11001, SpectrumNative.SPC_CHCOUNT);
        Assert.Equal(0x00000002, SpectrumNative.CHANNEL1);

        // 时钟（regs.h 20140/20200）
        Assert.Equal(0x00000008, SpectrumNative.SPC_CM_EXTERNAL);
        Assert.Equal(0x00000020, SpectrumNative.SPC_CM_EXTREFCLOCK);
        Assert.Equal(20140, SpectrumNative.SPC_REFERENCECLOCK);

        // 通道寄存器（regs.h 30000-30130；CH1 为 30100 系列）
        Assert.Equal(30000, SpectrumNative.SPC_OFFS0);
        Assert.Equal(30010, SpectrumNative.SPC_AMP0);
        Assert.Equal(30100, SpectrumNative.SPC_OFFS1);
        Assert.Equal(30110, SpectrumNative.SPC_AMP1);
        Assert.Equal(30130, SpectrumNative.SPC_50OHM1);

        // 功能位图（regs.h 2120 + 769-787）
        Assert.Equal(2120, SpectrumNative.SPC_PCIFEATURES);
        Assert.Equal(0x00000001, SpectrumNative.SPCM_FEAT_MULTI);
        Assert.Equal(0x00000002, SpectrumNative.SPCM_FEAT_GATE);
        Assert.Equal(0x00000004, SpectrumNative.SPCM_FEAT_DIGITAL);
        Assert.Equal(0x00000008, SpectrumNative.SPCM_FEAT_TIMESTAMP);
        Assert.Equal(0x00000020, SpectrumNative.SPCM_FEAT_STARHUB4);
        Assert.Equal(0x00000080, SpectrumNative.SPCM_FEAT_ABA);
        Assert.Equal(0x00000100, SpectrumNative.SPCM_FEAT_BASEXIO);

        // 时间戳（regs.h 47000-47045）+ 附加 DMA（regs.h 883）
        Assert.Equal(47000, SpectrumNative.SPC_TIMESTAMP_CMD);
        Assert.Equal(0x00000002, SpectrumNative.SPC_TSMODE_STANDARD);
        Assert.Equal(0x00000100, SpectrumNative.SPC_TSCNT_INTERNAL);
        Assert.Equal(47001, SpectrumNative.SPC_TIMESTAMP_AVAILMODES);
        Assert.Equal(47020, SpectrumNative.SPC_TIMESTAMP_COUNT);
        Assert.Equal(47040, SpectrumNative.SPC_TIMESTAMP_FIFO);
        Assert.Equal(3000u, SpectrumNative.SPCM_BUF_TIMESTAMP);
        Assert.Equal(0x00100000, SpectrumNative.M2CMD_EXTRA_STARTDMA);

        // 门控触发电平模式 / X 线 / 自动校准 / StarHub
        Assert.Equal(0x00000008, SpectrumNative.SPC_TM_HIGH);
        Assert.Equal(0x00000010, SpectrumNative.SPC_TM_LOW);
        Assert.Equal(47200, SpectrumNative.SPCM_X0_MODE);
        Assert.Equal(47201, SpectrumNative.SPCM_X1_MODE);
        Assert.Equal(47220, SpectrumNative.SPCM_XX_ASYNCIO);
        Assert.Equal(0x00000001, SpectrumNative.SPCM_XMODE_ASYNCIN);
        Assert.Equal(0x00000002, SpectrumNative.SPCM_XMODE_ASYNCOUT);
        Assert.Equal(50020, SpectrumNative.SPC_ADJ_AUTOADJ);
        Assert.Equal(48000, SpectrumNative.SPC_STARHUB_CMD);
    }

    [Fact]
    public void SpectrumCardCapabilities_DecodesFeatureMap()
    {
        var caps = new SpectrumCardCapabilities
        {
            FeatureMap = SpectrumNative.SPCM_FEAT_MULTI | SpectrumNative.SPCM_FEAT_TIMESTAMP
        };
        Assert.True(caps.MultipleRecording);
        Assert.True(caps.Timestamp);
        Assert.False(caps.GatedSampling);
        Assert.False(caps.AbaMode);
        Assert.False(caps.StarHub);
        Assert.False(caps.DigitalIo);
        Assert.False(caps.BaseXio);
        Assert.Contains("MultipleRecording=True", caps.Describe());
    }

    [Fact]
    public void SpectrumDaqCard_DefaultConfiguration()
    {
        using var card = new SpectrumDaqCard();
        // 默认 FifoMulti：帧边界与 PRF 触发一一对应（审查 P1-2 整改后）
        Assert.Equal(SpectrumAcquisitionMode.FifoMulti, card.AcquisitionMode);
        Assert.Equal(SpectrumClockSource.InternalPll, card.ClockSource);
        Assert.Equal(SpectrumNative.CHANNEL0, card.ChannelMask);
        Assert.Equal(1, card.EnabledChannelCount);
        Assert.Equal(500e6f, card.MaxSampleRateForChannels);   // 单通道 500 MS/s
        Assert.Null(card.Capabilities);                        // 未初始化
        Assert.Equal(1, card.Averages);
    }

    [Fact]
    public void SpectrumDaqCard_TwoChannels_HalvesMaxSampleRate()
    {
        using var card = new SpectrumDaqCard { ChannelMask = 0x3 };
        Assert.Equal(2, card.EnabledChannelCount);
        Assert.Equal(250e6f, card.MaxSampleRateForChannels);   // 双通道 250 MS/s（硬件手册）
    }

    [Fact]
    public void SpectrumDaqCard_ExtendedPropertyClamps()
    {
        using var card = new SpectrumDaqCard();
        // 通道掩码非法值回退 CH0
        card.ChannelMask = 0x5;
        Assert.Equal(SpectrumNative.CHANNEL0, card.ChannelMask);
        card.ChannelMask = 0x3;
        Assert.Equal(0x3, card.ChannelMask);

        // 平均次数 1~65536
        card.Averages = 0;
        Assert.Equal(1, card.Averages);
        card.Averages = 100000;
        Assert.Equal(65536, card.Averages);
        card.Averages = 64;
        Assert.Equal(64, card.Averages);

        // 输入偏移 ±10 V 内
        card.InputOffsetMv0 = 20000;
        Assert.Equal(10000, card.InputOffsetMv0);
        card.InputOffsetMv1 = -20000;
        Assert.Equal(-10000, card.InputOffsetMv1);

        // ABA 抽取因子 1~65536
        card.AbaDivider = 0;
        Assert.Equal(1, card.AbaDivider);
        card.AbaDivider = 500000;
        Assert.Equal(65536, card.AbaDivider);

        // 参考时钟非法回退 10 MHz
        card.ReferenceClockHz = 0;
        Assert.Equal(10_000_000, card.ReferenceClockHz);
    }

    [Theory]
    [InlineData(71e6f, 1, 70e6f)]      // 空洞 70-72：偏近下边界
    [InlineData(71.9e6f, 1, 72e6f)]    // 空洞 70-72：偏近上边界
    [InlineData(142e6f, 1, 140e6f)]    // 空洞 140-144
    [InlineData(285e6f, 1, 287e6f)]    // 空洞 281-287
    [InlineData(600e6f, 1, 500e6f)]    // 超单通道上限
    [InlineData(1e6f, 1, 9e6f)]        // 低于下限 9 MS/s
    [InlineData(300e6f, 2, 250e6f)]    // 双通道上限减半
    [InlineData(100e6f, 1, 100e6f)]    // 合法值不改变
    [InlineData(72e6f, 1, 72e6f)]      // 空洞上边界（开区间）合法
    public void SpectrumDaqCard_ClampSampleRate_RangeAndForbiddenBands(float requested, int channels, float expected)
    {
        Assert.Equal(expected, SpectrumDaqCard.ClampSampleRate(requested, channels), precision: 1);
    }

    [Fact]
    public void AScanData_TimestampAndChannel_ExtendedFields()
    {
        var frame = new AScanData
        {
            Samples = new float[16],
            SampleRate = 100e6f,
            ChannelIndex = 1,
            TimestampTicks = 1000,   // 1000 ticks @ 100 MHz = 10 μs = 10000 ns
            HasTimestamp = true,
        };
        Assert.Equal(1, frame.ChannelIndex);
        Assert.True(frame.HasTimestamp);
        Assert.Equal(10000.0, frame.TimestampNs, 1.0);

        // 无时间戳时 TimestampNs 为 0
        var plain = new AScanData { SampleRate = 100e6f };
        Assert.False(plain.HasTimestamp);
        Assert.Equal(0.0, plain.TimestampNs);
    }

    // ══════════════════════════════════════════════════════════════
    //  DI 切换逻辑测试
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DI_UseMockTrue_RegistersMockImplementations()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ConnectionConfig { UseMock = true });

        RegisterHardwareConditional(services,
            new ConnectionConfig { UseMock = true });

        using var provider = services.BuildServiceProvider();
        var motion = provider.GetRequiredService<IMotionController>();
        var daq = provider.GetRequiredService<IDataAcquisition>();
        var pulse = provider.GetRequiredService<IPulseGenerator>();

        Assert.IsType<MockMotionController>(motion);
        Assert.IsType<MockDaqCard>(daq);
        Assert.IsType<MockPulseGenerator>(pulse);
    }

    [Fact]
    public void DI_UseMockFalse_RegistersHardwareImplementations()
    {
        var services = new ServiceCollection();
        var config = new ConnectionConfig { UseMock = false };
        services.AddSingleton(config);

        RegisterHardwareConditional(services, config);

        using var provider = services.BuildServiceProvider();
        var motion = provider.GetRequiredService<IMotionController>();
        var daq = provider.GetRequiredService<IDataAcquisition>();
        var pulse = provider.GetRequiredService<IPulseGenerator>();

        Assert.IsType<ZmcMotionController>(motion);
        Assert.IsType<SpectrumDaqCard>(daq);
        Assert.IsType<Dpr500Controller>(pulse);
    }

    [Fact]
    public void DI_HardwareSwitch_ServicesLayerUnaffected()
    {
        var services = new ServiceCollection();
        var config = new ConnectionConfig { UseMock = true };
        services.AddSingleton(config);
        RegisterHardwareConditional(services, config);
        services.AddSingleton<GateSet>();
        services.AddSingleton<DaqParams>();
        services.AddSingleton<PulseParams>();
        services.AddSingleton<SystemParams>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<ISignalProcessor, FftProcessor>();
        services.AddSingleton<IScanEngine, ScanService>();

        using var provider = services.BuildServiceProvider();

        var scanEngine = provider.GetRequiredService<IScanEngine>();
        Assert.NotNull(scanEngine);
        Assert.False(scanEngine.IsScanning);
    }

    [Fact]
    public void DI_IPulseGenerator_InterfaceOnly_NoConcreteRegistration()
    {
        // 验证: IPulseGenerator 注册后可解析，不需要 MockPulseGenerator 具体类型
        // 这确保 MainForm/PulseGenForm 依赖 IPulseGenerator 接口而非具体类
        var services = new ServiceCollection();
        services.AddSingleton<IPulseGenerator, MockPulseGenerator>();

        using var provider = services.BuildServiceProvider();
        var pulse = provider.GetRequiredService<IPulseGenerator>();
        Assert.NotNull(pulse);
        Assert.IsType<MockPulseGenerator>(pulse);
    }

    // ══════════════════════════════════════════════════════════════
    //  H-3：SpectrumDaqCard.StopAsync 超时竞态（审查报告 2026-08-18-v2 H-3）
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SpectrumDaqCard_StopAsync_ThreadStillAlive_KeepsReferenceAndDefersCleanup()
    {
        // 模拟 StopAsync 在采集线程未及时退出（Join 2s 超时）时：
        //  - _acqThread 引用必须保留（StartContinuousAsync 重入检查依赖 IsAlive）
        //  - _cleanupDeferred 必须置位（线程退出时 finally 负责 FreeResources，防泄漏）
        using var card = new SpectrumDaqCard();

        // 反射注入一个阻塞在 ManualResetEvent 上的后台线程模拟卡死的采集线程
        using var gate = new ManualResetEvent(false);
        var neverExit = new Thread(() => { gate.WaitOne(); }) { IsBackground = true, Name = "H3-TestBlockedThread" };
        neverExit.Start();
        try
        {
            var fAcqThread = typeof(SpectrumDaqCard).GetField("_acqThread",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            fAcqThread.SetValue(card, neverExit);

            var fDeferred = typeof(SpectrumDaqCard).GetField("_cleanupDeferred",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var fRunning = typeof(SpectrumDaqCard).GetField("_isRunning",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            fRunning.SetValue(card, true);

            // Act：StopAsync 应 Join 超时（2s），但保留引用并推迟清理
            await card.StopAsync();

            // 引用保留（非 null）——Start 重入检查不会被绕过
            var threadAfter = (Thread?)fAcqThread.GetValue(card);
            Assert.NotNull(threadAfter);
            Assert.True(threadAfter!.IsAlive);

            // deferred 置位——线程退出时会 FreeResources，无泄漏
            Assert.True((bool)fDeferred.GetValue(card)!);

            // IsRunning 已复位（StopAsync 语义）
            Assert.False(card.IsRunning);

            // 再次 Start 必须被拒绝（_acqThread 仍存活 → 重入保护生效）
            await Assert.ThrowsAsync<SpectrumDaqException>(() => card.StartContinuousAsync());
        }
        finally
        {
            gate.Set();               // 释放阻塞线程使其退出
            neverExit.Join(3000);     // 确保线程已终止
            // 清理注入线程引用，避免 Dispose 再次 Join 卡死
            typeof(SpectrumDaqCard).GetField("_acqThread",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(card, null);
        }
    }

    /// <summary>
    /// 复制 Program.cs 中的条件注册逻辑，用于测试验证。
    /// 注意：与 Program.cs 保持一致 — 仅注册接口，不注册具体类型。
    /// </summary>
    private static void RegisterHardwareConditional(IServiceCollection services, ConnectionConfig config)
    {
        if (config.UseMock)
        {
            services.AddSingleton<IMotionController, MockMotionController>();
            services.AddSingleton<IDataAcquisition, MockDaqCard>();
            services.AddSingleton<IPulseGenerator, MockPulseGenerator>();
        }
        else
        {
            services.AddSingleton<IMotionController, ZmcMotionController>();
            services.AddSingleton<IDataAcquisition, SpectrumDaqCard>();
            services.AddSingleton<IPulseGenerator, Dpr500Controller>();
        }
    }
}
