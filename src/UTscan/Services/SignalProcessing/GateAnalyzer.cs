using UTscan.Core.Enums;
using UTscan.Core.Models;

namespace UTscan.Services.SignalProcessing;

/// <summary>
/// 闸门分析器（说明书 3.3.4 闸门测量 / 3.6.3 闸门成像模式）。
/// 既有实例方法用于闸门测量与 C 扫成像值计算，Preprocess 为静态工具方法。
/// </summary>
public class GateAnalyzer
{
    /// <summary>
    /// 分析闸门内的信号：峰值/正负峰/峰峰值/阈值穿越时间/同步闸门首穿偏移。
    /// </summary>
    public GateResult Analyze(AScanData data, GateConfig gate)
    {
        var result = new GateResult { GateName = gate.Name };
        if (data.Samples is not { Length: > 0 })
            return result;
        if (data.SampleRate <= 0)
            return result; // 防御：dt=∞ 无意义，直接返回空结果

        float dt = 1f / data.SampleRate * 1e6f;
        // P0-2-FIX：闸门时间(µs) 相对触发时刻(t=0)，而 samples[0] 对应 −TriggerOffsetUs。
        // 采样索引 = (闸门时间 + offset)/dt；测量时刻 = 采样索引×dt − offset。
        float offUs = data.TriggerOffsetUs;
        int maxIdx = Math.Max(0, Math.Min(data.PointCount, data.Samples.Length) - 1);
        int startIdx = Math.Clamp((int)((gate.StartUs + offUs) / dt), 0, maxIdx);
        int endIdx = Math.Clamp((int)((gate.StartUs + gate.WidthUs + offUs) / dt), 0, maxIdx);

        float posPeak = 0f, negPeak = 0f;
        int posIdx = -1, negIdx = -1, posCrossIdx = -1, negCrossIdx = -1;

        for (int i = startIdx; i <= endIdx; i++)
        {
            float s = data.Samples[i];

            if (posIdx < 0 || s > posPeak) { posPeak = s; posIdx = i; }
            if (negIdx < 0 || s < negPeak) { negPeak = s; negIdx = i; }

            if (posCrossIdx < 0 && s >= gate.ThresholdV) posCrossIdx = i;
            if (negCrossIdx < 0 && s <= -gate.ThresholdV) negCrossIdx = i;
        }

        // 峰值幅度：取 |max| 与 |min| 较大者，保留符号
        float peak = Math.Abs(posPeak) >= Math.Abs(negPeak) ? posPeak : negPeak;
        int peakIdx = Math.Abs(posPeak) >= Math.Abs(negPeak) ? posIdx : negIdx;

        result.PeakAmplitude = peak;
        result.PeakPositionUs = peakIdx >= 0 ? peakIdx * dt - offUs : 0f;
        result.TimeOfFlightUs = peakIdx >= 0 ? (peakIdx - startIdx) * dt : 0f;
        // L13-FIX（审查 20260828）：阈值 ≤0 时全零信号恒判"超阈"（Math.Abs(peak)>=0 恒真）。
        // 阈值语义为"超过该电平才算检测到信号"，非正阈值无意义——按未超阈处理。
        result.IsAboveThreshold = gate.ThresholdV > 0 && Math.Abs(peak) >= gate.ThresholdV;
        result.PositivePeak = posIdx >= 0 ? posPeak : 0f;
        result.NegativePeak = negIdx >= 0 ? negPeak : 0f;
        result.PeakToPeak = (posIdx >= 0 ? posPeak : 0f) - (negIdx >= 0 ? negPeak : 0f);
        result.PositivePeakPositionUs = posIdx >= 0 ? posIdx * dt - offUs : -1f;
        result.NegativePeakPositionUs = negIdx >= 0 ? negIdx * dt - offUs : -1f;
        result.PositiveThresholdCrossUs = posCrossIdx >= 0 ? posCrossIdx * dt - offUs : -1f;
        result.NegativeThresholdCrossUs = negCrossIdx >= 0 ? negCrossIdx * dt - offUs : -1f;
        result.SyncFirstCrossOffsetUs = posCrossIdx >= 0
            ? posCrossIdx * dt - offUs - gate.StartUs
            : -1f;

        return result;
    }

    /// <summary>
    /// 波形类型预处理（说明书 3.3.2）：检波取绝对值、正/负半波置零另一极性、RF 原样返回副本。
    /// </summary>
    public static float[] Preprocess(float[] samples, WaveformType type)
    {
        if (samples is null || samples.Length == 0)
            return Array.Empty<float>();

        var r = new float[samples.Length];
        switch (type)
        {
            case WaveformType.Detected:
                for (int i = 0; i < samples.Length; i++) r[i] = Math.Abs(samples[i]);
                break;
            case WaveformType.PositiveHalf:
                for (int i = 0; i < samples.Length; i++) r[i] = Math.Max(0f, samples[i]);
                break;
            case WaveformType.NegativeHalf:
                for (int i = 0; i < samples.Length; i++) r[i] = Math.Min(0f, samples[i]);
                break;
            default: // RF
                Array.Copy(samples, r, samples.Length);
                break;
        }
        return r;
    }

