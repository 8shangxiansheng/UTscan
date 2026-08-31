using System.Globalization;
using System.Text;
using UTscan.Core.Enums;
using UTscan.Core.Interfaces;
using UTscan.Core.Models;

namespace UTscan.Hardware.PulseGen;

/// <summary>
/// DPR500 脉冲收发仪串口协议命令构建器。
///
/// 协议来源（JSR DPR500 Operator Manual v2.2.0 + JSR Common SDK API v1.3）：
///
/// 串口配置（Manual Page 20）：
///   - 波特率: 4800
///   - 数据位: 8, 停止位: 1, 校验: None, 流控: None
///   - 接口: RS-232 via RJ45 8-conductor cable
///
/// 通信模型（Manual Appendix B - Configuration File Specification）：
///   DPR500 采用"配置文件驱动"的命令体系。设备开机后，主机查询设备型号，
///   再根据配置文件（如 PL01/RL01）中定义的命令字符和数据字节格式发送控制命令。
///
///   接收器命令（Receiver）：1 个命令字符 + 1 个数据字节
///     - 'g' + [0~79]  → 增益（索引 0=-13dB, 79=66dB, 步进 1dB）
///     - 'l' + [0~5]   → 低通滤波器（3/7.5/10/15/22.5/50 MHz）
///     - 'h' + [0~5]   → 高通滤波器（0/1/2.5/5/7.5/12.5 MHz）
///
///   脉冲器命令（Pulser）：1 个命令字符 + 1 个数据字节（组合位域）
///     - 'f' + [byte]  → 脉冲器配置字节：
///         bit 7: 能量挡位索引 (0 或 1)
///         bit 6: 工作模式 (0=Pulse-Echo, 1=Through)
///         bit 5~4: 阻尼索引 (0~3, 对应 330/104/44/34Ω for RP-L2)
///         bit 3~0: 保留 (0)
///     - 'p' + [2 bytes LE] → PRF (0~5000 Hz, little-endian uint16)
///
///   通道选择：
///     - 'c' + [0 或 1] → 选择 Channel A 或 Channel B
///
///   查询命令：
///     - 'n' → 查询设备型号（返回 ASCII 字符串，如 "DPR500"）
///
/// 响应格式：
///   - 成功: 单字节 ACK (0x06)
///   - 失败: 单字节 NAK (0x15)
///
/// 注意（Manual Page 20原文）：
///   "Documentation of that protocol is not included in this newer manual
///    in order to encourage developers to use either of the two modern
///    interfaces (JSR Common SDK DLL or JSR Simple ActiveX object)."
///   JSR 推荐使用 JSR_Common.dll (C API) 或 JSR_Simple ActiveX 对象。
///   如有 JSR_Common.dll，建议优先使用 JsrSdkNative.cs 封装。
///   本类基于配置文件规范推断协议格式，接真机前需验证。
/// </summary>
public static class Dpr500Protocol
{
    // ── 串口常量 ──

    /// <summary>DPR500 标准波特率（Manual Page 20: 4800 baud）</summary>
    public const int DefaultBaudRate = 4800;

    // ── 响应码 ──

    public const byte Ack = 0x06;
    public const byte Nak = 0x15;

    // ── 命令字符（来自配置文件 PL01 / RL01） ──

    /// <summary>接收器增益命令字符 'g'（RL01: gain, cmd='g', min=0, max=79, 8 bits）</summary>
    public const byte CmdGain = (byte)'g';

    /// <summary>接收器低通滤波器命令字符 'l'（RL01: low pass filter, cmd='l', min=0, max=5）</summary>
    public const byte CmdLowPass = (byte)'l';

    /// <summary>接收器高通滤波器命令字符 'h'（RL01: high pass filter, cmd='h', min=0, max=5）</summary>
    public const byte CmdHighPass = (byte)'h';

    /// <summary>脉冲器配置命令字符 'f'（PL01: damping/energy/e/t 组合字节）</summary>
    public const byte CmdPulserConfig = (byte)'f';

