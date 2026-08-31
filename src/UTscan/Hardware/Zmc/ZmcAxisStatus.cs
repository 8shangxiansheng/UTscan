namespace UTscan.Hardware.Zmc;

/// <summary>
/// ZMC 轴状态字（AXIS_STATUS）位定义。
/// 依据：正运动官方 PC 函数库/ZBasic 手册 AXISSTATUS 位表（2026-08-20 复审 H-11）。
/// 注意：数值 4 是"远程轴通讯错误"（0x4 位 2），不是轴停止位；轴是否运动结束必须读 IDLE。
/// 原实现 `Stopped=4` 与 `IsMoving=(status&4)==0` 会在远程轴通信故障时把轴误判为"停止/空闲"，
/// 且正常停止状态并不保证该位为 1——已删除。
/// </summary>
public static class ZmcAxisStatus
{
    /// <summary>0x000002：随动误差警告</summary>
    public const int FollowErrorWarning = 0x000002;

    /// <summary>0x000004：与远程轴通讯出错（原被误定义为"轴停止位"）</summary>
    public const int RemoteAxisCommunicationError = 0x000004;

    /// <summary>0x000008：远程驱动器错误</summary>
    public const int RemoteDriveError = 0x000008;

    /// <summary>0x000010：正向硬限位触发</summary>
    public const int ForwardHardLimit = 0x000010;

    /// <summary>0x000020：负向硬限位触发</summary>
    public const int ReverseHardLimit = 0x000020;

    /// <summary>0x000040：回零中</summary>
    public const int Homing = 0x000040;

    /// <summary>0x000100：随动误差故障</summary>
    public const int FollowErrorFault = 0x000100;

    /// <summary>0x000200：超出正向软限位（FS_LIMIT）</summary>
    public const int ForwardSoftLimit = 0x000200;

    /// <summary>0x000400：超出负向软限位（RS_LIMIT）</summary>
    public const int ReverseSoftLimit = 0x000400;

    /// <summary>0x000800：取消运动中</summary>
    public const int CancelInProgress = 0x000800;

    /// <summary>0x001000：脉冲频率超限</summary>
    public const int PulseFrequencyExceeded = 0x001000;

    /// <summary>0x040000：电源故障</summary>
    public const int PowerFault = 0x040000;

    /// <summary>0x080000：精确输出缓冲溢出</summary>
    public const int PreciseOutputBufferOverflow = 0x080000;

    /// <summary>0x100000：速度保护触发</summary>
    public const int SpeedProtection = 0x100000;

    /// <summary>0x200000：特殊运动失败</summary>
    public const int SpecialMoveFailed = 0x200000;

    /// <summary>0x400000：报警输入（驱动 ALM）</summary>
    public const int AlarmInput = 0x400000;

    /// <summary>0x800000：轴进入暂停状态（含急停/RAPIDSTOP 后）</summary>
    public const int Paused = 0x800000;

    /// <summary>同时保留旧命名（OverForwardSoftLimit/OverReverseSoftLimit），供既有调用点/测试使用</summary>
    public const int OverForwardSoftLimit = ForwardSoftLimit;
    public const int OverReverseSoftLimit = ReverseSoftLimit;

    public static bool IsOverForwardSoftLimit(int status) => (status & ForwardSoftLimit) != 0;
    public static bool IsOverReverseSoftLimit(int status) => (status & ReverseSoftLimit) != 0;
    public static bool IsPaused(int status) => (status & Paused) != 0;
    public static bool IsRemoteAxisCommunicationError(int status) => (status & RemoteAxisCommunicationError) != 0;
    public static bool IsHoming(int status) => (status & Homing) != 0;
    public static bool IsForwardHardLimit(int status) => (status & ForwardHardLimit) != 0;
    public static bool IsReverseHardLimit(int status) => (status & ReverseHardLimit) != 0;
    public static bool IsAlarmInput(int status) => (status & AlarmInput) != 0;
    public static bool IsDriveError(int status) => (status & (RemoteDriveError | PowerFault | AlarmInput)) != 0;

    /// <summary>
    /// 是否存在需要立即停止运动的危险位（硬/软限位、驱动/电源/随动故障、速度保护、特殊运动失败）。
    /// 注意：运动/停止本身不在此列——运动状态必须由 GetIfIdle 判断（H-12）。
    /// </summary>
    public static bool IsFault(int status) =>
        (status & (ForwardHardLimit | ReverseHardLimit | ForwardSoftLimit | ReverseSoftLimit |
                   RemoteDriveError | PowerFault | FollowErrorFault | SpeedProtection |
                   SpecialMoveFailed | AlarmInput | PulseFrequencyExceeded)) != 0;

    /// <summary>解码为可读描述（用于 UI/日志显示）</summary>
    public static string Describe(int status)
    {
        if (status == 0) return "正常";
        var parts = new List<string>();
        if (IsForwardHardLimit(status)) parts.Add("正向硬限位");
        if (IsReverseHardLimit(status)) parts.Add("负向硬限位");
        if (IsOverForwardSoftLimit(status)) parts.Add("超正向软限位");
        if (IsOverReverseSoftLimit(status)) parts.Add("超负向软限位");
        if (IsRemoteAxisCommunicationError(status)) parts.Add("远程轴通讯错误");
        if (IsDriveError(status))
        {
            if ((status & RemoteDriveError) != 0) parts.Add("远程驱动器错误");
            if ((status & PowerFault) != 0) parts.Add("电源故障");
            if ((status & AlarmInput) != 0) parts.Add("驱动报警输入");
        }
        if (IsFollowErrorFault(status)) parts.Add("随动误差故障");
        if ((status & FollowErrorWarning) != 0) parts.Add("随动误差警告");
        if (IsHoming(status)) parts.Add("回零中");
        if ((status & CancelInProgress) != 0) parts.Add("取消运动中");
        if ((status & PulseFrequencyExceeded) != 0) parts.Add("脉冲频率超限");
        if (IsPaused(status)) parts.Add("暂停");
        if (parts.Count > 0) return string.Join(" | ", parts);
        return $"状态字 0x{status:X}";
    }

    private static bool IsFollowErrorFault(int status) => (status & FollowErrorFault) != 0;
}