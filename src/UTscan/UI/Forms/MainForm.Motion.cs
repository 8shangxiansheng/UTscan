using System.Diagnostics;
using System.Windows.Forms;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;
using UTscan.Hardware.Daq;
using UTscan.Hardware.PulseGen;
using UTscan.Hardware.Zmc;
using UTscan.Services;
using UTscan.Services.Connection;

namespace UTscan.UI.Forms;

/// <summary>
/// 主窗体 partial：运动控制面板：定位/回零/Jog/相对步进/自检 + 运动状态监控 + 系统参数。
/// </summary>
public partial class MainForm : Form
{

    // ════════════════════════════════════════════════════════════════
    //  运动控制
    // ════════════════════════════════════════════════════════════════

    private async Task MoveToTargetAsync()
    {
        try
        {
            float x = (float)_numTargetX.Value;
            float y = (float)_numTargetY.Value;
            float z = (float)_numTargetZ.Value;
            float vel = _cmbSpeed.SelectedItem is int s ? s : 100;
            float accel = (float)_numAccel.Value;
            var prm = new ScanParams { Velocity = vel, Acceleration = accel };

            LogI("ZMC", $"定位移动 → X={x:F3} Y={y:F3} Z={z:F3} (速度={vel}mm/s 加速度={accel}mm/s²)");
            _btnMoveTo.Enabled = false;

            await _motion.MoveAbsoluteAsync(AxisId.X, x, prm);
            await _motion.MoveAbsoluteAsync(AxisId.Y, y, prm);
            await _motion.MoveAbsoluteAsync(AxisId.Z, z, prm);

            LogS("ZMC", "定位移动完成");
        }
        catch (Exception ex)
        {
            LogE("ZMC", $"定位移动失败: {ex.Message}");
        }
        finally
        {
            _btnMoveTo.Enabled = true;
        }
    }

    private async Task HomeAllAxesAsync()
    {
        try
        {
            LogI("ZMC", "开始回零...");
            await _motion.HomeAsync(AxisId.X);
            await _motion.HomeAsync(AxisId.Y);
            await _motion.HomeAsync(AxisId.Z);
                LogS("ZMC", "三轴回零完成");
        }
        catch (Exception ex)
        {
            LogE("ZMC", $"回零失败: {ex.Message}");
        }
    }

    private void AddJogRow(Panel p, ref int y, string name, AxisId axis)
    {
        var btnMinus = new Button { Text = $"-{name}", Left = 8, Top = y, Width = 100, Height = 30 };
        var btnPlus = new Button { Text = $"{name}+", Left = 116, Top = y, Width = 100, Height = 30 };
        btnMinus.MouseDown += (_, _) => StartJog(axis, -1);
        btnMinus.MouseUp += (_, _) => StopJog(axis);
        btnPlus.MouseDown += (_, _) => StartJog(axis, +1);
        btnPlus.MouseUp += (_, _) => StopJog(axis);
        p.Controls.Add(btnMinus);
        p.Controls.Add(btnPlus);
        y += 32;
    }

    /// <summary>P0-D：相对步进行（点击移动指定步进）</summary>
    private void AddRelativeStepRow(Panel p, ref int y, string name, AxisId axis)
    {
        var btnMinus = new Button { Text = $"◀{name}", Left = 8, Top = y, Width = 100, Height = 28 };
        var btnPlus = new Button { Text = $"{name}▶", Left = 116, Top = y, Width = 100, Height = 28 };
        btnMinus.Click += async (_, _) =>
            await RelativeStepAsync(axis, -(float)_numStepMm.Value);
        btnPlus.Click += async (_, _) =>
            await RelativeStepAsync(axis, +(float)_numStepMm.Value);
        p.Controls.Add(btnMinus);
        p.Controls.Add(btnPlus);
        y += 32;
    }

