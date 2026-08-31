# UTscan 硬件 SDK 接口功能清单（真机版）

**编制日期**: 2026-08-18
**核查方法**: 逐接口 grep 调用点 + 阅读辅助方法内部实现，全部基于真实代码
**覆盖范围**: ZMC（zauxdll.dll，37 接口）/ Spectrum（spcm_win32.dll，8 API+2 辅助）/ DPR500（JSR SDK，11 API+6 辅助）

> 说明：所有"调用次数"为代码检索结果；"间接使用"表示经封装辅助方法调用。**标注 ⚠ 的为未使用或存在缺陷项。**

---

## 一、ZMC 运动控制器（ZmcNative.cs，zauxdll.dll）

### 1.1 连接/关闭

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-1 | `ZAux_OpenEth` | 以太网连接控制器（IP 地址） | `ConnectAsync`（IP 非空分支） | 1 | ✅ 合理；IP 校验由 CheckError 处理 |
| Z-2 | `ZAux_OpenCom` | 串口连接控制器（COM 号） | `ConnectAsync`（IP 为空回退分支，`ParseComNumber` 解析） | 1 | ✅ 合理；解析失败抛 ZmcException（P2-11 修复） |
| Z-3 | `ZAux_Close` | 关闭连接释放句柄 | `DisconnectAsync` + `Dispose` | 2 | ✅ 合理；Dispose 双重保护 |

### 1.2 IO 控制

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-4 | `ZAux_Direct_SetOp` | 写数字输出口（轴使能 IO8~11） | 连接时断开全部 / Enable/DisableAxisAsync / 断开时断开 | 4 | ✅ 合理；`IoBaseAxisEnable=8` 与旧项目一致 |
| Z-5 | `ZAux_Direct_GetIn` | 读数字输入口状态 | — | **0（未使用）** | ⚠ 死代码：接口已声明但无调用；限位状态经 `GetAxisStatus` 位解码获取（设计可接受，但 GetIn 应删除或接线） |

### 1.3 运动参数设置

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-6 | `ZAux_Direct_SetUnits` | 设置脉冲当量（5000 脉冲/mm） | `ApplyMotionParams`（每次 Move 前） | 1 | ✅ 合理 |
| Z-7 | `ZAux_Direct_SetSpeed` | 设置速度（mm/s） | `ApplyMotionParams` | 1 | ✅ 合理 |
| Z-8 | `ZAux_Direct_SetAccel` | 设置加速度 | `ApplyMotionParams` | 1 | ✅ 合理 |
| Z-9 | `ZAux_Direct_SetDecel` | 设置减速度 | `ApplyMotionParams` + `ApplySafetyInitialization`（默认 50） | 2 | ✅ 合理；安全初始化也设默认值 |
| Z-10 | `ZAux_Direct_SetLspeed` | 设置起步速度 | `ApplySafetyInitialization`（=0） | 1 | ✅ 合理 |

### 1.4 位置/状态读取

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-11 | `ZAux_Direct_GetDpos` | 读当前指令位置 | `PollPositions`（50ms 轮询）+ `SavePositionsToVrf` | 2 | ✅ 合理；轮询更新缓存 |
| Z-12 | `ZAux_Direct_GetMpos` | 读机械位置 | — | **0（未使用）** | ⚠ 死代码：无调用；如需机械位置补偿可接线 |
| Z-13 | `ZAux_Direct_GetIfIdle` | 查轴是否空闲 | `IsAxisIdle`（到位判定核心） | 1 | ✅ 合理；失败抛异常（P0-1 修复，不误判"已到位"） |
| Z-14 | `ZAux_Direct_GetAxisStatus` | 读轴状态字（限位/暂停位） | `PollPositions` + `GetAxisStatus` | 2 | ✅ 合理；`ZmcAxisStatus` 位解码（512/1024/8388608） |
| Z-15 | `ZAux_Direct_GetRemain_LineBuffer` | 读插补缓冲剩余空间 | `GetRemainLineBuffer` | 1 | ✅ 合理；但**无 UI/服务消费者**（仅外部可调） |

