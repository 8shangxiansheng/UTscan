using UTscan.Core.Models;

namespace UTscan.Core.Interfaces;

/// <summary>
/// 数据采集卡接口
/// </summary>
public interface IDataAcquisition : IDisposable
{
    /// <summary>是否正在采集</summary>
    bool IsRunning { get; }

    /// <summary>
    /// RM-1：资源是否需要重新初始化——Stop 超时后 deferred 清理已完成（句柄/缓冲已释放），
    /// 此时 IsRunning=false 但直接 Start 会因 _handle==0 失败。UI 应据此禁止 Start 并提示重连。
    /// </summary>
    bool NeedsReinitialize { get; }

    /// <summary>数据就绪事件</summary>
    event EventHandler<AScanDataEventArgs>? DataReady;

    /// <summary>初始化采集卡</summary>
    Task<bool> InitializeAsync(ConnectionConfig config);

    /// <summary>开始连续采集</summary>
    Task StartContinuousAsync();

    /// <summary>停止采集（常规停机）</summary>
    Task StopAsync();

    /// <summary>
    /// RH-8：故障复位——停止采集并执行硬件 Reset（CARD_RESET 清除板卡残留错误状态）。
    /// 调用后需要重新 InitializeAsync。普通关闭路径应使用 StopAsync。
    /// </summary>
    Task ResetAsync();

    /// <summary>获取当前A扫数据（默认通道）</summary>
    AScanData GetCurrentData();

    /// <summary>获取指定通道的当前A扫数据（0=CH0, 1=CH1）</summary>
    AScanData GetCurrentData(int channel);

    /// <summary>
    /// 当前已完成的帧计数（H-4 帧同步：扫查服务据此判断是否产生新帧）。
    /// 未启动采集时返回 0。
    /// </summary>
    long GetCurrentFrameCount();

    /// <summary>
    /// 等待产生新帧（H-4 帧同步）：阻塞至 <see cref="GetCurrentFrameCount"/> 递增或超时/取消。
    /// 运动到位后调用，确保取到"当前位置"的新帧而非上一位置的旧缓存帧。
    /// </summary>
    /// <param name="timeoutMs">等待超时（ms），超时返回 false（不抛异常）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否在超时前产生新帧</returns>
    Task<bool> WaitForNewFrameAsync(int timeoutMs, CancellationToken ct = default);

    /// <summary>
    /// H-1 帧同步（严格单次触发）：在触发前抓取帧计数基线，触发后等待帧计数超过该基线。
    /// 与 WaitForNewFrameAsync 不同——本方法接收触发前基线参数，避免"先创建等待 Task 再发触发"
    /// 写法在触发到等待调用之间到达的帧被漏判。
    /// </summary>
    /// <param name="baseline">触发前调用 GetCurrentFrameCount 获取的基线</param>
    /// <param name="timeoutMs">等待超时（ms），超时返回 false（不抛异常）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>是否在超时前产生新帧（帧计数 &gt; baseline）</returns>
    Task<bool> WaitForFrameAfterAsync(long baseline, int timeoutMs, CancellationToken ct = default);

    // ── P5 诊断契约（消除 UI 对具体实现类型的分支）──

    /// <summary>当前已启用通道数（基于通道掩码，供采样率换算）</summary>
    int EnabledChannelCount { get; }

    /// <summary>诊断：采集卡全链路状态快照（状态机/句柄/线程/标志位），供 UI 记录定位。</summary>
    string DescribeState();

    /// <summary>实时性/丢帧 KPI 快照（锁内快照，跨线程安全）</summary>
    DaqKpiSnapshot GetKpis();

    /// <summary>最近一次连接/初始化错误描述（连接失败诊断；无错误时为空）</summary>
    string LastConnectError { get; }
}

/// <summary>
/// A扫数据事件参数
/// </summary>
public class AScanDataEventArgs : EventArgs
{
    public AScanData Data { get; set; } = new();
}
