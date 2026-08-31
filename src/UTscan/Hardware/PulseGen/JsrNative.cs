using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UTscan.Hardware.PulseGen;

/// <summary>
/// JSR Common API Library P/Invoke 封装。
/// 基于 JSR Common API SDK v3.3.0.0 官方头文件（从 NSIS 安装包提取）：
///   JSR_PropertyID.h — 所有属性 ID 的实际数值
///   JSR_Types.h      — 类型/枚举/结构体定义
///   JSR_Status.h    — 状态码定义
///   JSR_Common.h    — C API 函数声明
///
/// DLL 文件（由 JSR Control Panel Installer 安装，已复制到项目 runtimes 目录）：
///   32-bit app on 64-bit OS: JSR_Common3264.dll → C:\Windows\SysWOW64\
///   32-bit app on 32-bit OS: JSR_Common3232.dll → C:\Windows\System32\
///   64-bit app on 64-bit OS: JSR_Common6464.dll → C:\Windows\System32\
///   依赖: DPRIO3.dll / DPRIO364.dll（DPR 系列仪器 I/O 驱动）
///
/// 使用 [DllImport] 惰性加载：DLL 仅在首次调用时加载，
/// Mock 模式下不调用这些函数，因此 DLL 不存在也不影响编译运行。
/// </summary>
internal static class JsrNative
{
    private const string DllName = "JSR_Common3264.dll";
    private const CallingConvention Cc = CallingConvention.Cdecl;

    // ═══════════════════════════════════════════════════════════════
    //  句柄常量（JSR_Types.h）
    // ═══════════════════════════════════════════════════════════════

    public const int JSR_OK = 0;
    public const int JSR_INVALID_HANDLE = 0;
    public const int JSR_LIBRARY_HANDLE = 1;  // Library 句柄始终为 1

    // ── JSR_LibOpenOptionsEnum（JSR_Types.h）──
    public const int JSR_LIB_OPTION_DEFAULT       = 0x00;  // 无选项
    public const int JSR_LIB_OPTION_SIMULATE      = 0x01;  // 仿真模式（Bit 0）
    public const int JSR_LIB_OPTION_RESERVED0     = 0x02;  // 保留，勿用
    public const int JSR_LIB_OPTION_SIM_WITH_DRIVER = 0x03; // 已废弃
    public const int JSR_LIB_OPTION_DISABLE_MUTEX = 0x04;  // 禁用多线程互斥（Bit 2）

    // ── JSR_ModelEnum（JSR_Types.h）──
    public const int JSR_MODEL_UNKNOWN = 0;
    public const int JSR_MODEL_ANY     = 1;
    public const int JSR_MODEL_PRC50   = 2;
    public const int JSR_MODEL_DPR500  = 3;
    public const int JSR_MODEL_DPR300  = 4;

    // ── Object open modes（JSR_Types.h）──
    public const int JSR_INSTRUMENT_OPEN_DEFAULT = 0x00;
    public const int JSR_CHANNEL_OPEN_DEFAULT   = 0x00;
    public const int JSR_PULSER_OPEN_DEFAULT    = 0x00;
    public const int JSR_RECEIVER_OPEN_DEFAULT  = 0x00;

    // ── JSR_PulserBlinkEnum（JSR_ID_PulserLEDControl 的取值）──
    public const int JSR_LED_PULSE_ACTIVITY  = 0;  // 脉冲活动时闪烁（默认）
    public const int JSR_LED_IDENTIFY_BOARD  = 1;  // 常亮（识别板卡）

    // ── JSR_PowerLEDEnum（JSR_ID_InstrumentPowerLEDControl 的取值）──
    public const int JSR_POWER_LED_BLINK_VERY_SLOW = 0;
    public const int JSR_POWER_LED_BLINK_SLOW      = 25;
    public const int JSR_POWER_LED_BLINK_FAST      = 200;
    public const int JSR_POWER_LED_BLINK_VERY_FAST = 254;
    public const int JSR_POWER_LED_ON              = 255;

