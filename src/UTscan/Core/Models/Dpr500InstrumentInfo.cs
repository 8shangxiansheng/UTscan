using System;

namespace UTscan.Core.Models;

/// <summary>
/// DPR500 仪器信息（P5：从 Dpr500Controller 提升至共享模型）。
/// </summary>
public class Dpr500InstrumentInfo
{
    public string ModelName { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public int ComPort { get; set; }
    public int ChainAddress { get; set; }
    public string PulserModelName { get; set; } = "";      // RP-L2 / RP-H2 ...
    public string PulserHardwareRev { get; set; } = "";
    public string ReceiverModelName { get; set; } = "";    // H02(300MHz) / R01(50MHz) ...
    public string ReceiverHardwareRev { get; set; } = "";
    public int ReceiverBandwidthMHz { get; set; }
    public double[] DampingOhms { get; set; } = Array.Empty<double>();
    public double[] LowPassMHz { get; set; } = Array.Empty<double>();
    public double[] HighPassMHz { get; set; } = Array.Empty<double>();
    public int[] ExtTriggerZOhms { get; set; } = Array.Empty<int>();
    /// <summary>是否支持 SLAVE 触发源（双通道 DPR500 级联同步，运行时经 limitHi 探测）</summary>
    public bool SupportsSlaveTrigger { get; set; }
    /// <summary>是否支持 BOTH 信号选择（双工模式，运行时经 limitHi 探测）</summary>
    public bool SupportsBothSignalSelect { get; set; }

    public override string ToString() =>
        $"{ModelName} SN={SerialNumber} COM{ComPort}@{ChainAddress} Pulser={PulserModelName} Rx={ReceiverModelName}({ReceiverBandwidthMHz}MHz)";
}