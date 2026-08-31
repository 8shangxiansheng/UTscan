using UTscan.Core.Models;

namespace UTscan.Core.Interfaces;

/// <summary>
/// 信号处理器接口
/// </summary>
public interface ISignalProcessor
{
    /// <summary>FFT变换</summary>
    float[] Fft(float[] input);

    /// <summary>IFFT逆变换</summary>
    float[] Ifft(float[] input);

    /// <summary>中值滤波</summary>
    float[] MedianFilter(float[] input, int kernelSize);

    /// <summary>闸门分析</summary>
    GateResult AnalyzeGate(AScanData data, GateConfig gate);
}
