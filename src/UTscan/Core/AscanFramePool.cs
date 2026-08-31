using System.Buffers;
using UTscan.Core.Models;

namespace UTscan.Core;

/// <summary>
/// A 扫帧缓冲池（实时性支撑）。
/// 在采集侧复用 `float[Samples]` 与 <see cref="AScanData"/>，消除高 PRF 下每帧
/// new float[] + new AScanData 的 GC 分配与卡顿。
///
/// 所有权契约（保证正确性）：
///  - 采集线程 Rent → 填充 → 发布（内部/经 DataReady，订阅方须即时 Clone）；
///  - 被取代的旧帧 Return 回收；
///  - 任何可能被外部长期持有的读取（GetCurrentData）一律经 <see cref="CloneForExternal"/>
///    取得独立克隆，杜绝调用方持有已回收数组。
/// 池本身仅由采集线程访问，无需加锁。
/// </summary>
public sealed class AscanFramePool
{
    private readonly ArrayPool<float> _samplePool;
    private readonly Stack<AScanData> _frames = new();
    private readonly int _maxFrames;
    private long _rentCount, _returnCount;   // KPI：池租借/归还累计（Interlocked 读写）

    /// <summary>累计租借帧数（KPI/测试）</summary>
    public long RentedCount => Interlocked.Read(ref _rentCount);

    /// <summary>累计归还帧数（KPI/测试）</summary>
    public long ReturnedCount => Interlocked.Read(ref _returnCount);

    /// <summary>当前池内可用对象数</summary>
    public int AvailableCount => _frames.Count;

    public AscanFramePool(int maxFrames = 64, ArrayPool<float>? samplePool = null)
    {
        _maxFrames = maxFrames;
        _samplePool = samplePool ?? ArrayPool<float>.Shared;
    }

    /// <summary>
    /// 从池租借采样数组。ArrayPool 可能返回长度 &gt; minLength 的数组（按桶上取整），
    /// 直接返回——逻辑采样点数由 AScanData.PointCount 记录，数组尾部多余空间不作为有效数据。
    /// 严禁 Array.Resize 裁剪：Resize 分配的新数组不属于本池，归还时抛
    /// "The buffer is not associated with this pool"（v2.1.1 回归，本版修复）。
    /// </summary>
    public float[] RentSamples(int minLength) => _samplePool.Rent(minLength);

    /// <summary>归还采样数组到池（清空防止数据残留被误读）</summary>
    public void ReturnSamples(float[] samples)
    {
        if (samples is { Length: > 0 })
            _samplePool.Return(samples, clearArray: true);
    }

    /// <summary>从池取 AScanData（无则新建）</summary>
    public AScanData RentFrame()
    {
        Interlocked.Increment(ref _rentCount);
        return _frames.Count > 0 ? _frames.Pop() : new AScanData();
    }

    /// <summary>
    /// 归还整个帧（采样数组 + 对象）到池。调用方须确保不再持有该帧引用。
    /// 双重归还防护：采样数组已清空（Length==0）视为已归还/哨兵，整体跳过——
    /// 防止同一对象被二次入池导致池内重复引用（双持有者破坏发布不变式）。
    /// </summary>
    public void ReturnFrame(AScanData f)
    {
        if (f is null || f.Samples is not { Length: > 0 }) return;   // 空/已归还/哨兵：跳过
        _samplePool.Return(f.Samples, clearArray: true);
        f.Samples = Array.Empty<float>();
        f.SampleRate = 0;
        f.ChannelIndex = 0;
        f.TimestampTicks = 0;
        f.HasTimestamp = false;
        f.TriggerOffsetUs = 0f;   // P0-2：归还复位时间原点
        f.PointCount = -1;   // 复位为"跟随 Samples.Length"（哨兵语义）
        if (_frames.Count < _maxFrames)   // 有界池，防无限增长
            _frames.Push(f);
        Interlocked.Increment(ref _returnCount);
    }

    /// <summary>
    /// 生成供外部长期持有的独立克隆（拷贝逻辑采样点数对应的数组段），与池生命周期解耦。
    /// 空帧返回新的空 AScanData。PointCount 与 Samples 长度永远一致（克隆结果精确）。
    /// </summary>
    public static AScanData CloneForExternal(AScanData src)
    {
        if (src is null || src.Samples.Length == 0 || src.PointCount <= 0)
            return new AScanData();
        int n = Math.Min(src.PointCount, src.Samples.Length);
        var copy = new float[n];
        Array.Copy(src.Samples, copy, n);
        return new AScanData
        {
            Samples = copy,
            PointCount = n,
            SampleRate = src.SampleRate,
            ChannelIndex = src.ChannelIndex,
            TimestampTicks = src.TimestampTicks,
            HasTimestamp = src.HasTimestamp,
            TriggerOffsetUs = src.TriggerOffsetUs,   // P0-2：克隆保留时间原点
        };
    }
}
