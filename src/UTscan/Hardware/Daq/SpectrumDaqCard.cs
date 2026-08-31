using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UTscan.Core;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Hardware.Daq;

/// <summary>采集模式（regs.h SPC_CARDMODE 的 FIFO 系列组合，2026-08-18 §3.2 整改新增）</summary>
public enum SpectrumAcquisitionMode
{
    /// <summary>单次触发 FIFO 连续流（默认；触发后数据连续传输）</summary>
    FifoSingle,

    /// <summary>多记录 FIFO：每次触发记录一个定长段（编码器触发高速采集，需 Multiple Recording 选项）</summary>
    FifoMulti,

    /// <summary>门控 FIFO：门信号有效期间采样（硬件门控窗口，需 Gated Sampling 选项）</summary>
    FifoGate,

    /// <summary>块平均：N 次触发硬件求和平均后输出一段（SNR 提升）</summary>
    FifoAverage,

    /// <summary>Boxcar 平均：相邻点滑动平均（有效分辨率提升，需固件选项支持）</summary>
    FifoBoxcar,

    /// <summary>ABA 模式：B 高速段 + A 低速连续背景流（需 ABA 选项）</summary>
    FifoAba,
}

/// <summary>
/// H-2：采集卡生命周期状态机（不再只靠两个布尔值组合）。
/// 区分"已断开"、"初始化"、"采集中"、"停止中"、"清理延迟"、"已释放"。
/// </summary>
public enum DaqState
{
    /// <summary>未初始化或已完全释放</summary>
    Closed,
    /// <summary>已初始化，可启动采集</summary>
    Initialized,
    /// <summary>采集线程运行中</summary>
    Running,
    /// <summary>正在停止（已发 Stop，等待线程退出）</summary>
    Stopping,
    /// <summary>清理延迟：采集线程未在超时内退出，资源释放推迟到线程退出时</summary>
    CleanupDeferred,
    /// <summary>已 Dispose，禁止任何操作</summary>
    Disposed,
}

/// <summary>采样时钟源（regs.h SPC_CLOCKMODE）</summary>
public enum SpectrumClockSource
{
    /// <summary>内部 PLL（默认）</summary>
    InternalPll,

    /// <summary>外部时钟直接作为采样时钟（SPC_CM_EXTERNAL，由 X0 时钟输入）</summary>
    External,

    /// <summary>外部参考时钟 + 内部 PLL 倍频（SPC_CM_EXTREFCLOCK，典型 10 MHz 参考锁定）</summary>
    ExternalRefClock,
}

/// <summary>X0/X1 多功能线复用模式（regs.h SPCM_XMODE_*）</summary>
public enum SpectrumXLineMode
{
    Disable = SpectrumNative.SPCM_XMODE_DISABLE,
    AsyncInput = SpectrumNative.SPCM_XMODE_ASYNCIN,
    AsyncOutput = SpectrumNative.SPCM_XMODE_ASYNCOUT,
    SyncDigitalIn = SpectrumNative.SPCM_XMODE_DIGIN,
    SyncDigitalOut = SpectrumNative.SPCM_XMODE_DIGOUT,
    TriggerIn = SpectrumNative.SPCM_XMODE_TRIGIN,
    TriggerOut = SpectrumNative.SPCM_XMODE_TRIGOUT,
}

/// <summary>采集卡能力位图（SPC_PCIFEATURES 解码；选项安装情况随订货配置而异）</summary>
public class SpectrumCardCapabilities
{
    public int FeatureMap { get; init; }
    /// <summary>Multiple Recording 多记录选项（SPCM_FEAT_MULTI）</summary>
    public bool MultipleRecording => (FeatureMap & SpectrumNative.SPCM_FEAT_MULTI) != 0;
    /// <summary>Gated Sampling 门控采样选项（SPCM_FEAT_GATE）</summary>
    public bool GatedSampling => (FeatureMap & SpectrumNative.SPCM_FEAT_GATE) != 0;
    /// <summary>Timestamp 时间戳选项（SPCM_FEAT_TIMESTAMP）</summary>
    public bool Timestamp => (FeatureMap & SpectrumNative.SPCM_FEAT_TIMESTAMP) != 0;
    /// <summary>ABA 模式选项（SPCM_FEAT_ABA）</summary>
    public bool AbaMode => (FeatureMap & SpectrumNative.SPCM_FEAT_ABA) != 0;
    /// <summary>StarHub 多卡同步（SPCM_FEAT_STARHUB4；本系统单卡，仅探测）</summary>
    public bool StarHub => (FeatureMap & SpectrumNative.SPCM_FEAT_STARHUB4) != 0;
    /// <summary>同步数字输入/输出（SPCM_FEAT_DIGITAL）</summary>
    public bool DigitalIo => (FeatureMap & SpectrumNative.SPCM_FEAT_DIGITAL) != 0;
    /// <summary>基础卡额外 I/O（SPCM_FEAT_BASEXIO）</summary>
    public bool BaseXio => (FeatureMap & SpectrumNative.SPCM_FEAT_BASEXIO) != 0;

    public string Describe() =>
        $"MultipleRecording={MultipleRecording}, Gate={GatedSampling}, Timestamp={Timestamp}, " +
        $"ABA={AbaMode}, StarHub={StarHub}, DigitalIO={DigitalIo}, BaseXIO={BaseXio} [0x{FeatureMap:X8}]";
}

/// <summary>
/// Spectrum M3i.3242-exp 数据采集卡真实实现（spcm_win32.dll 寄存器型 API）。
///
/// 实现说明（依据官方数据手册 m3i32_datasheet 与硬件手册 m3i32_manual_english.pdf，CD_SPCM_348a）：
/// - 卡型号 M3i.3242-exp：PCIe x1 接口、12-bit、2 通道、板载 256 MS 采样内存。
///   单通道最高 500 MS/s，双通道最高 250 MS/s（通道使能数决定，见 MaxSampleRateForChannels）。
/// - 采样率可编程范围 9 MS/s ~ 500 MS/s（1 Hz 步进；70-72/140-144/281-287 MHz 为禁用空洞）。
/// - 输入量程 6 档：±200/500/1000/2000/5000/10000 mV；输入路径 50Ω HF（250MHz 带宽）或 1MΩ 缓冲（125MHz）。
/// - 12-bit 数据以 16-bit 有符号字传输（左对齐，满量程 = ±32768），电压 = raw × 量程(mV) / 32768 / 1000 (V)。
/// - 环形缓冲协议（硬件手册 p.71 + 官方示例 rec_fifo_single_poll.cpp）：
///   WAITDMA 等 notify 字节数就绪 → 读取 SPC_DATA_AVAIL_USER_LEN/USER_POS →
///   处理数据 → 写 SPC_DATA_AVAIL_CARD_LEN 释放已消费空间。
/// - SPC_TIMEOUT 设为 500 ms，使 WAITDMA 周期性返回（ERR_TIMEOUT）以检查停止标志。
///
/// 2026-08-18 §3.2 能力吸收整改（依据 regs.h + 官方示例库 spcm_lib_card.cpp）：
/// - 多通道（SPC_CHENABLE 位图 + CH1 量程/偏移/50Ω 寄存器 30100 系列 + 采样流解复用）
/// - 外部时钟（SPC_CM_EXTERNAL / SPC_CM_EXTREFCLOCK + SPC_REFERENCECLOCK）
/// - 输入偏移补偿编程（SPC_OFFS0/SPC_OFFS1，mV）
/// - Multiple Recording FIFO（SPC_REC_FIFO_MULTI + SPC_SEGMENTSIZE/SPC_POSTTRIGGER，
///   官方示例 rec_std_multi / basic/spcm_rec_fifo_multi）
/// - 硬件门控（SPC_REC_FIFO_GATE + SPC_PRETRIGGER/SPC_POSTTRIGGER + SPC_TM_HIGH/LOW 门极性，
///   官方示例 rec_std_gate / rec_fifo_gate）
/// - 块平均（SPC_REC_STD_AVERAGE + SPC_AVERAGES，官方示例 rec_std_average；
///   FIFO 流式求和后输出段，SNR 随 √N 提升）与 Boxcar（SPC_REC_FIFO_BOXCAR）
/// - ABA 模式（SPC_REC_FIFO_ABA + SPC_ABADIVIDER，需 SPCM_FEAT_ABA 选项）
/// - 硬件时间戳（SPC_TIMESTAMP_CMD 标准模式 + 内部采样时钟计数；M3i 为 64 位 tick，
///   每次触发锁存一个，经 SPC_TIMESTAMP_FIFO 单次访问读取，用于编码器位置关联）
/// - X0/X1 多功能线复用（SPCM_X0_MODE/SPCM_X1_MODE + SPCM_XX_ASYNCIO 异步读写 = 数字 I/O）
/// - 自动校准（SPC_ADJ_AUTOADJ，上电预热后执行）
/// - StarHub 多卡同步（SPC_PCIFEATURES 位探测；本系统单卡，仅报告能力）
/// </summary>
public sealed class SpectrumDaqCard : IDataAcquisition
{
    private const string DeviceName = "spc0";   // Windows 下 0 号卡
    private const int BytesPerSample = 2;      // 12-bit 以 16-bit 有符号字传输（左对齐）
    private const int TimeoutMs = 500;
    private const float MinSampleRate = 9e6f;    // M3i.32xx 最低采样率 9 MS/s

    // 采样率禁用空洞（硬件手册 m3i32_manual：PLL 无法锁定的频段，单位 Hz）
    private static readonly (float Lo, float Hi)[] ForbiddenRateBands =
    {
        (70e6f, 72e6f),
        (140e6f, 144e6f),
        (281e6f, 287e6f),
    };

    private IntPtr _handle = IntPtr.Zero;
    // NH-1/NH-2 修复：板卡生命周期串行化锁——Initialize/Start/Stop/Cleanup/Dispose 共用，
    // 防止延迟清理期间重新初始化导致旧线程操作新句柄/新缓冲区（DMA 错配/句柄误关）。
    private readonly object _lifeLock = new();
    private short[] _ringBuffer = Array.Empty<short>();
    private GCHandle _ringPin;
    private IntPtr _ringPtr = IntPtr.Zero;
    private uint _ringBytes;
    private uint _notifyBytes;

