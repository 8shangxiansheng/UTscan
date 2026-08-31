using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Mock;

/// <summary>
/// 模拟运动控制器（无硬件时使用）
/// </summary>
public class MockMotionController : IMotionController
{
    private readonly object _lock = new();
    private readonly float[] _positions = new float[5];
    private readonly float[] _targetPositions = new float[5];
    private readonly float[] _speeds = new float[5];   // L-6：每轴当前运动速度（mm/s）
    private readonly bool[] _axisEnabled = new bool[5];
    private bool _isConnected;
    private readonly System.Timers.Timer _updateTimer;

    public bool IsConnected => _isConnected;
    public event EventHandler<AxisPositionChangedEventArgs>? PositionChanged;

    public MockMotionController()
    {
        // Mock 语义：轴默认伺服开启（真实控制器需 EnableAxisAsync，
        // Mock 面向无硬件开发/演示，默认可动；DisableAxisAsync 仍可关断）
        for (int i = 0; i < _axisEnabled.Length; i++) _axisEnabled[i] = true;

        _updateTimer = new System.Timers.Timer(50);
        _updateTimer.Elapsed += (s, e) => UpdatePositions();
    }

    public Task<bool> ConnectAsync(ConnectionConfig config)
    {
        lock (_lock) { _isConnected = true; }
        _updateTimer.Start();
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        _updateTimer.Stop();
        lock (_lock) { _isConnected = false; }
        return Task.CompletedTask;
    }

    public Task<bool> EnableAxisAsync(AxisId axis)
    {
        lock (_lock) { _axisEnabled[(int)axis] = true; }
        return Task.FromResult(true);
    }

    public Task<bool> DisableAxisAsync(AxisId axis)
    {
        lock (_lock) { _axisEnabled[(int)axis] = false; }
        return Task.FromResult(true);
    }

    public Task MoveAbsoluteAsync(AxisId axis, float position, ScanParams parameters)
    {
        lock (_lock)
        {
            _targetPositions[(int)axis] = position;
            _speeds[(int)axis] = parameters.Velocity > 0 ? parameters.Velocity : 10f;   // L-6：记录该轴运动速度
        }
        return Task.CompletedTask;
    }

    public Task MoveRelativeAsync(AxisId axis, float distance, ScanParams parameters)
    {
        lock (_lock)
        {
            _targetPositions[(int)axis] += distance;
            _speeds[(int)axis] = parameters.Velocity > 0 ? parameters.Velocity : 10f;
        }
        return Task.CompletedTask;
    }

    public Task HomeAsync(AxisId axis)
    {
        lock (_lock) { _targetPositions[(int)axis] = 0; }
        return Task.CompletedTask;
    }

    public Task StopAsync(AxisId axis)
    {
        lock (_lock) { _targetPositions[(int)axis] = _positions[(int)axis]; }
        return Task.CompletedTask;
    }

    public Task EmergencyStopAsync()
    {
        lock (_lock)
        {
            for (int i = 0; i < 5; i++)
                _targetPositions[i] = _positions[i];
        }
        return Task.CompletedTask;
    }

    public float GetPosition(AxisId axis)
    {
        lock (_lock) { return _positions[(int)axis]; }
    }

    /// <summary>Mock 需求位置 = 当前目标位置（模拟 DPOS，无跟随误差时与 MPOS 一致）</summary>
    public float GetDemandPosition(AxisId axis)
    {
        lock (_lock) { return _targetPositions[(int)axis]; }
    }

    /// <summary>Mock 无硬件软限位，返回大值使扫查区域校验不误报。</summary>
    public float GetForwardSoftLimit(AxisId axis) => 10000f;

    /// <summary>Mock 无硬件软限位。</summary>
    public float GetReverseSoftLimit(AxisId axis) => -10000f;

    public bool IsAxisIdle(AxisId axis)
    {
        lock (_lock)
        {
            int i = (int)axis;
            return Math.Abs(_positions[i] - _targetPositions[i]) < 0.001f;
        }
    }

    /// <summary>Mock 无硬件，连续插补为空操作</summary>
    public void SetContinuousInterpolation(AxisId axis, bool enable) { }

    /// <summary>P0-D：Mock 轴置零（当前位置与目标同步归 0）</summary>
    public void SetPositionZero(AxisId axis)
    {
        lock (_lock)
        {
            int i = (int)axis;
            _positions[i] = 0f;
            _targetPositions[i] = 0f;
        }
    }

    /// <summary>H-1：Mock 触发输出（无硬件，记录后保持 pulseWidthMs 再释放）</summary>
    public async Task PulseTriggerOutputAsync(int io, int pulseWidthMs, CancellationToken ct = default)
    {
        if (pulseWidthMs < 1) pulseWidthMs = 1;
        await Task.Delay(pulseWidthMs, ct);
    }

    private void UpdatePositions()
    {
        // L-6：按速度换算每 tick 步长（原固定 0.5mm/tick ≈ 10mm/s 与参数无关）。
        // 定时器 50ms ⇒ 每 tick 步长 = speed × 0.05s。
        const float tickSeconds = 0.05f;
        for (int i = 0; i < 5; i++)
        {
            float step;
            lock (_lock)
            {
                if (!_axisEnabled[i]) continue;
                float diff = _targetPositions[i] - _positions[i];
                if (Math.Abs(diff) < 0.001f) continue;

                float speed = _speeds[i] > 0 ? _speeds[i] : 10f;
                step = Math.Sign(diff) * Math.Min(Math.Abs(diff), speed * tickSeconds);
                _positions[i] += step;
            }

            PositionChanged?.Invoke(this, new AxisPositionChangedEventArgs
            {
                Axis = (AxisId)i,
                Position = GetPosition((AxisId)i),
                Velocity = step * (1f / tickSeconds)
            });
        }
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── P5 诊断契约 ──
    public string LastConnectError => "";
}
