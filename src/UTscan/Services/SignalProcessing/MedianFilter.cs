namespace UTscan.Services.SignalProcessing;

/// <summary>
/// 中值滤波器
/// </summary>
public class MedianFilter
{
    /// <summary>
    /// 对信号进行中值滤波
    /// </summary>
    public float[] Apply(float[] input, int kernelSize = 5)
    {
        var result = new float[input.Length];
        int half = kernelSize / 2;

        for (int i = 0; i < input.Length; i++)
        {
            var window = new List<float>();
            for (int j = -half; j <= half; j++)
            {
                int idx = Math.Clamp(i + j, 0, input.Length - 1);
                window.Add(input[idx]);
            }
            window.Sort();
            result[i] = window[window.Count / 2];
        }
        return result;
    }
}