    private int _sampleCount = ConnectionConfig.DefaultSampleCount;
    // P3-FIX（现场部署 20260825）：Spectrum 卡要求 SEGMENTSIZE 为 8 的倍数，否则
    // spcm_dwSetParam 报 "value not allowed"。UI 默认采样长度 10.2µs×100MHz=1020
    // 即触发此错误。所有赋值路径统一向上取整到 8 的倍数。
    /// <summary>段大小向上取整到 8 的倍数（M3i SEGMENTSIZE 寄存器对齐约束）。D4-FIX：internal 供 UI 回读区显示对齐后点数。</summary>
    internal static int AlignSegmentSize(int samples)
        => Math.Max(16, ((samples + 7) / 8) * 8);

    // P3-FIX2（现场部署 20260826）：M3i 在 CARD_START 时才校验触发窗口不变式
    // PRETRIGGER + POSTTRIGGER = SEGMENTSIZE 且 PRETRIGGER > 0（寄存器写入期不校验）。
    // 官方示例 rec_fifo_multi 取 PRE=32。预留最小前置样本，避免 START 被拒。
    private const int MinPreTriggerSamples = 32;

    private float _sampleRate = 100e6f;        // 默认 100 MHz（9~500 MHz）
    // DaqParams.DelayUs → PRETRIGGER 样本数（触发前延迟采集的样本数）
    private int _pretriggerDelaySamples;
    private float _triggerOffsetUs;   // P0-2：PRETRIGGER 对应的 µs 偏移（samples[0] 对应时刻 −_triggerOffsetUs）
    private int _triggerDelaySamples; // SPC_TRIG_DELAY：触发后延时（采样时钟数），跳过始波；0=禁用
    private int _rangeMv = 2000;               // 输入量程 ±2000 mV
    private bool _fiftyOhm = true;             // 50Ω HF 路径（250MHz 带宽，与 DPR500 50Ω 输出匹配）
    private bool _isRunning;
    private volatile AScanData _currentData = new();
    // L-3：双通道时 CH0/CH1 各自缓存最近帧（原实现仅 CH0 更新，CH1 帧丢失）
    private readonly AScanData[] _currentDataByChannel = { new(), new() };

    // 实时性：采样数组与 AScanData 使用池复用，避免高 PRF 下每帧 new float[]+new AScanData 的
    // GC 压力。池仅由采集线程访问（租借/归还同线程），无需加锁。
    // 发布契约：_currentData/_currentDataByChannel 内部持有池化帧；
    // 外部消费者一律经 GetCurrentData(通道)/DataReady 取得，其中 GetCurrentData 返回克隆，
    // DataReady 订阅方须立即 Clone（扫查/成像路径已 Clone）——保证池化数组绝不被外部长期持有。
    private readonly AscanFramePool _framePool = new();
    // P4-FIX（池化并发加固 20260826）：发布（归还旧帧+写新引用）与外部克隆（GetCurrentData）
    // 在同一把锁内串行。仅靠 volatile 写序无法覆盖"读线程先读到旧引用、随后旧数组被归还
    // 并复用覆盖"的竞态窗口——加锁后克隆永远读到稳定数组，正确性闭环。
    private readonly object _frameLock = new();
    // ── KPI 实时性/丢帧观测（采集线程写，GetKpis 锁内读快照）──
    private long _publishedFrames;      // 已发布 segment 帧数（每通道计一次）
    private long _overrunTotal;         // 硬件 FIFO 溢出累计次数（丢帧统计）
    private long _acqThreadAborts;      // 采集线程异常退出累计次数
    private long _cycleCount;           // 采集处理周期累计
    private double _lastCycleMs, _maxCycleMs;      // 单周期（WAITDMA→发布）耗时
    private double _lastCallbackMs, _maxCallbackMs; // DataReady 订阅方回调耗时
    // H-4：帧计数器（采集线程递增，扫查服务据此等待新帧）——volatile 保证跨线程可见
    private long _frameCounter;
    private readonly ManualResetEventSlim _frameEvent = new(false);
    private Thread? _acqThread;
    private volatile bool _stopRequested;

    /// <summary>P0-3 释放竞态保护：Cleanup 等待采集线程超时后置位，资源释放推迟到线程自行退出时执行</summary>
    private volatile bool _cleanupDeferred;
    private readonly Queue<long> _timestampQueue = new();
    // NEW-L-1：InitializeAsync(config, daqParams) 显式参数生效期间置位（锁内访问）
    private bool _paramsSetExplicitly;
    // RM-1：曾经初始化过且句柄已释放（Stop 超时/故障复位）→ UI 须提示重新初始化后才能启动
    private bool _everInitialized;
    // H-2：生命周期状态机（不再只靠布尔值组合）
    private DaqState _state = DaqState.Closed;

    // ── §3.2 能力吸收新增配置（InitializeAsync 前设置）──
    // 默认 FifoMulti（Multiple Recording）：每触发一段、段长=SampleCount，
    // 帧边界与 DPR500 的 PRF 触发脉冲一一对应（审查 P1-2）。
    // FifoSingle 的固定长度切片帧起点在波形间漂移，仅适合自由运行频谱观察。
    private SpectrumAcquisitionMode _mode = SpectrumAcquisitionMode.FifoMulti;
    private int _channelMask = SpectrumNative.CHANNEL0;
    private SpectrumClockSource _clockSource = SpectrumClockSource.InternalPll;
    private int _referenceClockHz = 10_000_000;
    private int _offsetMv0;
    private int _offsetMv1;
    private int _averages = 1;
    private bool _enableTimestamp;
    private bool _gateActiveHigh = true;
    private int _abaDivider = 100;

    public bool IsRunning => _isRunning;
    /// <summary>RM-1：初始化过的卡若句柄已被释放（Stop 超时/故障复位路径）或已 Reset（寄存器回默认、需重配），
    /// 再次 Start 前必须先重新 InitializeAsync——UI 据此停用启动按钮并提示。
    /// L3-FIX（审查 20260828）：ResetAsync 成功路径保留 _handle 但置 _state=Closed，
    /// 原实现未识别该状态 → 故障复位后以默认 FIFO 模式误启动、段切片错位。现并入判定。</summary>
    public bool NeedsReinitialize => _everInitialized
        && (_cleanupDeferred || _handle == IntPtr.Zero || _state == DaqState.Closed);

    /// <summary>
    /// 诊断（停止→开始失败定位）：输出采集卡全链路状态快照——状态机/句柄/线程/标志位，
    /// 供 UI 在每次开始/停止时记录，区分 UI 控件问题与硬件层问题。
    /// </summary>
    public string DescribeState()
    {
        return $"state={_state}, everInit={_everInitialized}, handle={( _handle == IntPtr.Zero ? "NULL" : "OK")}, " +
               $"running={_isRunning}, stopReq={_stopRequested}, cleanupDeferred={_cleanupDeferred}, " +
               $"acqThreadAlive={(_acqThread?.IsAlive == true)}, " +
               $"sampleRate={_sampleRate / 1e6:F1}MHz, sampleCount={_sampleCount}, needsReinit={NeedsReinitialize}";
    }
    public event EventHandler<AScanDataEventArgs>? DataReady;

    /// <summary>M-2：FIFO 硬件溢出告警事件（连续溢出时触发，供扫查服务联动停止）</summary>
    public event EventHandler<string>? OverrunDetected;

    /// <summary>初始化后有效：卡能力位图（选项安装情况），null 表示尚未初始化</summary>
    public SpectrumCardCapabilities? Capabilities { get; private set; }

    /// <summary>最后一次连接/初始化失败的详细错误（供 UI 诊断日志显示）</summary>
    public string? LastConnectError { get; private set; }

    /// <summary>当前使能通道数（通道掩码位计数）</summary>
    public int EnabledChannelCount => System.Numerics.BitOperations.PopCount((uint)_channelMask);

    /// <summary>该通道数下最高采样率：单通道 500 MS/s、双通道 250 MS/s（硬件手册）</summary>
    public float MaxSampleRateForChannels => EnabledChannelCount >= 2 ? 250e6f : 500e6f;

    /// <summary>
    /// 采样率安全钳位（P0）：范围 9 MS/s ~ 通道数上限，并偏移出 PLL 禁用空洞
    /// （70-72 / 140-144 / 281-287 MHz，落入空洞时取最近的有效边界）。
    /// 独立纯函数便于单元测试锁定。
    /// </summary>
    public static float ClampSampleRate(float requestedHz, int channelCount)
    {
        float maxRate = channelCount >= 2 ? 250e6f : 500e6f;
        float rate = Math.Clamp(requestedHz, MinSampleRate, maxRate);

        foreach (var (lo, hi) in ForbiddenRateBands)
        {
            if (rate >= lo && rate < hi)
            {
                // L9-FIX（审查 20260828）：原实现边界值(rate==lo)取 lo 仍留在空洞内
                // （rate>=lo 恒命中，取 lo 不生效）。偏移到 lo-1（空洞外下界，低采样率更保守）；
                // 靠近上边界时取 hi。恰在中点时取低侧。
                float loOutside = Math.Max(MinSampleRate, lo - 1);
                rate = rate - lo <= hi - rate ? loOutside : hi;
            }
        }
        return rate;
    }

    /// <summary>输入量程（mV），可选 200/500/1000/2000/5000/10000（M3i.32xx 12-bit 双极性 6 档）</summary>
    public int InputRangeMv
    {
        get => _rangeMv;
        set => _rangeMv = value is 200 or 500 or 1000 or 2000 or 5000 or 10000 ? value : 2000;
    }

    /// <summary>true=50Ω HF 输入路径（250MHz 带宽）；false=1MΩ 高阻缓冲路径（125MHz 带宽）</summary>
    public bool InputFiftyOhm
    {
        get => _fiftyOhm;
        set => _fiftyOhm = value;
    }

