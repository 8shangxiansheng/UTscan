using System.Diagnostics;
using System.IO;
using System.Text;
using UTscan.Core.Models;

namespace UTscan.Services;

/// <summary>
/// .adtx 二进制数据格式读写服务（说明书 3.6 数据分析模块）。
///
/// 文件结构：
///   ┌─────────────────────────────────┐
///   │ Header (固定 256 字节)           │
///   │   - 魔数 "ADTX" (4B)            │
///   │   - 版本 (2B uint16)             │
///   │   - 头大小 (2B uint16)           │
///   │   - 扫查参数 (48B)               │
///   │   - 系统参数 (20B)               │
///   │   - 闸门配置 (32B)               │
///   │   - 数据维度 (16B)               │
///   │   - 保留 (132B)                  │
///   ├─────────────────────────────────┤
///   │ 位置轴数据 (nCols × 4B float)    │
///   ├─────────────────────────────────┤
///   │ A 扫波形数据 (nCols × nRows × 4B)│
///   └─────────────────────────────────┘
///
/// 所有多字节字段使用 Little-Endian 编码。
/// </summary>
public class AdtxDataService
{
    private const string Magic = "ADTX";
    private const ushort Version = 1;
    private const ushort HeaderSize = 256;

    /// <summary>
    /// 保存扫查数据到 .adtx 文件。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="ascans">A 扫波形数组 [nCols][nSamples]</param>
    /// <param name="positions">每条 A 扫对应的位置（mm）</param>
    /// <param name="region">扫查区域</param>
    /// <param name="systemParams">系统参数</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    public void Save(string path, float[][] ascans, float[] positions,
        ScanRegion region, SystemParams systemParams, float sampleRate)
    {
        if (ascans.Length == 0)
            throw new ArgumentException("A 扫数据为空", nameof(ascans));

        if (positions.Length != ascans.Length)
            throw new ArgumentException("位置数组长度与 A 扫数量不匹配", nameof(positions));

        int nCols = ascans.Length;
        int nSamples = ascans[0].Length;

        // 写前校验全部 A 扫长度一致（审查 P2-7）：原实现写到一半才抛异常，留下损坏文件
        for (int i = 0; i < nCols; i++)
            if (ascans[i].Length != nSamples)
                throw new ArgumentException($"A 扫 #{i} 长度 {ascans[i].Length} 与首条 {nSamples} 不一致", nameof(ascans));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.ASCII);

        // ── 写文件头 ──
        bw.Write(Encoding.ASCII.GetBytes(Magic));         // 4B 魔数
        bw.Write(Version);                                 // 2B 版本
        bw.Write(HeaderSize);                              // 2B 头大小

        // 扫查参数 (48B)
        bw.Write(region.StartX);                           // 4B
        bw.Write(region.StartY);                           // 4B
        bw.Write(region.Width);                            // 4B
        bw.Write(region.Height);                           // 4B
        bw.Write(region.StepX);                            // 4B
        bw.Write(region.StepY);                            // 4B
        bw.Write(sampleRate);                              // 4B 采样率
        bw.Write(0f);                                      // 4B 保留(扫描速度)
        bw.Write(0f);                                      // 4B 保留(加速度)
        bw.Write(0f);                                      // 4B 保留
        bw.Write(0f);                                      // 4B 保留
        bw.Write(0f);                                      // 4B 保留

        // 系统参数 (20B)
        bw.Write(systemParams.SoundVelocity);              // 4B 声速
        bw.Write(systemParams.FocalLength);                // 4B 焦距
        bw.Write(systemParams.ZeroOffsetUs);               // 4B 零点偏移
        bw.Write(Encoding.ASCII.GetBytes(systemParams.RulerUnit.PadRight(8).Substring(0, 8))); // 8B 单位

        // 闸门配置占位 (32B) — 未来扩展
        for (int i = 0; i < 8; i++)
            bw.Write(0f);                                  // 32B 保留

        // 数据维度 (16B)
        bw.Write(nCols);                                   // 4B 扫查位置数
        bw.Write(nSamples);                                // 4B 每条 A 扫的采样点数
        bw.Write(0);                                       // 4B 保留
        bw.Write(0);                                       // 4B 保留

        // 保留区填充到 HeaderSize
        long currentPos = fs.Position;
        int padding = HeaderSize - (int)currentPos;
        if (padding > 0)
            bw.Write(new byte[padding]);

        // ── 写位置轴数据 ──
        foreach (float pos in positions)
            bw.Write(pos);

        // ── 写 A 扫波形数据 ──（长度已在写前统一校验）
        foreach (var scan in ascans)
        {
            foreach (float sample in scan)
                bw.Write(sample);
        }

        Debug.WriteLine($"[Adtx] 已保存: {path} ({nCols}×{nSamples}, {fs.Position} bytes)");
    }

    /// <summary>
    /// H-10：ADTX 导入前的总内存预算校验——在大数组分配前拒绝超限维度。
    /// 预算包含位置数组、锯齿数组引用与对象开销、全部样本、导入后 C 扫矩阵、
    /// 当前进程已占用内存和 x86 安全上限（768 MiB 保守示例，非硬件常量）。
    /// </summary>
    private static void ValidateAllocationBudget(int nCols, int nSamples, ScanRegion region)
    {
        if (nCols <= 0 || nSamples <= 0)
            throw new InvalidDataException($"ADTX 数据维度必须大于 0: nCols={nCols}, nSamples={nSamples}");

        long samplesBytes;
        long positionsBytes;
        long jaggedOverhead;
        long matrixBytes;
        try
        {
            samplesBytes = checked((long)nCols * nSamples * sizeof(float));
            positionsBytes = checked((long)nCols * sizeof(float));
            jaggedOverhead = checked((long)nCols * (IntPtr.Size + 32L));
            matrixBytes = checked((long)region.PointCountX * region.PointCountY * sizeof(float));
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("ADTX 声明尺寸发生整数溢出", ex);
        }

        long requested = checked(samplesBytes + positionsBytes + jaggedOverhead + matrixBytes);
        long processUsed = GC.GetTotalMemory(forceFullCollection: false);
        long budget = IntPtr.Size == 4
            ? 768L * 1024 * 1024       // x86 保守上限（768 MiB）
            : 8L * 1024 * 1024 * 1024; // x64 上限（8 GiB）

        if (requested > budget || processUsed + requested > budget)
            throw new InvalidDataException(
                $"ADTX 预计新增内存 {requested / 1024.0 / 1024:F1} MiB（进程已用 {processUsed / 1024.0 / 1024:F1} MiB），" +
                $"超过当前进程安全预算 {budget / 1024 / 1024} MiB。请减小 nCols/nSamples 或扫查区域");

        // 校验 Region 步距为有限正数，PointCountX/Y 计算不溢出
        if (!float.IsFinite(region.StepX) || region.StepX <= 0 ||
            !float.IsFinite(region.StepY) || region.StepY <= 0)
            throw new InvalidDataException($"ADTX 区域步距非法: StepX={region.StepX}, StepY={region.StepY}");
    }

    /// <summary>
    /// 从 .adtx 文件加载扫查数据。
    /// </summary>
    public AdtxData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs, Encoding.ASCII);

        // ── 读文件头 ──
        byte[] magicBytes = br.ReadBytes(4);
        string magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != Magic)
            throw new InvalidDataException($"无效的文件格式: 期望 '{Magic}', 实际 '{magic}'");

        ushort version = br.ReadUInt16();
        ushort headerSize = br.ReadUInt16();

        // M-6：headerSize 合法性校验（审查报告 M-6）：
        // 声明头大小必须 ≥ 最小头（256）且 ≤ 文件长度，否则 fs.Position = headerSize
        // 会越界读错位数据（维度校验能兜底但异常类型不精确）。
        if (headerSize < HeaderSize)
            throw new InvalidDataException($"文件头大小非法: 声明 {headerSize} 字节，最小应为 {HeaderSize}");
        if (fs.Length < headerSize)
            throw new InvalidDataException($"文件头大小 {headerSize} 超过文件长度 {fs.Length}，文件已损坏或被截断");

        // 扫查参数
        var region = new ScanRegion
        {
            StartX = br.ReadSingle(),
            StartY = br.ReadSingle(),
            Width = br.ReadSingle(),
            Height = br.ReadSingle(),
            StepX = br.ReadSingle(),
            StepY = br.ReadSingle()
        };
        float sampleRate = br.ReadSingle();
        br.ReadSingle(); // 保留
        br.ReadSingle(); // 保留
        br.ReadSingle(); // 保留
        br.ReadSingle(); // 保留
        br.ReadSingle(); // 保留

        // 系统参数
        var systemParams = new SystemParams
        {
            SoundVelocity = br.ReadSingle(),
            FocalLength = br.ReadSingle(),
            ZeroOffsetUs = br.ReadSingle()
        };
        byte[] unitBytes = br.ReadBytes(8);
        systemParams.RulerUnit = Encoding.ASCII.GetString(unitBytes).TrimEnd('\0', ' ');

        // 闸门配置占位
        for (int i = 0; i < 8; i++)
            br.ReadSingle();

        // 数据维度
        int nCols = br.ReadInt32();
        int nSamples = br.ReadInt32();
        br.ReadInt32(); // 保留
        br.ReadInt32(); // 保留

        // NEW-M-7 修复：维度上限从 10M 降至 100K——x86 进程可用数据堆 ~1.2GB，
        // nCols × nSamples × sizeof(float) + 矩阵 + 对象开销必须在此范围内。
        // 100K × 100K × 4B = 40GB（已超限，但维度校验为单项上限；总内存在 LoadAdtxData 中二次校验）。
        // 保守取 100K 作为单维度上限（覆盖实际最大扫查 ~5000×5000=2500万点）。
        const int MaxDimension = 100_000;
        if (nCols < 0 || nSamples < 0 || nCols > MaxDimension || nSamples > MaxDimension)
            throw new InvalidDataException($"数据维度非法: nCols={nCols}, nSamples={nSamples}（上限 {MaxDimension}）");
        // 文件实际长度必须容纳位置轴 + 全部 A 扫数据
        long expectedBytes = (long)headerSize + ((long)nCols + (long)nCols * nSamples) * sizeof(float);
        if (fs.Length < expectedBytes)
            throw new InvalidDataException(
                $"文件长度不足: 声明 {expectedBytes} 字节（{nCols}×{nSamples}），实际 {fs.Length} 字节，文件可能已损坏或被截断");

        // 跳到头结束
        fs.Position = headerSize;

        // H-10 修复：在大数组分配前完成总预算校验（checked 防溢出 + GC 已用 + x86 768MiB 上限）。
        // 原实现在 positions/ascans 分配后才校验矩阵，x86 进程可能已因 ADTX 波形分配而 OOM。
        ValidateAllocationBudget(nCols, nSamples, region);

        // ── 读位置轴数据 ──
        var positions = new float[nCols];
        for (int i = 0; i < nCols; i++)
            positions[i] = br.ReadSingle();

        // ── 读 A 扫波形数据 ──
        var ascans = new float[nCols][];
        for (int col = 0; col < nCols; col++)
        {
            ascans[col] = new float[nSamples];
            for (int row = 0; row < nSamples; row++)
                ascans[col][row] = br.ReadSingle();
        }

        Debug.WriteLine($"[Adtx] 已加载: {path} ({nCols}×{nSamples})");

        return new AdtxData
        {
            Version = version,
            Region = region,
            SystemParams = systemParams,
            SampleRate = sampleRate,
            Positions = positions,
            Ascans = ascans
        };
    }
}

/// <summary>
/// .adtx 文件加载结果
/// </summary>
public class AdtxData
{
    public ushort Version { get; set; }
    public ScanRegion Region { get; set; } = new();
    public SystemParams SystemParams { get; set; } = new();
    public float SampleRate { get; set; }
    public float[] Positions { get; set; } = Array.Empty<float>();
    public float[][] Ascans { get; set; } = Array.Empty<float[]>();

    /// <summary>扫查位置数</summary>
    public int ColumnCount => Ascans.Length;

    /// <summary>每条 A 扫的采样点数</summary>
    public int SampleCount => Ascans.Length > 0 ? Ascans[0].Length : 0;
}
