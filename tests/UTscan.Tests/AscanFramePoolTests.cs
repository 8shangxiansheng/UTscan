using System.Buffers;
using UTscan.Core;
using UTscan.Core.Models;
using Xunit;

namespace UTscan.Tests;

/// <summary>
/// AscanFramePool 池化不变性测试。
/// 覆盖需求：租用→填充→归还→复用不破坏数据；CloneForExternal 与池生命周期解耦；
/// 双重归还/空数组归还防护；池有界性；KPI 计数；数据真实性（PointCount 与数组长度解耦）。
/// </summary>
public class AscanFramePoolTests
{
    private const int SampleCount = 1024;

    // ── 租用→填充→归还→复用不破坏数据 ──

    [Fact]
    public void RentReturnReuse_ReusesPooledObject()
    {
        var pool = new AscanFramePool(samplePool: new NoOpArrayPool());

        var first = pool.RentFrame();
        first.Samples = pool.RentSamples(SampleCount);
        pool.ReturnFrame(first);

        var second = pool.RentFrame();
        Assert.Same(first, second);          // 对象复用：同一实例
        Assert.Equal(0, second.SampleRate);  // 字段已复位
        Assert.Equal(0, second.ChannelIndex);
        Assert.False(second.HasTimestamp);
    }

    [Fact]
    public void ReturnThenReuse_DoesNotCorruptExternallyClonedData()
    {
        var pool = new AscanFramePool(samplePool: new NoOpArrayPool());

        // 第一帧：租用→填充标记值→外部克隆→归还
        var f1 = pool.RentFrame();
        f1.Samples = pool.RentSamples(SampleCount);
        f1.PointCount = SampleCount;
        for (int i = 0; i < SampleCount; i++) f1.Samples[i] = 111f;
        var external = AscanFramePool.CloneForExternal(f1);   // 模拟 GetCurrentData 克隆
        pool.ReturnFrame(f1);

        // 第二帧：复用同一数组，填充不同值
        var f2 = pool.RentFrame();
        f2.Samples = pool.RentSamples(SampleCount);
        f2.PointCount = SampleCount;
        for (int i = 0; i < SampleCount; i++) f2.Samples[i] = 222f;

        // 外部克隆必须不受池复用影响（数据完整，不是被覆盖后的 222）
        Assert.Equal(SampleCount, external.Samples.Length);
        for (int i = 0; i < SampleCount; i++)
            Assert.Equal(111f, external.Samples[i]);
    }

    [Fact]
    public void CloneForExternal_IndependentArray()
    {
        var src = new AScanData
        {
            Samples = new float[] { 1, 2, 3 },
            SampleRate = 100e6f,
            ChannelIndex = 1,
            TimestampTicks = 42,
            HasTimestamp = true,
        };

        var clone = AscanFramePool.CloneForExternal(src);
        Assert.NotSame(src.Samples, clone.Samples);
        Assert.Equal(100e6f, clone.SampleRate);
        Assert.Equal(1, clone.ChannelIndex);
        Assert.Equal(42, clone.TimestampTicks);
        Assert.True(clone.HasTimestamp);

        // 修改克隆不影响源
        clone.Samples[0] = 99f;
        Assert.Equal(1f, src.Samples[0]);
    }

    [Fact]
    public void CloneForExternal_EmptyFrame_ReturnsEmpty()
    {
        var clone = AscanFramePool.CloneForExternal(new AScanData());
        Assert.Empty(clone.Samples);
    }

    // ── 防护：双重归还 / 空数组 / null ──

    [Fact]
    public void ReturnFrame_Twice_DoesNotDuplicateInPool()
    {
        var pool = new AscanFramePool(maxFrames: 8, samplePool: new NoOpArrayPool());
        var f = pool.RentFrame();
        f.Samples = pool.RentSamples(SampleCount);

        pool.ReturnFrame(f);
        int availableAfterFirst = pool.AvailableCount;

        pool.ReturnFrame(f);   // 二次归还：应整体跳过
        Assert.Equal(availableAfterFirst, pool.AvailableCount);
        Assert.Equal(1, pool.ReturnedCount);   // 只计一次
    }

    [Fact]
    public void ReturnFrame_NullOrEmptySamples_NoThrowAndNotPooled()
    {
        var pool = new AscanFramePool(maxFrames: 8, samplePool: new NoOpArrayPool());

        pool.ReturnFrame(null);
        pool.ReturnFrame(new AScanData());   // Samples=Array.Empty 哨兵：跳过
        pool.ReturnFrame(new AScanData { Samples = Array.Empty<float>() });

        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(0, pool.ReturnedCount);
    }

    // ── 有界性 ──

    [Fact]
    public void PoolIsBounded_ByMaxFrames()
    {
        var pool = new AscanFramePool(maxFrames: 4, samplePool: new NoOpArrayPool());
        var frames = new AScanData[8];
        for (int i = 0; i < frames.Length; i++)
        {
            var f = pool.RentFrame();
            f.Samples = pool.RentSamples(SampleCount);
            frames[i] = f;
        }
        foreach (var f in frames)
            pool.ReturnFrame(f);

        Assert.Equal(4, pool.AvailableCount);   // 有界：不超 maxFrames
    }

