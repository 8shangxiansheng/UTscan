# UTscan 发布记录（Release Notes）

> 每次发版在此追加：版本号 / 日期 / 变更内容 / exe SHA256。
> 与 `dist/mock/version.json` / `dist/release/version.json` 保持一致。
---
## v2.9.1 (20260831) — 2026-08-31

**版本性质**：UI 结构拆分重构（MainForm 面板化 + ScanForm UI 初始化拆 Partial），零行为变更

**变更内容**
- **MainForm 面板化拆分**：`UI/Forms/MainForm.cs`（2412 行）按面板拆为 9 个 partial 文件——核心（字段/ctor/OnLoad）、Layout（InitializeComponent + 各 Build*）、Logging、Connection、Daq、Pulse、Motion、Menu、Shutdown；行为与布局逐字保真，无逻辑改动
- **ScanForm 拆 UI 初始化到 Partial**：`ScanForm` 改为 `public partial class`，`BuildUI`/`AddNum` 移至 `ScanForm.UI.cs`；扫查/渲染/配置逻辑留在主文件
- **发布脚本**：修复 `publish-self-contained.cmd` 中重复的 finalize 调用块
- **测试**：327/327 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.9.1

**exe SHA256**：`D6E6A0DB1B1D39C109996EC7188C6C020906E7AA407167FEA22B7610A20E51EA`

---
## v2.9.0 (20260829) — 2026-08-29

**版本性质**：架构分层解耦重构（R1~R5，按 DOCS/架构分析与重构建议 全量落地）

**变更内容**
- **R1 组合根 DI**：Program.cs 引入 `Microsoft.Extensions.DependencyInjection`，MainForm/硬件/服务经容器解析；useMock 条件装配保持 fail-closed
- **R5 配置服务注入**：新增 `IHardwareConfigService`/`HardwareConfigService`（hardware.json 加载/校验/采样回写），MainForm 构造注入替换静态调用
- **R2 ConnectionOrchestrator**：连接核心序列（探测→连接→使能→回滚→汇总→断开）自 MainForm 抽离至 `Services/Connection/ConnectionOrchestrator.cs`，事件驱动 UI 更新；MainForm 减负 ~350 行
- **R3 ScanSession**：C 扫矩阵/原始波形累积/点位映射抽离至 `Services/ScanSession.cs`，ScanForm 只做渲染；DaqSnapshot 提升共享模型
- **R4 日志门面**：新增 `LogFile` 统一落盘 %APPDATA%\UTscan\utscan.log；ScanService 恢复失败诊断由 Debug.WriteLine 提升为 Release 可追溯
- **P5 诊断契约**：三接口补齐 `DescribeState()`/`GetKpis()`/`LastConnectError`/`ConnectionKind`/`InstrumentInfo`/`ReadParamsFromHardware`/`SetPulserLedIdentifyAsync`/`EnabledChannelCount`；共享类型 DaqKpiSnapshot/DprConnectionKind/Dpr500InstrumentInfo 提升至 Core；**UI `is` 类型检查 23→1**（仅剩 DAQ 回读 Capabilities 专有分支）
- **测试**：327/327 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.9.0

---

## v2.8.4 (20260829) — 2026-08-29

**版本性质**：修复 A 扫窗「TCG」按钮永远灰色缺陷（无启用路径）

**变更内容**
- **缺陷修复**：A 扫窗「TCG」按钮初始化 `Enabled=false` 但从未有启用点 → 永远灰色不可用。现于采集有新帧时自动启用（与「-6dB」按钮同逻辑），点击可打开 TCG 曲线编辑器并在 A 扫波形上叠加深黄补偿曲线
- **构造初始化**：若此前会话编辑过 TCG 且已启用（`_tcgAscan.Enabled`），打开 A 扫窗时即自动叠加曲线并启用按钮
- **测试**：327/327 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.8.4

---

## v2.8.3 (20260829) — 2026-08-29

**版本性质**："停止后无法重新开始采集"根因修复（DMA 干净重建 + ABORT 重试，现场日志定位）

**变更内容**
- **[D-FIX 启动序列 DMA 重建]** `StartContinuousAsync` 在 `CARD_START` 前先发一次幂等的 `CARD_STOP | DATA_STOPDMA` 清除残留 DMA 状态——StopAsync 后 DMA 通道残留 ABORT，直接 START 会导致 WAITDMA 立即返回 ERR_ABORT、线程启动即退出（表现为 state=Running 但 running 立即回 false）
- **[D-FIX ABORT 重试]** `AcquisitionLoop` 的 `ERR_ABORT` 分支区分：仅 `_stopRequested==true`（正常停止）才 break；否则（重启后 DMA 未就绪瞬态）`Thread.Sleep(10)` 后重试 WAITDMA，不再静默退出采集线程
- **现场诊断依据**：`DescribeState()` 全链路状态 + 启停路径埋点（v2.8.2）+ 本次 ABORT 分支日志——"点击开始后 state=Running 但 running/acqThreadAlive 立即回 false"是硬件层 DMA 残留的特征
- **测试**：327/327 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.8.3

---

## v2.8.2 (20260828) — 2026-08-28

**版本性质**："停止后无法重新开始采集"故障诊断增强（区分 UI 层 / 硬件层根因）

**变更内容**
- **[诊断日志]** `SpectrumDaqCard.DescribeState()` 新增：输出采集卡全链路状态快照（state/everInit/handle/running/stopReq/cleanupDeferred/acqThreadAlive/sampleRate/sampleCount/needsReinit）
- **[启停路径埋点]** `DaqStartAsync` 入口记录按钮态 + 硬件状态；`NeedsReinitialize` 分支、重初始化成功/失败、正常启动成功、catch 异常均带状态快照；`DaqStopAsync` 停止后记录状态，明确是否进入"需重初始化"分支
- **[硬件守卫诊断]** `StartContinuousAsync` 两个拒绝分支（句柄释放 vs deferred 清理 / 线程残留）分别输出精确原因日志
- **用途**：现场复现"停止→开始无响应"后，`utscan.log` 中 `[诊断]` 行即可判定——若 `btnStart.Enabled=false` 且 `needsReinit=true` → 硬件层资源已释放需重初始化（A-FIX 已自动处理）；若 `btnStart.Enabled=true` 但 `StartContinuousAsync` 抛"线程仍在运行" → 线程残留（C-FIX 已处理）——两类根因均有自动恢复
- **测试**：327/327 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.8.2

---

## v2.8.1 (20260828) — 2026-08-28

**版本性质**：界面补充三个功能控件（采样点数配置入口 / A扫全屏按钮 / 深度补偿统一入口）

**变更内容**
- **[采样点数配置入口]** DAQ 页新增「采样点数」NumericUpDown（16~1000万，8 步进）——直接配置采样点数，与采样长度**双向联动**（点数=长度×采样率/1e6；改长度/采样率自动反向同步点数），`CaptureDaqParamsSnapshot` 继续按点数写入硬件
- **[A扫全屏按钮]** A 扫面板新增「全屏」按钮（F11 保留），点击切换隐藏顶部参数面板、波形占满，按钮文字在"全屏/退出"间切换
- **[深度补偿统一入口]** 主界面 视图 菜单新增「深度补偿曲线(T)...」——复用/打开扫查窗并编辑其 TCG 曲线；`ScanForm` 暴露 `TcgCurve` 属性供 MainForm 统一入口访问
- **测试**：327/327 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.8.1

---

## v2.8.0 (20260828) — 2026-08-28

**版本性质**：TCG（时间补偿增益/深度补偿）功能实现——随深度自动提升接收增益，厚大衰减工件定量关键（参照 EPOCH 650 DAC/TCG）

**变更内容**
- **[TcgCurve 模型]** `Core\Models\TcgCurve.cs`：折线断点（深度 mm × 补偿 dB）+ 线性插值 + µs↔mm 换算（×声速/2000）+ dB→幅值因子（10^(dB/20)）；断点增删/排序/重置，默认平直线 0dB
- **[TcgCurveEditorForm]** 曲线编辑器窗体：PictureBox 自绘折线 + 控制点拖拽、双击加点、右键删点、「加」按钮中点插入、重置、声速输入；深度(mm)横轴 × 补偿(dB)纵轴（±20dB）
- **[成像集成]** `GateAnalyzer.ComputeImagingValue` 新增可选 `TcgCurve` 参数（默认 null=向后兼容）：启用时预计算逐样点增益因子表（O(1) 查表），闸门内按绝对声程加权幅值；ScanForm 三处成像调用全部传入，扫查面板加「深度补偿」开关 + 「编辑曲线」入口
- **[A 扫叠加]** WaveformView 加 `TcgOverlay`：DashDot 深黄曲线叠加显示补偿增益随深度的走势；A 扫面板加「TCG」按钮打开编辑器并联动叠加
- **测试**：327/327 通过（+9 TCG 曲线测试：插值/换算/外推饱和/增删排序/默认/重置）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.8.0

---

## v2.7.5 (20260828) — 2026-08-28

**版本性质**：A 扫交互增强（全屏模式 / 增益实时条 / -6dB 定量）