    /// <summary>脉冲器 PRF 命令字符 'p'（PL01: prf, cmd='p', 0~5000 Hz）</summary>
    public const byte CmdPrf = (byte)'p';

    /// <summary>通道选择命令字符 'c'</summary>
    public const byte CmdChannel = (byte)'c';

    /// <summary>查询设备型号命令字符 'n'</summary>
    public const byte CmdQueryModel = (byte)'n';

    // ── 滤波器查找表（来自 RL01 配置文件） ──

    /// <summary>低通滤波器频率表（MHz），索引 0~5（RL01: 3, 7.5, 10, 15, 22.5, 50）</summary>
    public static readonly float[] LowPassMHz = { 3f, 7.5f, 10f, 15f, 22.5f, 50f };

    /// <summary>高通滤波器频率表（MHz），索引 0~5（RL01: 0, 1, 2.5, 5, 7.5, 12.5）</summary>
    public static readonly float[] HighPassMHz = { 0f, 1f, 2.5f, 5f, 7.5f, 12.5f };

    /// <summary>RP-L2 阻尼电阻表（Ω），索引 0~3（PL01: 330, 104, 44, 34）</summary>
    public static readonly int[] DampingOhmsRpL2 = { 330, 104, 44, 34 };

    /// <summary>RP-H2 阻尼电阻表（Ω），索引 0~3（PL01: 100, 50, 33, 25）</summary>
    public static readonly int[] DampingOhmsRpH2 = { 100, 50, 33, 25 };

    // ── 增益映射（来自 RL01 配置文件: gain min=0, max=79, 实际 -13~66 dB） ──

    /// <summary>增益最小值（dB）</summary>
    public const float GainMinDb = -13f;

    /// <summary>增益最大值（dB）</summary>
    public const float GainMaxDb = 66f;

    /// <summary>增益索引范围</summary>
    public const int GainIndexMin = 0;
    public const int GainIndexMax = 79;

    // ── 命令构建（二进制格式） ──

    /// <summary>
    /// 构建设置增益命令。
    /// dB → 索引: index = (gainDb - (-13)) / 1 = gainDb + 13
    /// </summary>
    public static byte[] BuildSetGain(float gainDb)
    {
        int index = (int)Math.Clamp(
            Math.Round(gainDb - GainMinDb),
            GainIndexMin, GainIndexMax);
        return new byte[] { CmdGain, (byte)index };
    }

    /// <summary>
    /// 构建设置低通滤波器命令。
    /// 根据 Hz 值找到最接近的索引。
    /// </summary>
    public static byte[] BuildSetLowPass(float hz)
    {
        int index = FindNearestIndex(hz / 1e6f, LowPassMHz);
        return new byte[] { CmdLowPass, (byte)index };
    }

    /// <summary>
    /// 构建设置高通滤波器命令。
    /// 根据 Hz 值找到最接近的索引。
    /// </summary>
    public static byte[] BuildSetHighPass(float hz)
    {
        int index = FindNearestIndex(hz / 1e6f, HighPassMHz);
        return new byte[] { CmdHighPass, (byte)index };
    }

    /// <summary>
    /// 构建脉冲器配置命令。
    /// 组合位域: bit7=energy, bit6=echo/thru, bit5~4=damping
    /// </summary>
    public static byte[] BuildSetPulserConfig(PulseMode mode, int energyIndex, int dampingIndex)
    {
        byte config = 0;
        config |= (byte)((energyIndex & 0x01) << 7);
        config |= (byte)((mode == PulseMode.ThroughTransmission ? 1 : 0) << 6);
        config |= (byte)((dampingIndex & 0x03) << 4);
        return new byte[] { CmdPulserConfig, config };
    }