    // ── KPI 计数 ──

    [Fact]
    public void RentReturnCounters_TrackCalls()
    {
        var pool = new AscanFramePool(samplePool: new NoOpArrayPool());
        var f = pool.RentFrame();
        f.Samples = pool.RentSamples(SampleCount);

        Assert.Equal(1, pool.RentedCount);
        pool.ReturnFrame(f);
        Assert.Equal(1, pool.ReturnedCount);

        pool.ReturnFrame(f);   // 重复归还不计
        Assert.Equal(1, pool.ReturnedCount);
    }

    // ── 数据真实性：PointCount 与超长池化数组解耦 ──

    /// <summary>
    /// 测试用 ArrayPool：实际分配（不真复用），保证测试与 ArrayPool.Shared 全局池隔离。
    /// 池逻辑（对象复用/防重复/有界）仍被测到。
    /// </summary>
    private sealed class NoOpArrayPool : ArrayPool<float>
    {
        public override float[] Rent(int minimumLength) => new float[minimumLength];
        public override void Return(float[] array, bool clearArray = false) { }
    }

    /// <summary>
    /// 模拟 ArrayPool 桶上取整行为：返回长度 = 向上取整到 blockSize 的倍数
    /// （如请求 1016 得 1024）。同时校验归还的数组确实来自本池——
    /// 复现生产 ArrayPool 对"非本池数组"的拒绝（Resize 回归的抓鬼测试）。
    /// </summary>
    private sealed class OversizedArrayPool : ArrayPool<float>
    {
        private readonly int _blockSize;
        private readonly HashSet<float[]> _rented = new();
        public OversizedArrayPool(int blockSize = 1024) => _blockSize = blockSize;

        public override float[] Rent(int minimumLength)
        {
            var a = new float[((minimumLength + _blockSize - 1) / _blockSize) * _blockSize];
            _rented.Add(a);
            return a;
        }

        public override void Return(float[] array, bool clearArray = false)
        {
            // 非本池归还 → 抛与生产 ArrayPool 同类的异常（Resize 回归即在此暴露）
            if (array is null || !_rented.Remove(array))
                throw new ArgumentException("The buffer is not associated with this pool and may not be returned to it.", nameof(array));
        }
    }

    [Fact]
    public void RentSamples_KeepsOversizedArray_PoolReturnSafe()
    {
        // 关键回归测试：RentSamples 不得 Resize 裁剪（否则新数组非本池、归还抛异常）。
        // 超长数组必须能安全还回本池。
        var pool = new AscanFramePool(samplePool: new OversizedArrayPool());
        const int logical = 1016;   // 非桶边界 → 池返回 1024
        var samples = pool.RentSamples(logical);
        Assert.True(samples.Length >= logical);   // 保留超长数组（≥ 请求长度）
        pool.ReturnSamples(samples);              // 不抛异常 = 池关联有效
    }

    [Fact]
    public void PooledFrame_PointCount_IsExplicitAndClonedBound()
    {
        // 端到端不变式：逻辑采样点数由 PointCount 显式记录（与超长数组解耦）；
        // 克隆结果 PointCount == 逻辑长度（尾部多余空间不进入克隆）。
        var pool = new AscanFramePool(samplePool: new OversizedArrayPool());
        const int logical = 1016;
        var frame = pool.RentFrame();
        frame.Samples = pool.RentSamples(logical);
        frame.PointCount = logical;               // 采集路径显式设置
        frame.SampleRate = 100e6f;
        Assert.True(frame.Samples.Length >= logical);   // 数组可超长
        Assert.Equal(logical, frame.PointCount);        // 逻辑点数精确

        var clone = AscanFramePool.CloneForExternal(frame);
        Assert.Equal(logical, clone.PointCount);
        Assert.Equal(logical, clone.Samples.Length);    // 克隆精确到逻辑长度
        Assert.Equal(logical, clone.GetTimeAxis().Length);
    }

    [Fact]
    public void PooledFrame_PointCount_DefaultsToArrayLength()
    {
        // 非池化路径（测试/模拟数据）：未显式设置 PointCount 时，跟随 Samples.Length
        var d = new AScanData { Samples = new float[512], SampleRate = 100e6f };
        Assert.Equal(512, d.PointCount);
        Assert.Equal(512, AscanFramePool.CloneForExternal(d).PointCount);
    }

    [Fact]
    public void ReturnFrame_ResetsPointCount()
    {
        var pool = new AscanFramePool(samplePool: new NoOpArrayPool());
        var f = pool.RentFrame();
        f.Samples = pool.RentSamples(SampleCount);
        f.PointCount = SampleCount;
        pool.ReturnFrame(f);

        var reused = pool.RentFrame();
        Assert.Equal(reused.Samples.Length, reused.PointCount);   // 复位为"跟随数组长度"哨兵语义
    }
}