    /// <summary>
    /// 计算单个扫查点的 C 扫成像值（说明书 3.6.3 闸门模式）。
    /// 先按波形类型预处理，再在数据闸门内按成像模式提取标量。
    /// </summary>
    public float ComputeImagingValue(AScanData data, GateConfig gate, CScanImagingMode mode, WaveformType waveType,
        TcgCurve? tcg = null)
    {
        if (data.Samples is not { Length: > 0 })
            return 0f;
        if (data.SampleRate <= 0)
            return 0f; // 防御：dt=∞ 无意义
        float[] s = Preprocess(data.Samples, waveType);
        float dt = 1f / data.SampleRate * 1e6f;
        // P0-2-FIX：成像闸门时间(µs) 相对触发时刻，samples[0] 对应 −TriggerOffsetUs。
        float offUs = data.TriggerOffsetUs;
        int maxIdx = Math.Max(0, Math.Min(data.PointCount, s.Length) - 1);
        int startIdx = Math.Clamp((int)((gate.StartUs + offUs) / dt), 0, maxIdx);
        int endIdx = Math.Clamp((int)((gate.StartUs + gate.WidthUs + offUs) / dt), 0, maxIdx);

        // TCG：启用时按逐样点声程查补偿因子（10^(dB/20)），对幅值加权。
        // 用与 samples 等长的预计算因子表（O(1) 查表，避免成像循环内重复换算 µs→mm→插值）。
        float[]? gainFactor = null;
        if (tcg is { Enabled: true, PointCount: >= 2 })
        {
            gainFactor = new float[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                // 样点 i 的绝对声程 = (i×dt − offUs)（相对触发时刻）
                float tUs = i * dt - offUs;
                gainFactor[i] = TcgCurve.DbToAmplitudeFactor(tcg.GainAtTimeUs(tUs));
            }
        }

        float posPeak = 0f, negPeak = 0f;
        int posIdx = -1, negIdx = -1, posCrossIdx = -1;

        for (int i = startIdx; i <= endIdx; i++)
        {
            float si = gainFactor != null ? s[i] * gainFactor[i] : s[i];   // TCG 加权
            if (posIdx < 0 || si > posPeak) { posPeak = si; posIdx = i; }
            if (negIdx < 0 || si < negPeak) { negPeak = si; negIdx = i; }
            if (posCrossIdx < 0 && si >= gate.ThresholdV) posCrossIdx = i;
        }

        switch (mode)
        {
            case CScanImagingMode.PeakPeak:
                return (posIdx >= 0 ? posPeak : 0f) - (negIdx >= 0 ? negPeak : 0f);
            case CScanImagingMode.PositivePeak:
                return posIdx >= 0 ? posPeak : 0f;
            case CScanImagingMode.NegativePeak:
                return negIdx >= 0 ? negPeak : 0f;
            case CScanImagingMode.MaxPeak:
                if (posIdx < 0 && negIdx < 0) return 0f;
                return Math.Abs(posPeak) >= Math.Abs(negPeak) ? posPeak : negPeak;
            case CScanImagingMode.TofPositivePeak:
                return posIdx >= 0 ? (posIdx - startIdx) * dt : -1f;
            case CScanImagingMode.TofNegativePeak:
                return negIdx >= 0 ? (negIdx - startIdx) * dt : -1f;
            case CScanImagingMode.TofPositiveThreshold:
                return posCrossIdx >= 0 ? (posCrossIdx - startIdx) * dt : -1f;
            case CScanImagingMode.TofNegativeThreshold:
                {
                    int negCross = -1;
                    for (int i = startIdx; i <= endIdx; i++)
                        if (s[i] <= -gate.ThresholdV) { negCross = i; break; }
                    return negCross >= 0 ? (negCross - startIdx) * dt : -1f;
                }
            case CScanImagingMode.PhaseReversal:
                {
                    if (posIdx < 0 && negIdx < 0) return 0f;
                    float pk = Math.Abs(posPeak) >= Math.Abs(negPeak) ? posPeak : negPeak;
                    return -pk;
                }
            case CScanImagingMode.Mean:
                {
                    // P0-F：闸门内采样点算术平均（说明书 2.6 明确列出的成像模式）
                    double sum = 0;
                    for (int i = startIdx; i <= endIdx; i++) sum += s[i];
                    int cnt = endIdx - startIdx + 1;
                    return cnt > 0 ? (float)(sum / cnt) : 0f;
                }
            default:
                return 0f;
        }
    }

    /// <summary>
    /// 由同步闸门分析结果联动计算数据闸门实际起点：
    /// 数据闸门起点 = 同步闸门首穿偏移 Δt + 数据闸门标称起点；无同步信号时按标称起点。
    /// </summary>
    public float ComputeDataGateStart(GateResult syncResult, GateConfig dataGate)
    {
        if (syncResult is null || syncResult.SyncFirstCrossOffsetUs < 0f)
            return dataGate.StartUs;
        return syncResult.SyncFirstCrossOffsetUs + dataGate.StartUs;
    }
}