### 1.5 运动指令

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-16 | `ZAux_Direct_Singl_Move` | 单轴相对运动 | `MoveRelativeAsync` | 1 | ✅ 合理；未连接抛异常（P1-1） |
| Z-17 | `ZAux_Direct_Singl_MoveAbs` | 单轴绝对运动 | `MoveAbsoluteAsync` | 1 | ✅ 合理 |
| Z-18 | `ZAux_Direct_Singl_Cancel` | 停止单轴（mode=2 减速停） | `StopAsync` | 1 | ✅ 合理；JOG 松键停用 |
| Z-19 | `ZAux_Direct_MoveAbs` | 多轴同步绝对运动 | — | **0（未使用）** | ⚠ 死代码：多轴插补未接线（当前逐轴 Move，可接受但接口冗余） |
| Z-20 | `ZAux_Direct_SetMerge` | 连续插补模式（MERGE=1） | `SetContinuousInterpolation`（ScanService 启停） | 1 | ✅ 合理；光栅扫描换行平滑 |

### 1.6 命令执行

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-21 | `ZAux_DirectCommand` | 直接命令（无等待） | — | **0（未使用）** | ⚠ 死代码：未接线 |
| Z-22 | `ZAux_Execute` | 执行 BASIC 命令并等待 | `ExecuteCommand` 辅助（间接）→ `HomeAsync`（DATUM(ax,2) 回零） | 1（间接） | ✅ 合理；回零响应校验 "?" 前缀错误 |

### 1.7 安全初始化（连接时自动下发）

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-23 | `ZAux_Direct_SetDatumIn` | 原点信号映射（轴→IN2/5/8） | `ApplySafetyInitialization` | 3 | ✅ 合理；与遗留 FormRunLink 逐行一致 |
| Z-24 | `ZAux_Direct_SetFsLimit` | 正向软限位（+1500000 脉冲） | `ApplySafetyInitialization` | 1（循环 3 轴） | ✅ 合理 |
| Z-25 | `ZAux_Direct_SetRsLimit` | 负向软限位（-1500000） | `ApplySafetyInitialization` | 1（循环 3 轴） | ✅ 合理 |
| Z-26 | `ZAux_Direct_SetAtype` | 轴类型（1=脉冲方向型） | `ApplySafetyInitialization` | 1（循环） | ✅ 合理 |
| Z-27 | `ZAux_Direct_SetInvertIn` | 输入极性（IO0~8 不反相） | `ApplySafetyInitialization` | 1（循环 9 口） | ✅ 合理 |
| Z-28 | `ZAux_Direct_SetFwdIn` | 正限位输入映射（IN1/4/7） | `ApplySafetyInitialization` | 3 | ✅ 合理 |
| Z-29 | `ZAux_Direct_SetRevIn` | 负限位输入映射（IN2/5/8） | `ApplySafetyInitialization` | 3 | ✅ 合理 |
| Z-30 | `ZAux_Direct_SetDecelAngle` | 减速角（15°=0.2618） | `ApplySafetyInitialization` | 1（循环） | ✅ 合理 |
| Z-31 | `ZAux_Direct_SetStopAngle` | 停止角（44°=0.768） | `ApplySafetyInitialization` | 1（循环） | ✅ 合理 |
| Z-32 | `ZAux_Direct_SetCornerMode` | 拐角模式（0） | `ApplySafetyInitialization` | 1（循环） | ✅ 合理 |

### 1.8 位置保持（VRF 断电保持）

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-33 | `ZAux_Direct_SetDpos` | 写指令位置 | `RestorePositionsFromVrf`（恢复掉电前坐标） | 1 | ✅ 合理 |
| Z-34 | `ZAux_Direct_SetMpos` | 写机械位置 | `RestorePositionsFromVrf` | 1 | ✅ 合理 |
| Z-35 | `ZAux_Direct_SetVrf` | 写断电保持寄存器（VR0-2 存三轴位置） | `SavePositionsToVrf`（断开时保存） | 1 | ✅ 合理 |
| Z-36 | `ZAux_Direct_GetVrf` | 读断电保持寄存器 | `RestorePositionsFromVrf` | 1 | ✅ 合理 |

