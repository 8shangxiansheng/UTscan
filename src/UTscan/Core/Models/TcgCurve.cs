using System;
using System.Collections.Generic;

namespace UTscan.Core.Models;

/// <summary>
/// TCG（时间补偿增益 / 深度补偿）曲线。
/// 用户编辑 N 个"声程-补偿增益"断点，两点间线性插值；按材料声速换算 µs↔mm。
/// 用于厚大衰减工件：随探伤深度自动提升接收增益，使等尺寸缺陷在不同深度产生等幅回波。
/// </summary>
public class TcgCurve
{
    /// <summary>断点：深度(mm) → 补偿增益(dB)。按深度升序保存。</summary>
    private readonly List<KeyValuePair<float, float>> _points = new();

    /// <summary>材料声速（m/s），µs↔mm 换算用</summary>
    public float SoundVelocity { get; set; } = 1480f;

    /// <summary>TCG 是否启用（默认关闭=原行为）</summary>
    public bool Enabled { get; set; }

    /// <summary>断点数量</summary>
    public int PointCount => _points.Count;

    public TcgCurve()
    {
        // 默认一条平直线（0dB 补偿），用户可编辑
        _points.Add(new KeyValuePair<float, float>(0f, 0f));
        _points.Add(new KeyValuePair<float, float>(100f, 0f));
    }

    /// <summary>添加/更新断点（深度 mm，补偿 dB）。按深度排序插入，已存在则更新。</summary>
    public void SetPoint(float depthMm, float gainDb)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            if (Math.Abs(_points[i].Key - depthMm) < 0.01f)
            {
                _points[i] = new KeyValuePair<float, float>(depthMm, gainDb);
                return;
            }
        }
        _points.Add(new KeyValuePair<float, float>(depthMm, gainDb));
        _points.Sort((a, b) => a.Key.CompareTo(b.Key));
    }

    /// <summary>删除断点（至少保留 2 个）</summary>
    public bool RemovePoint(int index)
    {
        if (index < 0 || index >= _points.Count || _points.Count <= 2) return false;
        _points.RemoveAt(index);
        return true;
    }

    /// <summary>读取断点（index→(depthMm, gainDb)），只读副本</summary>
    public (float DepthMm, float GainDb) GetPoint(int index)
        => (_points[index].Key, _points[index].Value);

    /// <summary>清空并重置为默认平直线</summary>
    public void Reset()
    {
        _points.Clear();
        _points.Add(new KeyValuePair<float, float>(0f, 0f));
        _points.Add(new KeyValuePair<float, float>(100f, 0f));
    }

    /// <summary>
    /// 查询深度(mm)对应的补偿增益(dB)，折线线性插值。
    /// 深度超出首/末断点范围时按最近端点外推（饱和）。
    /// </summary>
    public float GainAtDepthMm(float depthMm)
    {
        if (_points.Count == 0) return 0f;
        if (depthMm <= _points[0].Key) return _points[0].Value;
        if (depthMm >= _points[^1].Key) return _points[^1].Value;
        for (int i = 1; i < _points.Count; i++)
        {
            if (depthMm <= _points[i].Key)
            {
                float t = (depthMm - _points[i - 1].Key) / (_points[i].Key - _points[i - 1].Key);
                return _points[i - 1].Value + t * (_points[i].Value - _points[i - 1].Value);
            }
        }
        return _points[^1].Value;
    }

    /// <summary>查询声程(µs)对应的补偿增益(dB)：µs → mm（往返 ÷2）→ 插值。</summary>
    public float GainAtTimeUs(float timeUs)
    {
        float depthMm = timeUs * SoundVelocity / 2000f;
        return GainAtDepthMm(depthMm);
    }

    /// <summary>补偿增益 dB → 幅值线性因子（10^(dB/20)）。</summary>
    public static float DbToAmplitudeFactor(float db) => (float)Math.Pow(10.0, db / 20.0);
}
