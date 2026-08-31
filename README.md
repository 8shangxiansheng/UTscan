# UTscan — 超声显微扫查系统上位机软件

超声波无损检测（NDT）上位机控制与成像软件，驱动 **Spectrum M3i 数据采集卡**、**JSR DPR500 脉冲收发仪** 与 **ZMC 运动控制器**，实现 A/B/C 扫成像、闸门测量、TCG 深度补偿、缺陷定量与数据归档。

> 版本：v2.9.1 | 平台：Windows x86 | 框架：.NET 8 / WinForms

---

## 功能特性

- **A 扫实时显示**：多帧叠加、测量游标、-6dB 缺陷定量、深度轴（µs↔mm）切换、显示滤波、异常自动检测、历史帧回放与批量导出
- **C 扫成像**：9 种闸门成像模式、TCG（深度补偿增益曲线）、断点续扫、图像保存、ADTX/CSV 导出
- **B 扫截面**：扫查实时构建 + 数据回放提取，深度轴按材料声速换算
- **FFT 频谱**：探头频率确认、可选 Hann 窗抑制泄漏
- **硬件联调**：PRETRIGGER 时间原点修正、SPC_TRIG_DELAY 触发后延时（跳过始波）、DMA 干净重建、全链路诊断日志
- **双模式**：真机驱动 + 无硬件 Mock 仿真（`useMock`）
- **程序内更新**：manifest 多文件 + SHA256 校验 + 原子交换

---

## 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10/11 x64（x86 程序经 WoW64） |
| 运行时 | 无需安装 .NET（self-contained 发布） |
| 硬件 | Spectrum M3i.3242 采集卡 + JSR DPR500 脉冲收发仪 + ZMC 运动控制器 |
| 驱动 | Spectrum spcm_win32.dll、JSR JSR_Common3264.dll（32 位） |

---

## 构建与打包

### 环境

- .NET 8 SDK
- Windows（P/Invoke 依赖 Win32 API）

### 构建

```bash
dotnet restore UTscan.sln
dotnet build src/UTscan/UTscan.csproj -c Debug
```

### 测试

```bash
dotnet test tests/UTscan.Tests/UTscan.Tests.csproj --nologo
```

基线：327 测试全部通过。

### 发布（自包含 x86 多文件包）

```bat
scripts\publish-self-contained.cmd 2.9.1
```

输出：`dist\UTscan-win-x86\`（含 manifest.json / version.json）。

> 原生硬件 DLL（zmotion/zauxdll/spcm_win32/JSR_Common3264/DPRIO3）随仓库 `src\UTscan\Hardware\NativeDlls\` 提供，发布脚本有缺失门禁。

---

## 使用

1. 放置 `hardware.json`（示例见 `src\UTscan\hardware.json`）到程序目录；
2. 安装驱动（`dist\UTscan-win-x86\drivers\`）；
3. 启动 `UTscan.exe` → 自动连接 → 配置脉冲/采集参数 → 启用发射 → 扫查成像。

完整操作见仓库内 `DOCS/软件使用说明书-20260829.md`。

---

## 硬件接线（关键）

```
DPR500 TRIG/SYNC ──▶ M3i 前面板 TRIG 口（主外部触发=EXT0，非 X0/X1）
DPR500 RS-232  ──▶ 工控机 COM1（4800 8N1）
M3i CH1/CH2   ──▶ 超声探头
ZMC           ──▶ 以太网
```

---

## 架构概览

```
UI (WinForms) ── ScanService ── Hardware 层
  MainForm       扫查编排      SpectrumDaqCard (P/Invoke spcm)
    ├ Layout     断点续扫      Dpr500Controller (JSR SDK)
    ├ Motion     帧同步        ZmcMotionController (zauxdll)
    ├ Pulse                    Mock* (无硬件仿真)
    ├ Daq
    ├ System
    ├ Menu
    ├ Logging
    ├ Connection
    └ Shutdown
  ScanForm (UI 初始化拆分 ScanForm.UI)
  AscanForm      帧同步
  BScanForm
  FftForm
      │
      ▼
