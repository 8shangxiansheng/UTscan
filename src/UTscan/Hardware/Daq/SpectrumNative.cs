using System.Runtime.InteropServices;
using System.Text;

namespace UTscan.Hardware.Daq;

/// <summary>
/// Spectrum M3i.3242 采集卡 P/Invoke 封装（spcm_win32.dll，寄存器型 API）。
/// 常量取自官方 regs.h / spcm_drv.h（CD_SPCM_348a 驱动包 v4.0.13877）。
/// 驱动安装后 32 位 API DLL 为 spcm_win32.dll（SysWOW64，与官方 SpcmDrv32.NET 封装一致）；
/// 进程必须以 x86 运行（csproj PlatformTarget=x86），本目录随构建复制到输出。
/// </summary>
internal static class SpectrumNative
{
    private const string DllName = "spcm_win32.dll";

    // ── C API 函数 ──

    [DllImport(DllName, EntryPoint = "spcm_hOpen", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern IntPtr Open(string szDeviceName);

    [DllImport(DllName, EntryPoint = "spcm_vClose", CallingConvention = CallingConvention.StdCall)]
    public static extern void Close(IntPtr hDevice);

    [DllImport(DllName, EntryPoint = "spcm_dwSetParam_i32", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SetParam32(IntPtr hDevice, int register, int value);

    [DllImport(DllName, EntryPoint = "spcm_dwGetParam_i32", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetParam32(IntPtr hDevice, int register, ref int value);

    [DllImport(DllName, EntryPoint = "spcm_dwSetParam_i64", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SetParam64(IntPtr hDevice, int register, long value);

    [DllImport(DllName, EntryPoint = "spcm_dwGetParam_i64", CallingConvention = CallingConvention.StdCall)]
    public static extern uint GetParam64(IntPtr hDevice, int register, ref long value);

    [DllImport(DllName, EntryPoint = "spcm_dwDefTransfer_i64", CallingConvention = CallingConvention.StdCall)]
    public static extern uint DefTransfer(IntPtr hDevice, uint bufType, uint direction,
        uint notifySize, IntPtr dataBuffer, ulong boardOffset, ulong transferLen);

    [DllImport(DllName, EntryPoint = "spcm_dwGetErrorInfo_i32", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern uint GetErrorInfo(IntPtr hDevice, ref uint errorReg, ref int errorValue,
        byte[] errorText);

    // ── 命令 / 状态寄存器 ──
    public const int SPC_M2CMD = 100;
    public const int SPC_M2STATUS = 110;

    public const int M2CMD_CARD_RESET = 0x00000001;
    public const int M2CMD_CARD_START = 0x00000004;          // 启动（含写配置）
    public const int M2CMD_CARD_ENABLETRIGGER = 0x00000008;  // 使能触发引擎
    public const int M2CMD_CARD_FORCETRIGGER = 0x00000010;
    public const int M2CMD_CARD_STOP = 0x00000040;           // 停止
    public const int M2CMD_DATA_STARTDMA = 0x00010000;       // 启动数据 DMA
    public const int M2CMD_DATA_WAITDMA = 0x00020000;        // 等待下一块数据就绪
    public const int M2CMD_DATA_STOPDMA = 0x00040000;        // 中止数据传输

    // 状态位
    public const int M2STAT_DATA_BLOCKREADY = 0x00000100;
    public const int M2STAT_DATA_OVERRUN = 0x00000400;

    // ── FIFO 环形缓冲可用量寄存器（字节）──
    public const int SPC_DATA_AVAIL_USER_LEN = 200;  // 用户可用数据字节数
    public const int SPC_DATA_AVAIL_USER_POS = 201;  // 可用数据起始位置
    public const int SPC_DATA_AVAIL_CARD_LEN = 202;  // 写回此值以释放环形缓冲空间

    // ── 卡模式 ──
    public const int SPC_CARDMODE = 9500;
    public const int SPC_REC_STD_SINGLE = 0x00000001;   // 单次采集到板载内存
    public const int SPC_REC_STD_MULTI = 0x00000002;    // 多记录：每次触发一个段到板载内存
    public const int SPC_REC_STD_GATE = 0x00000004;     // 门控采集到板载内存
    public const int SPC_REC_STD_ABA = 0x00000008;      // ABA：A 慢速流 + B 每次触发
    public const int SPC_REC_STD_AVERAGE = 0x00020000;  // 多段求和平均（板载内存）
    public const int SPC_REC_STD_BOXCAR = 0x00800000;   // Boxcar 平均
    public const int SPC_REC_FIFO_SINGLE = 0x00000010;  // 单次触发 FIFO 连续流
    public const int SPC_REC_FIFO_MULTI = 0x00000020;   // 每次触发一个记录段到 FIFO
    public const int SPC_REC_FIFO_GATE = 0x00000040;    // 门控采样到 FIFO
    public const int SPC_REC_FIFO_ABA = 0x00000080;     // ABA 到 FIFO
    public const int SPC_REC_FIFO_AVERAGE = 0x00200000; // 多段求和平均流式到主机
    public const int SPC_REC_FIFO_BOXCAR = 0x01000000;  // Boxcar 平均 FIFO 模式

    // ── 内存 / 通道 / 段参数（regs.h 10000-11001）──
    public const int SPC_MEMSIZE = 10000;
    public const int SPC_SEGMENTSIZE = 10010;   // 多记录：每段采样数
    public const int SPC_LOOPS = 10020;         // 段数（FIFO 模式 0=无限）
    public const int SPC_PRETRIGGER = 10030;    // 门控：门内前置采样数
    public const int SPC_POSTTRIGGER = 10100;   // 段内触发后采样数
    public const int SPC_ABADIVIDER = 10040;    // ABA：A 通道抽取因子
    public const int SPC_AVERAGES = 10050;      // 平均模式：求和次数
    public const int SPC_BOX_AVERAGES = 10060;  // Boxcar 模式：求和次数（regs.h 1181；与 SPC_AVERAGES 不同寄存器！）
    public const int SPC_CHENABLE = 11000;
    public const int SPC_CHCOUNT = 11001;       // 当前使能通道数
    public const int CHANNEL0 = 0x00000001;
    public const int CHANNEL1 = 0x00000002;

    // ── 功能探测（regs.h 2120 + 769-787 功能位）──
    public const int SPC_PCIFEATURES = 2120;
    public const int SPCM_FEAT_MULTI = 0x00000001;      // Multiple Recording 选项
    public const int SPCM_FEAT_GATE = 0x00000002;       // 门控采样选项
    public const int SPCM_FEAT_DIGITAL = 0x00000004;    // 同步数字输入/输出
    public const int SPCM_FEAT_TIMESTAMP = 0x00000008;  // 时间戳选项
    public const int SPCM_FEAT_STARHUB4 = 0x00000020;   // StarHub（M3i 4 卡）
    public const int SPCM_FEAT_ABA = 0x00000080;        // ABA 模式选项
    public const int SPCM_FEAT_BASEXIO = 0x00000100;    // 基础卡额外 I/O

    // ── 时间戳（regs.h 47000-47050；M2i/M3i 为 64 位采样时钟计数）──
    public const int SPC_TIMESTAMP_CMD = 47000;
    public const int SPC_TSMODE_DISABLE = 0x00000000;
    public const int SPC_TSMODE_STANDARD = 0x00000002;  // 标准模式：每次触发记录时间戳
    public const int SPC_TSCNT_INTERNAL = 0x00000100;   // 内部采样时钟计数源
    public const int SPC_TIMESTAMP_AVAILMODES = 47001;  // 可用时间戳模式位图
    public const int SPC_TIMESTAMP_COUNT = 47020;       // FIFO 中待读时间戳数
    public const int SPC_TIMESTAMP_FIFO = 47040;        // 读取（弹出）一个时间戳
    public const int SPC_TIMESTAMP_TIMEOUT = 47045;

    // ── 采样时钟（regs.h 20140/20200）──
    public const int SPC_SAMPLERATE = 20000;         // Hz
    public const int SPC_CLOCKMODE = 20200;
    public const int SPC_CM_INTPLL = 0x00000001;     // 内部 PLL
    public const int SPC_CM_EXTERNAL = 0x00000008;   // 外部时钟直接采样
    public const int SPC_CM_EXTREFCLOCK = 0x00000020;// 外部参考时钟 + 内部 PLL
    public const int SPC_REFERENCECLOCK = 20140;     // 参考时钟频率（Hz）

    // ── 通道幅度/偏移编程（regs.h 30000-30030；mV）──
    public const int SPC_AMP0 = 30010;               // CH0 输入量程
    public const int SPC_OFFS0 = 30000;              // CH0 输入偏移补偿（mV）
    public const int SPC_AMP1 = 30110;               // CH1 输入量程
    public const int SPC_OFFS1 = 30100;              // CH1 输入偏移补偿（mV）
    public const int SPC_50OHM0 = 30030;          // 0=1MΩ 高阻缓冲路径, 1=50Ω HF 路径(250MHz 带宽)
    public const int SPC_50OHM1 = 30130;
    public const int AMP_BI200 = 200;
    public const int AMP_BI500 = 500;
    public const int AMP_BI1000 = 1000;
    public const int AMP_BI2000 = 2000;
    public const int AMP_BI5000 = 5000;
    public const int AMP_BI10000 = 10000;

    // ── 触发 ──
    public const int SPC_TRIG_ORMASK = 40410;        // 触发源组合（或）
    public const int SPC_TRIG_ANDMASK = 40430;       // 触发源组合（与）
    public const int SPC_TMASK_SOFTWARE = 0x00000001;
    public const int SPC_TMASK_EXT0 = 0x00000002;    // 专用外部触发口 EXT0
    public const int SPC_TMASK_EXT1 = 0x00000004;
    public const int SPC_TMASK_CH0 = 0x00010000;

    public const int SPC_TRIG_EXT0_MODE = 40510;       // EXT0 外触发边沿模式：SPC_TM_POS/NEG/BOTH（硬件手册 p.90）
    public const int SPC_TM_POS = 0x00000001;
    public const int SPC_TM_NEG = 0x00000002;
    public const int SPC_TM_BOTH = 0x00000004;
    public const int SPC_TM_HIGH = 0x00000008;   // 门控模式：门信号高电平有效（门内采样）
    public const int SPC_TM_LOW = 0x00000010;    // 门控模式：门信号低电平有效

    public const int SPC_TRIG_EXT0_ACDC = 40120;     // EXT0 交直流耦合
    public const int SPC_TRIG_TERM0 = 40110;         // EXT0 50Ω 终端

    public const int SPC_TRIG_EXT0_LEVEL0 = 42320;   // EXT0 触发电平（mV）
    public const int SPC_TRIG_EXT0_LEVEL1 = 42330;   // EXT0 第二触发电平（窗口模式）

    // ── 触发延迟（硬件手册 p.85：触发链最末级，平移触发事件本身，不影响 pre/post 比例）──
    public const int SPC_TRIG_AVAILDELAY = 40800;    // 只读：硬件最大可用触发延迟（采样时钟数）
    public const int SPC_TRIG_DELAY = 40810;         // 读写：附加触发延迟，单位采样时钟；0=禁用；合法值 0 或 8 的倍数

    // ── 传输定义 ──
    public const uint SPCM_DIR_PCTOCARD = 0;
    public const uint SPCM_DIR_CARDTOPC = 1;
    public const uint SPCM_BUF_DATA = 1000;
    public const uint SPCM_BUF_TIMESTAMP = 3000;  // 时间戳 DMA 缓冲（spcm_drv.h 142）

    // ── 其他 ──
    public const int SPC_TIMEOUT = 295130;           // WAITDMA 超时（ms）
    public const int SPC_PCIFIRMWARE = 24000;        // 固件版本（诊断用）
    public const int M2CMD_CARD_WAITREADY = 0x00004000;   // 等待卡就绪（内存模式）
    public const int M2CMD_EXTRA_STARTDMA = 0x00100000;   // 启动附加数据（ABA/时间戳）DMA
    public const int M2CMD_EXTRA_WAITDMA = 0x00200000;    // 等待附加数据 DMA 就绪

    // ── X0/X1 多功能线（regs.h 47200-47220）──
    public const int SPCM_X0_MODE = 47200;
    public const int SPCM_X1_MODE = 47201;
    public const int SPCM_X0_AVAILMODES = 47210;
    public const int SPCM_X1_AVAILMODES = 47211;
    public const int SPCM_XX_ASYNCIO = 47220;        // 异步输入/输出读写寄存器
    public const int SPCM_XMODE_DISABLE = 0x00000000;
    public const int SPCM_XMODE_ASYNCIN = 0x00000001;   // 异步输入
    public const int SPCM_XMODE_ASYNCOUT = 0x00000002;  // 异步输出
    public const int SPCM_XMODE_DIGIN = 0x00000004;     // 同步数字输入（数据混入采样流）
    public const int SPCM_XMODE_DIGOUT = 0x00000008;    // 同步数字输出
    public const int SPCM_XMODE_TRIGIN = 0x00000010;    // 触发输入
    public const int SPCM_XMODE_TRIGOUT = 0x00000020;   // 触发输出

    // ── 自动校准（regs.h 50020）──
    public const int SPC_ADJ_AUTOADJ = 50020;        // 启动自动校准（阻塞至完成）

    // ── StarHub 多卡同步（regs.h 48000；单卡系统仅探测）──
    public const int SPC_STARHUB_CMD = 48000;
    public const int SPC_STARHUB_STATUS = 48010;

    // ── 驱动错误码（spcerr.h）──
    public const uint ERR_OK = 0x0000;               // 无错误
    public const uint ERR_TIMEOUT = 0x0107;          // 等待中断超时（WAITDMA 周期返回）
    public const uint ERR_FIFOHWOVERRUN = 0x0301;    // FIFO 硬件缓冲溢出（可继续排空数据）
    public const uint ERR_ABORT = 0x0020;            // 等待函数被中止

    // ── 辅助方法 ──

    /// <summary>读取错误文本（错误信息在驱动内部读后自动复位）</summary>
    public static string GetErrorText(IntPtr hDevice)
    {
        uint errorReg = 0;
        int errorValue = 0;
        var text = new byte[256];
        GetErrorInfo(hDevice, ref errorReg, ref errorValue, text);
        return $"{Encoding.ASCII.GetString(text).TrimEnd('\0')} [reg={errorReg}, value={errorValue}]";
    }

    /// <summary>检查错误码，非 0 则抛异常并附带驱动错误文本</summary>
    public static void CheckError(uint code, IntPtr hDevice, string operation)
    {
        if (code != 0)
        {
            string detail = hDevice != IntPtr.Zero ? GetErrorText(hDevice) : $"code=0x{code:X8}";
            throw new SpectrumDaqException($"{operation} 失败: {detail}");
        }
    }
}

/// <summary>Spectrum 采集卡异常</summary>
public class SpectrumDaqException : Exception
{
    public SpectrumDaqException(string message) : base(message) { }
    public SpectrumDaqException(string message, Exception innerException) : base(message, innerException) { }
}
