using System.Collections.Generic;

namespace UTscan.Core.Models;

/// <summary>
/// 闸门集合（说明书 3.3.4）：一个同步闸门 + 最多 10 个数据闸门。
/// 在线扫查时最多 10 个数据闸门，但成像只使用一个。
/// </summary>
public class GateSet
{
    /// <summary>同步闸门（黄色）</summary>
    public GateConfig SyncGate { get; set; } = new()
    {
        Name = "Sync",
        Role = Core.Enums.GateRole.Sync,
        Color = unchecked((int)0xFFFFFF00),
        StartUs = 0f,
        WidthUs = 3f,
        ThresholdV = 0.5f,
        Enabled = true
    };

    /// <summary>数据闸门列表（默认 1 个，最多 10 个）</summary>
    public List<GateConfig> DataGates { get; set; } = new()
    {
        new GateConfig
        {
            Name = "G1",
            Role = Core.Enums.GateRole.Data,
            Color = unchecked((int)0xFFFF0000),
            StartUs = 5f,
            WidthUs = 5f,
            ThresholdV = 0.3f,
            Enabled = true
        }
    };

    /// <summary>用于成像的数据闸门索引（默认 0）</summary>
    public int ActiveDataGateIndex { get; set; } = 0;

    /// <summary>获取当前用于成像的数据闸门</summary>
    public GateConfig? ActiveDataGate =>
        (DataGates != null && DataGates.Count > 0 && ActiveDataGateIndex >= 0 && ActiveDataGateIndex < DataGates.Count)
            ? DataGates[ActiveDataGateIndex]
            : null;

    /// <summary>添加一个数据闸门，返回是否成功（上限 10）</summary>
    public bool TryAddDataGate(GateConfig gate)
    {
        if (DataGates.Count >= 10) return false;
        gate.Role = Core.Enums.GateRole.Data;
        DataGates.Add(gate);
        return true;
    }

    /// <summary>移除指定数据闸门</summary>
    public bool RemoveDataGate(GateConfig gate) => DataGates.Remove(gate);
}