SignalProcessing: GateAnalyzer / FftProcessor / TcgCurve / BScanImageService / CScanImageService
Services: ScanSession（C 扫矩阵+波形累积）/ ConnectionOrchestrator（连接编排）/ LogFile（日志门面）
Core: AScanData / AscanFramePool（缓冲池+并发加固）/ TcgCurve / 配置模型
```

关键设计：`AscanFramePool` 所有权契约（池化复用 + 外部克隆 + `_frameLock` 串行化）、`TriggerOffsetUs` 时间原点修正、DMA 干净重建（停止后可重启）、`ScanSession` 数据状态与窗体解耦、`ConnectionOrchestrator` 连接编排下沉、`MainForm` 面板化 partial 拆分（v2.9.1）。

---

## 目录结构

```
src/UTscan/         主程序源码
  Core/             模型、接口、缓冲池、TCG 曲线
  Hardware/         DAQ / DPR500 / ZMC / NativeDlls
  Services/         扫查编排、信号处理、成像、配置、更新、连接编排
  UI/               WinForms 窗体与控件
    Forms/MainForm*.cs   主窗体 partial（Layout/Motion/Pulse/Daq/System/Menu/Logging/Connection/Shutdown）
    Forms/ScanForm*.cs   扫查窗体（ScanForm + ScanForm.UI 初始化拆分）
  Mock/             无硬件仿真
DOCS/               文档（说明书/历程/接线/移植/架构/配置）
tests/UTscan.Tests/ 单元测试（327 用例）
scripts/            发布/更新脚本
```

---

## 厂商二进制依赖（随源码提供）

本仓库 `src\UTscan\Hardware\` 已包含全部 5 个编译/打包必需的原生 DLL，**备份可独立完成 build → test → publish**：

| DLL | 来源厂商 | 位置 |
|---|---|---|
| `spcm_win32.dll` | Spectrum（M3i 采集卡） | `Hardware\NativeDlls\` |
| `zauxdll.dll` / `zmotion.dll` | 众为兴 ZMC 运动控制器 | `Hardware\NativeDlls\` |
| `JSR_Common3264.dll` | JSR DPR500 脉冲收发仪 | `Hardware\PulseGen\runtimes\win-x64\native\` |
| `DPRIO3.dll`（含 DPRIO364） | JSR DPR 系列 I/O 驱动 | 同上 |

> 说明：以上二进制系现场部署用的厂商 SDK 副本，随仓库提供以保证开箱可构建；**正式对外发布前请自行核对厂商许可条款**。OS 级驱动安装包（`spcm_drv_install_4.0.13877.exe`、`JSRControlPanelInstaller.3.3.0.0.exe`）不随源码分发，部署到目标机时另行安装。

## 已知限制

- 仅支持 x86（原生 DLL 为 32 位）；
- 双通道采集数据层就绪，UI 通道选择器待实现；
- TCG/-6dB 定量曲线需参考块实测校准。

---

## 文档

- 文档索引与引用关系：`DOCS/README.md`
- 开发历程：`DOCS/开发历程总结-20260829.md`
- 使用说明书：`DOCS/软件使用说明书-20260829.md`
- 接线组装：`DOCS/接线组装指南.md`
- 硬件移植：`DOCS/硬件移植使用说明.md`
- 架构与重构：`DOCS/架构分析与重构建议.md`
- 运动控制配置：`DOCS/运动控制配置说明.md`
- 发布记录：`DOCS/RELEASE-NOTES.md`

---

## 许可证

本项目为工业超声检测系统上位机软件，核心代码随仓库提供。**Spectrum / JSR / 众为兴厂商 DLL 与 SDK 属各自厂商所有**：本仓库内的厂商二进制为现场部署副本，正式对外分发前请从厂商确认许可条款；驱动安装包需从厂商获取。
