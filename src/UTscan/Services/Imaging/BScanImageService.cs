using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using UTscan.Core.Models;
using UTscan.Services.SignalProcessing;

namespace UTscan.Services.Imaging;

/// <summary>
/// B 扫截面成像服务（说明书 3.6.1）。
///
/// B 扫是 X-Z 或 Y-Z 截面图：
///   - 横轴 = 扫查位置（mm），沿扫查轴线方向
///   - 纵轴 = 深度（mm），由时间 × 声速 / 2 计算
///   - 颜色 = 信号幅值
///
/// 每一条 A 扫波形构成 B 扫图像的一列。
/// </summary>
public class BScanImageService
{
    /// <summary>
    /// 渲染 B 扫截面图。
    /// </summary>
    /// <param name="ascans">A 扫波形数组，每个元素是一行扫查的波形</param>
    /// <param name="positions">每条 A 扫对应的扫查位置（mm）</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <param name="soundVelocity">材料声速（m/s）</param>
    /// <param name="zeroOffsetUs">零点偏移（μs）</param>
    /// <param name="cmap">色图</param>
    /// <param name="maxDepthMm">最大显示深度（mm），≤0 表示自动</param>
    public Bitmap Render(
        float[][] ascans,
        float[] positions,
        float sampleRate,
        float soundVelocity,
        float zeroOffsetUs,
        Colormap cmap,
        float maxDepthMm = 0)
    {
        if (ascans.Length == 0 || ascans[0].Length == 0)
            return new Bitmap(1, 1);

        int nCols = ascans.Length;       // 扫查位置数（图像列数）
        int nSamples = ascans[0].Length; // 每条 A 扫的采样点数（图像行数 = 深度方向）

        // 计算深度轴：depth_mm = (t_us - zeroOffset) * velocity / 2000
        // t_us = i / sampleRate * 1e6
        // depth_mm = (i / sampleRate * 1e6 - zeroOffset) * soundVelocity / 2000
        double dtUs = 1.0 / sampleRate * 1e6;
        double mmPerSample = soundVelocity / 2000.0 * dtUs; // mm per sample

        // 零点偏移生效（审查 P2-6）：零点之前的样本位于工件表面以上，
        // 渲染时跳过前 zeroSkip 个样本，行 0 对应深度 0（原实现行 0 恒为深度 0 且忽略偏移）
        int zeroSkip = Math.Max(0, (int)(zeroOffsetUs / dtUs));
        if (zeroSkip >= nSamples)
            return new Bitmap(1, 1); // 零点偏移超出记录长度，无有效深度数据
        int usableSamples = nSamples - zeroSkip;

        // 最大深度
        double maxDepth;
        if (maxDepthMm > 0)
        {
            maxDepth = maxDepthMm;
        }
        else
        {
            maxDepth = usableSamples * mmPerSample;
        }

        // 限制行数为合理范围（避免图像过高）
        int nRows = Math.Min(usableSamples, (int)(maxDepth / mmPerSample) + 1);
        nRows = Math.Max(1, nRows);

        // 求幅值范围
        float ampMin = float.MaxValue, ampMax = float.MinValue;
        for (int col = 0; col < nCols; col++)
        {
            var scan = ascans[col];
            for (int row = 0; row < Math.Min(nRows, scan.Length - zeroSkip); row++)
            {
                float v = scan[zeroSkip + row];
                // 取绝对值用于着色（B 扫通常显示幅值）
                float absV = Math.Abs(v);
                if (absV < ampMin) ampMin = absV;
                if (absV > ampMax) ampMax = absV;
            }
        }
        if (ampMax - ampMin < 1e-9f) ampMax = ampMin + 1e-6f;

        // 渲染位图
        int cellW = Math.Max(1, 800 / nCols);  // 自适应列宽
        int cellH = Math.Max(1, 600 / nRows);  // 自适应行高
        int imgW = nCols * cellW;
        int imgH = nRows * cellH;

        var bmp = new Bitmap(imgW, imgH, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, imgW, imgH);
        var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        int stride = bd.Stride;
        var rowBuf = new byte[stride];

        for (int row = 0; row < nRows; row++)
        {
            // B 扫纵轴方向：row=0 对应最浅深度（顶部），row=nRows-1 对应最深（底部）
            // 加 zeroSkip 使行 0 对应零点偏移后的深度 0
            int sampleIdx = zeroSkip + row;
            for (int col = 0; col < nCols; col++)
            {
                var scan = ascans[col];
                float v = sampleIdx < scan.Length ? Math.Abs(scan[sampleIdx]) : 0f;
                Color c = cmap.Map(v, ampMin, ampMax);

                for (int dx = 0; dx < cellW; dx++)
                {
                    int px = (col * cellW + dx) * 3;
                    if (px + 2 < stride)
                    {
                        rowBuf[px] = c.B;
                        rowBuf[px + 1] = c.G;
                        rowBuf[px + 2] = c.R;
                    }
                }
            }

            // 复制该行到 bitmap（每个 row 对应 cellH 个像素行）
            for (int dy = 0; dy < cellH; dy++)
            {
                int y = row * cellH + dy;
                if (y < imgH)
                    Marshal.Copy(rowBuf, 0, bd.Scan0 + y * stride, stride);
            }
        }

        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>
    /// 生成深度坐标轴标签（mm）。
    /// L11-FIX（审查 20260828）：与 Render 的 zeroSkip 截断语义一致——原实现用精确公式
    /// (i*mmPerSample - zeroOffsetUs*v/2000)，零点偏移非整采样时轴标签与渲染行差 ≤1 样本。
    /// 现统一为"渲染行 0 = 深度 0"，i<zeroSkip 的样本（零点前）标 0（未显示区）。
    /// </summary>
    public float[] GetDepthAxis(int nSamples, float sampleRate, float soundVelocity, float zeroOffsetUs)
    {
        double dtUs = 1.0 / sampleRate * 1e6;
        double mmPerSample = soundVelocity / 2000.0 * dtUs;
        int zeroSkip = Math.Max(0, (int)(zeroOffsetUs / dtUs));
        var axis = new float[nSamples];
        for (int i = 0; i < nSamples; i++)
            axis[i] = (float)((i - zeroSkip) * mmPerSample);
        return axis;
    }

    /// <summary>
    /// 从 B 扫数据中提取指定深度的截面线（用于缺陷定量）。
    /// </summary>
    public float[] ExtractDepthSlice(float[][] ascans, int sampleIndex)
    {
        var result = new float[ascans.Length];
        for (int i = 0; i < ascans.Length; i++)
        {
            result[i] = sampleIndex < ascans[i].Length
                ? Math.Abs(ascans[i][sampleIndex])
                : 0f;
        }
        return result;
    }
}