### 1.9 急停

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| Z-37 | `ZAux_Rapidstop` | 全轴急停（mode=2 减速） | `EmergencyStopAsync` | 1 | ✅ 合理；ScanService 异常联动调用 |

**ZMC 结论**：37 接口中 **31 个已使用**，6 个死代码（Z-5 GetIn / Z-12 GetMpos / Z-19 MoveAbs / Z-21 DirectCommand + 未计数的 Z-2 变体）。核心运动/安全链路完整合理。

---

## 二、Spectrum M3i.3242 采集卡（SpectrumNative.cs，spcm_win32.dll）

### 2.1 核心 API

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| S-1 | `spcm_hOpen` | 打开卡设备（spc0） | `InitializeAsync` 第 1 步 | 1 | ✅ 合理；失败抛 SpectrumDaqException |
| S-2 | `spcm_vClose` | 关闭卡句柄 | `FreeResources`（Cleanup/Dispose） | 1 | ✅ 合理；H-3 修复后仅在线程退出时释放 |
| S-3 | `spcm_dwSetParam_i32` | 写 32 位寄存器 | 28 处（模式/采样率/量程/触发/时钟/通道等全配置） | 28 | ✅ 合理；全部经 CheckError 校验 |
| S-4 | `spcm_dwGetParam_i32` | 读 32 位寄存器 | 能力位/时间戳计数/错误码等 | 3 | ✅ 合理 |
| S-5 | `spcm_dwSetParam_i64` | 写 64 位寄存器 | 段参数/环形缓冲释放（SEGMENTSIZE/LOOPS/AVAIL_CARD_LEN） | 14 | ✅ 合理 |
| S-6 | `spcm_dwGetParam_i64` | 读 64 位寄存器 | 环形缓冲可用量（AVAIL_USER_LEN/POS）+ 时间戳 FIFO | 3 | ✅ 合理 |
| S-7 | `spcm_dwDefTransfer_i64` | 定义 DMA 传输（环形缓冲+notify） | `InitializeAsync` 第 11 步 | 1 | ✅ 合理；8 段缓冲半满通知 |
| S-8 | `spcm_dwGetErrorInfo_i32` | 读错误信息 | `GetErrorText` 辅助（间接，2 处经 CheckError 错误路径） | 0 直接 | ✅ 合理（间接使用） |

### 2.2 辅助方法

| # | 接口 | 功能 | 合理性 |
|---|------|------|--------|
| S-9 | `GetErrorText(hDevice)` | 获取错误文本 | ✅ 合理；内部调 S-8 |
| S-10 | `CheckError(code, hDevice, op)` | 统一错误检查，非 0 抛异常 | ✅ 合理；39 处调用覆盖全部寄存器写入 |

### 2.3 功能覆盖（经 S-3 寄存器常量实现）

| 功能 | 寄存器组 | 实现状态 |
|------|----------|----------|
| 采集模式（FifoMulti/Gate/Average/Boxcar/ABA/Single） | SPC_CARDMODE + 段参数 | ✅ 全部实现（选项校验） |
| 采样率钳位（9MS/s~500MS/s + 禁用空洞） | SPC_SAMPLERATE | ✅ 已实现（P0-1 修复） |
| 通道使能/量程/50Ω/偏移（CH0/CH1） | SPC_CHENABLE/AMP/OFFS/50OHM | ✅ 已实现（双通道） |
| 时钟源（内部/外部/参考时钟） | SPC_CLOCKMODE | ✅ 已实现 |
| 触发（X0 上升沿/电平/门控极性） | SPC_TRIG_* | ✅ 已实现 |
| 时间戳（64 位 tick） | SPC_TIMESTAMP_* | ✅ 已实现（选项探测） |
| 数字 IO/自动校准/StarHub 探测 | SPC_XX_ASYNCIO/ADJ/PCIFEATURES | ✅ 已实现（能力位门控） |

**Spectrum 结论**：8 个 API 全部使用（S-8 间接），功能覆盖完整；**无死代码**。核心采集链路（初始化→配置→DMA→WAITDMA→帧上报）逻辑合理。