**变更内容**
- **[全屏模式]** F11 切换隐藏顶部参数面板，波形占满整个窗体（对焦找波）；`_topPanel` 提升为字段，`ToggleFullScreen()` 隐藏/恢复，波形实时刷新与冻结不受影响
- **[增益实时条]** A 扫面板新增 TrackBar（-13~66dB，与 DPR500 接收增益范围一致）+ 数值显示；拖动/按键后 300ms 防抖才实际调用 `SetGainAsync` 下发——免切主界面直接调增益，联动 DPR500 硬件
- **[-6dB 定量]** 「-6dB」按钮：在闸门内自动搜索峰值 → 检波包络上向两侧找幅值=峰值×0.708（-6dB）的两个交点 → 时域宽度 → 换算缺陷尺寸（宽度×声速/2000）；结果叠加显示 -6dB 宽度（µs 或 mm，随深度轴）与缺陷定量（mm），并在波形上临时标记 A/B 交点游标。多峰回波先做包络避免落在相邻波谷
- **测试**：318/318 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.7.5

---

## v2.7.4 (20260828) — 2026-08-28

**版本性质**：深度轴单位显示全链路同步修复（闸门读数/游标标签/顶部参数控件随 µs↔mm 切换）

**变更内容**
- **[闸门读数]** `WaveformView.DrawGate` 峰值位置标注随 `DepthAxis` 换算为 mm（此前始终 µs，与 X 轴刻度混合单位）
- **[游标标签]** `WaveformView.DrawCursor` 位置标签随 `DepthAxis` 换算为 mm（此前始终 µs；AscanForm 状态栏已有 DepthAxis 分支，两处现一致）
- **[顶部参数控件]** AscanForm 新增 `AxisValueToUs`/`UsToAxisValue`/`ToggleAxisUnits`：切换「深度(mm)」时，闸门起始/宽度/平移/采样长 4 个控件**值域 µs↔mm 换算 + 标签单位同步更新**；编辑方向反向换算（mm 输入→内部 µs 存储）；`ZoomView`/`PanView`/`LoadGateToPanel`/`ResetDisplay`/`SyncSampleLengthToAcquisition` 全部改为基于内部 µs 运算后再换算显示值，防混合单位
- **测试**：318/318 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.7.4

---

## v2.7.3 (20260828) — 2026-08-28

**版本性质**：修复"停止采集后无法恢复开始"（A+C 方案：自动重初始化 + 线程引用清理）

**变更内容**
- **[A-FIX] `DaqStartAsync` 自动重初始化**：遇 `NeedsReinitialize`（Stop 超时/故障复位后资源已释放）时自动调 `InitializeAsync(_config)` + `StartContinuousAsync` 恢复采集，不再要求用户手动"文件→连接"整机重连；重初始化失败才提示手动重连
- **[C-FIX] 采集线程 finally 清 `_acqThread` 引用**：无论是否 `_cleanupDeferred`，线程真正退出时用 `ReferenceEquals` 判断并清空当前线程引用——消除 `StopAsync` 2s Join 超时后 `_acqThread?.IsAlive` 误判"线程仍在运行"导致的恢复失败；同时避免误清新线程引用
- **测试**：318/318 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.7.3

---

## v2.7.2 (20260828) — 2026-08-28

**版本性质**：H1-FIX——修改采样参数后回写 hardware.json（持久化闭环）

**变更内容**
- **[H1-FIX] hardware.json 采样参数回写**：`Program.SaveHardwareConfigSampleParams(rate, count)`——用 `JsonDocument` 最小化更新，只改写 `sampleRate`/`sampleCount` 两个键，其余键（IP/串口/触发IO 等）与顺序原样保留
- **触发时机**：`ApplyDaqParamsAsync` 应用成功后将 `_config.SampleRate/SampleCount` 回写 hardware.json——重启后采样参数保持，消除"运行期改参数→重启还原"的持久化缺口
- **容错**：文件不存在/解析失败/写权限不足均静默（不阻断采集流程）；容忍注释与尾逗号（与读取一致）
- **测试**：318/318 通过（+2 回写测试：最小化更新只改采样键保留其他键、缺失文件静默不抛）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.7.2

---

## v2.7.1 (20260828) — 2026-08-28

**版本性质**：采样点数硬编码回退值统一替换为可配置常量（`DefaultSampleCount=1024`）

**变更内容**
- **`ConnectionConfig.DefaultSampleCount`** 新增 public const 作为采样点数默认值唯一来源
- **9 处回退替换**：`SpectrumDaqCard` 字段默认值+初始化回退、`ScanService` 2 处、`MainForm` 3 处、`MockDaqCard` 字段默认值+初始化回退——全部由 `: 1024` 改为 `: ConnectionConfig.DefaultSampleCount`
- **效果**：所有回退值集中到一处定义，修改 `DefaultSampleCount` 即可全局同步，无需在 9 个文件中逐一改数字。采样点数已在 UI 层面可配置（DAQ 采样长度控件→`SampleCount = 长度×采样率/1e6`），本次改造解决的是"回退默认值"的硬编码分散问题。
- 关联硬件参数（环形缓冲/notify 大小/DMA 带宽/PRF×点数校验）均基于 `_sampleCount` 在 `InitializeAsync` 中自动重算，无需额外改动。
- **测试**：316/316 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.7.1

---

## v2.7.0 (20260828) — 2026-08-28

**版本性质**：SPC_TRIG_DELAY 触发后延时实现（跳过始波直接采集底波）

**变更内容**
- **[SPC_TRIG_DELAY]** 依据 M3i 手册 p.85 实现触发后延时：
  - `SpectrumNative` 加 `SPC_TRIG_DELAY=40810`（读写）与 `SPC_TRIG_AVAILDELAY=40800`（只读）
  - `SpectrumDaqCard` 加 `TriggerDelayUs` 属性，`ConfigureAcquisitionMode` FifoMulti 分支写 `SPC_TRIG_DELAY`（µs→采样时钟，`AlignSegmentSize` 对齐 8 的倍数）；`DaqParams` 重载同样支持
  - DAQ 面板新增「触发延迟(µs)」控件（0~10000，默认 0=禁用），经 `DaqSnapshot`→`ApplyDaqParamsToHardware` 传递
  - 语义：延迟位于触发链最末级，仅平移触发事件本身，不影响 PRETRIGGER 的 pre/post 比例（与现有 `DelayUs`→PRETRIGGER 方向相反且可共存）——用于跳过始波保留后续底波
- **测试**：316/316 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.7.0

---

## v2.6.1 (20260828) — 2026-08-28

**版本性质**：FFT 频谱链路修复（PointCount 防御 + 可选 Hann 窗）

**变更内容**
- **[FFT 输入长度修复]**：`FftForm.UpdateSpectrum` 改用 `Math.Min(PointCount, Samples.Length)` 限制 FFT 长度——池化数组可能超长、尾部为归还清零的冗余样点，直接取 `Samples.Length` 会把零值当信号，频谱被 sinc 插值污染（峰值频率不受影响，但频谱形状失真）
- **[可选 Hann 窗]**：FFT 窗体顶部新增「Hann窗」CheckBox（默认关闭，保持与既往行为一致）。开启时对输入加周期性 Hann 窗（`w[i]=0.5×(1−cos(2πi/N))`），抑制矩形窗频谱泄漏，改善探头频率峰分辨
- **测试**：316/316 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.6.1

---

## v2.6.0 (20260828) — 2026-08-28

**版本性质**：A 扫 P0 交互增强（历史帧批量导出 + 深度轴 µs↔mm 切换）

**变更内容**
- **[P0-导出] 历史帧批量导出**：A 扫「导出历史」按钮——将历史环形缓冲（512 帧）批量导出：
  - CSV 格式：每帧一个块（`# frame=索引, sample_rate, trigger_offset_us` 头行 + 样点行），Excel 可直接打开逐帧对比
  - 二进制 `.bin` 格式：逐帧写 `点数 + 采样率(float) + 触发偏移(float) + float 样点`，紧凑可解析
  - 帧数 ≥1 即启用按钮
- **[P0-深度] 横轴 µs ↔ mm(深度) 切换**：A 扫顶部「深度(mm)」勾选 + 声速输入（默认 1480 m/s）：
  - `WaveformView.DepthAxis/SoundVelocity/TimeUsToDepthMm`（depth = t_us × v / 2000，与 B 扫 `GetDepthAxis` 同式）
  - X 轴刻度按切换换算为深度 mm，单位标注 µs/mm 随切换；游标读数同步显示 mm 与 Δd
- **测试**：316/316 通过（+7 深度换算/刻度测试：1480 m/s 换算正确性、mm 刻度自适应位数）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.6.0

---

## v2.5.1 (20260828) — 2026-08-28

**版本性质**：硬件与信号处理系统审查报告 H 级问题修复（H-A/H-B/H-C/H-D + 3.2）