    /// <summary>采集模式（默认 FifoMulti，帧与 PRF 触发对齐）。段类模式每段点数 = SampleCount；FifoSingle 仅适合自由运行频谱观察</summary>
    public SpectrumAcquisitionMode AcquisitionMode
    {
        get => _mode;
        set
        {
            // H-B：FifoGate 模式下 EXT0 触发极性改为门控电平（TM_HIGH/LOW），
            // 与 DPR500 脉冲边沿不兼容——若需门控模式需外部配置门信号。
            if (value == SpectrumAcquisitionMode.FifoGate && _mode != SpectrumAcquisitionMode.FifoGate)
                Debug.WriteLine("[DAQ] ⚠ 切换到 FifoGate 模式：触发极性变更为门控电平，DPR500 脉冲边沿可能无法触发");
            _mode = value;
        }
    }

    /// <summary>通道使能掩码：CHANNEL0=0x1、CHANNEL1=0x2、双通道=0x3（多探头并行采集）</summary>
    public int ChannelMask
    {
        get => _channelMask;
        set => _channelMask = value is >= SpectrumNative.CHANNEL0 and <= 0x3 ? value : SpectrumNative.CHANNEL0;
    }

    /// <summary>采样时钟源：内部 PLL（默认）/ 外部时钟直接采样 / 外部参考时钟锁相</summary>
    public SpectrumClockSource ClockSource
    {
        get => _clockSource;
        set => _clockSource = value;
    }

    /// <summary>外部参考时钟频率（Hz，ClockSource=ExternalRefClock 时生效；默认 10 MHz）</summary>
    public int ReferenceClockHz
    {
        get => _referenceClockHz;
        set => _referenceClockHz = value > 0 ? value : 10_000_000;
    }

    /// <summary>CH0 输入偏移补偿（mV，±量程内；用于抵消前端直流失调）</summary>
    public int InputOffsetMv0
    {
        get => _offsetMv0;
        set => _offsetMv0 = Math.Clamp(value, -10000, 10000);
    }

    /// <summary>CH1 输入偏移补偿（mV）</summary>
    public int InputOffsetMv1
    {
        get => _offsetMv1;
        set => _offsetMv1 = Math.Clamp(value, -10000, 10000);
    }

    /// <summary>块平均/Boxcar 模式的硬件求和次数（1=关闭；2~65536，SNR 提升 √N）</summary>
    public int Averages
    {
        get => _averages;
        set => _averages = Math.Clamp(value, 1, 65536);
    }

    /// <summary>启用硬件时间戳（每次触发锁存 64 位采样时钟计数，需 Timestamp 选项）</summary>
    public bool EnableTimestamp
    {
        get => _enableTimestamp;
        set => _enableTimestamp = value;
    }

    /// <summary>门控模式门极性：true=高电平采样（SPC_TM_HIGH），false=低电平采样（SPC_TM_LOW）</summary>
    public bool GateActiveHigh
    {
        get => _gateActiveHigh;
        set => _gateActiveHigh = value;
    }

    /// <summary>ABA 模式 A 通道抽取因子（1~65536，A 流 = B 采样率/Divider）</summary>
    public int AbaDivider
    {
        get => _abaDivider;
        set => _abaDivider = Math.Clamp(value, 1, 65536);
    }

    private int _externalTriggerLevelMv = 1000;

    /// <summary>外触发（X0 口模拟比较器）触发电平，mV（审查 P3-12：原硬编码 1000 不可配置）</summary>
    public int ExternalTriggerLevelMv
    {
        get => _externalTriggerLevelMv;
        set => _externalTriggerLevelMv = Math.Clamp(value, -10000, 10000);
    }

    private float _triggerDelayUs;   // 触发后延时（µs），SPC_TRIG_DELAY 输入（UI 快照设置）

    /// <summary>
    /// 触发后延时（µs）：经 SPC_TRIG_DELAY 跳过始波直接采集后续底波（手册 p.85）。
    /// 0=禁用；写入硬件前按采样率换算为采样时钟数并对齐 8 的倍数。
    /// </summary>
    public float TriggerDelayUs
    {
        get => _triggerDelayUs;
        set => _triggerDelayUs = Math.Max(0f, value);
    }

