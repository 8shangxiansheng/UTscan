using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Mock;

/// <summary>
/// 模拟数据采集卡（无硬件时使用）
/// </summary>
public class MockDaqCard : IDataAcquisition
{
    private readonly object _lock = new();
    private bool _isRunning;
    private AScanData _currentData = new();
    private readonly System.Timers.Timer _dataTimer;
    private volatile int _sampleCount = ConnectionConfig.DefaultSampleCount;
    private volatile float _sampleRate = 100f;

    public bool IsRunning => _isRunning;
    public bool NeedsReinitialize => false; // Mock 永远不需要重初始化
    public event EventHandler<AScanDataEventArgs>? DataReady;

    public MockDaqCard()
    {
        _dataTimer = new System.Timers.Timer(100);
        // System.Timers.Timer 回调中的异常默认被吞掉，捕获并输出便于诊断（审查 P3-10）。
        _dataTimer.Elapsed += (s, e) =>
        {
            try { GenerateData(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MockDaqCard GenerateData 异常: {ex.Message}"); }
        };
    }

    public Task<bool> InitializeAsync(ConnectionConfig config)
    {
        _sampleCount = config.SampleCount > 0 ? config.SampleCount : ConnectionConfig.DefaultSampleCount;
        // M-9：默认采样率与硬件一致（100 MHz）。原默认 100 Hz 与真实卡差 6 个数量级，
        // hardware.json 缺 sampleRate 字段时 Mock 波形时间轴会错误（dt=10000μs）。
        _sampleRate = config.SampleRate > 0 ? config.SampleRate : 100e6f;
        _currentData = new AScanData { SampleRate = _sampleRate };
        return Task.FromResult(true);
    }

    public Task StartContinuousAsync()
    {
        _isRunning = true;
        _dataTimer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _isRunning = false;
        _dataTimer.Stop();
        return Task.CompletedTask;
    }

    public Task ResetAsync()
    {
        _isRunning = false;
        _dataTimer.Stop();
        _frameCounter = 0;
        _frameEvent.Reset();
        return Task.CompletedTask;
    }

    public AScanData GetCurrentData()
    {
        lock (_lock) { return _currentData; }
    }

    public AScanData GetCurrentData(int channel) => GetCurrentData();

    // H-4：帧同步（与真机 SpectrumDaqCard 语义一致）
    private long _frameCounter;
    private readonly System.Threading.ManualResetEventSlim _frameEvent = new(false);

    public long GetCurrentFrameCount() => Interlocked.Read(ref _frameCounter);

    public async Task<bool> WaitForNewFrameAsync(int timeoutMs, CancellationToken ct = default)
    {
        long startCount = GetCurrentFrameCount();
        // Mock 未运行时立即成功（Mock 数据视为总是新鲜；真机外触发缺失由 Spectrum 超时模拟）
        if (!_isRunning) return true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (GetCurrentFrameCount() <= startCount)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            // 真正异步让出（替代原同步 _frameEvent.Wait）
            try { await Task.Delay(10, ct); }
            catch (OperationCanceledException) { throw; }
        }
        return true;
    }

    // H-1：严格单次触发帧同步——等待帧计数超过触发前基线（不重新抓基线）
    public async Task<bool> WaitForFrameAfterAsync(long baseline, int timeoutMs, CancellationToken ct = default)
    {
        // RH-3 契约：采集未运行时返回失败（与真机 SpectrumDaqCard 语义一致，不误报帧就绪）
        if (!_isRunning) return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (GetCurrentFrameCount() <= baseline)
        {
            ct.ThrowIfCancellationRequested();
            if (!_isRunning) return false;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            try { await Task.Delay(2, ct); }
            catch (OperationCanceledException) { throw; }
        }
        return true;
    }

    private void GenerateData()
    {
        var samples = new float[_sampleCount];
        double t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        for (int i = 0; i < _sampleCount; i++)
        {
            double x = (double)i / _sampleCount;
            // 模拟多频正弦波 + 衰减 + 噪声
            double signal =
                Math.Sin(2 * Math.PI * 5 * x + t * 3) * 0.5 +
                Math.Sin(2 * Math.PI * 15 * x + t * 7) * 0.3 +
                Math.Sin(2 * Math.PI * 30 * x + t * 2) * 0.2;

            // 衰减包络
            double envelope = Math.Exp(-3 * x);
            signal *= envelope;

            // 添加噪声（Random.Shared 线程安全，.NET 6+）
            double noise = (Random.Shared.NextDouble() - 0.5) * 0.1;
            samples[i] = (float)(signal + noise);
        }

        var newData = new AScanData
        {
            Samples = samples,
            SampleRate = _sampleRate
        };

        lock (_lock) { _currentData = newData; }

        // H-4：帧计数递增 + 通知等待者（扫查服务帧同步）
        Interlocked.Increment(ref _frameCounter);
        _frameEvent.Set();

        DataReady?.Invoke(this, new AScanDataEventArgs { Data = newData });
    }

    public void Dispose()
    {
        _dataTimer?.Dispose();
        _frameEvent.Dispose();   // H-4：释放帧事件
        GC.SuppressFinalize(this);
    }

    // ── P5 诊断契约 ──
    public int EnabledChannelCount => 1;   // Mock 固定单通道
    public string DescribeState() => "MockDaqCard (state=Running, everInit=true, running=IsRunning)";
    public DaqKpiSnapshot GetKpis() => new(0, 0, 0, 0, 0, 0, 0, 0);
    public string LastConnectError => "";
}