**变更内容**
- **[H-A/1.5] A 扫无数据诊断**：AscanForm 注入 `IPulseGenerator`，无新帧时若 DPR 已连接但未发射，状态栏显示"无触发：脉冲发射未启动"（此前泛化"无新帧"，操作员易误判采集卡故障）
- **[H-C] DataReady 派发前先克隆**：`PublishCompletedSegment` 回调前 `CloneForExternal`——订阅方可安全长期持有 `e.Data`，消除"须即时 Clone"的架构性契约依赖（防池归还复用覆盖损坏数据）
- **[H-B] FifoGate 模式切换警示**：`AcquisitionMode` setter 检测切到 FifoGate 时日志警示"触发极性变更为门控电平，DPR500 脉冲边沿可能无法触发"
- **[3.2] PRF×采样点数乘积上限**：脉冲参数应用处加 DMA 带宽校验（与 ScanService 同源 500MB/s），参数应用阶段即拦截高负载组合，防采集中途 FIFO 溢出/线程退出
- **[H-D] 发射使能联动采集就绪**：`TogglePulseOutputAsync` 启用发射前校验 DAQ 运行——未采集时拒绝并弹窗"采集卡未在采集，禁止启用脉冲发射（防无监控受激）"
- **测试**：309/309 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.5.1

---

## v2.5.0 (20260828) — 2026-08-28

**版本性质**：A 扫时间轴换算修复（P0-1 横轴映射 + P0-2 时间原点 + P1/P2 语义澄清）

**变更内容**
- **[P0-1] 波形/叠加帧横轴映射丢失 startUs**：波形与叠加帧的 x 映射原为 `tUs = startUs + i*dt` 再 `(tUs−startUs)/viewUs`——startUs 被完全抵消，等价 `i*dt/viewUs`。延迟非零时右端溢出、波形飞出窗口，且与闸门/游标坐标系（绝对时间−startUs）错位。修复为 `tUs = i*dt`（第 i 点绝对时刻），抽取纯函数 `AscanViewport.SampleToPixelX/PixelToTimeUs/TimeUsToIndex` 并加 4 个单测（含延迟非零、延迟超窗飞出、往返一致、索引钳制）。
- **[P0-2] PRETRIGGER 时间原点未计入（物理错误）**：`AScanData` 新增 `TriggerOffsetUs`（触发前偏移 µs），采集侧在 FifoMulti 下按 `pre×dt` 填入；时间轴/闸门测量/成像/CSV 导出/波形渲染统一减偏移，使触发时刻为 t=0。修复前 pre=32@100MHz 偏 0.32µs（钢深 ~0.94mm 误差），DelayUs=5µs 时偏 5µs（~14.7mm 误差）。
- **[P1-3] DelayUs→PRETRIGGER 语义澄清**：注释修正——PRETRIGGER 增加是采集窗口前移（纳入更多触发前数据），非"触发后延迟开始采"；若需后者应走 SPC_TRIG_DELAY（未实现，已注明）。
- **[P2-5] TimeOfFlightUs 语义澄清**：注释注明为"闸门内相对时间"，非物理飞行时间（物理 TOF = 绝对峰值时刻 − 零点偏移），防导出误读。
- **[P2-6] 两"延迟"命名区分**：A 扫显示层"延迟(µs)"改名为"平移(µs)"（`WaveformView.StartTimeUs` 显示镜头平移），与硬件层 `DaqParams.DelayUs`（PRETRIGGER）区分。
- **测试**：309/309 通过（+4 个 P0-1 映射单测）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.5.0

---

## v2.4.0 (20260828) — 2026-08-28

**版本性质**：扫查断点续扫功能（手动/异常停止后从中断点恢复，数据不重复不丢失）

**变更内容**
- **[断点记录]** `ScanService` 保存扫查上下文（区域/参数/采样配置）与断点位置：列粒度（每点完成记录 `_lastResumeRow/_lastResumeCol`，含首行中途停止），停止（取消/异常）时 `SaveBreakpoint` 落盘
- **[恢复触发]** `IScanEngine` 新增 `ResumeFromBreakpointAsync`/`ClearBreakpoint`/`HasBreakpoint`/`BreakpointPercent`；ScanForm 新增「续扫」按钮（显示已扫百分比），停止后自动启用
- **[恢复逻辑]** 续扫用同参数从断点行/列继续：`startRow=断点行`、`startCol=断点列`（行完成时 col 归 0 用 `_hadBreakpoint` 判定，避免从头重扫）；停止路径 `SafeResetAllAsync` 复位硬件后，续扫前自动恢复 DAQ 运行（`InitializeAsync`+`StartContinuousAsync`）
- **[数据一致性]** 数据不重复：已完成点位跳过不重采；不丢失：ScanForm 已累积矩阵/波形保留（续扫不清空）
- **修复**：停止后 `SaveBreakpoint` 判定改为"行或列任一完成即可恢复"（原仅列>0，行完成时 col=0 误判无断点）；Mock 出帧语义适配
- **测试**：305/305 通过（新增 2 个断点续扫回归：停止→断点→续扫不重复；无断点续扫返回 false）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.4.0

---

## v2.3.3 (20260828) — 2026-08-28

**版本性质**：A 扫显示 6 项缺陷彻底修复（闸门默认/联动/波形消失/阈值联动/单位标注/重启刷新）