    // ── 断连/通信异常状态码（JSR_Status.h，用于断连检测与自动重连）──
    public const int JSR_WARN_NO_INSTRUMENT_FOUND          = 1036;
    public const int JSR_FAIL_INSTRUMENT_DISCONNECTED      = 2223;
    public const int JSR_FAIL_INSTRUMENT_POWER_CYCLED      = 2226;
    public const int JSR_FAIL_PULSER_RECONNECTED           = 2266;
    public const int JSR_FAIL_PULSER_DISCONNECTED          = 2267;
    public const int JSR_FAIL_DPR_COMMO_FAILURE            = 2703;
    public const int JSR_FAIL_PULSER_HARDWARE_FAILED       = 2269;
    public const int JSR_FAIL_INSTRUMENT_STILL_OPEN        = 2222;
    // P3-FIX3（现场部署 20260826）：请求元素数超过可用数——读仪器句柄时出现即"扫描 0 台"
    public const int JSR_FAIL_INCOUNT_TOO_HIGH = 2111;
    public const int JSR_WARN_NO_BOARDS_FOUND = 1028;

    // ── NEW-M-5：警告范围常量（JSR_Status.h）──
    // JSR_STATUS_PASS = OK(0) || WARN(1024-2047)；APPLICATION_STATUS(1-1023) 不属于 PASS
    public const int JSR_WARN_GENERAL = 1024;
    public const int JSR_WARN_LAST    = 2047;

    // ── JSR_TriggerSourceEnum（JSR_Types.h）──
    public const int JSR_TRIGGER_INTERNAL = 0;
    public const int JSR_TRIGGER_EXTERNAL = 1;
    public const int JSR_TRIGGER_SLAVE    = 2;

    // ── JSR_SignalSelectEnum（JSR_Types.h）──
    public const int JSR_SIGNAL_SELECT_TR_ECHO  = 0;
    public const int JSR_SIGNAL_SELECT_THROUGH = 1;
    public const int JSR_SIGNAL_SELECT_BOTH     = 2;

    // ── JSR_BoolEnum ──
    public const int JSR_FALSE = 0;
    public const int JSR_TRUE  = 1;

    // ── JSR_EnableEnum ──
    public const int JSR_DISABLE = 0;
    public const int JSR_ENABLE  = 1;

    // ── JSR_TriggerEdgeEnum ──
    public const int JSR_TRIGGER_EDGE_RISING  = 0;
    public const int JSR_TRIGGER_EDGE_FALLING = 1;

    // ── JSR_ObjTypeEnum ──
    public const int JSR_OBJTYPE_LIBRARY    = 1;
    public const int JSR_OBJTYPE_INSTRUMENT = 2;
    public const int JSR_OBJTYPE_CHANNEL    = 3;
    public const int JSR_OBJTYPE_PULSER     = 4;
    public const int JSR_OBJTYPE_RECEIVER   = 5;

    // ═══════════════════════════════════════════════════════════════
    //  Property ID 常量（JSR_PropertyID.h — 实际值）
    // ═══════════════════════════════════════════════════════════════

    // ── Standard Properties (0-5) ──
    public const int JSR_ID_Null                   = 0;
    public const int JSR_ID_AvailablePropertyIDs   = 1;
    public const int JSR_ID_FirstIDNumber          = 2;
    public const int JSR_ID_LastIDNumber            = 3;
    public const int JSR_ID_ParentHandle            = 4;
    public const int JSR_ID_ObjectType              = 5;

    // ── Reference Properties (500-514) ──
    public const int JSR_ID_ReferenceModelNames         = 500;
    public const int JSR_ID_ReferenceDataTypeNames      = 501;
    public const int JSR_ID_ReferenceUnitNames          = 502;
    public const int JSR_ID_ReferenceUnitAbbrNames      = 503;
    public const int JSR_ID_ReferenceObjectTypeNames   = 504;
    public const int JSR_ID_ReferenceEnableNames        = 505;
    public const int JSR_ID_ReferenceTriggerSourceNames = 506;
    public const int JSR_ID_ReferenceSignalSelectNames  = 507;
    public const int JSR_ID_ReferenceLowHighNames       = 508;
    public const int JSR_ID_ReferenceBlinkNames         = 509;
    public const int JSR_ID_ReferenceTriggerEdgeNames   = 510;
    public const int JSR_ID_ReferenceOffOnNames         = 511;
    public const int JSR_ID_ReferenceFalseTrueNames     = 512;
    public const int JSR_ID_ReferenceLowHighNames4      = 513;
    public const int JSR_ID_ReferenceHighLowNamesInv    = 514;  // 0=high（DPR300 脉冲器阻抗）