    public Task<bool> InitializeAsync(ConnectionConfig config)
    {
        // H-2 修复：旧资源必须完全释放后才能初始化。CleanupForReinitialize() 在锁外 Join 线程（避免死锁），
        // 返回 false 表示清理延迟——此时立即失败，不打开新设备。
        if (!CleanupForReinitialize())
        {
            LastConnectError = "旧 DAQ 资源未完全释放（采集线程仍在运行或清理延迟），跳过初始化";
            Debug.WriteLine("[DAQ] CleanupForReinitialize 返回 false——旧资源未完全释放，跳过初始化");
            return Task.FromResult(false);
        }

        lock (_lifeLock)   // NH-2：生命周期串行化
        {
            try
            {
                LastConnectError = null; // 成功时清空
                // H-2 二次验证：旧资源必须已完全释放
                if (_cleanupDeferred || _acqThread is { IsAlive: true } || _handle != IntPtr.Zero)
                    throw new SpectrumDaqException("旧 DAQ 资源未完全释放，禁止重新初始化");

                // NEW-L-1：显式 DaqParams 初始化（InitializeAsync(config, daqParams)）已预置
                // _sampleCount/_sampleRate——基础初始化不得再用 config 默认值覆盖用户显式参数
                if (!_paramsSetExplicitly)
                {
                    _sampleCount = AlignSegmentSize(config.SampleCount > 0 ? config.SampleCount : ConnectionConfig.DefaultSampleCount);
                    _sampleRate = config.SampleRate > 0 ? config.SampleRate : 100e6f;
                }
                // SPC_TRIG_DELAY：单参数路径从 UI 快照属性读取（µs→采样时钟，对齐 8 的倍数）
                if (!_paramsSetExplicitly)
                    _triggerDelaySamples = _triggerDelayUs > 0
                        ? AlignSegmentSize((int)(_triggerDelayUs * _sampleRate / 1e6))
                        : 0;


            // 1. 打开设备
            _handle = SpectrumNative.Open(DeviceName);
            if (_handle == IntPtr.Zero)
                throw new SpectrumDaqException($"打开设备 {DeviceName} 失败（驱动未安装或无可用卡）");

            // 2. 读取能力位图（选项安装情况，SPC_PCIFEATURES）
            int features = 0;
            SpectrumNative.CheckError(
                SpectrumNative.GetParam32(_handle, SpectrumNative.SPC_PCIFEATURES, ref features),
                _handle, "PCIFEATURES");
            Capabilities = new SpectrumCardCapabilities { FeatureMap = features };
            Debug.WriteLine($"[DAQ] M3i.3242 能力: {Capabilities.Describe()}");

            // 3. 复位硬件配置
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD, SpectrumNative.M2CMD_CARD_RESET),
                _handle, "CARD_RESET");

            // 4. 采样率钳位（单通道 500 / 双通道 250 MS/s + 禁用空洞校验）
            _sampleRate = ClampSampleRate(_sampleRate, EnabledChannelCount);

            // 5. 卡模式 + 段参数（§3.2：Multiple Recording / Gate / Average / Boxcar / ABA）
            ConfigureAcquisitionMode();

            // 6. 通道使能 + 各通道量程/偏移/50Ω（§3.2：多通道 + 偏移编程）
            ConfigureChannels();

            // 7. 采样时钟（§3.2：内部 PLL / 外部时钟 / 外部参考时钟锁相）
            ConfigureClock();

            // 8. 触发：外部 X0 口（模拟比较器）；门控模式为门极性，其余为上升沿 1000 mV
            int trigMode = _mode == SpectrumAcquisitionMode.FifoGate
                ? (_gateActiveHigh ? SpectrumNative.SPC_TM_HIGH : SpectrumNative.SPC_TM_LOW)
                : SpectrumNative.SPC_TM_POS;
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_TRIG_ORMASK, SpectrumNative.SPC_TMASK_EXT0),
                _handle, "TRIG_ORMASK(EXT0)");
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_TRIG_ANDMASK, 0),
                _handle, "TRIG_ANDMASK(0)");
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_TRIG_EXT0_MODE, trigMode),
                _handle, $"TRIG_EXT0_MODE({trigMode:X})");
            if (_mode != SpectrumAcquisitionMode.FifoGate)
            {
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_TRIG_EXT0_LEVEL0, _externalTriggerLevelMv),
                    _handle, $"EXT0_LEVEL0({_externalTriggerLevelMv}mV)");
            }

            // 9. 硬件时间戳（§3.2：标准模式 + 内部采样时钟计数；每次触发锁存 64 位 tick）
            ConfigureTimestamp();

            // 10. WAITDMA 超时 500 ms（超时返回以便检查停止标志）
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_TIMEOUT, TimeoutMs),
                _handle, "TIMEOUT");

            // 11. 分配环形缓冲 + 定义 DMA 传输。
            // P3-FIX4（现场 20260826，v2.0.7 吸收快照时被回退、本版恢复）：手册 m3i32_manual §"DefTransfer"：
            // "the notify size must be a multiple of 4 kByte. For data transfer it may also be a fraction
            //  of 4k in the range of 16,32,64,128,256,512,1k or 2k. No other values are allowed."
            // 旧实现 notify=bytesPerSegment 整数倍（如 1020点×2B=2040 → 半环 8160B），既非 4096 倍数
            // 也不在合法小值集合 → 驱动报 "notify block size isn't valid"。v2.0.1 的 1024 点（2048B=2k）
            // 只是侥幸合法。现统一对齐到 4KB 倍数；环形缓冲改为容纳整数个 notify 块。
            // 段完整性不依赖 notify 边界——采集线程按 bytesPerSegment 切片，跨 notify 残段由
            // carry buffer 拼接（H-3 已支持）。
            const int NotifyAlignBytes = 4096;
            int bytesPerSegment = checked(_sampleCount * EnabledChannelCount * BytesPerSample);

            // notify 取 ≥1 个 segment 的最小 4KB 倍数（保证每次中断至少一段数据，消费不过频）
            uint minNotify = (uint)Math.Min(int.MaxValue - NotifyAlignBytes,
                ((bytesPerSegment + NotifyAlignBytes - 1) / NotifyAlignBytes) * NotifyAlignBytes);
            _notifyBytes = Math.Max(NotifyAlignBytes, minNotify);

            long ringBlocks = (long)_sampleCount * EnabledChannelCount * 8 * BytesPerSample / NotifyAlignBytes;
            if (ringBlocks < 2) ringBlocks = 2;   // 至少 2 块，半满握手才有意义
            _ringBuffer = new short[ringBlocks * NotifyAlignBytes / BytesPerSample];
            _ringPin = GCHandle.Alloc(_ringBuffer, GCHandleType.Pinned);
            _ringPtr = _ringPin.AddrOfPinnedObject();
            _ringBytes = (uint)(_ringBuffer.Length * BytesPerSample);

            SpectrumNative.CheckError(
                SpectrumNative.DefTransfer(_handle, SpectrumNative.SPCM_BUF_DATA,
                    SpectrumNative.SPCM_DIR_CARDTOPC, _notifyBytes,
                    _ringPtr, 0, _ringBytes),
                _handle, $"DefTransfer(notify={_notifyBytes}B)");

            // P4-FIX（池槽回收）：重初始化（应用采集参数/重连）会覆盖槽位引用，若旧槽位仍持有
            // 池化帧且不归还则造成池漏（帧对象+采样数组永久驻留）。先归还旧槽位帧再写入新哨兵。
            lock (_frameLock)
            {
                _framePool.ReturnFrame(_currentData);
                for (int i = 0; i < _currentDataByChannel.Length; i++)
                {
                    _framePool.ReturnFrame(_currentDataByChannel[i]);
                    _currentDataByChannel[i] = new AScanData { SampleRate = _sampleRate };
                }
                _currentData = new AScanData { SampleRate = _sampleRate };
            }
            _everInitialized = true;   // RM-1：此后句柄释放（Stop 超时/故障复位）须重新初始化
            Debug.WriteLine($"[DAQ] M3i.3242 初始化完成: 模式={_mode}, 采样率={_sampleRate / 1e6:F1}MHz, " +
                            $"点数={_sampleCount}, 通道=0x{_channelMask:X}, 量程=±{_rangeMv}mV, " +
                            $"时钟={_clockSource}, 时间戳={(_enableTimestamp && Capabilities.Timestamp ? "开" : "关")}, " +
                            $"FIFO环形缓冲={_ringBytes / 1024}KB (notify={_notifyBytes}B, {EnabledChannelCount}通道)");
            _state = DaqState.Initialized;   // H-2：生命周期状态机
            return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                LastConnectError = $"初始化失败 @ {ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[DAQ] M3i.3242 初始化失败: {ex.Message}");
                Cleanup();
                // 3.1 修复：不再吞掉异常——向上传播让 UI 日志显示真实 SDK 错误（如驱动未安装、无可用卡）
                throw new SpectrumDaqException($"采集卡初始化失败: {ex.Message}", ex);
            }
        }
    }

    /// <summary>使用 DaqParams 初始化（扩展参数，说明书 3.3.2）</summary>
    public Task<bool> InitializeAsync(ConnectionConfig config, DaqParams daqParams)
    {
        // NEW-L-1：显式参数优先——原实现先写 _sampleRate/_sampleCount，基础初始化
        // 立即用 ConnectionConfig 覆盖，用户通过 DaqParams 设置的采样率/长度全部丢失。
        lock (_lifeLock)
        {
            _paramsSetExplicitly = true;
            try
            {
                if (daqParams.SampleRate > 0)
                    _sampleRate = daqParams.SampleRate;
                if (daqParams.SampleLengthUs > 0)
                    _sampleCount = AlignSegmentSize((int)(daqParams.SampleLengthUs * _sampleRate / 1e6));
                else if (config.SampleCount > 0)
                    _sampleCount = AlignSegmentSize(config.SampleCount);
                // P1-3-FIX：PRETRIGGER 语义澄清——PRETRIGGER 增加是让采集窗口**前移**，
                // 纳入更多触发前数据（samples[0] 对应触发前时刻），并非"触发后延迟开始采"。
                // 若要"触发后延迟开始采"应走 SPC_TRIG_DELAY/外部延迟门控（本版本未实现）。
                // P0-2：此偏移已记录为 _triggerOffsetUs，显示/测量统一以触发时刻为 t=0。
                _pretriggerDelaySamples = daqParams.DelayUs > 0
                    ? Math.Max(0, (int)(daqParams.DelayUs * _sampleRate / 1e6))
                    : 0;
                // SPC_TRIG_DELAY：触发后延时（µs→采样时钟，对齐 8 的倍数，手册 p.85 要求）。
                // 与 PRETRIGGER 可共存——delay 只平移触发事件本身，不影响 pre/post 比例。
                _triggerDelaySamples = daqParams.TriggerDelayUs > 0
                    ? AlignSegmentSize((int)(daqParams.TriggerDelayUs * _sampleRate / 1e6))
                    : 0;
                return InitializeAsync(config);
            }
            finally
            {
                _paramsSetExplicitly = false;
            }
        }
    }

    // ── §3.2 能力吸收：各子模块配置 ──

    private void ConfigureAcquisitionMode()
    {
        int cardMode = _mode switch
        {
            SpectrumAcquisitionMode.FifoSingle => SpectrumNative.SPC_REC_FIFO_SINGLE,
            SpectrumAcquisitionMode.FifoMulti => SpectrumNative.SPC_REC_FIFO_MULTI,
            SpectrumAcquisitionMode.FifoGate => SpectrumNative.SPC_REC_FIFO_GATE,
            SpectrumAcquisitionMode.FifoAverage => SpectrumNative.SPC_REC_FIFO_AVERAGE,
            SpectrumAcquisitionMode.FifoBoxcar => SpectrumNative.SPC_REC_FIFO_BOXCAR,
            SpectrumAcquisitionMode.FifoAba => SpectrumNative.SPC_REC_FIFO_ABA,
            _ => SpectrumNative.SPC_REC_FIFO_SINGLE,
        };

        // 选项依赖校验（SPC_PCIFEATURES）
        if (_mode == SpectrumAcquisitionMode.FifoMulti && !Capabilities!.MultipleRecording)
            throw new SpectrumDaqException("Multiple Recording 选项未安装（SPCM_FEAT_MULTI），无法使用 FifoMulti 模式");
        if (_mode == SpectrumAcquisitionMode.FifoGate && !Capabilities!.GatedSampling)
            throw new SpectrumDaqException("Gated Sampling 选项未安装（SPCM_FEAT_GATE），无法使用 FifoGate 模式");
        if (_mode == SpectrumAcquisitionMode.FifoAba && !Capabilities!.AbaMode)
            throw new SpectrumDaqException("ABA 选项未安装（SPCM_FEAT_ABA），无法使用 FifoAba 模式");

        SpectrumNative.CheckError(
            SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_CARDMODE, cardMode),
            _handle, $"CARDMODE({_mode})");

        // 段参数（官方 spcm_lib_card.cpp 的 bSpcMSetupModeRecXXX 序列）
        switch (_mode)
        {
            case SpectrumAcquisitionMode.FifoMulti:
                // 每段点数 + 触发后点数；FIFO 模式 LOOPS=0 表示无限段。
                // P3-FIX2：触发窗口不变式 PRETRIGGER+POSTTRIGGER=SEGMENTSIZE 且 PRETRIGGER>0
                // 在 CARD_START 时由固件校验。DelayUs=0 时 PRE 仍须≥MinPreTriggerSamples，
                // 否则 START 报 "posttrigger exceeds segment size"。POST 相应缩短。
                int pre = Math.Min(Math.Max(_pretriggerDelaySamples, MinPreTriggerSamples),
                                   _sampleCount / 2);
                // P0-2-FIX：记录 PRETRIGGER 时间偏移（µs）——samples[0] 对应触发前 pre 个采样。
                // 帧构造时填入 AScanData.TriggerOffsetUs，显示/测量统一以触发时刻为 t=0。
                _triggerOffsetUs = _sampleRate > 0 ? pre / _sampleRate * 1e6f : 0f;
                int postMulti = _sampleCount - pre;
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_SEGMENTSIZE, _sampleCount),
                    _handle, $"SEGMENTSIZE({_sampleCount})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_PRETRIGGER, pre),
                    _handle, $"PRETRIGGER({pre})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_POSTTRIGGER, postMulti),
                    _handle, $"POSTTRIGGER({postMulti})");
                // SPC_TRIG_DELAY：触发后延时（采样时钟），跳过始波保留后续底波。
                // 位于触发链最末级，仅平移触发事件，不影响 pre/post 比例（手册 p.85）。
                // 0=禁用（默认）；合法值 0 或 8 的倍数（AlignSegmentSize 已保证）。
                if (_triggerDelaySamples > 0)
                    SpectrumNative.CheckError(
                        SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_TRIG_DELAY, _triggerDelaySamples),
                        _handle, $"TRIG_DELAY({_triggerDelaySamples}clk={_triggerDelaySamples / _sampleRate * 1e6f:F1}µs)");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_LOOPS, 0),
                    _handle, "LOOPS(0=无限)");
                break;

            case SpectrumAcquisitionMode.FifoGate:
                int post = Math.Max(1, _sampleCount / 2);
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_PRETRIGGER, _sampleCount - post),
                    _handle, $"PRETRIGGER({_sampleCount - post})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_POSTTRIGGER, post),
                    _handle, $"POSTTRIGGER({post})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_LOOPS, 0),
                    _handle, "LOOPS(0=无限)");
                break;

            case SpectrumAcquisitionMode.FifoAverage:
            case SpectrumAcquisitionMode.FifoBoxcar:
                // 块平均（rec_std_average 示例 + spcm_lib bSpcMSetupModeRecFIFOAverage 序列）
                if (_averages <= 1)
                    throw new SpectrumDaqException("FifoAverage/FifoBoxcar 模式需设置 Averages ≥ 2");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_SEGMENTSIZE, _sampleCount),
                    _handle, $"SEGMENTSIZE({_sampleCount})");
                int preAverage = Math.Min(MinPreTriggerSamples, Math.Max(1, _sampleCount / 2));
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_PRETRIGGER, preAverage),
                    _handle, $"PRETRIGGER({preAverage})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_POSTTRIGGER, _sampleCount - preAverage),
                    _handle, $"POSTTRIGGER({_sampleCount - preAverage})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_LOOPS, 0),
                    _handle, "LOOPS(0=无限)");
                // NEW-M-6：Boxcar 与 Average 的求和次数寄存器不同（regs.h 1174/1181）——
                // 原实现两种模式都写 SPC_AVERAGES，Boxcar 的 Averaging 数实际未生效
                int averagesRegister = _mode == SpectrumAcquisitionMode.FifoBoxcar
                    ? SpectrumNative.SPC_BOX_AVERAGES
                    : SpectrumNative.SPC_AVERAGES;
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, averagesRegister, _averages),
                    _handle, $"AVERAGES({_averages})@reg{averagesRegister}");
                break;

            case SpectrumAcquisitionMode.FifoAba:
                int preAba = Math.Min(MinPreTriggerSamples, Math.Max(1, _sampleCount / 2));
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_SEGMENTSIZE, _sampleCount),
                    _handle, $"SEGMENTSIZE({_sampleCount})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_PRETRIGGER, preAba),
                    _handle, $"PRETRIGGER({preAba})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_POSTTRIGGER, _sampleCount - preAba),
                    _handle, $"POSTTRIGGER({_sampleCount - preAba})");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_LOOPS, 0),
                    _handle, "LOOPS(0=无限)");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_ABADIVIDER, _abaDivider),
                    _handle, $"ABADIVIDER({_abaDivider})");
                break;
        }
    }

    private void ConfigureChannels()
    {
        SpectrumNative.CheckError(
            SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_CHENABLE, _channelMask),
            _handle, $"CHENABLE(0x{_channelMask:X})");

        if ((_channelMask & SpectrumNative.CHANNEL0) != 0)
        {
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_AMP0, _rangeMv),
                _handle, $"AMP0(±{_rangeMv}mV)");
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_50OHM0, _fiftyOhm ? 1 : 0),
                _handle, $"50OHM0({(_fiftyOhm ? "50Ω HF" : "1MΩ 缓冲")})");
            if (_offsetMv0 != 0)
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_OFFS0, _offsetMv0),
                    _handle, $"OFFS0({_offsetMv0}mV)");
        }
        if ((_channelMask & SpectrumNative.CHANNEL1) != 0)
        {
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_AMP1, _rangeMv),
                _handle, $"AMP1(±{_rangeMv}mV)");
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_50OHM1, _fiftyOhm ? 1 : 0),
                _handle, $"50OHM1({(_fiftyOhm ? "50Ω HF" : "1MΩ 缓冲")})");
            if (_offsetMv1 != 0)
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_OFFS1, _offsetMv1),
                    _handle, $"OFFS1({_offsetMv1}mV)");
        }
    }

    private void ConfigureClock()
    {
        switch (_clockSource)
        {
            case SpectrumClockSource.InternalPll:
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_CLOCKMODE, SpectrumNative.SPC_CM_INTPLL),
                    _handle, "CLOCKMODE(INTPLL)");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_SAMPLERATE, (int)_sampleRate),
                    _handle, $"SAMPLERATE({_sampleRate / 1e6:F1}MHz)");
                break;

            case SpectrumClockSource.External:
                // 外部时钟直接采样：实际采样率由输入时钟决定，不编程 SPC_SAMPLERATE
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_CLOCKMODE, SpectrumNative.SPC_CM_EXTERNAL),
                    _handle, "CLOCKMODE(EXTERNAL)");
                break;

            case SpectrumClockSource.ExternalRefClock:
                // 外部参考时钟（典型 10 MHz）+ 内部 PLL 锁相倍频到目标采样率
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_CLOCKMODE, SpectrumNative.SPC_CM_EXTREFCLOCK),
                    _handle, "CLOCKMODE(EXTREFCLOCK)");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_REFERENCECLOCK, _referenceClockHz),
                    _handle, $"REFERENCECLOCK({_referenceClockHz}Hz)");
                SpectrumNative.CheckError(
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_SAMPLERATE, (int)_sampleRate),
                    _handle, $"SAMPLERATE({_sampleRate / 1e6:F1}MHz)");
                break;
        }
    }

    private void ConfigureTimestamp()
    {
        if (!_enableTimestamp)
            return;

        if (!Capabilities!.Timestamp)
        {
            Debug.WriteLine("[DAQ] 时间戳选项未安装（SPCM_FEAT_TIMESTAMP），HasTimestamp 将保持 false");
            return;
        }

        // 标准模式 + 内部采样时钟计数（官方示例 rec_std_multi 时间戳设置序列）
        SpectrumNative.CheckError(
            SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_TIMESTAMP_CMD,
                SpectrumNative.SPC_TSMODE_STANDARD | SpectrumNative.SPC_TSCNT_INTERNAL),
            _handle, "TIMESTAMP_CMD(STANDARD|INTERNAL)");
    }

    // ── §3.2 能力吸收：数字 I/O / 自动校准 ──

    /// <summary>
    /// 配置 X0/X1 多功能线复用模式（异步输入/输出、同步数字 IO、触发输入/输出）。
    /// line: 0 或 1。需先于 InitializeAsync 或在停止状态调用。
    /// </summary>
    public bool ConfigureXLine(int line, SpectrumXLineMode mode)
    {
        if (_handle == IntPtr.Zero) return false;
        int reg = line == 0 ? SpectrumNative.SPCM_X0_MODE : SpectrumNative.SPCM_X1_MODE;
        uint rc = SpectrumNative.SetParam32(_handle, reg, (int)mode);
        if (rc != SpectrumNative.ERR_OK)
        {
            Debug.WriteLine($"[DAQ] X{line} 模式设置失败: {SpectrumNative.GetErrorText(_handle)}");
            return false;
        }
        return true;
    }

    /// <summary>读异步数字 I/O 线状态（X0/X1 配置为 AsyncInput 后有效；SPCM_XX_ASYNCIO）</summary>
    public int ReadAsyncIo()
    {
        if (_handle == IntPtr.Zero) return 0;
        int value = 0;
        SpectrumNative.GetParam32(_handle, SpectrumNative.SPCM_XX_ASYNCIO, ref value);
        return value;
    }

    /// <summary>写异步数字 I/O 线（X0/X1 配置为 AsyncOutput 后有效；SPCM_XX_ASYNCIO）</summary>
    public bool WriteAsyncIo(int value)
    {
        if (_handle == IntPtr.Zero) return false;
        uint rc = SpectrumNative.SetParam32(_handle, SpectrumNative.SPCM_XX_ASYNCIO, value);
        return rc == SpectrumNative.ERR_OK;
    }

    /// <summary>
    /// 执行自动校准（SPC_ADJ_AUTOADJ）。建议上电预热 30 分钟后、采集前执行；
    /// 校准系数存于卡内 EEPROM（SPC_ADJ_SAVE 可持久化）。
    /// </summary>
    public Task<bool> RunAutoAdjustAsync()
    {
        if (_handle == IntPtr.Zero) return Task.FromResult(false);
        try
        {
            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_ADJ_AUTOADJ, 1),
                _handle, "ADJ_AUTOADJ");
            Debug.WriteLine("[DAQ] M3i.3242 自动校准完成");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DAQ] 自动校准失败: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    // ── 采集启停 ──

    public Task StartContinuousAsync()
    {
        lock (_lifeLock)   // NH-2：生命周期串行化
        {
            if (_handle == IntPtr.Zero || _cleanupDeferred)
            {
                // 诊断（停止→开始失败定位）：明确记录是"句柄释放"还是"deferred 清理"卡住
                Debug.WriteLine($"[DAQ] StartContinuousAsync 拒绝：handleNull={_handle == IntPtr.Zero}, " +
                    $"cleanupDeferred={_cleanupDeferred}, state={_state}, acqAlive={(_acqThread?.IsAlive == true)}");
                throw new SpectrumDaqException("采集卡未初始化（资源已释放），需重新 InitializeAsync");
            }

            // P0 重入保护：重复启动会导致两个采集线程竞争消费同一环形缓冲（数据错乱/重复帧）
            if (_isRunning || (_acqThread?.IsAlive == true))
            {
                // 诊断：记录线程残留（StopAsync 2s Join 超时后 _acqThread 未清的典型场景）
                Debug.WriteLine($"[DAQ] StartContinuousAsync 拒绝：running={_isRunning}, " +
                    $"acqAlive={(_acqThread?.IsAlive == true)}, state={_state}");
                throw new SpectrumDaqException("采集线程仍在运行，须先 StopAsync 并等待线程退出后方可再次启动");
            }

            // 启动：启动卡 + 使能触发 + 启动数据 DMA。
            // D-FIX（现场 20260829 诊断）：StopAsync（CARD_STOP|DATA_STOPDMA）后 DMA 通道残留
            // ABORT 状态，若直接 CARD_START 则 WAITDMA 立即返回 ERR_ABORT、线程启动即退出——
            // 表现为"停止后无法重新开始采集"（日志：state=Running 但 running 立即回 false）。
            // 先发一次幂等的 STOP 清残留 DMA 状态，再完整启动。
            uint cleanupRc = SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                SpectrumNative.M2CMD_CARD_STOP | SpectrumNative.M2CMD_DATA_STOPDMA);
            if (cleanupRc != SpectrumNative.ERR_OK && cleanupRc != SpectrumNative.ERR_ABORT)
                SpectrumNative.CheckError(cleanupRc, _handle, "START前DMA清理");

            SpectrumNative.CheckError(
                SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                    SpectrumNative.M2CMD_CARD_START | SpectrumNative.M2CMD_CARD_ENABLETRIGGER |
                    SpectrumNative.M2CMD_DATA_STARTDMA),
                _handle, "START");

            _stopRequested = false;
            _timestampQueue.Clear();
            _isRunning = true;
            _state = DaqState.Running;   // H-2：生命周期状态机
            Interlocked.Exchange(ref _frameCounter, 0);   // 6-FIX：重启后帧计数从 0 开始，否则 AscanForm 的
            // frameCount > _lastFrameCount 永远不成立 → 波形不刷新（旧计数 5000 > 新计数 0）。
            _acqThread = new Thread(AcquisitionLoop) { IsBackground = true, Name = "SpectrumFifo" };
            _acqThread.Start();
            Debug.WriteLine($"[DAQ] M3i.3242 FIFO 采集已启动: 模式={_mode}, notify={_notifyBytes / 1024}KB");
            return Task.CompletedTask;
        }
    }

    public Task StopAsync()
    {
        // H-2 修复：Join 不得在持有 _lifeLock 时执行（采集线程 finally 也要取 _lifeLock，会死锁）。
        // 锁内发 Stop 标志 + StopDMA/CardStop + 取线程引用，锁外 Join。
        Thread? thread;
        lock (_lifeLock)   // NH-2：生命周期串行化
        {
            // H-5 修复：先发 StopDMA/CardStop（让 WAITDMA 立即以 ERR_ABORT 返回）→ 再 Join 线程。
            _stopRequested = true;
            _frameEvent.Reset();   // H-4：停止时复位帧事件，避免残留信号误导等待者
            _state = DaqState.Stopping;

            if (_handle != IntPtr.Zero)
            {
                try
                {
                    uint rc = SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                        SpectrumNative.M2CMD_CARD_STOP | SpectrumNative.M2CMD_DATA_STOPDMA);
                    // RM-7 修复：记录 Stop/DMA 停止的板卡返回码——故障诊断需要区分"正常停止"与"停止失败"。
                    if (rc != 0)
                        Debug.WriteLine($"[DAQ] StopDMA/CardStop 返回非零: 0x{rc:X8}");
                }
                catch (Exception ex) { Debug.WriteLine($"[DAQ] StopDMA/CardStop 异常: {ex.Message}"); }
            }
            thread = _acqThread;
        }

        // 锁外等待线程退出
        if (thread is { IsAlive: true })
        {
            try { thread.Join(2000); } catch { }
        }

        lock (_lifeLock)
        {
            if (thread is { IsAlive: true })
            {
                // 线程未在 2s 内退出。置 _cleanupDeferred，资源释放由线程退出时 finally 完成。
                _cleanupDeferred = true;
                _state = DaqState.CleanupDeferred;
                Debug.WriteLine("[DAQ] 采集线程未在 2s 内退出，资源释放推迟至线程退出时执行");
            }
            else
            {
                _acqThread = null;
                if (_state != DaqState.Disposed)
                    _state = DaqState.Initialized;   // 可重新 Start
            }
            _isRunning = false;
            Debug.WriteLine("[DAQ] M3i.3242 采集已停止");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// RH-8：故障复位——Stop DMA + Card Stop + Card Reset（清除板卡残留错误状态）。
    /// 与普通 StopAsync 不同：Reset 会重置所有板卡寄存器到上电默认，需要重新 InitializeAsync。
    /// 故障路径（溢出排空/通信错误/DMA 异常）调用此方法后 UI 必须提示重新初始化。
    /// </summary>
    public Task ResetAsync()
    {
        // H-2：锁外 Join。M-6：分别检查 Stop 和 Reset 返回码，失败抛异常。
        Thread? thread;
        lock (_lifeLock)
        {
            _stopRequested = true;
            _frameEvent.Reset();
            _state = DaqState.Stopping;

            if (_handle != IntPtr.Zero)
            {
                try
                {
                    // M-6 修复：分别检查 Stop 和 Reset 返回码，读取错误文本后抛异常。
                    // Stop 成功但 Reset 失败状态应为 FaultedNeedsReinitialize。
                    uint stopRc = SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                        SpectrumNative.M2CMD_CARD_STOP | SpectrumNative.M2CMD_DATA_STOPDMA);
                    if (stopRc != SpectrumNative.ERR_OK && stopRc != SpectrumNative.ERR_ABORT)
                        throw new SpectrumDaqException(
                            $"Stop DMA/Card 失败: 0x{stopRc:X8} ({SpectrumNative.GetErrorText(_handle)})");

                    uint resetRc = SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                        SpectrumNative.M2CMD_CARD_RESET);
                    if (resetRc != SpectrumNative.ERR_OK)
                        throw new SpectrumDaqException(
                            $"Card Reset 失败: 0x{resetRc:X8} ({SpectrumNative.GetErrorText(_handle)})");
                    Debug.WriteLine("[DAQ] M3i.3242 故障复位已执行（CARD_STOP + CARD_RESET）");
                }
                catch (SpectrumDaqException) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DAQ] 故障复位部分失败: {ex.Message}");
                }
            }
            thread = _acqThread;
        }

        // 锁外等待线程退出
        if (thread is { IsAlive: true })
        {
            try { thread.Join(2000); } catch { }
        }

        lock (_lifeLock)
        {
            if (thread is { IsAlive: true })
            {
                _cleanupDeferred = true;
                _state = DaqState.CleanupDeferred;
                Debug.WriteLine("[DAQ] 故障复位：采集线程未在 2s 内退出，资源释放推迟至线程退出时执行");
            }
            else
            {
                _acqThread = null;
                if (_state != DaqState.Disposed)
                    _state = DaqState.Closed;   // Reset 后需重新 InitializeAsync
            }
            _isRunning = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 返回最近一帧的独立克隆。内部帧走池复用，单次克隆开销远低于采集速率，
    /// 确保调用方（冻结显示/FFT/导出等）可安全长期持有，不受后续池归还影响。
    /// P4-FIX：克隆在 _frameLock 内完成，与发布侧归还互斥——杜绝克隆到已归还复用数组。
    /// </summary>
    public AScanData GetCurrentData()
    {
        lock (_frameLock)
            return AscanFramePool.CloneForExternal(_currentData);
    }

    /// <summary>
    /// 获取指定通道的最近一帧（L-3 新增：双通道时 CH1 数据不再丢失）。
    /// channel: 0 基通道索引；越界回退 CH0。返回独立克隆，可安全持有。
    /// </summary>
    public AScanData GetCurrentData(int channel)
    {
        lock (_frameLock)
            return channel >= 0 && channel < _currentDataByChannel.Length
                ? AscanFramePool.CloneForExternal(_currentDataByChannel[channel])
                : AscanFramePool.CloneForExternal(_currentData);
    }

    /// <summary>实时性/丢帧 KPI 快照（P5：类型提升至 Core.Models）</summary>
    public DaqKpiSnapshot GetKpis()
    {
        lock (_frameLock)
            return new DaqKpiSnapshot(
                Interlocked.Read(ref _publishedFrames),
                Interlocked.Read(ref _cycleCount),
                _lastCycleMs, _maxCycleMs,
                _lastCallbackMs, _maxCallbackMs,
                Interlocked.Read(ref _overrunTotal),
                Interlocked.Read(ref _acqThreadAborts));
    }

    /// <summary>H-4：当前已完成的帧计数（volatile 读）</summary>
    public long GetCurrentFrameCount() => Interlocked.Read(ref _frameCounter);

    /// <summary>
    /// H-4：等待产生新帧（运动到位后调用，确保取到当前位置的新帧）。
    /// 轮询 + 事件通知双机制：采集线程每次出帧 Set 事件，此处等待；
    /// 避免仅轮询的延迟与仅事件的唤醒丢失。
    /// </summary>
    public async Task<bool> WaitForNewFrameAsync(int timeoutMs, CancellationToken ct = default)
    {
        long startCount = GetCurrentFrameCount();
        // RH-3：采集未运行（从未启动/已停止/线程异常退出）时返回失败——
        // 原实现返回 true 会把"无新帧"误报为"帧已就绪"，扫描服务据此持续采数而不自知（静默错位）
        if (!_isRunning) return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (GetCurrentFrameCount() <= startCount)
        {
            ct.ThrowIfCancellationRequested();
            // RH-3 修复：采集线程可能在等待期间异常退出（WAITDMA 错误/溢出排空），
            // _isRunning 已变 false 但帧计数未增——此时不得继续等待，必须返回失败
            // 防止旧帧被标记到新坐标（审查报告 H-4/RH-3）。
            if (!_isRunning) return false;
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return false;
            // 真正异步让出（替代原同步 _frameEvent.Wait，避免 async 缺 await 警告）
            try { await Task.Delay(5, ct); }
            catch (OperationCanceledException) { throw; }
        }
        return true;
    }

    /// <summary>
    /// H-1：严格单次触发帧同步——接收触发前基线，等待帧计数超过该基线。
    /// 与 WaitForNewFrameAsync 不同：不重新抓基线，避免触发到等待调用之间到达的帧被漏判。
    /// 异步轮询实现（2ms 间隔），不依赖同步 _frameEvent.Wait。
    /// </summary>
    public async Task<bool> WaitForFrameAfterAsync(long baseline, int timeoutMs, CancellationToken ct = default)
    {
        // RH-3：采集未运行时返回失败（不把无新帧误报为帧已就绪）
        if (!_isRunning) return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (GetCurrentFrameCount() <= baseline)
        {
            ct.ThrowIfCancellationRequested();
            // 采集线程可能在等待期间异常退出，_isRunning 已变 false 但帧计数未增
            if (!_isRunning) return false;
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return false;
            await Task.Delay(2, ct);
        }
        return true;
    }

    /// <summary>
    /// H-4：发布一个完整 segment 的所有启用通道帧。一个 segment 只递增一次帧计数 + Set 一次事件，
    /// 不依赖 CH1 是否启用（CH2-only 也正常出帧）。GetCurrentData 默认返回启用列表第一个物理通道。
    /// </summary>
    private void PublishCompletedSegment(
        AScanData[] frames, int[] physicalChannels, long timestamp, bool hasTimestamp)
    {
        var cycleSw = Stopwatch.StartNew();

        // P4-FIX：换帧+归还旧帧在 _frameLock 内完成，与 GetCurrentData 的克隆互斥——
        // 保证读取线程克隆到的是稳定数组，绝不克隆到"刚归还进池、随即被复用覆盖"的数组。
        lock (_frameLock)
        {
            // GetCurrentData 默认语义：返回本次启用列表中的第一个物理通道。
            // 先更新 _currentData，确保其绝不指向稍后被归还的旧帧（避免读取线程克隆到已回收数组）。
            if (frames.Length > 0)
                _currentData = frames[0];

            for (int logical = 0; logical < frames.Length; logical++)
            {
                int physical = physicalChannels[logical];
                if (physical >= 0 && physical < _currentDataByChannel.Length)
                {
                    // 归还被取代的旧帧（池复用；外部经 GetCurrentData 取的是克隆，DataReady 订阅方即时 Clone，
                    // 故旧的池化帧在此刻已无人长期持有，可安全回收）。池侧双重归还防护兜底。
                    var old = _currentDataByChannel[physical];
                    if (!ReferenceEquals(old, null) && !ReferenceEquals(old, frames[logical]))
                        _framePool.ReturnFrame(old);
                    // P1：数组元素跨线程发布，用 Volatile.Write 保证采集线程写入对读线程可见，
                    // 与 _currentData 的 volatile 语义一致（避免读到未发布/陈旧的引用）。
                    Volatile.Write(ref _currentDataByChannel[physical], frames[logical]);
                }
            }
        }

        // H-C-FIX：DataReady 派发前先克隆——订阅方可安全长期持有 e.Data，无需"即时 Clone"契约。
        // 消除架构性隐患（若订阅方持有池化引用，池归还后数组被复用覆盖导致数据损坏）。
        // 代价：每帧多一次克隆（1024×4B≈4KB），在 PRF 千赫兹级可忽略。
        if (DataReady != null)
        {
            var cbSw = Stopwatch.StartNew();
            for (int logical = 0; logical < frames.Length; logical++)
            {
                var clone = AscanFramePool.CloneForExternal(frames[logical]);
                DataReady?.Invoke(this, new AScanDataEventArgs { Data = clone });
            }
            cbSw.Stop();
            _lastCallbackMs = cbSw.Elapsed.TotalMilliseconds;
            if (cbSw.Elapsed.TotalMilliseconds > _maxCallbackMs) _maxCallbackMs = cbSw.Elapsed.TotalMilliseconds;
        }

        // 所有启用通道都已发布后，一个 segment 只递增一次；KPI 累计周期耗时
        Interlocked.Increment(ref _frameCounter);
        Interlocked.Increment(ref _publishedFrames);
        Interlocked.Increment(ref _cycleCount);
        cycleSw.Stop();
        _lastCycleMs = cycleSw.Elapsed.TotalMilliseconds;
        if (cycleSw.Elapsed.TotalMilliseconds > _maxCycleMs) _maxCycleMs = cycleSw.Elapsed.TotalMilliseconds;
        _frameEvent.Set();
    }

    /// <summary>
    /// H-2：为重新初始化执行清理。返回 true 表示旧资源已完全释放，可安全打开新设备；
    /// false 表示清理延迟（采集线程未在超时内退出），调用方必须拒绝初始化。
    /// Join 在锁外执行（避免采集线程 finally 也要取 _lifeLock 形成死锁）。
    /// </summary>
    private bool CleanupForReinitialize()
    {
        Thread? thread;
        lock (_lifeLock)
        {
            if (_state == DaqState.Disposed)
                return false;

            _stopRequested = true;
            // 锁外 Join 前先发 StopDMA/CardStop（让 WAITDMA 立即以 ERR_ABORT 返回）
            if (_handle != IntPtr.Zero)
            {
                try
                {
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                        SpectrumNative.M2CMD_CARD_STOP | SpectrumNative.M2CMD_DATA_STOPDMA);
                }
                catch { /* 清理期错误不阻止 */ }
            }
            thread = _acqThread;
            _state = DaqState.Stopping;
        }

        // 锁外等待线程退出（不得在持有 _lifeLock 时 Join）
        if (thread is { IsAlive: true })
        {
            try { thread.Join(2000); } catch { }
        }

        lock (_lifeLock)
        {
            if (thread is { IsAlive: true })
            {
                // 线程未在 2s 内退出：置 deferred，资源释放由线程退出时 finally 完成
                _cleanupDeferred = true;
                _state = DaqState.CleanupDeferred;
                Debug.WriteLine("[DAQ] CleanupForReinitialize: 采集线程未在 2s 内退出，禁止重新初始化");
                return false;
            }

            _acqThread = null;
            FreeResources();
            _cleanupDeferred = false;
            _state = DaqState.Closed;
            return true;
        }
    }

    // ── FIFO 环形缓冲采集线程 ──

    private void AcquisitionLoop()
    {
        int chCount = EnabledChannelCount;

        // NEW-M-3 修复：建立实际启用物理通道索引列表——仅启用 CH2 时 chCount=1，
        // 原实现 i % chCount 恒为 0，帧被标记为 ChannelIndex=0（CH1），物理通道归属错误。
        int[] physicalCh = new int[chCount];
        {
            int idx = 0;
            if ((_channelMask & SpectrumNative.CHANNEL0) != 0) physicalCh[idx++] = 0;  // CH1 → index 0
            if ((_channelMask & SpectrumNative.CHANNEL1) != 0) physicalCh[idx++] = 1;  // CH2 → index 1
        }
        // M-5：实时性——采样数组/AScanData 复用 _samplePool/_framePool（见 PublishCompletedSegment 归还），
        // 消除高 PRF 下每帧 new float[]+new AScanData 的 GC 压力。
        // H-3：segment 维度——一次硬件触发记录一个定长段（_sampleCount × chCount 交织样本）。
        // notify 字节按 segment 整数倍对齐；未对齐的残段存 carry buffer 与下一次 notify 拼接。
        int samplesPerSegment = checked(_sampleCount * chCount);
        int bytesPerSegment = checked(samplesPerSegment * BytesPerSample);
        var carryBuffer = new List<short>();   // 跨 notify 的残段缓存
        bool overrunDetected = false;
        int overrunCount = 0;   // M-2：连续溢出计数（触发联动阈值）

        // P0-3：整体异常保护——若异常逃逸到后台线程顶层，整个进程会被 CLR 终止。
        // 关闭期（缓冲已释放/句柄已关闭）或 DMA 中的意外异常统一在此捕获并记日志退出。
        try
        {
        while (!_stopRequested)
        {
            // 等待 notify 字节就绪（超时则循环检查停止标志）
            uint waitRet = SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                SpectrumNative.M2CMD_DATA_WAITDMA);
            if (waitRet != SpectrumNative.ERR_OK)
            {
                if (waitRet == SpectrumNative.ERR_TIMEOUT)
                    continue;   // 周期性超时返回：检查停止标志后继续等待
                if (waitRet == SpectrumNative.ERR_FIFOHWOVERRUN)
                {
                    Interlocked.Increment(ref _overrunTotal);   // KPI：丢帧累计
                    // 硬件 FIFO 溢出：按官方示例继续排空板载内存中剩余数据
                    if (!overrunDetected)
                    {
                        Debug.WriteLine("[DAQ] M3i.3242 FIFO 硬件缓冲溢出（消费速度不足），继续读取剩余数据");
                        overrunDetected = true;
                    }
                    // M-2：连续溢出累计，超阈值触发联动事件（扫查服务据此停止，防数据丢帧污染成像）
                    if (++overrunCount >= 3)
                    {
                        OverrunDetected?.Invoke(this,
                            $"FIFO 连续溢出 {overrunCount} 次（消费速度不足，可能 PRF 过高或采样点数过多）");
                        overrunCount = 0;   // 复位，避免反复触发
                    }
                }
                else if (waitRet == SpectrumNative.ERR_ABORT)
                {
                    // D-FIX：仅 stopReq=true 时 ABORT 才是"正常停止退出"；若 stopReq=false 仍 ABORT
                    // （如重启后 DMA 未就绪的瞬态），短暂等待后重试 WAITDMA，而非 break 退出采集线程。
                    // 避免"停止后无法重新开始采集"（线程启动即退）。
                    if (_stopRequested)
                    {
                        Debug.WriteLine("[DAQ] WAITDMA ABORT：正常停止退出");
                        break;
                    }
                    Debug.WriteLine($"[DAQ] WAITDMA ABORT 但未请求停止（state={_state}）——重试等待 DMA 就绪");
                    Thread.Sleep(10);
                    continue;
                }
                else
                {
                    // 其他真实错误：读取错误文本（读后自动复位）并退出线程
                    Debug.WriteLine($"[DAQ] WAITDMA 错误 0x{waitRet:X4}: {SpectrumNative.GetErrorText(_handle)}");
                    break;
                }
            }

            // 官方示例对 FIFO 可用量寄存器使用 64 位访问器
            long availLen = 0, availPos = 0;
            if (SpectrumNative.GetParam64(_handle, SpectrumNative.SPC_DATA_AVAIL_USER_LEN, ref availLen) != SpectrumNative.ERR_OK ||
                SpectrumNative.GetParam64(_handle, SpectrumNative.SPC_DATA_AVAIL_USER_POS, ref availPos) != SpectrumNative.ERR_OK)
                continue;

            if (availLen <= 0)
            {
                // 溢出后数据已排空（M2STAT_DATA_END）：结束采集线程
                if (overrunDetected)
                {
                    Debug.WriteLine("[DAQ] 溢出后板载数据已排空，采集线程退出");
                    break;
                }
                continue;
            }

            // 从环形缓冲拷贝（处理回绕）
            // M-4 修复：DMA 位置/长度边界校验——非法值（负/非2字节对齐/超缓冲）按 DMA 故障处理
            if (availLen <= 0 || availPos < 0 || (availLen & 1) != 0 || (availPos & 1) != 0 ||
                availPos >= _ringBytes || availLen > _ringBytes)
            {
                Debug.WriteLine($"[DAQ] DMA 可用区非法: len={availLen} pos={availPos} ring={_ringBytes}，按故障停止");
                OverrunDetected?.Invoke(this, $"DMA 可用区非法（len={availLen}, pos={availPos}），停止采集");
                break;
            }
            int sampleCount = (int)(availLen / BytesPerSample);
            int startSample = (int)(availPos / BytesPerSample);
            var raw = new short[sampleCount];
            int first = Math.Min(sampleCount, _ringBuffer.Length - startSample);
            Array.Copy(_ringBuffer, startSample, raw, 0, first);
            if (sampleCount > first)
                Array.Copy(_ringBuffer, 0, raw, first, sampleCount - first);

            // 释放已消费的环形缓冲空间
            SpectrumNative.SetParam64(_handle, SpectrumNative.SPC_DATA_AVAIL_CARD_LEN, availLen);

            // 时间戳入队（每次触发锁存一个 64 位 tick，经 SPC_TIMESTAMP_FIFO 弹出式读取）
            DrainTimestampFifo();

            // 电压转换（12-bit 左对齐于 16-bit 字：满量程 ±32768）+ 多通道解复用 + 按 segment 切片上报
            const float scale = 1f / 32768f / 1000f; // raw → mV → V

            // H-3 修复：按 segment（一次硬件触发）外层循环，每个 segment 只取一个时间戳，
            // 同 segment 的所有物理通道共享该时间戳。原实现整个 availLen 只 Dequeue 一次时间戳，
            // 一次 notify 包含多个 segment 时多个触发记录共享同一时间戳导致错位。
            // 先把 carry buffer 上一次残段拼接到本次 raw 前面，再按 segment 切片。
            short[] segmentRaw;
            int totalSamples = sampleCount;
            if (carryBuffer.Count > 0)
            {
                segmentRaw = new short[carryBuffer.Count + sampleCount];
                carryBuffer.CopyTo(segmentRaw, 0);
                Array.Copy(raw, 0, segmentRaw, carryBuffer.Count, sampleCount);
                carryBuffer.Clear();
                totalSamples = segmentRaw.Length;
            }
            else
            {
                segmentRaw = raw;
            }

            int completeSegments = totalSamples / samplesPerSegment;
            for (int seg = 0; seg < completeSegments; seg++)
            {
                long timestamp = 0;
                bool hasTimestamp = _enableTimestamp && _timestampQueue.TryDequeue(out timestamp);
                int segmentBase = seg * samplesPerSegment;

                // 该 segment 的各物理通道波形（实时性：采样数组/AScanData 走池复用，消除每帧 GC 分配）
                var frames = new AScanData[chCount];
                for (int logical = 0; logical < chCount; logical++)
                {
                    int physical = physicalCh[logical];
                    float[] samples = _framePool.RentSamples(_sampleCount);
                    for (int sample = 0; sample < _sampleCount; sample++)
                    {
                        int rawIndex = segmentBase + sample * chCount + logical;
                        samples[sample] = segmentRaw[rawIndex] * _rangeMv * scale;
                    }
                    var frame = _framePool.RentFrame();
                    frame.Samples = samples;
                    frame.PointCount = _sampleCount;   // 逻辑采样点数（池化数组可能更长）
                    frame.SampleRate = _sampleRate;
                    frame.TriggerOffsetUs = _triggerOffsetUs;   // P0-2：时间原点（触发前偏移）
                    frame.ChannelIndex = physical;
                    frame.TimestampTicks = hasTimestamp ? timestamp : 0;
                    frame.HasTimestamp = hasTimestamp;
                    frames[logical] = frame;
                }

                // H-4：一个完整 segment 发布一次——帧计数递增 + 事件 Set 只执行一次（CH2-only 也正常）
                PublishCompletedSegment(frames, physicalCh, timestamp, hasTimestamp);
            }

            // H-3：残段（不足一个 segment）存入 carry buffer 与下一次 notify 拼接，不消耗时间戳
            int remainderSamples = totalSamples % samplesPerSegment;
            if (remainderSamples > 0)
            {
                int remainderStart = completeSegments * samplesPerSegment;
                for (int i = 0; i < remainderSamples; i++)
                    carryBuffer.Add(segmentRaw[remainderStart + i]);
            }
        }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _acqThreadAborts);   // KPI：异常退出累计
            Debug.WriteLine($"[DAQ] 采集线程异常退出: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 线程退出（停止/溢出排空/错误）：同步运行状态
            _isRunning = false;

            // C-FIX（恢复采集）：无论是否 _cleanupDeferred，线程真正退出时都清 _acqThread 引用——
            // 消除 StopAsync 2s Join 超时后 `_acqThread?.IsAlive` 误判"线程仍在运行"导致的恢复失败。
            // 若此线程仍是 _acqThread 的当前引用则清空（可能已被新 Start 替换，需用引用相等判断避免误清新线程）。
            lock (_lifeLock)
            {
                if (ReferenceEquals(_acqThread, Thread.CurrentThread))
                    _acqThread = null;
            }

            // RH-2 修复：Cleanup 曾因等待超时而推迟释放，此刻线程即将退出，安全完成剩余清理。
            // FreeResources() 必须在 _lifeLock 内执行——原实现锁外释放时，InitializeAsync
            // 可在锁内打开新句柄/新缓冲，线程随后关闭新句柄造成资源错配。
            if (_cleanupDeferred)
            {
                lock (_lifeLock)
                {
                    _cleanupDeferred = false;
                    _acqThread = null;
                    FreeResources();
                    // H-2：在同一锁内依次完成 FreeResources、清线程引用、清 deferred 标志、提交 Closed 状态。
                    // 不能先发布"清理完成"再释放资源。
                    if (_state != DaqState.Disposed)
                        _state = DaqState.Closed;
                }
                Debug.WriteLine("[DAQ] 采集线程退出，推迟的资源释放已完成");
            }
        }
    }

    /// <summary>
    /// 从卡时间戳 FIFO 读取待读时间戳（SPC_TIMESTAMP_COUNT 计数 + SPC_TIMESTAMP_FIFO 弹出读取，
    /// M3i 为 64 位采样时钟 tick）。时间戳选项未安装/未启用时计数恒为 0，无副作用。
    /// </summary>
    private void DrainTimestampFifo()
    {
        if (!_enableTimestamp || !Capabilities!.Timestamp) return;

        int count = 0;
        if (SpectrumNative.GetParam32(_handle, SpectrumNative.SPC_TIMESTAMP_COUNT, ref count) != SpectrumNative.ERR_OK)
            return;
        for (int i = 0; i < count && _timestampQueue.Count < 1024; i++)
        {
            long ts = 0;
            if (SpectrumNative.GetParam64(_handle, SpectrumNative.SPC_TIMESTAMP_FIFO, ref ts) != SpectrumNative.ERR_OK)
                break;
            _timestampQueue.Enqueue(ts);
        }
    }

    private void Cleanup()
    {
        // H-2：锁外 Join（不得在持有 _lifeLock 时 Join 采集线程 finally）。
        Thread? thread;
        lock (_lifeLock)
        {
            _isRunning = false;
            _stopRequested = true;
            _frameEvent.Reset();
            if (_state != DaqState.Disposed)
                _state = DaqState.Stopping;

            // H-5 修复：FreeResources 前发送 Stop/Reset，清除板卡残留状态（防下次 spcm_hOpen 失败）
            if (_handle != IntPtr.Zero)
            {
                try
                {
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD,
                        SpectrumNative.M2CMD_CARD_STOP | SpectrumNative.M2CMD_DATA_STOPDMA);
                    SpectrumNative.SetParam32(_handle, SpectrumNative.SPC_M2CMD, SpectrumNative.M2CMD_CARD_RESET);
                }
                catch { /* 忽略清理期错误 */ }
            }
            thread = _acqThread;
        }

        // 锁外等待线程退出
        if (thread is { IsAlive: true })
        {
            try { thread.Join(1000); } catch { }
        }

        lock (_lifeLock)
        {
            if (thread is { IsAlive: true })
            {
                // P0-3 释放竞态：线程仍阻塞在 WAITDMA（最长 500ms 超时）中。
                _cleanupDeferred = true;
                _state = DaqState.CleanupDeferred;
                Debug.WriteLine("[DAQ] 采集线程未在 1s 内退出，资源释放推迟至线程退出时执行");
                return;
            }
            _acqThread = null;
            FreeResources();
            _cleanupDeferred = false;
            if (_state != DaqState.Disposed)
                _state = DaqState.Closed;
        }
    }

    /// <summary>pinned 缓冲与卡句柄的实际释放（仅在确认采集线程已退出后调用）</summary>
    private void FreeResources()
    {
        if (_ringPin.IsAllocated) _ringPin.Free();
        _ringBuffer = Array.Empty<short>();
        _ringPtr = IntPtr.Zero;

        if (_handle != IntPtr.Zero)
        {
            try { SpectrumNative.Close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            // RM-6 修复：仅从 Dispose() 调用时释放托管资源（Cleanup/线程/事件）。
            // 终结器线程不执行这些——防死锁、Timer 回调重入和线程 Join 阻塞终结器。
            Cleanup();
            if (!_cleanupDeferred)
                _frameEvent.Dispose();
            _state = DaqState.Disposed;   // H-2：生命周期状态机
        }
        // RM-6：终结器（disposing=false）仅关闭非托管句柄——不涉及锁、Timer 或线程操作。
        if (_handle != IntPtr.Zero && !_cleanupDeferred)
        {
            try
            {
                SpectrumNative.Close(_handle);
                _handle = IntPtr.Zero;
            }
            catch { /* 终结线程尽力而为 */ }
        }
    }

    // RM-6：Finalizer 仅兜底关闭非托管句柄（P/Invoke 终结器安全）。
    // 完整 Cleanup（线程 Join、DMA 停止、缓冲释放）由 Dispose 负责。
    ~SpectrumDaqCard() => Dispose(false);
}
