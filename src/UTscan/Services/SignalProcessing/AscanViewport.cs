using System;

namespace UTscan.Services.SignalProcessing;

/// <summary>
/// A 扫纵轴（电压）稳定标度引擎。
/// 解决"波形每帧按 min/max 自适应全屏缩放"带来的两个显示缺陷：
///  1. 抖动——每帧重新按瞬时 min/max 缩放，噪声使显示增益逐帧跳动；
///  2. 波形不随样品变化——按每帧最大值归一化到满屏，把不同材质的回波幅值差异抹平。
/// 本类用"快攻（遇更强信号立即放大防削顶） + 慢释放（微弱信号缓慢收敛）"的迟滞标度，
/// 使纵轴稳定不抖动，同时保留不同样品回波幅值/时间的真实差异。
/// </summary>
public class AscanViewport
{
    /// <summary>快攻系数：当信号峰值超过当前显示量程时，立即放大到不削顶（0~1，越大反应越快）。</summary>
    public float AttackFactor { get; set; } = 0.5f;

    /// <summary>慢释放系数：无更强信号时，显示量程以该比例缓慢向信号峰值收敛（0~1，越小越稳定）。</summary>
    public float ReleaseFactor { get; set; } = 0.02f;

    /// <summary>当前稳定的纵轴峰值（V），即 +(−)满量程。初始化为极小值，由首次信号建立。</summary>
    public float DisplayPeakV { get; private set; } = 0f;

    /// <summary>顶部/底部显示余量比例（相对半量程，参考原实现 8%）。</summary>
    public float HeadroomRatio { get; set; } = 0.08f;

    /// <summary>
    /// 手动纵轴半量程（V）。非 null 时优先于迟滞自动标度（鼠标 Ctrl+滚轮幅值缩放），
    /// 设为 null 恢复自动标度。重标（Reset）会清空。
    /// </summary>
    public float? ManualHalfScale { get; set; } = null;

    /// <summary>
    /// 用当前帧的绝对峰值电压更新纵轴标度，返回新的纵轴范围 [−ry, +ry]。
    /// 绝对值（RF 检波均适用）驱动标度，避免正负极值不对称导致基线偏移。
    /// </summary>
    public float RangeHalfV(float framePeakAbsV)
    {
        if (ManualHalfScale.HasValue) return ManualHalfScale.Value;
        float target = Math.Max(0f, framePeakAbsV);

        if (DisplayPeakV <= 0f)
        {
            // 首次：直接用当前峰值建立标度
            DisplayPeakV = target;
        }
        else if (target >= DisplayPeakV)
        {
            // 快攻：信号更强，立即放大到不削顶（取目标本身，保证 ≥ 目标）
            DisplayPeakV = target;
        }
        else
        {
            // 慢释放：向更弱信号缓慢收敛，抑制噪声造成的逐帧跳动
            DisplayPeakV -= DisplayPeakV * ReleaseFactor;
            if (DisplayPeakV < target) DisplayPeakV = target; // 未收敛过头
        }

        if (DisplayPeakV <= 0f)
            DisplayPeakV = 1e-6f;

        return DisplayPeakV * (1f + HeadroomRatio);
    }

    /// <summary>重置标度（冻结/更换信号源/切换通道时调用）。</summary>
    public void Reset()
    {
        DisplayPeakV = 0f;
        ManualHalfScale = null;
    }

    /// <summary>
    /// 便捷入口：从当前帧采样数组更新标度，返回稳定纵轴峰值 [0, +∞)。
    /// 与 RangeHalfV 相同语义，仅在阈值处理上做防御。
    /// </summary>
    public float UpdateFromSamples(float[] samples)
    {
        if (samples is not { Length: > 0 })
            return RangeHalfV(0f);

        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = Math.Abs(samples[i]);
            if (v > peak) peak = v;
        }
        return RangeHalfV(peak);
    }

    /// <summary>
    /// 计算可见时间窗口 [startUs, startUs+viewUs] 对应的采样索引区间 [i0, i1]（闭区间）。
    /// 关键修复：当"延迟"(startUs) 增大到超过整个采集范围、或窗口退化为空/单点时，
    /// 原先 Direct-Lines 用 1 个点或 NaN 坐标绘制会抛异常（OnPaint 崩溃 → 界面红叉）。
    /// 本方法总是返回合法索引（i0≤i1，且被钳制到 [0, pointCount-1]），调用方据
    /// (i1-i0+1)&lt;2 判定不可绘制。
    /// </summary>
    /// <param name="startUs">可见窗口起点（µs）。</param>
    /// <param name="viewUs">可见窗口宽度（µs），须为非负。</param>
    /// <param name="dt">采样间隔（µs/点）；&lt;=0 表示采样率无效，回退显示全部。</param>
    /// <param name="pointCount">采样点数（&gt;0）。</param>
    public static void ComputeVisibleRange(float startUs, float viewUs, float dt, int pointCount,
        out int i0, out int i1)
    {
        if (pointCount <= 0)
        {
            i0 = 0; i1 = -1;
            return;
        }
        int last = pointCount - 1;
        if (dt <= 0 || pointCount == 1)
        {
            // 无有效采样率 / 仅 1 点：退化为显示该点（调用方 count<2 会跳过绘制）
            i0 = 0; i1 = pointCount == 1 ? 0 : last;
            return;
        }
        // L12-FIX（审查 20260828）：零宽窗口（viewUs<=0）不再退化为"整段全显"——
        // 原实现使窗口缩到 0 时波形突然全量显示，与"缩小采样长聚焦"语义相悖。
        // 现返回空窗口（单点），调用方 n>=2 跳过绘制即隐藏波形。
        if (viewUs <= 0)
        {
            i0 = 0; i1 = 0;
            return;
        }
        i0 = (int)(startUs / dt);
        i1 = (int)((startUs + viewUs) / dt);
        i0 = Math.Clamp(i0, 0, last);
        i1 = Math.Clamp(i1, Math.Max(0, i0), last);
        // i1 可能被向下钳制到 < i0 → 保证闭区间有效
        if (i1 < i0) i1 = i0;
    }

    /// <summary>
    /// P0-1-FIX：时间→像素横坐标映射纯函数（可单测）。
    /// 第 i 个采样点的绝对时刻 = i×dt（µs），映射到窗口 [startUs, startUs+viewUs] 内的像素坐标。
    /// 与 <see cref="ComputeVisibleRange"/> 的索引区间一致，与闸门/游标坐标系统一
    /// （闸门/游标用 "绝对时间 − startUs"，波形必须同源，否则延迟时二者错位）。
    /// </summary>
    public static float SampleToPixelX(int i, float dt, float startUs, float viewUs, int plotW, int ml)
    {
        float tUs = i * dt;                     // 第 i 点绝对时刻（不是 startUs+i*dt）
        return ml + (tUs - startUs) / viewUs * plotW;
    }

    /// <summary>像素 x 坐标 → 时间（µs）反向映射，供游标/鼠标交互使用。</summary>
    public static float PixelToTimeUs(float px, float startUs, float viewUs, int plotW, int ml)
    {
        return startUs + (px - ml) / plotW * viewUs;
    }

    /// <summary>时间（µs）→ 采样点索引（整数截断，防越界）。</summary>
    public static int TimeUsToIndex(float us, float dt, int pointCount)
    {
        int idx = dt > 0 ? (int)(us / dt) : 0;
        return Math.Clamp(idx, 0, Math.Max(0, pointCount - 1));
    }
}
