using UTscan.Core.Interfaces;
using UTscan.Core.Enums;
using UTscan.Core.Models;

namespace UTscan.Mock;

/// <summary>
/// Mock 脉冲收发仪（说明书 3.3.3）：模拟 DPR500 全部参数的设置与读取，
/// 不连接真实设备，用于无硬件开发与演示。
/// </summary>
public class MockPulseGenerator : IPulseGenerator
{
    private readonly PulseParams _params = new();

    // L-5：增益范围与真机 DPR500（RL01 50MHz 接收器）一致——原 -50~50 与硬件 -13~66 偏差，
    // 开发时 Mock 接受的值在真机上可能被拒绝
    private const float GainMinDb = -13f;
    private const float GainMaxDb = 66f;

    public bool IsConnected { get; private set; }

    /// <summary>当前参数快照</summary>
    public PulseParams Params => _params;

    public Task<bool> ConnectAsync(ConnectionConfig config)
    {
        IsConnected = true;
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SetGainAsync(float gainDb)
    {
        _params.GainDb = Math.Clamp(gainDb, GainMinDb, GainMaxDb);
        return Task.CompletedTask;
    }

    public Task SetPulseWidthAsync(float widthNs)
    {
        _params.PulseWidthNs = Math.Max(0, widthNs);
        return Task.CompletedTask;
    }

    public Task SetPrfAsync(float prfHz)
    {
        _params.PrfHz = Math.Clamp(prfHz, 100f, 5000f);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(PulseMode mode)
    {
        _params.Mode = mode;
        // H-1：与真机 Dpr500Controller 一致——无论 PulseEcho/Through 均保持 Internal 触发
        //（DPR500 自主 PRF 发射，TRIG/SYNC 输出同步脉冲给 Spectrum 专用 EXT0）
        _params.TriggerMode = TriggerMode.Internal;
        return Task.CompletedTask;
    }

    /// <summary>一次性应用全部参数（UI 提交时调用）</summary>
    public Task ApplyParamsAsync(PulseParams p)
    {
        _params.Model = p.Model;
        _params.Channel = p.Channel;
        _params.PowerOn = p.PowerOn;
        _params.Mode = p.Mode;
        _params.GainDb = Math.Clamp(p.GainDb, GainMinDb, GainMaxDb);
        _params.LowPassHz = p.LowPassHz;
        _params.HighPassHz = p.HighPassHz;
        _params.Enabled = p.Enabled;
        _params.PrfHz = Math.Clamp(p.PrfHz, 100f, 5000f);
        _params.Voltage = Math.Clamp(p.Voltage, 100f, 330f);
        _params.EnergyLevel = Math.Clamp(p.EnergyLevel, 1, 4);
        _params.EnergyPerPulseUj = Math.Max(0, p.EnergyPerPulseUj);
        _params.Damping = p.Damping;
        _params.Impedance = p.Impedance;
        _params.PulseWidthNs = Math.Max(0, p.PulseWidthNs);
        // M-3：与真机 ApplyParamsAsync 一致——同步触发模式（UI 下拉选择）
        _params.TriggerMode = p.TriggerMode;
        return Task.CompletedTask;
    }

    // ── M-3：扩展 API（提升到接口后 Mock 统一实现，UI 无需类型分支）──

    public Task<bool> SelectChannelAsync(int channel)
    {
        _params.Channel = Math.Clamp(channel, 1, 2);
        return Task.FromResult(true);
    }

    public Task<bool> SetTriggerSourceAsync(int source)
    {
        // 0=Internal 1=External 2=Slave；Mock 记录参数
        _params.TriggerMode = source == 0 ? TriggerMode.Internal : TriggerMode.External;
        return Task.FromResult(true);
    }

    public Task<bool> SetSignalSelectAsync(int select)
    {
        // 0=T/R Echo 1=Through 2=Both；Mock 记录（无独立字段，保持兼容）
        return Task.FromResult(true);
    }

    public Task<bool> SetOutputEnabledAsync(bool enable)
    {
        // NH-3：Mock 记录输出状态（与真机语义一致——参数应用不自动发射，需显式启用）
        _params.Enabled = enable;
        return Task.FromResult(true);
    }

    // ── H-1：单次触发语义（Mock 不支持严格单发，触发需真机 ZMC 边沿）──

    /// <summary>Mock 不支持严格单次硬件触发（无 ZMC 边沿）。</summary>
    public bool SupportsSingleTrigger => false;

    /// <summary>Mock 装备外触发模式（记录参数，无硬件动作）。</summary>
    public Task ArmExternalTriggerAsync(CancellationToken ct = default)
    {
        _params.TriggerMode = TriggerMode.External;
        _params.Enabled = true;
        return Task.CompletedTask;
    }

    /// <summary>Mock 无 ZMC 边沿能力，抛 NotSupportedException 提示需真机触发输出。</summary>
    public Task TriggerOnceAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Mock 不支持严格单次触发，需真机 ZMC 触发输出边沿");

    /// <summary>Mock 禁用并确认（模拟立即关断）。</summary>
    public Task<bool> DisableOutputAndConfirmAsync(CancellationToken ct = default)
    {
        _params.Enabled = false;
        return Task.FromResult(true);
    }

    public void Dispose() => IsConnected = false;

    // ── P5 诊断契约 ──
    public DprConnectionKind ConnectionKind => IsConnected ? DprConnectionKind.Physical : DprConnectionKind.Disconnected;
    public Dpr500InstrumentInfo InstrumentInfo => new() { ModelName = "MockDPR500" };
    public string LastConnectError => "";
    public void ReadParamsFromHardware() { /* Mock：参数即缓存，无需硬件回读 */ }
    public Task SetPulserLedIdentifyAsync(bool identify) => Task.CompletedTask;   // Mock 空操作
}