**变更内容**
- **[#1] 数据闸门默认参数修正**：G1 默认 2µs 起点 × 8µs 宽度（不超默认 10.2µs 采样窗）；闸门面板控件默认值与 G1 数据一致，构造后主动回填面板——消除"控件显示 5/50 但实际闸门 2/8"的不一致
- **[#2] 闸门与采样长联动**：采样长变化时若闸门宽度超窗，自动钳到窗口 80%（不改变绝对起始位置）
- **[#3] 采样长 10µs 波形消失修复**：`SyncSampleLengthToAcquisition(float totalUs)` 由 MainForm 用采集配置显式计算传入——原实现从当前帧读取，DAQ 重初始化后帧为空（PointCount=0）导致同步被跳过、旧窗口（如 50µs）残留 → 10µs 数据被压缩到窗口左 20% 视觉消失。同时移除 `_numSampleLenUs.Maximum` 钳制（允许放大视图超出数据范围）
- **[#4] 闸门阈值联动**：C 扫成像路径硬编码 `ThresholdV=0.05f`（4 处）改为快照/属性传递——`ScanSnapshot` 增 `GateThresholdV`，ScanForm 增可同步属性，成像阈值与 A 扫闸门阈值一致
- **[#5] Y 轴单位标注**：单位去括号（`(V)`→`V`），纵轴顶部醒目标注 `幅值(V/mV/µV)`
- **[#6] 停止→开始波形不刷新修复**：`StartContinuousAsync` 重置 `_frameCounter=0`——原实现旧计数（如 5000）> 重启后新计数（0）导致 AscanForm `frameCount > _lastFrameCount` 永假、波形静止；AscanForm 同步处理帧计数回退
- **测试**：303/303 通过（单位断言同步更新）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.3.3

---

## v2.3.2 (20260828) — 2026-08-28

**版本性质**：闸门/回读/脉宽/阈值/状态栏问题修复（审查报告 6 项，同 v2.3.1 内容；本条目补录）

**变更内容**
- **[#3·P0] 参数回读真实化**：`ReadbackPulseParams` 调用前先触发 `ReadParamsFromHardware`（硬件寄存器回读），再按回读值同步刷新 UI 输入框（增益/电压/PRF/阻尼/能量）——消除"UI 显示 200V 但硬件实际 275V"的缓存分叉。应用参数后自动触发硬件回读。
- **[#4b·P0] 阈值未超时灰度显示**：闸门峰值读数未达阈值时用 `DimGray` 色绘并明示"未达阈值"（超阈时用原闸门颜色+显示"超阈值"），避免 0.03V 回波在 0.5V 阈值下仍被误读为检出。
- **[#4a·P1] 脉宽控件灰化禁用**：`_numWidth` 设为 `ReadOnly=true, Enabled=false`，工具提示注明"硬件决定不可软件修改"——消除"以为能改"的误导，保留记录值显示。
- **[#1/#2·P1] 闸门超窗警示**：闸门起始+宽度超出采样窗口时在控件旁显示橙色 `⚠ 闸门超窗(窗 10.24µs)` 提示，不禁止输入——消除"配置参数 50µs vs 绘图区间 10.2µs"的混淆。
- **[#5a·P2] ZMC 断连报警**：`ZmcMotionController` 新增 `ConnectionLost` 事件（断开时触发），MainForm 订阅后显示"运动控制器通信中断"并写日志——区分"未连接"与"通信中断"。
- **[#5b·P2] 新控件尺寸**：叠加/平均/游标 CheckBox 48→56px，回放按钮 48→56px，消除中文标签截断。
- **测试**：303/303 通过（现有功能零破坏）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.3.2

---

## v2.3.1 (20260828) — 2026-08-28

**版本性质**：闸门/回读/脉宽/阈值/状态栏问题修复（审查报告 6 项）

**变更内容**
- **[#3·P0] 参数回读真实化**：`ReadbackPulseParams` 调用前先触发 `ReadParamsFromHardware`（硬件寄存器回读），再按回读值同步刷新 UI 输入框（增益/电压/PRF/阻尼/能量）——消除"UI 显示 200V 但硬件实际 275V"的缓存分叉。应用参数后自动触发硬件回读。
- **[#4b·P0] 阈值未超时灰度显示**：闸门峰值读数未达阈值时用 `DimGray` 色绘并明示"未达阈值"（超阈时用原闸门颜色+显示"超阈值"），避免 0.03V 回波在 0.5V 阈值下仍被误读为检出。
- **[#4a·P1] 脉宽控件灰化禁用**：`_numWidth` 设为 `ReadOnly=true, Enabled=false`，工具提示注明"硬件决定不可软件修改"——消除"以为能改"的误导，保留记录值显示。
- **[#1/#2·P1] 闸门超窗警示**：闸门起始+宽度超出采样窗口时在控件旁显示橙色 `⚠ 闸门超窗(窗 10.24µs)` 提示，不禁止输入（闸门配置合法，仅显示受限）——消除"配置参数 50µs vs 绘图区间 10.2µs"的混淆。
- **[#5a·P2] ZMC 断连报警**：`ZmcMotionController` 新增 `ConnectionLost` 事件（断开时触发），MainForm 订阅后显示"运动控制器通信中断"并写日志——区分"未连接"与"通信中断"。
- **[#5b·P2] 新控件尺寸**：叠加/平均/游标 CheckBox 48→56px，回放按钮 48→56px，消除中文标签截断。
- **测试**：303/303 通过（现有功能零破坏）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.3.1

---

## v2.3.0 (20260828) — 2026-08-28

**版本性质**：A 扫实用功能增强（叠加对比/游标测量/平均显示/异常检测/显示滤波/历史回放/触发可视化）

**变更内容**
- **[P0-1] 多帧叠加对比**：A 扫窗口「叠加」开关——缓存最近 20 帧，以半透明灰度绘制在实时波形后方（使用同一纵轴标度，幅值对比不失真）
- **[P0-2] 测量游标**：「游标」开关——A/B 双游标（青/橙虚线）+ 实时读数（位置/幅值/时间差/幅值差），替代目视读数
- **[P0-3] 多帧平均显示**：「平均」开关——最近 16 帧逐点平均（滑动窗），缓解 10Hz 轮询 vs 高 PRF 的帧抽取随机性
- **[P1-1] 波形异常自动检测**：帧间峰位漂移（σ > 2 采样间隔）、幅值波动（CV > 30%）、FIFO 丢帧——异常时红色标注原因
- **[P1-2] 显示滤波模式**：原始/中值3/中值5/平滑下拉——仅作用于显示副本，不影响原始数据/导出/成像
- **[P2-1] 历史帧回放**：「回放」按钮 + ◀▶ 逐帧翻看最近 512 帧（约 51 秒 @10Hz）；回放帧克隆安全，不中断采集
- **[P3-1] 触发参数可视化**：KPI 标签实时显示 FIFO 溢出计数与采集周期峰值耗时（`GetKpis()`），异常时标红
- **窗体适配**：A 扫窗体加宽至 900×540，顶部面板扩展至四行容纳新控件
- **测试**：303/303 通过（现有功能零破坏）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.3.0

---

## v2.2.1 (20260828) — 2026-08-28

**版本性质**：低优先级缺陷批量修复（审查报告 16 项，防御性/状态/显示/配置四组）

**防御性修复**
- **[L5]** DPR 空通道句柄 Math.Clamp 越界防护（全句柄无效时明确报错而非泛化异常）
- **[L13]** GateAnalyzer 阈值 ≤0 不再恒判超阈（阈值非正视为无信号）
- **[IO7]** AScanData Min/Max 空 Samples 越界防御
- **[L8]** FFT 频域滤波器镜像 bin 索引 off-by-one 修正（n-k 非 n-1-k）——高通/低通/带通三处，消除 IFFT 实部频谱泄漏
- **[L9]** ClampSampleRate 空洞下边界偏移（rate==lo 不再残留空洞内）
- **[IO4]** CSV 导出 null/Samples 空防御 + 头行单位标注 `voltage_(V)`

**状态与行为**
- **[L4]** DPR 发射状态同步：ArmExternalTrigger 置 `_params.Enabled=true`/Running；DisablePulsing 关断确认后同步 false/Ready——扫查期间 UI 发射状态与实际一致
- **[L6]** ZMC 触发输出断连时用延迟前句柄拉低（消除 IO 残留高电平）
- **[L14]** ResumeAsync DPR 断连时保持暂停并抛真实诊断（不再误导为"单次触发超时"）
- **[IO3]** SetTriggerSourceAsync 返回 false 不再静默（Slave 不支持时日志明示）
- **[B3]** 加载设置 `_systemParams` 引用替换改逐字段拷贝（消除加载后旧对象引用分叉）
- **[D4]** DAQ 回读区显示对齐后点数（消除"回读 1020 vs A 扫 1024"不一致）

**舍入与显示**
- **[L10]** ScanRegion 点数截断改上取整（覆盖完整声明宽度，尾段不再漏扫）
- **[L11]** B 扫深度轴与渲染 zeroSkip 截断统一（消除 ≤1 样本轴标签偏差）
- **[L12]** A 扫零宽窗口语义修正（隐藏而非整段全显）
- **[D5]** 保存设置警示"面板值可能未应用"
- **[IO6]** 步距 UI 范围与后端校验统一（X: 0.1~1000，Y: 0.001~100）

**配置与兼容**
- **[D6]** hardware.json 补 `enableMotionController` 键（显式声明，现场可启用 ZMC）
- **[D7]** .acf 配置含 TriggerIo/脉宽（保存写入、加载回写 _config，触发配置来源单一化）
- **[C5]** 发布脚本新增原生 DLL 缺失门禁（5 个 DLL 缺一即失败，杜绝"成功但不可用"构建）

**测试**：303/303 通过（302 基线 + 1 新零宽窗口回归）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.2.1

---

## v2.2.0 (20260828) — 2026-08-28

**版本性质**：审查修复版（7 维度审查报告的 9 项缺陷修复，全部落盘）

**变更内容**
- **[L1·高] 扫描启动异常路径 `_isScanning` 锁死修复**：`ScanService.StartScanAsync` 前置守卫（ValidateScanRegion/ValidateScanDataSize/ArmExternalTriggerAsync）抛异常时不再锁死扫描——引入 `ownsScan` 局部标志 + 外层 finally 兜底复位（并发第二调用提前返回不受影响）。新增 2 个回归测试
- **[L3·高] ResetAsync 后 `NeedsReinitialize` 失效修复**：`SpectrumDaqCard.NeedsReinitialize` 并入 `_state == DaqState.Closed` 判定——故障复位（CARD_RESET）后 UI 正确提示需重新初始化，避免以默认 FIFO 模式误启动采集（段切片错位）
- **[L2·中] 扫查后 DPR 触发模式未恢复 Internal 修复**：`DisableOutputAndConfirmAsync` 在关断前触发源为 External（严格单次触发残留）时恢复 Internal 并同步 `_params.TriggerMode`，消除下次扫查被触发拓扑守卫拒绝的连环问题
- **[D2·中] ConnectionForm 补 TriggerIo/TriggerPulseWidthMs 编辑回写**：真机严格单次触发（DPR500 External）的唯一软件配置途径——此前仅能手工改 hardware.json，缺省 -1 导致真机扫描被拒。MainForm 连接回写同步
- **[D3·中] 加载设置后提示未应用**：`OnLoadSettings` 加载 .acf 后警示"参数仅回填面板未下发硬件"，防按硬件旧参数采集的脏数据
- **[F1·中] 通信链路诊断挂菜单入口**：帮助菜单新增「通信链路诊断(D)」（`LinkDiagnosticsService` 完整实现此前为死代码）
- **[B1·中] 更新回滚 `/xo` 失效修复**：`UpdateService.BuildSwapScriptContent` 失败恢复分支 `/xo`（排除旧文件）→ `/e /y` 强制覆盖，回滚真正生效
- **[IO1·中] ADTX 导入 C 扫矩阵不填充修复**：`ScanForm.LoadAdtxData` 按与扫查相同的 `GateAnalyzer.ComputeImagingValue` 逐点填充矩阵 + min/max + 点位映射——导入后 C 扫热图正常显示
- **[C1-C4] 发布链清理**：删除无代码引用的遗留包 `dist/mock`、`dist/release`（含弃用 64 位 DLL）；`update.cmd`/`rollback.cmd` 标注为被 UpdateService 取代的遗留脚本
- **测试**：302/302 通过（300 基线 + 2 新 L1 回归）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.2.0

---

## v2.1.3 (20260828) — 2026-08-28

**版本性质**：参数变更日志全面性补强（P5-FIX）——逐属性写入结果/改动前后值/断连事件落盘

**变更内容**
- **[P5-FIX] 新增 `Dpr500Controller.LogEvent` 事件**：控制器内部所有 SDK 参数写入、关断确认、错误经事件上抛，MainForm 订阅后统一写入文件日志（`utscan.log`）——弥补 `Debug.WriteLine` 在 Release 包被裁剪、现场无法看到逐属性写入结果的缺口
- **改动前值记录**：`ApplyParamsAsync` 开头记录当前生效值（增益/LP/HP 索引）→ 目标值（增益/滤波/PRF/电压/能量/阻尼/触发源）全貌，供前后对比
- **错误与断连落盘**：`HandleStatus` 失败、`DisablePulsing` 关断失败/轮询超时、IsPulsing 重试均经 LogEvent 写入文件日志（带 0x 错误码）
- **DAQ 应用日志补全**：成功/失败日志扩展为采样率/采样长度/量程/通道掩码/模式/阻抗/平均/时间戳/触发电平全参数
- **测试**：300/300 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.1.3

---

## v2.1.2 (20260828) — 2026-08-28

**版本性质**：修复 v2.1.1 回归——Array.Resize 裁剪池化数组导致 ArrayPool 归还异常（调整采样时间即触发）

**回归根因**：v2.1.1 用 `Array.Resize` 将池化数组裁剪到精确长度，但 Resize **分配的新数组不属于 ArrayPool**，`ReturnFrame → ReturnSamples → _samplePool.Return` 时抛 `ArgumentException: The buffer is not associated with this pool and may not be returned to it`。默认 1024 点恰为桶边界不触发裁剪，采样时间一改（如 1016 点）即崩溃。

**修复方案（PointCount 与数组长度解耦）**：
- `AScanData.PointCount` 改为可设置的逻辑采样点数（默认跟随 `Samples.Length`，采集路径显式设置真实点数）——池化数组保留超长（安全归还），尾部多余空间不作为有效数据
- `AscanFramePool.RentSamples` 移除 Array.Resize，直接返回池化数组；`ReturnFrame` 复位 PointCount
- `CloneForExternal` 只克隆逻辑点数对应数组段（克隆结果 PointCount==长度，精确）
- 消费方（CSV 导出、GateAnalyzer 测量/成像、GetTimeAxis/Max/Min）全部改按 `PointCount` 取数，杜绝超长尾部污染
- **回归测试**：`OversizedArrayPool` 带池关联校验（归还非本池数组即抛同款异常），新增 `RentSamples_KeepsOversizedArray_PoolReturnSafe` 等用例钉死该回归

**测试**：300/300 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.1.2

---

## v2.1.1 (20260828) — 2026-08-28

**版本性质**：数据真实性缺陷修复（池化数组长度裁剪）+ 导出真实性确认 + 样品对比测试支持（重标按钮）

**变更内容**
- **[数据真实性] 池化数组长度精确匹配**：`AscanFramePool.RentSamples` 对 `ArrayPool.Rent` 返回的数组裁剪到精确长度 `minLength`。修复非桶边界采样点数（如 1016）时 `Samples.Length > _sampleCount` 导致波形尾部出现虚假零值平线、时间轴拉长的问题。`Array.Resize` 仅在首次/跨桶交换时分配，稳态热路径零分配。
- **[导出真实性确认]**：`CsvExportService` 写 `data.Samples[i]` 逐点值，无重构/插值/平滑；`OnExportCsv` 从 `_daq.GetCurrentData()` 取数，`hardware.json` 中 `useMock=false` → 数据来源于真实 `SpectrumDaqCard` 采集卡。Mock 模式仅在 `useMock=true` 时启用，日志可区分。追加 `OversizedArrayPool` 模拟测试验证长度裁剪。
- **[样品对比测试支持]**：A 扫窗口新增「重标」按钮（`ResetViewport`）。解决 `AscanViewport` 慢释放（2%/帧）在切换至弱信号样品时纵轴量程需 ~11 秒才收敛到真实幅值的问题——点「重标」立即恢复真实比例，不同样品波形差异即时可见。
- **测试扩展**：`AscanFramePoolTests` 新增 4 用例（`RentSamples_TrimsToExactLength`、`RentSamples_ExactLength`、`PooledFrame_PointCount_MatchesSampleCount`、`OversizedPool_AfterTrim_RentReuseStaysExact`）。300/300 通过。

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.1.1

---

## v2.1.0 (20260828) — 2026-08-28

**版本性质**：采样长度逻辑修复（A 扫动态换算）+ 坐标轴刻度自适应优化

**变更内容**
- **采样长度动态显示（AscanForm.ResetDisplay）**：修复采样长度变化后 A 扫显示窗口"固定 20.48µs"的问题——`ResetDisplay` 改为从 `_daq.GetCurrentData()` 取当前帧实际总时长，换算为采样长控件的值，用户每次打开/重置 A 扫窗口时看到的采样长度与 DAQ 配置一致。
- **坐标轴刻度自适应（WaveformView）**：
  - X 轴（µs）：5 个主刻度（含首尾）——标签值按量级自适应位数（<0.1µs 3 位小数，<1µs 2 位，<100µs 1 位，≥100µs 无小数），单位 "µs" 统一标注在轴右端，相邻标签最小间距 40px 防重叠。
  - Y 轴（V/mV/µV）：4 个主刻度——满量程 ≥0.1V 用 V（1 位小数），≥1mV 用整数 mV，≥1µV 用整数 µV；刻度值与轴单位标注严格一致（`FormatAxisV` 与 `FormatAxisVUnit` 共用同一阈值），消除旧实现中 0.8V 显示 "800" 但单位标 "(V)" 的矛盾。
- **刻度格式化测试**：新增 `WaveformAxisFormatTests` 21 用例（µs 自适应位数、电压单位换算一致、值-单位一致性断言）。
- **测试**：296/296 通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.1.0

---

## v2.0.9 (20260828) — 2026-08-28

**版本性质**：恢复 P3-FIX4（notify 块对齐，v2.0.7 吸收快照时被回退导致现场"应用采集参数→DefTransfer 失败"回归）

**变更内容**
- **[P3-FIX4 恢复]** Spectrum M3i DefTransfer notify 块对齐：notify 统一对齐到 4096 字节倍数（手册要求 "must be a multiple of 4 kByte. No other values are allowed"），环形缓冲改为容纳整数个 notify 块。修复现场「应用采集参数 → 采集卡初始化失败 → notify block size isn't valid [reg=0, value=0]」
- **回归根因**：v2.0.7 从 `Backup\UTscan-v2.0.6-src-20260826`（快照早于 P3-FIX4）"覆盖吸收 14 个差异文件"时，将含 P3-FIX4 的 SpectrumDaqCard.cs 覆盖为旧版。**流程警示：快照吸收不得覆盖快照时间点之后的新修复**——后续发版吸收快照前须先 diff 快照后新增的 P3/P4 修复标记
- **验证**：notify 计算对 16~4096 点全参数域均为 4096 倍数；275/275 测试通过

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.0.9

---

## v2.0.8 (20260828) — 2026-08-28

**版本性质**：A 扫缓冲池化并发加固 + KPI 观测 + 池化单元测试（实时性配套交付）

**变更内容**
- **[P4-FIX] 池化并发加固**：`PublishCompletedSegment` 换槽+归还旧帧与 `GetCurrentData` 克隆共用 `_frameLock` 互斥，闭合“读取线程克隆到已归还复用数组”的竞态窗口；`DataReady` 锁外派发避免慢订阅方阻塞临界区
- **[P4-FIX] 池槽回收**：`InitializeAsync` 重初始化覆盖槽位前先归还旧池化帧，消除池槽泄露
- **[P4-FIX] 防重复归还**：`AscanFramePool.ReturnFrame` 双重归还/哨兵/空数组一律跳过，防池内重复引用
- **KPI 观测**：新增 `SpectrumDaqCard.GetKpis()`（`DaqKpiSnapshot`：PublishedFrames/CycleCount/Last-MaxCycleMs/Last-MaxCallbackMs/FifoOverrunTotal/AcquisitionThreadAborts），采集耗时、回调耗时、丢帧计数、异常计数全量可观测
- **单元测试**：新增 `AscanFramePoolTests` 8 用例（对象复用/克隆隔离/防重复归还/有界性/KPI 计数），275/275 通过

**文档化**
- `docs\A扫缓冲池化与KPI设计.md` 新增（所有权契约 R1-R8 / KPI 定义 / 测试覆盖 / 真机验证要点）
- `docs\RELEASE-NOTES.md` 更新为 v2.0.8

---

## v2.0.7 (20260827) — 2026-08-27

**版本性质**：吸收 v2.0.6 现场源码快照（三硬件首次现场连接里程碑）+ 应用参数输出状态恢复修复

**变更内容**
- **吸收 v2.0.6 现场修复（Backup\UTscan-v2.0.6-src-20260826）**：覆盖吸收 14 个差异文件，含关键现场修复——
  - **[P3-FIX2]** Spectrum M3i POSTTRIGGER 修复：`AlignSegmentSize` 将 SEGMENTSIZE 对齐 8 的倍数，PRETRIGGER+POSTTRIGGER=SEGMENTSIZE 且 PRETRIGGER>0，解决 CARD_START 时 "posttrigger exceeds segment size" 报错
  - **[P3-FIX3c/3d]** JSR SDK 句柄枚举改为元信息驱动：`JSR_GetAsciiInfo` 先取 elementCount 再按量分配，消除硬编码 4 在单台仪器时的 0x83F 整次拒绝（仪器/通道/Pulser/Receiver 四层均修复）
- **应用参数输出状态恢复（本会话修复）**：`ApplyPulseParamsAsync` 保存应用前输出状态，参数操作完成后自动恢复发射；刷新发射按钮文字与硬件状态一致，避免"按钮显示停止发射但实际已停止"的错位
- **采集卡连接回归修复（v2.0.7 现场验证）**：`HardwareProbeService` 启动探测阶段曾对采集卡执行 `spcm_hOpen("spc0")` + `spcm_vClose`（v2.0.6 引入），随后正式连接 `ConnectHardwareCoreAsync` 再次 `spcm_hOpen` 时驱动返回空句柄，报"打开设备 spc0 失败（驱动未安装或无可用卡）"——而 v1.8.8 能正常连接（软回归）。已移除探测阶段的设备打开，探测仅做 DLL 文件检查，采集卡设备连接完全交由正式连接（成为首次打开，消除竞态）
- **测试**：249/249 通过（0 新增，0 破坏）

**文档化**
- `docs\RELEASE-NOTES.md` 更新为 v2.0.7
- `PLAN.md` 版本记录更新

---

## v2.0.0 (20260825) — 2026-08-25

**版本性质**：硬件连接修复（P0/P1/P2 六项）+ 程序内更新机制落地

**硬件连接修复（Phase 1）**
- **[P0] 连接对话框全字段回传**：`ConnectionForm` 重写——超时输入（NumericUpDown 1000–60000ms）、触发 IO、触发脉宽全部可编辑并回写 `ConnectionConfig`；DPR500 串口标注"由 JSR SDK 自动发现（4800,8,N,1），无需配置"
- **[P0] 触发链路在线自检**：链路诊断新增触发回环检查，复刻生产链路（DPR ArmExternalTrigger → ZMC `PulseTriggerOutputAsync` 单边沿 → Spectrum X0 帧计数推进）；无运动控制器或 triggerIo<0 时降级 Skipped 不伪造结果；结束 finally 恢复安全状态（关断确认+触发源回 Internal）
- **[P1] hardware.json 超时 3000→8000ms**（生效值 Math.Max(config,5000)，此前实际 5000ms 偏短）
- **[P1] DPR 重连竞争保护**：连接超时后 `_lastTimeoutTick` 记录时刻，10 秒冷却门快速失败提示剩余秒数；超时分支 `CleanupAll()` 改 fire-and-forget 避免与后台孤儿扫描竞争 `_sdkLock`
- **[P2] 关断确认轮询加宽**：`DisablePulsing` 5×100ms→10×100ms（共 1s），单次读失败重试一次再判通信断，保持 fail-safe false 语义
- **[P2] 仿真模式 LED 黄色警示**：`SetLed` 扩展 simulated 参数，DAQ/脉冲 LED 在 Mock 或 DPR Simulation 连接下显示 Gold 色"仿真模式"

**部署能力（Phase 2）**
- **UpdateService 新增**：程序内手动「检查更新」（帮助菜单）——完整目录覆盖+单文件 SHA256 差异跳过；顺序升级校验（manifest.Previous 必须等于当前版本，禁止跳级/降级）；hardware.json 双重防线绝不覆盖；swap-on-exit 分离脚本模式（staging→pending.json→退出→UpdateSwap.cmd 备份/覆盖/重启），启动时自动检测待定升级；含 robocopy 镜像备份与失败恢复路径
- **发布脚本改造**：`publish-self-contained.cmd` 多文件发布（PublishSingleFile=false，R2R）+ `finalize-dist.ps1` 生成逐文件 SHA256 的 manifest.json 与 version.json；驱动安装包与部署清单入 dist\drivers\
- **新增测试**：UpdateServiceTests 30 例（ParseVersion/CompareVersions/IsProtected 红线/CheckForUpdate 全分支/staging 保护排除/pending 检测/交换脚本 ASCII 与内容契约/SHA256 已知向量）

**测试**：246/246（30 新增，0 破坏）

**已知限制**
- 更新包须解压到应用目录 `_update\` 后由用户在程序内触发；不做自动检测（决策 A）
- 版本链规则要求按发布记录顺序逐版升级；降级请使用 `.update\backup` 回滚脚本
- 发布脚本 RID 固定 win-x86（硬件 DLL 32 位），不得单方面更改

---


## v1.8.8 (20260825) — 2026-08-25

**版本性质**：日志系统重构 + 硬件连接诊断增强

**修复项**
- **日志系统升级**：新增 `LogLevel.Debug`（UI 灰色）；引入带模块标签的结构化日志方法 `LogI/LogS/LogW/LogE(module, msg)`，格式 `[HH:mm:ss] [模块] 消息`，模块标签：系统/ZMC/DAQ/DPR
- **连接失败诊断可见**：DPR500/Spectrum/ZMC 三个硬件类新增 `LastConnectError` 属性，连接失败时存储详细 SDK 错误码+阶段信息；`ConnectHardwareCoreAsync` 读取并输出到 UI 日志
- **分阶段连接日志**：`ConnectHardwareCoreAsync` 重构为每设备分阶段诊断输出（DLL 检查→SDK 调用→设备信息→汇总），失败时自动附带排查指引
- **~80 处 Log 调用迁移**：MainForm 全部旧签名 `Log(string, LogLevel)` 迁移到新签名 `LogI/LogW/LogE(module, msg)`

**测试**：216/216（0 新增，0 破坏）

---

## v1.8.7 (20260825) — 2026-08-25

**版本性质**：探测服务句柄占用修复 + DPR500 真超时 + DAQ 异常可见

**修复项**
- **Spectrum 探测句柄占用**（最关键）：`HardwareProbeService` 调 `InitializeAsync`→`StopAsync` 占用卡句柄，后续 `ConnectHardwareCoreAsync` 二次 `InitializeAsync` 失败 → 改为仅 `spcm_hOpen`/`Close` 验证卡物理存在，不执行完整初始化
- **DPR500 超时保护失效**：`GetAwaiter().GetResult()` 同步阻塞无法中断 native P/Invoke → 改为 `Task.WhenAny(connectTask, timeoutTask)`，超时后立即返回
- **DAQ 异常不可见**：`SpectrumDaqCard.InitializeAsync` 内部 catch 吞异常只返回 false → 改为抛出 `SpectrumDaqException` 向上传播，UI 日志可看到真实 SDK 错误（如"驱动未安装"、"无可用卡"）

**测试**：216/216（0 新增，0 破坏）

---

## v1.8.6 (20260825) — 2026-08-25

**版本性质**：硬件部分连接 + UI 诊断修复

**修复项**
- **部分连接支持**（核心）：`ConnectHardwareCoreAsync` 重构为每设备独立 try/catch——运动控制器连接失败不再阻塞采集卡与脉冲收发仪的连接。用户可在运动控制器不可用时仍操作脉冲参数和采集功能
- **LED 即时点亮**：各设备连接成功后**立即**点亮对应 LED（原实现等全部 3 设备成功后一次性点亮，任一失败则全部不亮）
- **按钮即时刻用**：脉冲页/采集页按钮在各自设备连接成功后立即启用，不受其他设备状态影响
- **断开处理补全**：`OnDisconnectClick` 补全 `_btnPulseLed` 和 `_btnDaqApply` 的禁用逻辑
- **状态栏分层显示**：全部连接→绿色；部分连接→橙色（显示未连接设备名）；全失败→红色

**测试**：216/216（0 新增，0 破坏）

---

## v1.8.5 (20260825) — 2026-08-25

**版本性质**：DPR500 脉冲收发器连接排查修复

**修复项**
- **DPR500 连接超时保护**（最关键）：`JSR_OpenLibrary` 内部扫描串口无超时限制，设备未上电时可能永久阻塞 UI 线程 → 用 `CancellationToken` + `Task.Run` 包装，超时=`config.TimeoutMs`（最小 5000ms）
- **探测服务双连问题**：`HardwareProbeService` 对 ZMC/DPR500 执行 `ConnectAsync`→`DisconnectAsync`，然后 `ConnectHardwareCoreAsync` 再连一次（双连浪费时间且可能遗留 SDK 状态）→ 探测改为仅检查 DLL 存在性
- **DPR500 串口配置可见性**：新增 `ProbeDprSerialConfig()` 检查串口参数合规性（4800,8,N,1 vs 配置值），不一致时告警
- **连接失败诊断增强**：DPR500 连接失败时输出 6 项排查清单（电源/USB/串口参数/SDK/设备管理器/超时），替代原先简略提示
- **连接成功设备信息**：DPR500 连接成功后输出型号、COM 端口号、通道信息

**已知限制**：JSR Common SDK 内部管理串口参数，应用层无法直接指定 COM 端口号。SDK 通过 USB vendor/product ID 或扫描默认串口自动发现 DPR500。如设备不在默认端口，需通过 JSR Control Panel 配置或确保 USB 驱动正确安装。

**测试**：216/216（0 新增，0 破坏）

---

## v1.8.4 (20260825) — 2026-08-25

**版本性质**：启动时硬件探测

**新增功能**
- `HardwareProbeService`：真机模式启动时自动探测三个设备（ZMC 运动控制器 / DPR500 脉冲收发仪 / Spectrum DAQ 采集卡）的 DLL 可用性与连接状态
  - 第一阶段：检查 DLL 文件是否存在
  - 第二阶段：快速连接→断开验证（每个设备一次往返）
  - 探测结果以 `[OK]`/`[!!]`/`[XX]`/`[--]` 前缀输出到操作日志
  - 状态栏实时显示探测进度与最终状态
- Mock 模式跳过探测，直接显示 Mock 状态

**测试**：216/216（0 新增，0 破坏）

**exe SHA256**：（待真机验证后补充）

---

## v1.8.3 (20260821) — 2026-08-21

**版本性质**：三设备联动接线断层修复 + DAQ 延迟校验 + 端到端测试

**修复项**
- **TriggerIo 接线断层**（最关键）：`hardware.json` 缺少 `triggerIo`/`triggerPulseWidthMs` 字段 → `ConnectionConfig` 有定义但无消费者 → `ScanForm` 未传递到 `ScanParams` → 真机扫描因 `TriggerIo=-1` 被拒绝启动。修复：hardware.json 补字段 → ScanForm 构造接收 ConnectionConfig → ScanParams 实例化时传递 TriggerIo/TriggerPulseWidthMs
- **DaqParams.DelayUs 未生效**：`DelayUs` 定义在 DaqParams 但从未写入 Spectrum 寄存器。修复：`InitializeAsync(config, daqParams)` 将 DelayUs 转换为 PRETRIGGER 样本数，在 FifoMulti 模式下写入 SPC_PRETRIGGER
- **PRF/采样率前置校验**：PRF × 采样点数超过 DMA 安全带宽时拒绝启动（防 DMA 溢出丢帧），阈值 500 MB/s
- **ScanService TOCTOU 竞态**：`_isScanning` 的 check-then-set 非原子操作，两个并发 `StartScanAsync` 可同时通过守卫。修复：`_scanLock` 包裹 check+set
- **ScanService 验证失败状态重置**：前置守卫/校验失败时未重置 `_isScanning`，导致后续扫描请求被永久拒绝。修复：每个 throw 前显式 `_isScanning = false`

**新增文件**
- `tests/UTscan.Tests/ThreeDeviceLinkageTests.cs`：三设备端到端联动验证（9 个测试用例）
  - TriggerIo 接线断层（Mock 单次触发路径 / PRF 回退 / 无脉冲发生器）
  - 触发链完整性（ArmExternalTriggerAsync + PulseTriggerOutputAsync 调用链）
  - 状态同步与并发安全（并发启动仅一个执行 / Stop 中断 / Pause 恢复）
  - 触发拓扑校验（External 模式拒绝）
  - 扫描参数传递（速度加速度 / 步进尺寸 → 点数）

**测试**：216/216（+10 新增，+6 已有修复）

**exe SHA256**：`98F75129D7147C3295761F4BD59F7D0F581FD316B0466BEF17CDBB22ADFF0D70`

---

## v1.8.2 (20260821) — 2026-08-21

**版本性质**：通信链路诊断服务

**新增功能**
- 帮助 → 通信链路诊断：逐级检测 DPR500 脉冲收发器与 Spectrum DAQ 采集卡通信链路
  - DPR500：DLL 探测 → 连接状态 → PRF 参数下发/回读验证 → 健康状态（功率超限/脉冲状态）
  - Spectrum：DLL 探测 → 连接状态 → 能力位图 → 500ms 采集帧计数验证
- 诊断报告输出到控制台 + 弹窗摘要，明确标注故障环节

**exe SHA256**：`98F75129D7147C3295761F4BD59F7D0F581FD316B0466BEF17CDBB22ADFF0D70`

---

## v1.8.1 (20260821) — 2026-08-21

**版本性质**：运动控制参数交叉审查修正（依据 `运动控制系统资料/运动控制软件说明-huang.docx`）

**修复项**
- Z 轴脉冲当量从 2000 修正为 1000（伺服+10mm 丝杆导程，原来 Z 轴位移被放大 2 倍）
- W1/W2 旋转轴脉冲当量设为 27.78 脉冲/度（0.1KW 伺服）
- 回零前显式设 SPEED=1500 / CREEP=500，防止控制器残留值导致回零速度异常
- 回零后等待轴空闲（SpinWaitForIdle）再收紧 RS_LIMIT 至 -300 脉冲（防反向过冲）
- 连接初始化补设 SPEED=500 / ACCEL=DECEL=5000（防首次运动以默认 0 运行）
- 扫描步距代码级校验：StepX [0.1, 1000]mm，StepY [0.001, 100]mm
- 清理 MainForm.cs `ZmcAxisStatus.IsMoving` 编译错误（bit2 是通讯错误非运动位）

**exe SHA256**：`98F75129D7147C3295761F4BD59F7D0F581FD316B0466BEF17CDBB22ADFF0D70`

---

## v1.8.0 (20260819) — 2026-08-19

**版本性质**：静态代码复审整改（按 `docs/静态代码复审报告-整改后复核-2026-08-18.md` 全部可修复项）

**高危修复（RH 系列，11 项）**
- RH-1 ZmcMotionController 线程安全：_threadLock 统一保护轴使能/运动/急停
- RH-2 SpectrumDaqCard 生命周期：FreeResources 移入 _lifeLock，防旧线程误关新资源
- RH-3 WaitForNewFrameAsync 二次校验 IsRunning，避免 _stopRequested 后误报超时
- RH-4 ScanService 线程同步：_scanLock 保护 Start/Stop 并发
- RH-5 Pause/Resume 联动 DPR 输出开关
- RH-6 ApplyParams 前先 DisablePulsing（安全联锁）
- RH-7 DisablePulsing 返回 bool + 确认轮询（防止脉冲未停）
- RH-8 ResetAsync 故障复位：CARD_STOP + CARD_RESET
- RH-9 DAQ Error 事件 UI 显示（实时告警）
- RH-10 DAQ FIFO 溢出：连续 3 次触发自动停止采集
- RH-11 扫描数据矩阵上限 1.5 GiB（x86 内存保护）

**中风险修复（RM 系列，8 项）**
- RM-1 Spectrum deferred 清理状态同步：NeedsReinitialize 禁止 Start
- RM-2 后台线程 WinForms 控件访问：BeginInvoke/Invoke 安全调用
- RM-3 DAQ 参数 UI 线程快照（ReadbackDaqParams 用 Invoke 读取控件值）
- RM-4 ZMC 原生调用 _nativeLock 防重入（Timer 触发的调用与 Stop 竞争）
- RM-5 ScanService ScanRegion 快照（步距写入后不可变）
- RM-6 Finalizer 安全：Dispose(bool) 模式，SpectrumDaqCard/Dpr500Controller/ZmcMotionController 仅关非托管句柄
- RM-7 Stop/Close 返回码记录日志（故障诊断证据）
- RM-8 ADTX 导入内存校验（512MB 上限 + MaxDimension 100K）

**新增问题修复（NEW-M 系列，7 项）**
- NEW-M-1 ZmcMotionController _closing 标志修复（Disconnect 中重置为 false）
- NEW-M-2 ScanService 波形均值计算修正
- NEW-M-3 CH2 单通道解交织（physicalCh 数组映射）
- NEW-M-4 Dpr500Controller OpenChannelObjects 失败回滚
- NEW-M-5 JsrNative.IsPass 对齐厂商宏：status==0 或 1024~2047
- NEW-M-6 SpectrumDaqCard FifoBoxcar 模式使用 SPC_BOX_AVERAGES
- NEW-M-7 ADTX 文件导入前矩阵内存预检

**架构改进**
- DAQ 接口新增 `ResetAsync()` + `NeedsReinitialize` 属性
- 关闭顺序统一：Program.cs DPR→DAQ→ZMC（先关脉冲→停采集→停运动）

**打包重构**
- `dist/UTscan-win-x86/` 废弃 → 拆分为 `dist/mock/`（纯软件）+ `dist/release/`（真机）
- Mock 版：无原生 DLL，567 KB，useMock=true
- Release 版：含 zmotion.dll/zauxdll.dll/spcm_win32.dll + drivers，3.4 MB

**测试**：187/187

**exe SHA256**：`98F75129D7147C32...`（完整见 dist/mock/version.json 或 dist/release/version.json）

---

## v1.7.0 (20260818) — 2026-08-18

**版本性质**：静态审查补充整改（按 `docs/静态代码审查补充报告-新增问题-2026-08-18.md`）

**高危修复**
- NH-1/NH-2 Spectrum 生命周期锁（延迟清理期间拒绝初始化，防旧线程误关新资源）
- NH-3 参数应用与脉冲启用分离（ApplyParams 不发射，SetOutputEnabledAsync 显式启用）
- NH-4 功率超限抛异常 + 禁用脉冲（安全联锁）
- NH-6 B 扫采样率用 DAQ 实际值（消除 10^6 倍尺度误差）
- NH-7 扫描矩阵 5000 万点/512MB 上限校验（防 OOM）
- NH-8 三设备就绪检查（ZMC/DAQ/DPR 全就绪才允许运动）

**中风险修复**
- NM-1 双通道时间戳按 segment 共享（CH0/CH1 同一触发）
- NM-3 DAQ 参数 UI 线程快照（后台不读控件）
- NM-4 连接窗体端口提示（不参与 ZMC）

**文档化**
- NH-5 编码器模式语义说明（实为逐点停稳+行缓存，非真编码器同步）
- NL-1 AcquisitionsPerPoint 语义说明（当前未参与流程）

**测试**：186/186

**exe SHA256**：`A0A2FB3AAEDB0FE4...`（完整见 dist/mock/version.json 或 dist/release/version.json）

---

## v1.6.0 (20260818) — 2026-08-18

**版本性质**：静态审查整改（按 `docs/静态代码审查报告-Windows-PInvoke与硬件时序-2026-08-18.md` 全部 H/M 项）

**高危修复**
- H-1 ZAux_Execute 签名校准（4 参数，与遗留 zmcaux.cs 一致，消除 x86 调用栈错位）
- H-2 ZMC 原生调用串行化（_nativeLock + Timer 防重入）
- H-3 安全初始化/运动参数全 CheckError（失败不提交连接）
- H-4 帧同步超时抛异常终止扫查（禁止旧帧发布）
- H-5 Spectrum 先 StopDMA 再 Join + Cleanup 补 Stop/Reset
- H-6 DPR500 关闭前 DisablePulsing（TriggerEnable=FALSE + IsPulsing 回读）
- H-7 ApplyParams 关键写入 bool 汇总失败抛异常
- H-8 ScanService 注入 pulse，SafeResetAllAsync 统一故障复位（含用户停止停轴）
- H-10 连接事务回滚（DAQ/DPR 失败回滚 + 真机拒绝仿真）
- H-11 关闭超时不再并发 Dispose（单所有者 + 幂等）

**中风险修复**
- M-1/M-2 ZMC 重复连接关旧句柄 + 关闭与轮询共用锁
- M-3 扫前触发模式校验（Internal+EXT0 拓扑）
- M-4 DMA 可用区边界校验；M-5 Dispose 确认线程退出再释放事件
- M-6 Pulser/Receiver Open 失败置句柄 0；M-8 NativeLibrary.Free
- M-9 配置来源绝对路径 UI 显示；M-10 CSV InvariantCulture

**测试**：186/186

**exe SHA256**：`265A3236B7F6F5C9...`（完整见 dist/mock/version.json 或 dist/release/version.json）

---

## v1.5.0 (20260818) — 2026-08-18

**版本性质**：需求对齐整改（按 `.workbuddy/需求对齐分析报告`：P0 对焦找波流程 + 后端就绪的 UI 接线）

**P0 新增功能**
- 对焦找波：A 扫窗体新增延迟时间（波形平移）+ 采样长度（缩放）按钮，说明书 4.3/4.9 找表面波流程
- 同步闸门 UI（黄色显示 + 参数面板，GateAnalyzer 联动已实现）+ 波形类型选择（RF/检波/正/负半波）
- FFT 频谱窗体（F5，确认探头频率/滤波范围）
- 运动自检（限位遍历）+ 相对步进 ◀▶ + 轴置零（SetPositionZero，说明书 4.5 定位起始点）
- 超行程（±300mm）+ 超 16G 数据量扫前校验（防撞机/防爆盘）
- C 扫保存图像 + 成像模式补齐（Mean 均值，9 种）

**P1 新增功能**
- 多数据闸门（最多 10 个 G1~G10，下拉选择 + 添加/删除）
- DPR500 LED 识别按钮（机内板卡识别）
- 离线滤波（中值/低通）重算 C 扫 + D 扫视图（按列切 → BScanImageService）

**测试**：186/186

**exe SHA256**：`8481FC54368AE821...`（完整见 dist/mock/version.json 或 dist/release/version.json）

---

## v1.4.0 (20260818) — 2026-08-18

**版本性质**：最终审查整改（按 `docs/最终代码审查-PInvoke与时序.md` 全部 H/M/L 项）

**高危修复**
- H-1 DPR500 触发源：默认改 Internal（DPR500 自主 PRF 发射，TRIG/SYNC 输出给 Spectrum X0）；SetModeAsync 映射修正（原 PulseEcho→External 反转错误）；UI 下拉默认内部
- H-2 关窗补 DPR500 断开（高压立即关闭）；H-3 超时改强制 Dispose
- H-4 帧同步：IDataAcquisition 新增 GetCurrentFrameCount/WaitForNewFrameAsync；ScanService 到位后等新帧再取数（消除读旧缓存帧）

**中风险修复**
- M-1 ConnectionLost UI 订阅（日志+LED+状态栏）；M-2 FIFO 溢出联动停止（连续 3 次触发）
- M-3 扩展 API 提升到接口（SelectChannel/SetTriggerSource/SetSignalSelect），Mock 统一实现
- M-4 Zmc GetPosition 同步读硬件；M-5 采集线程预分配缓冲

**低风险优化**
- L-1 ZMC 句柄校验；L-2 ExecuteCommand 动态缓冲；L-3 JsrAsciiInfoStruct Pack=8
- L-4 急停后等待；L-5 Mock 增益对齐 -13~66；L-6 文件日志；L-7 读回哨兵验证

**测试**：185/185

**exe SHA256**：`3210B503C56A7098...`（完整见 dist/mock/version.json 或 dist/release/version.json）

---

## v1.3.0 (20260818) — 2026-08-18

**版本性质**：硬件接口整改（按 `docs/硬件SDK接口功能清单.md` 执行）

**修复**
- L-2 VRF 断电位置保护：`EmergencyStopAsync` 与 `Dispose` 补充 `SavePositionsToVrf`（原仅 DisconnectAsync 保存，异常退出/断电时掉电前坐标丢失）
- L-3 双通道缓存：`SpectrumDaqCard` 增加 `_currentDataByChannel`，CH0/CH1 各自缓存最近帧；新增 `GetCurrentData(int channel)` 重载
- L-4 脉宽 UI 标注：DPR500 脉宽控件加 ToolTip（"由脉冲器硬件决定，仅记录"）+ 应用时日志明示
- 死代码清理：ZMC 删 GetIn/GetMpos/MoveAbs/DirectCommand；DPR500 删 JSR_GetInfo(+JsrInfoStruct)/IsFail/IsWarn

**测试**：183/183（新增 GetCurrentDataByChannel 用例）

**exe SHA256**：`34DA98D54B4B4879...`（完整见 dist/mock/version.json 或 dist/release/version.json）

---

## v1.2.1 (20260818) — 2026-08-18

**版本性质**：缺陷修复（CSV 导出路径 UX + 发布脚本编码）

**修复**
- CSV 导出：4 处 SaveFileDialog 增加 `InitialDirectory`（默认系统文档目录，避免文件"找不到"）；无数据提示明确"请先执行 文件→连接"
- 发布脚本 `publish-self-contained.cmd`：编码从 UTF-8(BOM) 转为 GBK（中文 cmd 代码页兼容）；⚠ 字符替换为 [!]
- **发布规范确立**：AI-DEV-GUIDE 新增 §13 发布与交付同步规范——任何改动必须同时同步 src（Mock）与 dist（真机）

**exe SHA256**：`4433754B612B13B30E85C8BB8F53CE3F17E5E5DE67B44C0C5713E699CB41FE11`

---

## v1.2.0 (20260818) — 2026-08-18

**版本性质**：真机联调就绪版（首个带版本机制的正式发布）

**新增功能**
- 版本号机制：程序启动读取 `version.json`（发布脚本自动生成，含 version/build/date/exeSha256），标题栏与关于框显示真实版本
- 离线更新链路：`update.cmd`（SHA256 校验→备份→原子替换→日志）+ `rollback.cmd`（一键回滚），随交付包提供
- 完整交付包：`dist/mock/` + `dist/release/` 含 exe + version.json + update/rollback + drivers（驱动/SDK 安装器）+ 部署清单

**修复**
- 发布脚本 RID 修正：`win-x64` → `win-x86`（与 PlatformTarget=x86 匹配，消除 NETSDK1032 构建失败；x86 应用在 x64 工控机经 WoW64 正常运行）
- Dockerfile 同步修正 RID
- 文档一致性：CONTAINERIZATION 发布命令、测试数（181/181）、部署清单同步

**已知说明**
- JSR SDK（`JSR_Common3264.dll`）不随应用内嵌，需按部署清单安装 JSRControlPanelInstaller（drivers/ 内含）
- 构建可能有 NU1900 警告（NuGet 漏洞数据源网络不可达），非代码问题；CI 可用 `-p:NuGetAudit=false`

**exe SHA256**：`2262B7DD3979D5ECF6F19B9C1F2D9F1618CC25E5EC1194484B9401DD5B066006`

---

<!-- 后续版本在此追加 -->