    // ── Library Properties (1000-1004) ──
    public const int JSR_ID_LibraryName              = 1000;
    public const int JSR_ID_LibraryInstrumentHandles = 1001;
    public const int JSR_ID_LibraryVersion           = 1002;
    public const int JSR_ID_LibrarySupportedModels   = 1003;
    public const int JSR_ID_LibraryDriversStatus    = 1004;

    // ── Instrument Properties — Baseline (2000-2005) ──
    public const int JSR_ID_InstrumentChannelHandles    = 2000;
    public const int JSR_ID_InstrumentModelName         = 2001;
    public const int JSR_ID_InstrumentModelEnum         = 2002;
    public const int JSR_ID_InstrumentSerNum            = 2003;
    public const int JSR_ID_InstrumentHasManualControls = 2004;
    public const int JSR_ID_InstrumentConnectStatus     = 2005;

    // ── Instrument Properties — Extended DPR500/DPR300 (2520-2522) ──
    public const int JSR_ID_InstrumentSerialComPort      = 2520;
    public const int JSR_ID_InstrumentSerialChainAddress = 2521;
    public const int JSR_ID_InstrumentPowerLEDControl     = 2522;

    // ── Instrument Properties — Extended PRC50 (2500-2501) ──
    public const int JSR_ID_InstrumentPCISlot     = 2500;  // R  - 1 - Int32
    public const int JSR_ID_InstrumentFirmwareVer = 2501;  // R  - 1 - String（仅 PRC50）

    // ── Channel Properties — Baseline (3000-3003) ──
    public const int JSR_ID_ChannelDescription    = 3000;
    public const int JSR_ID_ChannelLetter         = 3001;
    public const int JSR_ID_ChannelPulserHandles  = 3002;
    public const int JSR_ID_ChannelReceiverHandles = 3003;

    // ── Channel Properties — Extended DPR500 (3500-3501) ──
    public const int JSR_ID_ChannelMuxLPTNumber = 3500;
    public const int JSR_ID_ChannelMuxSelector   = 3501;

    // ── Pulser Properties — Baseline (4000-4020) ──
    public const int JSR_ID_PulserModelName          = 4000;
    public const int JSR_ID_PulserTriggerEnable      = 4001;
    public const int JSR_ID_PulserTriggerSource      = 4002;
    public const int JSR_ID_PulserPRF               = 4003;
    public const int JSR_ID_PulserVolts             = 4004;
    public const int JSR_ID_PulserEnergyPerPulse   = 4005;
    public const int JSR_ID_PulserEnergyIndex       = 4006;
    public const int JSR_ID_PulserDampResistorList  = 4007;
    public const int JSR_ID_PulserDampResistorIndex = 4008;
    public const int JSR_ID_PulserLEDControl        = 4009;
    public const int JSR_ID_PulserIsPulsing          = 4010;
    public const int JSR_ID_PulserExtTriggerZList   = 4011;
    public const int JSR_ID_PulserExtTriggerZIndex  = 4012;
    public const int JSR_ID_PulserTriggerEdge        = 4013;
    public const int JSR_ID_PulserPowerLimitStatus   = 4014;
    public const int JSR_ID_PulserPowerLimitPRF      = 4015;
    public const int JSR_ID_PulserPowerLimitVolts    = 4016;
    public const int JSR_ID_PulserPowerLimitEnergyIndex = 4017;
    public const int JSR_ID_PulserSerNum             = 4018;
    public const int JSR_ID_PulserHardwareSerNum     = 4019;
    public const int JSR_ID_PulserHardwareRev        = 4020;

    // ── Pulser Properties — Extended PRC50 (4501-4506) ──
    public const int JSR_ID_PulserTriggerCount    = 4501;
    public const int JSR_ID_PulserHoursMeter      = 4503;
    public const int JSR_ID_PulserHoursPowerLimit = 4504;
    public const int JSR_ID_PulserLifetimeUseCount = 4505;
    public const int JSR_ID_PulserEnergyList      = 4506;

    // ── Pulser Properties — Extended DPR300 (4510) ──
    public const int JSR_ID_PulserImpedance = 4510;