    /// <summary>
    /// 构建设置 PRF 命令。
    /// 2 字节 little-endian uint16, 范围 0~5000 Hz
    /// </summary>
    public static byte[] BuildSetPrf(float prfHz)
    {
        ushort prf = (ushort)Math.Clamp((int)prfHz, 0, 5000);
        return new byte[] { CmdPrf, (byte)(prf & 0xFF), (byte)((prf >> 8) & 0xFF) };
    }

    /// <summary>构建通道选择命令（1→A=0, 2→B=1）</summary>
    public static byte[] BuildSetChannel(int channel)
    {
        // UI 层使用 1-based 通道号（1=Channel A, 2=Channel B）
        // 协议层使用 0-based（0=A, 1=B）
        return new byte[] { CmdChannel, (byte)(channel <= 1 ? 0 : 1) };
    }

    /// <summary>构建查询设备型号命令</summary>
    public static byte[] BuildQueryModel()
    {
        return new byte[] { CmdQueryModel };
    }

    // ── 批量命令构建 ──

    /// <summary>
    /// 批量应用全部参数，生成命令字节序列。
    /// 按顺序: 通道选择 → 增益 → 低通 → 高通 → 脉冲器配置 → PRF
    /// </summary>
    public static List<byte[]> BuildApplyAllParams(PulseParams p)
    {
        var commands = new List<byte[]>
        {
            BuildSetChannel(p.Channel),  // BuildSetChannel 内部处理 1→0, 2→1
            BuildSetGain(p.GainDb),
            BuildSetLowPass(p.LowPassHz),
            BuildSetHighPass(p.HighPassHz),
            BuildSetPulserConfig(p.Mode, p.EnergyLevel - 1, (int)p.Damping),
            BuildSetPrf(p.PrfHz)
        };
        return commands;
    }

    // ── 响应解析 ──

    /// <summary>
    /// 解析设备响应字节。
    /// ACK (0x06) = 成功, NAK (0x15) = 失败
    /// </summary>
    public static bool ParseResponse(byte responseByte, out string? error)
    {
        error = null;
        if (responseByte == Ack) return true;
        if (responseByte == Nak)
        {
            error = "NAK - 命令被拒绝";
            return false;
        }
        // 未知响应，保守返回 false
        error = $"未知响应: 0x{responseByte:X2}";
        return false;
    }

    /// <summary>
    /// 解析查询型号响应（ASCII 字符串）
    /// </summary>
    public static string ParseModelResponse(byte[] responseBytes)
    {
        if (responseBytes is null || responseBytes.Length == 0)
            return "Unknown";

        // 过滤掉 ACK/NAK 字节，提取 ASCII 文本
        var sb = new StringBuilder();
        foreach (byte b in responseBytes)
        {
            if (b is >= 0x20 and < 0x7F)  // 可打印 ASCII
                sb.Append((char)b);
        }
        string model = sb.ToString().Trim();
        return string.IsNullOrEmpty(model) ? "Unknown" : model;
    }

    // ── 增益转换辅助 ──

    /// <summary>增益索引 → dB</summary>
    public static float GainIndexToDb(int index) =>
        GainMinDb + Math.Clamp(index, GainIndexMin, GainIndexMax);

    /// <summary>增益 dB → 索引</summary>
    public static int GainDbToIndex(float db) =>
        (int)Math.Clamp(Math.Round(db - GainMinDb), GainIndexMin, GainIndexMax);

    // ── 滤波器索引查找（公开，供 Dpr500Controller 使用） ──

    /// <summary>根据 Hz 值找到最接近的低通滤波器索引</summary>
    public static int FindNearestLowPassIndex(float hz) =>
        FindNearestIndex(hz / 1e6f, LowPassMHz);

    /// <summary>根据 Hz 值找到最接近的高通滤波器索引</summary>
    public static int FindNearestHighPassIndex(float hz) =>
        FindNearestIndex(hz / 1e6f, HighPassMHz);

    // ── 内部辅助 ──

    private static int FindNearestIndex(float target, float[] table)
    {
        int best = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < table.Length; i++)
        {
            float diff = Math.Abs(table[i] - target);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }
        return best;
    }
}