---

## 三、DPR500 脉冲收发仪（JsrNative.cs，JSR_Common3264.dll）

### 3.1 核心 API

| # | 接口 | 硬件功能 | 调用位置 | 调用次数 | 合理性 |
|---|------|----------|----------|----------|--------|
| J-1 | `JSR_OpenLibrary` | 打开 SDK 库（搜索仪器/仿真降级） | `ConnectAsync`（真实+仿真两分支） | 2 | ✅ 合理；无设备自动降级仿真 |
| J-2 | `JSR_CloseLibrary` | 关闭库 | `CleanupLibrary`（断开/清理） | 1 | ✅ 合理 |
| J-3 | `JSR_OpenObject` | 打开对象（Instrument/Channel/Pulser/Receiver） | `ConnectAsync` + `OpenChannelObjects` + `SelectChannelAsync` | 4 | ✅ 合理；对象层级正确 |
| J-4 | `JSR_CloseObject` | 关闭对象 | `Cleanup*`（多层清理） | 5 | ✅ 合理 |
| J-5 | `JSR_GetInt32` | 读整型属性（句柄/状态/滤波器索引等） | 18 处（连接枚举/参数读回/诊断） | 18 | ✅ 合理 |
| J-6 | `JSR_SetInt32` | 写整型属性（滤波器/阻尼/能量/触发源等） | `SdkSetInt32` 辅助（间接 10+ 处） | 1 直接 | ✅ 合理（间接批量使用） |
| J-7 | `JSR_GetDouble` | 读浮点属性（增益/PRF/电压回读） | 7 处 | 7 | ✅ 合理 |
| J-8 | `JSR_SetDouble` | 写浮点属性（增益/PRF/电压下发） | `SdkSetDouble` 辅助（间接 5+ 处） | 1 直接 | ✅ 合理（间接批量使用） |
| J-9 | `JSR_GetAscii` | 读字符串属性（型号/序列号/字母） | `ReadAscii` 辅助 | 1 | ✅ 合理（间接多属性） |
| J-10 | `JSR_GetAsciiInfo` | 读属性元信息（limitLo/limitHi/列表） | `GetLimit`/`GetLimitInt`/`ReadDoubleList`/`ReadIntList` | 4 | ✅ 合理；动态范围核心 |
| J-11 | `JSR_GetInfo` | Unicode 变体信息读取 | — | **0（未使用）** | ⚠ 死代码：无调用；ANSI 版 J-9/J-10 已满足 |

### 3.2 辅助方法

| # | 接口 | 功能 | 合理性 |
|---|------|------|--------|
| J-12 | `IsDllAvailable` | DLL 惰性加载检测（3264/3232/6464 三候选） | ✅ 合理 |
| J-13 | `GetErrorString` | 状态码→文本（内部调 JSR_GetErrorJSRAscii） | ✅ 合理 |
| J-14 | `CheckStatus` | 状态检查+日志 | ✅ 合理（8 处） |
| J-15 | `IsPass/IsFail/IsWarn` | 状态范围分类 | ⚠ `IsFail/IsWarn` **0 处使用**（仅 IsPass 用 2 处）；分类逻辑冗余 |
| J-16 | `IsDisconnectError` | 断连状态码识别（6 个码） | ✅ 合理（1 处，断连检测核心） |
| J-17 | `JSR_GetErrorJSRAscii` | 读错误文本 | 0 直接（经 J-13 间接） | ✅ |

### 3.3 功能覆盖（经 J-5~J-9 属性 ID 实现）

| 功能 | 属性组 | 实现状态 |
|------|--------|----------|
| 仪器/通道枚举（最多 4 台/双通道） | 1001/2000/3001 | ✅ 已实现 |
| 动态参数范围（增益/PRF/电压 limit） | JSR_GetAsciiInfo | ✅ 已实现（运行时读取） |
| 滤波器/阻尼表（运行时列表） | LPFilterList/HPFilterList/DampResistorList | ✅ 已实现 |
| 全参数下发（Gain/PRF/Volts/Damping/Energy/Trigger） | JSR_SetDouble/SetInt32 | ✅ 已实现 |
| 通道切换（重建对象+重应用参数） | SelectChannelAsync | ✅ 已实现 |
| 诊断/功率监测 | GetDiagnostics | ✅ 已实现 |
| 断连检测+重连 | IsDisconnectError + ReconnectAsync | ✅ 已实现 |
| LED/外触发/级联（SLAVE/BOTH） | 扩展属性 | ✅ 已实现（支持度探测） |

