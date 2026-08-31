using System.Runtime.InteropServices;
using System.Text;

namespace UTscan.Hardware.Zmc;

/// <summary>
/// ZMC/ZAux 运动控制器 P/Invoke 封装（zauxdll.dll）。
/// 仅包含项目实际使用的函数子集，全部声明为 internal static。
/// DLL 为 32 位，生产构建时需将主工程 PlatformTarget 切换为 x86。
/// </summary>
internal static class ZmcNative
{
    private const string DllName = "zauxdll.dll";

    // ── 连接 / 断开 ──

    [DllImport(DllName, EntryPoint = "ZAux_OpenEth", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int OpenEth(string ipaddr, out IntPtr handle);

    [DllImport(DllName, EntryPoint = "ZAux_OpenCom", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int OpenCom(uint comid, out IntPtr handle);

    [DllImport(DllName, EntryPoint = "ZAux_Close", CallingConvention = CallingConvention.StdCall)]
    public static extern int Close(IntPtr handle);

    // ── IO 输出（轴使能通过 IO 8-11 控制）──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetOp", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetOp(IntPtr handle, int ioNum, uint value);

    // ── 轴参数 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetUnits", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetUnits(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetSpeed", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetSpeed(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetAccel", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetAccel(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetDecel", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetDecel(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetLspeed", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetLspeed(IntPtr handle, int axis, float value);

    // ── 位置 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetDpos", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetDpos(IntPtr handle, int axis, ref float value);

    // H-05（复审 2026-08-20）：编码器测量位置 MPOS——说明书 §5.2.2 明确 DPOS 是需求位置、
    // MPOS 是测量反馈位置；到位判定与扫描坐标发布必须用 MPOS。官方签名已确认（见复审 9.3 模板）。
    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetMpos", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetMpos(IntPtr handle, int axis, ref float value);

    // H-02（复审 2026-08-20）：UNITS 回读——初始化"写入并回读"校验每轴工程单位生效。
    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetUnits", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetUnits(IntPtr handle, int axis, ref float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetIfIdle", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetIfIdle(IntPtr handle, int axis, ref int value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetAxisStatus", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetAxisStatus(IntPtr handle, int axis, ref int value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetRemain_LineBuffer", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetRemainLineBuffer(IntPtr handle, int axis, ref int value);

    // ── 单轴运动 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_Singl_Move", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSinglMove(IntPtr handle, int axis, float distance);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_Singl_MoveAbs", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSinglMoveAbs(IntPtr handle, int axis, float position);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_Singl_Cancel", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSinglCancel(IntPtr handle, int axis, int mode);

    // H-10（复审 2026-08-20）：连续速度运动 VMOVE——替代 JOG 的"速度×60 秒超长相对运动"。
    // EntryPoint 以现场 dumpbin 确认（新旧命名 Single/Singl 混用会在调用时抛
    // EntryPointNotFoundException，调用方 StartJog 已做回退与日志）。
    [DllImport(DllName, EntryPoint = "ZAux_Direct_Single_Vmove", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSingleVmove(IntPtr handle, int axis, int direction);

    // ── 插补运动 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetMerge", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetMerge(IntPtr handle, int axis, int value);

    // ── 通用命令 ──

    [DllImport(DllName, EntryPoint = "ZAux_Execute", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int Execute(IntPtr handle, string command, byte[] response, uint responseLength);

    // ── 回零 / 限位 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetDatumIn", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetDatumIn(IntPtr handle, int axis, int value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetFsLimit", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetFsLimit(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetRsLimit", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetRsLimit(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetAtype", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetAtype(IntPtr handle, int axis, int value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetInvertIn", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetInvertIn(IntPtr handle, int ioNum, int value);

    // ── 硬限位输入 / 减速角度 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetFwdIn", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetFwdIn(IntPtr handle, int axis, int value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetRevIn", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetRevIn(IntPtr handle, int axis, int value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetDecelAngle", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetDecelAngle(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetStopAngle", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetStopAngle(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetCornerMode", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetCornerMode(IntPtr handle, int axis, int value);

    // ── 位置设定 / VRF 断电保持 ──

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetDpos", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetDpos(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetMpos", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetMpos(IntPtr handle, int axis, float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_SetVrf", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectSetVrf(IntPtr handle, int vrStartNum, int num, ref float value);

    [DllImport(DllName, EntryPoint = "ZAux_Direct_GetVrf", CallingConvention = CallingConvention.StdCall)]
    public static extern int DirectGetVrf(IntPtr handle, int vrStartNum, int num, ref float value);

    // ── 急停 ──

    [DllImport(DllName, EntryPoint = "ZAux_Rapidstop", CallingConvention = CallingConvention.StdCall)]
    public static extern int RapidStop(IntPtr handle, int mode);

    // ── 辅助方法 ──

    /// <summary>执行 BASIC 命令并返回响应文本（同步阻塞）</summary>
    public static string ExecuteCommand(IntPtr handle, string command)
    {
        // H-1 修复：签名与仓库内遗留封装 zmcaux.cs 一致（4 参数，无 msWait）——
        // 原 5 参数声明在 x86 StdCall 下调用栈错位，回零命令可能参数错位/崩溃。
        uint responseLen = Math.Max(1024u, (uint)(command.Length + 512));
        var response = new byte[responseLen];
        int ret = Execute(handle, command, response, responseLen);
        if (ret != 0)
            throw new ZmcException($"ZAux_Execute 失败 (code={ret}): {command}");
        return Encoding.ASCII.GetString(response).TrimEnd('\0', ' ', '\r', '\n');
    }

    /// <summary>检查错误码，非 0 则抛异常</summary>
    public static void CheckError(int code, string operation)
    {
        if (code != 0)
            throw new ZmcException($"{operation} 失败，错误码 = {code}");
    }
}

/// <summary>ZMC 运动控制器异常</summary>
public class ZmcException : Exception
{
    public ZmcException(string message) : base(message) { }
    public ZmcException(string message, Exception inner) : base(message, inner) { }
}
