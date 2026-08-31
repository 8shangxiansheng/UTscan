namespace UTscan.Core.Models;

/// <summary>
/// 连接配置
/// </summary>
public class ConnectionConfig
{
    /// <summary>
    /// 默认采样点数。所有"回退 1024"的硬编码统一替换为此常量引用，
    /// 一处修改全局同步，无需在 9 个文件中逐一改数字。
    /// </summary>
    public const int DefaultSampleCount = 1024;
    /// <summary>IP地址</summary>
    public string IpAddress { get; set; } = "192.168.0.11";

    /// <summary>端口号</summary>
    public int Port { get; set; } = 502;

    /// <summary>串口名称</summary>
    public string SerialPort { get; set; } = "COM1";

    /// <summary>波特率（DPR500 标准为 4800）</summary>
    public int BaudRate { get; set; } = 4800;

    /// <summary>超时时间（ms）</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>采样率（Hz）—— 采集卡/Mock 使用。默认 100 MHz 与硬件（M3i.3242）一致；
    /// 历史默认 100 Hz 属 Mock 量级，与 DaqParams.SampleRate（100e6）不一致，易致单位陷阱（审查 M-9/L-2）</summary>
    public float SampleRate { get; set; } = 100e6f;

    /// <summary>采样点数 —— 采集卡/Mock 使用（默认 DefaultSampleCount）</summary>
    public int SampleCount { get; set; } = DefaultSampleCount;

    /// <summary>是否使用Mock模式。默认 false（真机模式）——部署到工控机后自动以真实硬件运行，
    /// 程序启动即自动识别并连接采集卡/脉冲收发仪；运动控制器是否参与由 EnableMotionController 控制。</summary>
    public bool UseMock { get; set; } = false;

    /// <summary>
    /// 是否启用 ZMC 运动控制器。false 时保留运动控制接口和扫描代码，但启动探测、自动连接、
    /// 轴使能及关闭期 ZMC 调用全部跳过，便于先独立联调 DPR500 + Spectrum。
    /// </summary>
    public bool EnableMotionController { get; set; } = false;

    /// <summary>
    /// H-1：ZMC 单次触发输出 IO 口编号（驱动 DPR500 External Trigger Input）。
    /// 必须按现场接线确定，禁止在代码中猜测；不得复用轴使能占用的 IO0/3/10/11/12。
    /// 默认 -1 表示未配置——ScanService 据此拒绝严格单次触发扫描。
    /// </summary>
    public int TriggerIo { get; set; } = -1;

    /// <summary>H-1：单次触发脉冲高电平保持时间（ms），默认 5ms。</summary>
    public int TriggerPulseWidthMs { get; set; } = 5;
}
