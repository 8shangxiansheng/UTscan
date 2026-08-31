namespace UTscan.Core.Models;

/// <summary>
/// A扫数据
/// </summary>
public class AScanData
{
    /// <summary>
    /// 采样点电压值。池化路径下此数组长度可能 &gt;= 逻辑采样点数（ArrayPool 按桶上取整），
    /// 实际采样点数以 <see cref="PointCount"/> 为准；数组多余尾部不作为有效数据。
    /// </summary>
    public float[] Samples { get; set; } = Array.Empty<float>();

    /// <summary>采样率（Hz）</summary>
    public float SampleRate { get; set; }

    private int _pointCount = -1;

    /// <summary>
    /// 逻辑采样点数。默认等于 Samples.Length；采集路径（池化）显式设置为真实采样点数，
    /// 与超长池化数组解耦——杜绝"波形尾部虚假零值/时间轴拉长"的数据真实性缺陷。
    /// </summary>
    public int PointCount
    {
        get => _pointCount >= 0 ? _pointCount : Samples.Length;
        set => _pointCount = value;
    }

    /// <summary>
    /// 通道索引（0 基）。多通道采集时每通道各产生一帧 A 扫数据
    /// （Spectrum M3i.3242 双通道支持，2026-08-18 整改新增）。
    /// </summary>
    public int ChannelIndex { get; set; }

    /// <summary>
    /// 硬件时间戳（采样时钟 tick 数，64 位计数器自启动/复位起累计）。
    /// 由采集卡时间戳单元在每次触发事件时锁存（SPC_TIMESTAMP_CMD 标准模式），
    /// 用于编码器触发扫查的精确位置关联。无效/不可用时 HasTimestamp=false。
    /// </summary>
    public long TimestampTicks { get; set; }

    /// <summary>时间戳是否有效（时间戳选项未安装或未启用时为 false）</summary>
    public bool HasTimestamp { get; set; }

    /// <summary>
    /// P0-2-FIX：触发前偏移（µs），即 PRETRIGGER 样本对应的采样时间。
    /// samples[0] 对应时刻 = −TriggerOffsetUs（触发前），而非 0。
    /// 换算时间轴/闸门/深度/游标时统一减去此值，使 t=0 对应触发时刻。
    /// 采集侧在帧构造时填入（pre×dt），显示/测量侧在 dt 换算时使用。
    /// </summary>
    public float TriggerOffsetUs { get; set; }

    /// <summary>时间戳换算为纳秒（相对采集启动；无效时为 0）</summary>
    public double TimestampNs => HasTimestamp && SampleRate > 0
        ? TimestampTicks / (double)SampleRate * 1e9
        : 0;

    /// <summary>时间轴（μs）。P0-2-FIX：samples[0] 对应时刻 = −TriggerOffsetUs，
    /// 第 i 点时刻 = i×dt − TriggerOffsetUs，使触发时刻为 t=0。</summary>
    public float[] GetTimeAxis()
    {
        var time = new float[PointCount];
        float dt = SampleRate > 0 ? 1e6f / SampleRate : 0f; // 转换为μs
        for (int i = 0; i < PointCount; i++)
            time[i] = i * dt - TriggerOffsetUs;
        return time;
    }

    /// <summary>最大值（按逻辑采样点数；Samples 为空/越界时返回 0 防越界）</summary>
    public float Max
    {
        get
        {
            // IO7-FIX（审查 20260828）：PointCount>0 但 Samples 为空（异常构造帧）时
            // Samples[0] 越界——上限取实际数组长度，为空返回 0。
            int n = Math.Min(PointCount, Samples.Length);
            if (n <= 0) return 0f;
            float m = Samples[0];
            for (int i = 1; i < n; i++)
                if (Samples[i] > m) m = Samples[i];
            return m;
        }
    }

    /// <summary>最小值（按逻辑采样点数；Samples 为空/越界时返回 0 防越界）</summary>
    public float Min
    {
        get
        {
            int n = Math.Min(PointCount, Samples.Length);
            if (n <= 0) return 0f;
            float m = Samples[0];
            for (int i = 1; i < n; i++)
                if (Samples[i] < m) m = Samples[i];
            return m;
        }
    }
}
