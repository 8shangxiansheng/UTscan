using UTscan.Core.Enums;
using UTscan.Core.Models;

namespace UTscan.Core.Interfaces;

/// <summary>
/// 脉冲发生器接口（DPR500）
/// </summary>
public interface IPulseGenerator : IDisposable
{
    /// <summary>是否已连接</summary>
    bool IsConnected { get; }

    /// <summary>当前参数快照（读取当前硬件状态）</summary>
    PulseParams Params { get; }

    /// <summary>连接设备</summary>
    Task<bool> ConnectAsync(ConnectionConfig config);

    /// <summary>断开连接</summary>
    Task DisconnectAsync();

    /// <summary>设置增益</summary>
    Task SetGainAsync(float gainDb);

    /// <summary>设置脉冲宽度</summary>
    Task SetPulseWidthAsync(float widthNs);

    /// <summary>设置脉冲重复频率</summary>
    Task SetPrfAsync(float prfHz);

    /// <summary>设置工作模式</summary>
    Task SetModeAsync(PulseMode mode);

    /// <summary>一次性应用全部参数（UI 提交时调用）</summary>
    Task ApplyParamsAsync(PulseParams p);

    /// <summary>
    /// 切换工作通道（M-3：原为 Dpr500Controller 专有方法，UI 需类型分支；
    /// 提升到接口后 Mock/真机统一实现，消除分支）
    /// </summary>
    Task<bool> SelectChannelAsync(int channel);

    /// <summary>设置触发源（0=Internal 1=External 2=Slave）</summary>
    Task<bool> SetTriggerSourceAsync(int source);

    /// <summary>设置接收器信号选择（0=T/R Echo 1=Through 2=Both）</summary>
    Task<bool> SetSignalSelectAsync(int select);

    /// <summary>
    /// NH-3：启用/禁用脉冲输出（参数应用后需显式启用才开始发射）。
    /// 启用前检查功率限制，超限拒绝开启。
    /// </summary>
    Task<bool> SetOutputEnabledAsync(bool enable);

    // ── H-1：单次触发语义（严格一点一脉冲、一次采集）──
    // 扫描必须保证每个空间点由唯一的一次硬件脉冲产生一帧采集，不能依赖 Internal PRF
    // 连续流（启用窗口内脉冲数量不确定）。下列能力由具体实现据硬件接线提供：
    //   - DPR500 切换为 External 触发源，由 ZMC 数字输出口产生单个边沿驱动；
    //   - 或厂商 SDK 提供已验证的软件单发 API。
    // 无法提供时 SupportsSingleTrigger 返回 false，TriggerOnceAsync 抛 NotSupportedException，
    // 调用方（ScanService）必须据此拒绝启动严格时序扫描，不得用软件延时包装 Internal PRF。

    /// <summary>是否支持严格单次硬件触发（H-1）。false 表示当前硬件/接线无法保证一点一脉冲。</summary>
    bool SupportsSingleTrigger { get; }

    /// <summary>
    /// 装备外触发模式（H-1）：DPR500 切换为 External 触发源并保持 TriggerEnable 使能，
    /// 实际发射由每个点位的单次外部边沿决定。必须在 Spectrum 已进入外触发等待状态后调用。
    /// </summary>
    Task ArmExternalTriggerAsync(CancellationToken ct = default);

    /// <summary>
    /// 产生一次硬件触发（H-1）：发出恰好一个外部触发边沿（由 ZMC 数字输出或厂商单发 API）。
    /// 不支持严格单发时抛 NotSupportedException——不得用软件延时包装 Internal PRF 后静默成功。
    /// </summary>
    Task TriggerOnceAsync(CancellationToken ct = default);

    /// <summary>
    /// 禁用脉冲输出并确认 IsPulsing=false（H-1/H-5/H-6）。比 SetOutputEnabledAsync(false) 更强：
    /// 不仅写入 TriggerEnable=FALSE，还轮询回读 IsPulsing 直至确认关断或超时失败。
    /// </summary>
    Task<bool> DisableOutputAndConfirmAsync(CancellationToken ct = default);

    // ── P5 诊断契约（消除 UI 对具体实现类型的分支）──

    /// <summary>连接种类（物理/仿真/未连接，结构化区分）</summary>
    DprConnectionKind ConnectionKind { get; }

    /// <summary>仪器信息（连接后由 SDK 运行时读取；未连接时为空默认）</summary>
    Dpr500InstrumentInfo InstrumentInfo { get; }

    /// <summary>最近一次连接错误描述（连接失败诊断；无错误时为空）</summary>
    string LastConnectError { get; }

    /// <summary>触发硬件寄存器回读，刷新 Params（消除 UI 显示缓存值与硬件实际值的分叉）</summary>
    void ReadParamsFromHardware();

    /// <summary>DPR500 LED 识别（机内识别板卡，仅真机有效；Mock 空操作）</summary>
    Task SetPulserLedIdentifyAsync(bool identify);
}
