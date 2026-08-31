using UTscan.Core.Enums;
using UTscan.Core.Models;

namespace UTscan.Core.Interfaces;

/// <summary>
/// 运动控制器接口
/// </summary>
public interface IMotionController : IDisposable
{
    /// <summary>是否已连接</summary>
    bool IsConnected { get; }

    /// <summary>位置变化事件</summary>
    event EventHandler<AxisPositionChangedEventArgs>? PositionChanged;

    /// <summary>连接控制器</summary>
    Task<bool> ConnectAsync(ConnectionConfig config);

    /// <summary>断开连接</summary>
    Task DisconnectAsync();

    /// <summary>使能轴</summary>
    Task<bool> EnableAxisAsync(AxisId axis);

    /// <summary>禁用轴</summary>
    Task<bool> DisableAxisAsync(AxisId axis);

    /// <summary>绝对运动</summary>
    Task MoveAbsoluteAsync(AxisId axis, float position, ScanParams parameters);

    /// <summary>相对运动</summary>
    Task MoveRelativeAsync(AxisId axis, float distance, ScanParams parameters);

    /// <summary>回零</summary>
    Task HomeAsync(AxisId axis);

    /// <summary>停止轴</summary>
    Task StopAsync(AxisId axis);

    /// <summary>紧急停止</summary>
    Task EmergencyStopAsync();

    /// <summary>当前位置（编码器测量反馈 MPOS，单位=该轴工程单位，说明书 §5.2.2）</summary>
    float GetPosition(AxisId axis);

    /// <summary>需求位置（DPOS，单位=该轴工程单位）——用于跟随误差/诊断</summary>
    float GetDemandPosition(AxisId axis);

    /// <summary>正向软限位（工程单位 mm/°）——扫查区域校验的单一来源（H-03）</summary>
    float GetForwardSoftLimit(AxisId axis);

    /// <summary>负向软限位（工程单位 mm/°）</summary>
    float GetReverseSoftLimit(AxisId axis);
    /// <summary>轴是否空闲（未在运动中）</summary>
    bool IsAxisIdle(AxisId axis);

    /// <summary>
    /// 设置连续插补模式（MERGE）：相邻运动指令自动平滑衔接，不停减速。
    /// 光栅扫描换行时避免停顿，提高数据均匀性。
    /// </summary>
    void SetContinuousInterpolation(AxisId axis, bool enable);

    /// <summary>
    /// P0-D：轴置零（当前位置设为 0，说明书 4.5 定位起始点流程）。
    /// 真机：DPOS/MPOS 同步写 0；Mock：位置归 0。
    /// </summary>
    void SetPositionZero(AxisId axis);

    /// <summary>
/// <summary>H-1：单次触发输出——从指定数字输出口产生单个电平脉冲（拉高 → 保持 pulseWidthMs → 拉低），
    /// 用于驱动 DPR500 External Trigger Input。高电平保持期间不得持有原生锁（否则阻塞急停），
    /// 拉低操作必须放在 finally 中确保释放。两次 ZAux_Direct_SetOp 返回码均需检查。
    /// io 编号必须按现场接线确定，禁止在代码中猜测。
    /// </summary>
    /// <param name="io">数字输出口编号（现场接线确定，不得复用轴使能占用的 IO）</param>
    /// <param name="pulseWidthMs">脉冲高电平保持时间（ms）</param>
    /// <param name="ct">取消令牌</param>
    Task PulseTriggerOutputAsync(int io, int pulseWidthMs, CancellationToken ct = default);

    // ── P5 诊断契约（消除 UI 对具体实现类型的分支）──

    /// <summary>最近一次连接错误描述（连接失败诊断；无错误时为空）</summary>
    string LastConnectError { get; }
}

/// <summary>
/// 轴位置变化事件参数
/// </summary>
public class AxisPositionChangedEventArgs : EventArgs
{
    public AxisId Axis { get; set; }
    public float Position { get; set; }
    public float Velocity { get; set; }
}
