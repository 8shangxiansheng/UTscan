using System.IO;
using System.Text;
using UTscan.Core.Models;

namespace UTscan.Services;

/// <summary>
/// CSV 导出服务（说明书“导出数据”）：将 A 扫波形及闸门内数据导出为 .csv。
/// </summary>
public class CsvExportService
{
    // M-10：数值格式固定 InvariantCulture（句点小数点）——避免逗号小数区域把数值拆成两列
    private static readonly System.Globalization.CultureInfo CsvCulture =
        System.Globalization.CultureInfo.InvariantCulture;
    /// <summary>
    /// 导出 A 扫数据为 CSV：列为 index,time_us,voltage。
    /// </summary>
    public async Task ExportAsync(string path, AScanData data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        // IO4-FIX（审查 20260828）：头行标注单位；Samples 为空时导出仅表头。
        sb.AppendLine("index,time_us,voltage_(V)");
        if (data is null || data.Samples is null || data.Samples.Length == 0)
        {
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
            return;
        }
        // IO4-FIX：SampleRate<=0 时 dt 无意义（无法换算时间轴），time_us 列标 0 并注明
        float dt = data.SampleRate > 0 ? 1e6f / data.SampleRate : 0f;
        int count = Math.Min(data.PointCount, data.Samples.Length);   // 防御：逻辑点数不超数组长度
        for (int i = 0; i < count; i++)
        {
            // P0-2-FIX：时间轴统一减触发前偏移（samples[0] 对应 −TriggerOffsetUs，触发时刻为 t=0）
            float tUs = i * dt - data.TriggerOffsetUs;
            sb.Append(i).Append(',').Append(tUs.ToString("F6", CsvCulture)).Append(',')
              .Append(data.Samples[i].ToString("F6", CsvCulture)).AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 导出 C 扫矩阵为 CSV（行=索引轴，列=扫查轴）。
    /// </summary>
    public async Task ExportMatrixAsync(string path, float[,] values)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        int rows = values.GetLength(0), cols = values.GetLength(1);
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                if (x > 0) sb.Append(',');
                sb.Append(values[y, x].ToString("F6", CsvCulture));
            }
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
    }
}
