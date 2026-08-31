using System.Drawing;
using System.Drawing.Imaging;
using UTscan.Core.Enums;
using UTscan.Core.Models;

namespace UTscan.Services.SignalProcessing;

/// <summary>
/// C 扫成像服务（说明书 3.6.2）：将扫查点标量矩阵渲染为热图位图，并生成色条。
/// </summary>
public class CScanImageService
{
    /// <summary>
    /// 渲染 C 扫热图。
    /// </summary>
    /// <param name="values">二维矩阵 [y, x]，y=索引轴行，x=扫查轴列</param>
    /// <param name="cmap">色图</param>
    /// <param name="min">着色下界（NaN 表示自动取最小）</param>
    /// <param name="max">着色上界（NaN 表示自动取最大）</param>
    /// <param name="cellW">单格宽像素</param>
    /// <param name="cellH">单格高像素</param>
    public Bitmap Render(float[,] values, Colormap cmap, float min, float max, int cellW = 4, int cellH = 4)
    {
        int rows = values.GetLength(0);
        int cols = values.GetLength(1);
        if (float.IsNaN(min) || float.IsNaN(max))
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    float v = values[y, x];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            if (float.IsNaN(min)) min = lo;
            if (float.IsNaN(max)) max = hi;
        }
        if (max - min < 1e-9f) max = min + 1e-6f;

        var bmp = new Bitmap(cols * cellW, rows * cellH, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        int stride = bd.Stride;
        var row = new byte[stride];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                Color c = cmap.Map(values[y, x], min, max);
                for (int dx = 0; dx < cellW; dx++)
                {
                    int px = (x * cellW + dx) * 3;
                    for (int dy = 0; dy < cellH; dy++)
                    {
                        int off = dy * stride + px;
                        if (off + 2 < row.Length)
                        {
                            row[off] = c.B;
                            row[off + 1] = c.G;
                            row[off + 2] = c.R;
                        }
                    }
                }
            }
            // 仅复制该行各 dy
            for (int dy = 0; dy < cellH; dy++)
            {
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + (y * cellH + dy) * stride, stride);
            }
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>
    /// 生成竖直色条位图（上=最大，下=最小）。
    /// </summary>
    public Bitmap RenderColorBar(Colormap cmap, float min, float max, int width = 24, int height = 256)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        for (int y = 0; y < height; y++)
        {
            float t = 1f - (float)y / (height - 1);
            Color c = cmap.Map(t);
            for (int x = 0; x < width; x++)
                bmp.SetPixel(x, y, c);
        }
        return bmp;
    }

    /// <summary>
    /// 计算矩阵的 [min,max]（用于自动归一化）。
    /// </summary>
    public static (float min, float max) Range(float[,] values)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        int rows = values.GetLength(0), cols = values.GetLength(1);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                float v = values[y, x];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        return (lo, hi);
    }
}