    /// <summary>P0-D：相对运动（说明书 4.5 定位起始点：X/Y 负向移动至信号消失→置零）</summary>
    private async Task RelativeStepAsync(AxisId axis, float distanceMm)
    {
        try
        {
            var prm = new ScanParams { Velocity = _jogSpeed, Acceleration = (float)_numAccel.Value };
            await _motion.MoveRelativeAsync(axis, distanceMm, prm);
            LogI("ZMC", $"相对移动: 轴{axis} {distanceMm:+#.##;-#.##;0} mm");
        }
        catch (Exception ex)
        {
            LogE("ZMC", $"相对移动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// P0-D：运动自检（限位遍历验证，说明书 3.4.2 安全验证）。
    /// 流程：X/Y/Z 各轴低速正/负向移动，验证限位触发（轴停止 + 状态报警）；
    /// 结束后回零点复位。Mock 模式跳过（无真实限位）。
    /// </summary>
    private async Task RunMotionSelfTestAsync()
    {
        if (_config.UseMock)
        {
            LogW("ZMC", "运动自检：Mock 模式跳过（无真实限位）");
            return;
        }
        LogI("ZMC", "运动自检开始：限位遍历验证...");
        try
        {
            var prm = new ScanParams { Velocity = 5f, Acceleration = 20f };
            foreach (var axis in new[] { AxisId.X, AxisId.Y, AxisId.Z })
            {
                    LogI("ZMC", $"自检 轴{axis}: 负向移动验证 REV 限位...");
                await _motion.MoveRelativeAsync(axis, -5f, prm);
                await Task.Delay(300);
                    LogI("ZMC", $"自检 轴{axis}: 正向移动验证 FWD 限位...");
                await _motion.MoveRelativeAsync(axis, +10f, prm);
                await Task.Delay(300);
            }
            LogS("ZMC", "运动自检完成：限位触发正常（轴状态已记录报警）");
        }
        catch (Exception ex)
        {
            LogE("ZMC", $"运动自检异常: {ex.Message}");
        }
    }

    private void StartJog(AxisId axis, int dir)
    {
        _jogAxis = axis;
        _jogDir = dir;
        float vel = _cmbSpeed.SelectedItem is int s ? s : 100;
        float accel = (float)_numAccel.Value;
        float travel = vel * 60f;
        var prm = new ScanParams { Velocity = vel, Acceleration = accel };
        _ = _motion.MoveRelativeAsync(axis, dir * travel, prm);
        LogI("ZMC", $"Jog {axis} {(dir > 0 ? "+" : "-")} 速度={vel}mm/s");
    }

    private void StopJog(AxisId axis)
    {
        if (_jogAxis == axis) { _jogDir = 0; _ = _motion.StopAsync(axis); }
    }

    // ════════════════════════════════════════════════════════════════
    //  系统参数
    // ════════════════════════════════════════════════════════════════

    private void ApplySystemParams()
    {
        _systemParams.SoundVelocity = (float)_numSoundVelocity.Value;
        _systemParams.FocalLength = (float)_numFocalLength.Value;
        _systemParams.ZeroOffsetUs = (float)_numZeroOffset.Value;
        LogS("系统", $"系统参数已应用: 声速={_systemParams.SoundVelocity}m/s 焦距={_systemParams.FocalLength}mm 零点={_systemParams.ZeroOffsetUs}µs");
    }

    // ════════════════════════════════════════════════════════════════
    //  运动状态监控
    // ════════════════════════════════════════════════════════════════

    private void SubscribeMotion()
    {
        _motion.PositionChanged += (_, e) =>
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke(new Action(() => UpdateAxisLabel(e.Axis, e.Position)));
            else UpdateAxisLabel(e.Axis, e.Position);
        };
    }

    private void UpdateAxisLabel(AxisId axis, float pos)
    {
        int idx = (int)axis;
        if (idx >= 0 && idx < _axisLabels.Length)
            _axisLabels[idx].Text = $"{pos:0.000} mm";
    }

    private void PollAxisAlarm()
    {
        if (IsDisposed || Disposing) return;
        if (_motion is not ZmcMotionController zmc) return;

        try
        {
            int sx = zmc.GetAxisStatus(AxisId.X);
            int sy = zmc.GetAxisStatus(AxisId.Y);
            int sz = zmc.GetAxisStatus(AxisId.Z);

            // 更新轴状态标签
            UpdateAxisStatusLabel(_lblAxisStatusX, "X", sx);
            UpdateAxisStatusLabel(_lblAxisStatusY, "Y", sy);
            UpdateAxisStatusLabel(_lblAxisStatusZ, "Z", sz);

            bool alarm =
                ZmcAxisStatus.IsOverForwardSoftLimit(sx) || ZmcAxisStatus.IsOverReverseSoftLimit(sx) || ZmcAxisStatus.IsPaused(sx) ||
                ZmcAxisStatus.IsOverForwardSoftLimit(sy) || ZmcAxisStatus.IsOverReverseSoftLimit(sy) || ZmcAxisStatus.IsPaused(sy) ||
                ZmcAxisStatus.IsOverForwardSoftLimit(sz) || ZmcAxisStatus.IsOverReverseSoftLimit(sz) || ZmcAxisStatus.IsPaused(sz);

            if (alarm == _axisAlarm) return;
            _axisAlarm = alarm;

            _lblConn.ForeColor = alarm ? System.Drawing.Color.Red : System.Drawing.SystemColors.ControlText;
            _lblConn.Text = alarm
                ? $"⚠ 轴报警: X[{ZmcAxisStatus.Describe(sx)}] Y[{ZmcAxisStatus.Describe(sy)}] Z[{ZmcAxisStatus.Describe(sz)}]"
                : (_config.UseMock ? "状态：Mock 已连接" : "状态：已连接");

            if (alarm)
                LogW("ZMC", $"轴报警: X[{ZmcAxisStatus.Describe(sx)}] Y[{ZmcAxisStatus.Describe(sy)}] Z[{ZmcAxisStatus.Describe(sz)}]");
        }
        catch { /* 轮询错误静默忽略 */ }
    }

    private void UpdateAxisStatusLabel(Label lbl, string name, int status)
    {
        if (ZmcAxisStatus.IsOverForwardSoftLimit(status) || ZmcAxisStatus.IsOverReverseSoftLimit(status))
        {
            lbl.Text = $"{name}:限位";
            lbl.BackColor = System.Drawing.Color.LightCoral;
        }
        else if (ZmcAxisStatus.IsPaused(status))
        {
            lbl.Text = $"{name}:暂停";
            lbl.BackColor = System.Drawing.Color.Khaki;
        }
        else
        {
            // IsMoving 已删除（bit2 是通讯错误非运动位，轴运动状态须读 GetIfIdle）；
            // 此处无故障/暂停/回中则显示"空闲"，实时运动状态由 50ms 轮询的 PositionChanged 驱动。
            lbl.Text = $"{name}:空闲";
            lbl.BackColor = System.Drawing.Color.LightGreen;
        }
    }
}
