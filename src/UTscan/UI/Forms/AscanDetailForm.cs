using System.Windows.Forms;
using UTscan.Core.Models;
using UTscan.UI.Controls;

namespace UTscan.UI.Forms;

/// <summary>
/// A 扫详情窗体（C 扫双击跳转目标视图）。
/// 显示 C 扫图像上所选扫查点的冻结 A 扫波形，叠加当前闸门配置做闸门测量，
/// 标题栏显示该点的物理坐标，实现 C 扫 ↔ A 扫联动导航。
/// </summary>
public class AscanDetailForm : Form
{
    private readonly WaveformView _view;

    /// <param name="data">所选扫查点的原始 A 扫波形（快照，不随实时采集刷新）</param>
    /// <param name="caption">标题信息（物理坐标 + 成像值等）</param>
    /// <param name="gates">叠加显示的闸门配置（来自扫查窗体当前闸门参数）</param>
    public AscanDetailForm(AScanData data, string caption, IEnumerable<GateConfig> gates)
    {
        Text = $"A 扫详情 - {caption}";
        ClientSize = new System.Drawing.Size(820, 420);
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        var lbl = new Label
        {
            Dock = DockStyle.Top, Height = 26, Text = caption, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, ForeColor = System.Drawing.Color.DimGray
        };
        Controls.Add(lbl);

        _view = new WaveformView { Dock = DockStyle.Fill };
        _view.Data = data;
        foreach (var g in gates) _view.Gates.Add(g);
        Controls.Add(_view);
        Controls.SetChildIndex(lbl, 0);
    }
}