    // ── Receiver Properties — Baseline (5000-5009) ──
    public const int JSR_ID_ReceiverBandwidth      = 5000;
    public const int JSR_ID_ReceiverSignalSelect   = 5001;
    public const int JSR_ID_ReceiverGainDB         = 5002;
    public const int JSR_ID_ReceiverLPFilterList   = 5003;
    public const int JSR_ID_ReceiverLPFilterIndex  = 5004;
    public const int JSR_ID_ReceiverHPFilterList   = 5005;
    public const int JSR_ID_ReceiverHPFilterIndex  = 5006;
    public const int JSR_ID_ReceiverModelName      = 5007;
    public const int JSR_ID_ReceiverSerNum          = 5008;
    public const int JSR_ID_ReceiverHardwareRev    = 5009;

    // ── Receiver Properties — Extended PRC50 (5500-5501) ──
    public const int JSR_ID_ReceiverTREchoGainDB  = 5500;
    public const int JSR_ID_ReceiverThroughGainDB = 5501;

    // ═══════════════════════════════════════════════════════════════
    //  P/Invoke 函数声明（JSR_Common.h）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>打开 JSR Common API 库（必须是最先调用的函数）</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_OpenLibrary(
        int inOptionBits,
        int[] inLoadModelArray,
        int inArrayCount,
        int inReserved0,
        int inReserved1);

    /// <summary>关闭 JSR Common API 库（释放资源）</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_CloseLibrary();

    /// <summary>打开对象（Instrument / Channel / Pulser / Receiver）</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_OpenObject(int inObjectHandle, int inOpenOptionBits);

    /// <summary>关闭对象</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_CloseObject(int inObjectHandle);

    /// <summary>读取 Int32 属性（JSR_Int32 / Bool / Enum / Status / Handle / PropID）</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_GetInt32(
        int inObjectHandle, int inID, int inCount, int[] outpValue);

    /// <summary>设置 Int32 属性</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_SetInt32(
        int inObjectHandle, int inID, int inCount, int[] inpValue);

    /// <summary>读取 Double 属性</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_GetDouble(
        int inObjectHandle, int inID, int inCount, double[] outpValue);

    /// <summary>设置 Double 属性</summary>
    [DllImport(DllName, CallingConvention = Cc)]
    public static extern int JSR_SetDouble(
        int inObjectHandle, int inID, int inCount, double[] inpValue);

    /// <summary>读取 ASCII 字符串属性（JSR_Ascii = char[64]）</summary>
    [DllImport(DllName, CallingConvention = Cc, CharSet = CharSet.Ansi)]
    public static extern int JSR_GetAscii(
        int inObjectHandle, int inID, int inCount, [Out] JsrAscii[] outpAscii);

    /// <summary>读取属性信息（ASCII 版本，对应 JSR_AsciiInfoStruct）</summary>
    [DllImport(DllName, CallingConvention = Cc, CharSet = CharSet.Ansi)]
    public static extern int JSR_GetAsciiInfo(
        int inObjectHandle, int inID, ref JsrAsciiInfoStruct outAsciiInfo);

    /// <summary>将状态码转换为可读 ASCII 文本（JSR_Common.h: JSR_GetErrorJSRAscii）</summary>
    [DllImport(DllName, CallingConvention = Cc, CharSet = CharSet.Ansi)]
    public static extern int JSR_GetErrorJSRAscii(
        int inErrorNumber, ref JsrAscii outpAsciiString);

    // ═══════════════════════════════════════════════════════════════
    //  结构体定义（JSR_Types.h）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>JSR_Ascii — 定长 64 字节 ASCII 字符串</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64, CharSet = CharSet.Ansi)]
    public struct JsrAscii
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Value;
    }

    /// <summary>
    /// JSR_AsciiInfoStruct — 属性元信息（ASCII 版本，对应 JSR_Types.h）
    /// 字段顺序必须与 C 结构体完全一致。
    /// </summary>
    // L-3：显式 Pack=8（JSR_Types.h 默认 8 字节对齐，double/int 联合体 8 字节），防默认对齐差异
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
    public struct JsrAsciiInfoStruct
    {
        public int propertyID;      // JSR_PropID
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string name;         // 属性名（与 JSR_PropertyID.h 中的拼写一致）
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string displayText;  // GUI 显示文本（带空格）
        public int attribs;         // JSR_AttribEnum
        public int listID;          // 关联的 list 属性 ID（0 = 无）
        public int elementType;     // JSR_TypeEnum
        public int elementCount;    // 元素数（1=标量）
        public int byteCount;       // 总字节数
        public JsrLimitUnion limitLo;  // 下限
        public JsrLimitUnion limitHi;  // 上限
        public int units;           // JSR_UnitsEnum
        public int precision;       // 显示精度
    }

    /// <summary>JSR_LimitUnion — double/int 联合体（8 字节）</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct JsrLimitUnion
    {
        [FieldOffset(0)]
        public double d;   // JSR_Double
        [FieldOffset(0)]
        public int i;      // JSR_Int32
    }

    // 兼容旧代码的别名
    // [Obsolete] public struct JsrPropInfo — 已被 JsrAsciiInfoStruct 替代

    // ═══════════════════════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>检查 JSR_Common DLL 是否可用（不实际调用任何函数）</summary>
    public static bool IsDllAvailable()
    {
        try
        {
            // M-8 修复：探测成功后必须释放模块句柄——否则模块引用计数累积，
            // DLL 长期保持加载，影响更新/卸载/错误恢复。实际 P/Invoke 加载由运行时独立管理。
            if (NativeLibrary.TryLoad(DllName, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            if (NativeLibrary.TryLoad("JSR_Common3232.dll", out handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            if (NativeLibrary.TryLoad("JSR_Common6464.dll", out handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>将状态码转换为可读错误信息（使用 JSR_GetErrorJSRAscii）</summary>
    public static string GetErrorString(int status)
    {
        if (status == JSR_OK) return "OK";

        // 状态码范围（JSR_Status.h）：
        // 0 = OK, 1-1023 = 应用错误, 1024-2047 = 警告, 2048+ = 致命错误
        if (status is > 0 and < 1024)
            return $"应用错误 #{status}";

        try
        {
            if (!IsDllAvailable())
                return status < 2048 ? $"警告 (0x{status:X})" : $"错误 (0x{status:X})";

            var ascii = new JsrAscii();
            JSR_GetErrorJSRAscii(status, ref ascii);
            var text = ascii.Value?.TrimEnd('\0') ?? string.Empty;
            return string.IsNullOrEmpty(text)
                ? (status < 2048 ? $"警告 (0x{status:X})" : $"错误 (0x{status:X})")
                : text;
        }
        catch
        {
            return status < 2048 ? $"警告 (0x{status:X})" : $"错误 (0x{status:X})";
        }
    }

    /// <summary>检查状态码是否为 OK，否则输出 Debug 信息</summary>
    public static bool CheckStatus(int status, string operation)
    {
        if (status == JSR_OK) return true;

        Debug.WriteLine($"[JSR] {operation} 失败: {GetErrorString(status)} (0x{status:X})");
        return false;
    }

    /// <summary>状态码分类：是否通过（OK 或警告，对应 JSR_STATUS_PASS 宏）</summary>
    public static bool IsPass(int status) => status == JSR_OK || (status >= JSR_WARN_GENERAL && status <= JSR_WARN_LAST);

    /// <summary>NEW-M-5：是否为应用状态码（JSR_STATUS_APPLICATION_STATUS：0 &lt; status &lt; 1024）——
    /// 信息性状态（如"校准已应用"），非错误，但也不属于 OK/WARN。</summary>
    public static bool IsApplicationStatus(int status) => status > JSR_OK && status < JSR_WARN_GENERAL;

    /// <summary>
    /// 判断状态码是否指示仪器断连/通信故障（应触发重连流程）。
    /// 依据 JSR_Status.h：INSTRUMENT_DISCONNECTED / POWER_CYCLED /
    /// PULSER_DISCONNECTED / PULSER_RECONNECTED / PULSER_HARDWARE_FAILED / DPR_COMMO_FAILURE
    /// </summary>
    public static bool IsDisconnectError(int status) => status is
        JSR_FAIL_INSTRUMENT_DISCONNECTED or
        JSR_FAIL_INSTRUMENT_POWER_CYCLED or
        JSR_FAIL_PULSER_RECONNECTED or
        JSR_FAIL_PULSER_DISCONNECTED or
        JSR_FAIL_PULSER_HARDWARE_FAILED or
        JSR_FAIL_DPR_COMMO_FAILURE;
}
