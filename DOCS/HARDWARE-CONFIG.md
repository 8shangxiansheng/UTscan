# 硬件装配配置说明（hardware.json）

程序启动时读取 `src/UTscan/hardware.json`（构建后复制到可执行文件目录，可执行文件目录优先）。

## 字段说明

| 字段 | 含义 | 默认值 |
|------|------|--------|
| `ipAddress` | ZMC 运动控制器以太网 IP；为空时回退串口连接 | `192.168.0.11` |
| `port` | 以太网端口 | `502` |
| `serialPort` | 串口名称（IP 为空时用于 ZMC 串口回退） | `COM1` |
| `baudRate` | 串口波特率 | `4800` |
| `timeoutMs` | 超时时间（ms） | `3000` |
| `sampleRate` | 采集卡采样率（**Hz**，Spectrum M3i.3242 最低 9,000,000 = 9 MS/s） | `100000000` |
| `sampleCount` | 每条 A 扫采样点数 | `1024` |
| `useMock` | `true` = Mock 模拟硬件（开发/演示）；`false` = 真实硬件（ZMC + Spectrum + DPR500） | `true` |

## 切换 Mock ↔ 真实硬件

编辑 `hardware.json` 中 `useMock` 字段，无需改代码：

```json
{
  "useMock": false,
  "ipAddress": "192.168.0.11",
  "sampleRate": 100000000
}
```

## 注意事项

- 本文件为**标准 JSON**，不允许 `//` 注释（历史版本曾含注释，导致 System.Text.Json 解析失败并静默回退 Mock——见 `docs/CODE-REVIEW-2026-08-18-v2.md` C-1）。
- `sampleRate` 单位为 Hz，低于 9 MHz 的值会被识别为 Mock 量级配置并回退 100 MHz（防护逻辑见 `Program.cs`）。
- 接入真实硬件前请确认：
  - ZMC：`zauxdll.dll` 可用（IP 或串口）；
  - Spectrum：`spcm_drv.dll` 已随驱动安装；
  - DPR500：JSR Common API SDK（`JSR_Common3264.dll`）已安装；DLL 缺失时自动降级仿真模式。