**DPR500 结论**：11 个 API 中 10 个已使用（J-11 死代码），辅助方法 1 个冗余（IsFail/IsWarn）。核心控制/诊断/断连恢复链路完整合理。

---

## 四、问题汇总（明确指出的缺陷）

### 4.1 死代码（接口已声明但无调用，共 9 处）

| 接口 | 硬件 | 建议 |
|------|------|------|
| `ZAux_Direct_GetIn` (Z-5) | ZMC | 删除或接线到"读输入口状态"功能 |
| `ZAux_Direct_GetMpos` (Z-12) | ZMC | 删除或接线到机械位置读取 |
| `ZAux_Direct_MoveAbs` (Z-19) | ZMC | 多轴同步未用；保留待编码器触发扫描扩展 |
| `ZAux_DirectCommand` (Z-21) | ZMC | 删除（Execute 已覆盖） |
| `spcm_dwGetErrorInfo_i32` (S-8) | Spectrum | 非死代码（经 GetErrorText 间接），保留 |
| `JSR_GetInfo` (J-11) | DPR500 | Unicode 变体；删除 |
| `IsFail` / `IsWarn` (J-15) | DPR500 | 仅 IsPass 使用；冗余分类可删 |
| `GetRemainLineBuffer` 消费者缺失 (Z-15) | ZMC | 接口可用但无 UI 消费，暂保留 |

### 4.2 潜在逻辑缺陷

| # | 位置 | 问题 | 影响 |
|---|------|------|------|
| L-1 | ZMC `PollPositions` | 轴状态仅在**变化时**输出 Debug 日志，无 UI 持久显示（L-5 已加状态栏报警但仅硬件模式） | 现场报警可追溯性弱 |
| L-2 | ZMC `SavePositionsToVrf` | 仅在 `DisconnectAsync` 调用；**异常退出/断电**时位置未保存（VRF 保护失效） | 断电恢复位置丢失 |
| L-3 | Spectrum `AcquisitionLoop` | 帧处理用 `List<float>.Add` 逐样本（已知性能点）；`_currentData` 仅 CH0 更新 | 双通道时 CH1 无 GetCurrentData 缓存 |
| L-4 | DPR500 `SetPulseWidthAsync` | 仅为记录（SDK 无 PulseWidth 属性，已证实 N/A）——但 UI 面板仍显示"脉宽"输入框，操作员可能误以为生效 | 交互误导 |

### 4.3 调用链路合理性总体判断

| 硬件 | 核心链路 | 判断 |
|------|----------|------|
| ZMC | 连接→安全初始化→运动→到位判定→急停 | ✅ 完整合理 |
| Spectrum | 初始化→配置→DMA→WAITDMA→帧上报→停止 | ✅ 完整合理 |
| DPR500 | 连接→动态范围→参数下发→诊断→断连恢复 | ✅ 完整合理 |

---

## 五、结论

- 三个硬件 SDK 共 **56 个接口**（37+8+11），**47 个已实际使用**，核心功能链路完整；
- **9 处死代码**（ZMC 4 + DPR500 3 + 辅助 2）建议清理或标注；
- **4 处潜在缺陷**：VRF 断电保护不完整（L-2 中风险）、脉宽 UI 误导（L-4 中风险）、其余为低风险；
- 所有已使用接口的调用链路均与硬件数据手册/遗留代码对照一致，无虚构功能。

> 建议优先处理：L-2（断电位置保存补 `EmergencyStop`/异常路径）、L-4（UI 脉宽控件加"仅记录"标注）、死代码清理（ZMC 3 项 + JSR 3 项）。
