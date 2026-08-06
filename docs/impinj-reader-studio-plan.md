# Impinj Reader Studio 核心版实施规划

## 1. 目标与边界

将现有 WPF MVP 重构为面向研发、现场调试和性能评估的 Impinj Reader 工作台，沿用 ItemTest 的高频工作流，但不追求界面像素级复刻。

首版支持：

- Impinj R700；
- Impinj Speedway R120、R220、R420；
- LLRP 连接与 Impinj LLRP 扩展；
- 同时连接和盘存 1～2 台 Reader；
- 约 2,000 个唯一标签、连续运行 1 小时。

明确不包含：

- xArray、xSpan、xPortal 等 Gateway；
- Location、Direction；
- RDD、FDD、RShell；
- Impinj IoT Device Interface；
- 产线工单、自动 Pass/Fail 流程；
- 自动更新功能。

## 2. 总体架构

### 2.1 项目分层

将当前集中在 `MainViewModel` 和 `MainWindow` 中的逻辑拆分为：

- `LlrpReaderStudio.Core`：领域模型、Reader 会话、盘存聚合、Preset、TOI、运行统计和公共接口；
- `LlrpReaderStudio.Infrastructure`：EF Core、SQLite、Zeroconf、文件日志和诊断包实现；
- `LlrpReaderStudio.Wpf`：中文界面、导航、ViewModel、对话框和 UI 状态；
- 测试项目：Core 单元测试、Infrastructure 集成测试和 WPF ViewModel 测试。

WPF 使用 `CommunityToolkit.Mvvm`，通过依赖注入组合服务。ViewModel 不得直接调用 LLRP 协议类型、EF `DbContext` 或 Zeroconf 静态 API。

### 2.2 主要应用接口

- `IReaderDiscoveryService`：持续发布发现、更新和离线的 Reader；
- `IZeroconfResolverAdapter`：封装 `ZeroconfResolver` 静态调用；
- `IReaderProfileRepository`：Reader Profile 的增删改查；
- `IPresetRepository`：Reader Preset 与 Inventory Preset 管理；
- `ITagListRepository`：TOI 清单和条目管理；
- `IReaderFleetService`：多 Reader 连接、设置、事件与操作串行化；
- `IInventoryRunService`：一次多 Reader 盘存运行的完整生命周期；
- `IInventoryLogWriter`：原始 TagReport CSV 流式写入；
- `IDiagnosticsExporter`：导出应用级 LLRP 诊断包。

### 2.3 并发与状态

- 每台 Reader 的连接、设置、盘存和 Tag Access 操作通过独立异步门串行化；
- SDK 事件线程只做轻量转换、核心聚合和入队，不访问 WPF Dispatcher；
- 核心层处理每个 TagReport，确保计数不因 UI 节流而丢失；
- WPF 最多每 100 ms 合并刷新一次可见标签行；
- 自动重连只恢复 LLRP 传输连接，不自动恢复 RF 盘存；
- 用户取消不进入 `Faulted`，意外断线进入重连状态，真实协议错误进入故障状态。

## 3. Zeroconf Reader 发现

使用 NuGet `Zeroconf` 3.7.16，不自行实现 mDNS/DNS-SD 报文解析。

- 查询服务：`_llrp._tcp.local.`；
- 每 5 秒开始一轮可取消扫描；
- `ZeroconfResolver` 调用不得重叠；
- 解析服务实例名、主机名、IPv4/IPv6、SRV 端口和 TXT 属性；
- 使用规范化主机名与服务实例去重；
- 同一 Reader 的多个地址作为候选端点保存，默认优先 IPv4；
- 连续 3 轮未发现后标记离线，避免多播短时丢包造成 UI 闪烁；
- 发现结果只是候选设备，添加前连接 LLRP 并确认 Manufacturer ID 为 Impinj；
- mDNS 被防火墙阻止、未广播或不可用时，手动主机名/IP 添加仍可工作。

## 4. EF Core 与 SQLite 持久化

### 4.1 存储边界

运行期结构化数据统一使用 EF Core + SQLite；JSON 用于导入、导出和诊断快照；高频原始 TagReport 使用 CSV，不写入 SQLite。

数据库位置：

```text
%LocalAppData%\LlrpReaderStudio\Data\studio.db
```

日志位置：

```text
%LocalAppData%\LlrpReaderStudio\Logs\<运行开始时间>\tags.csv
%LocalAppData%\LlrpReaderStudio\Logs\<运行开始时间>\session.json
```

### 4.2 数据表

- `AppSettings`：应用设置、日志默认值、最后页面等；
- `ReaderProfiles`：名称、Host、Port、自动重连和最近身份信息；
- `ReaderPresets`：名称、版本、Reader 设置 JSON 和时间戳；
- `InventoryPresets`：名称、版本、Inventory 设置 JSON 和时间戳；
- `TagLists`：TOI 名称、来源、启用状态和默认颜色；
- `TagListEntries`：EPC、显示名、颜色及扩展元数据；
- `InventoryRuns`：运行时间、结束原因、设置快照、日志路径及汇总统计；
- `DiagnosticEvents`：连接、天线、GPI、Keepalive、缓冲区和错误事件；
- `__EFMigrationsHistory`：由 EF Core 管理。

复杂的 `ReaderSettings`、`InventorySettings` 和能力快照使用带 `schemaVersion` 的 JSON 列保存，避免把 SDK 嵌套模型拆成大量脆弱表结构。

### 4.3 数据库约束

- 使用与 .NET 10 对齐的 EF Core SQLite 版本并通过中央包管理锁定；
- 使用 `IDbContextFactory<StudioDbContext>`，每次操作创建短生命周期 Context；
- ViewModel 和 Reader 会话不得长期持有 `DbContext`；
- 启用 WAL、`foreign_keys` 和合理的 `busy_timeout`；
- 启动时调用 `MigrateAsync()`，不得使用 `EnsureCreated()`；
- 迁移前备份现有数据库；迁移失败时保留原数据库并提供只读恢复提示；
- Preset 和 TOI 支持独立 JSON 导入/导出，SQLite 不是唯一交换格式；
- Protected Mode 密码和 Tag Access 密码默认不持久化；未来如需保存凭据，使用 Windows DPAPI。

## 5. 功能设计

### 5.1 中文主界面

沿用 ItemTest 工作流，左侧分为：

- 功能展示：盘存、标签存储器；
- 数据源：已保存 Reader 和自动发现 Reader；
- 标签清单：TOI 文件与启用状态；
- 应用设置：日志、数据目录和关于信息。

点击数据源进入设备配置页。能力不支持的功能隐藏或禁用，并显示明确原因。

### 5.2 Reader 数据源

- Zeroconf 自动发现和手动 Host/IP 添加；
- 添加数据源只保存 Impinj Reader endpoint，并执行一次 TCP/LLRP 连通性验证；验证结束后不保留长连接；
- “开始寻卡”负责完整运行生命周期：连接 → 应用设置 → 启动盘存 → 接收 TagReport；
- “停止寻卡”负责完整收尾：停止盘存 → 刷新/关闭日志 → 断开连接；
- 添加失败、启动失败或中途异常都要回收连接，不能留下后台 RF 盘存；
- 编辑、删除和批量管理数据源；连接/断开状态由盘存生命周期驱动；
- 显示型号、固件、协议版本、温度、区域、天线/GPI/GPO 数量；
- 展示连接、盘存、故障、重连和发现离线状态；
- 支持自动重连，但断线后不自动重新发射 RF。

### 5.3 Reader 设置

- 查询设备设置；
- 载入 SDK 默认设置；
- 编辑草稿并预览差异；
- 应用草稿；
- 保存和套用 Reader Preset。

覆盖的设置包括：

- 天线状态、发射功率、接收灵敏度、RF Mode；
- Keepalive、Link Monitor 和事件通知；
- GPI debounce、GPO；
- 固定/降功率频率、低占空比；
- Impinj Search Mode；
- AccessSpec BlockWrite、重试和排序设置。

功率和灵敏度同时显示索引及能力表中的实际值。Preset 应用前执行能力检查，不支持字段必须列出，不得静默忽略。

### 5.4 Inventory Showcase

支持选择一台或两台已登记 Reader 同时盘存。启动时建立连接，停止时先停止盘存再断开连接；界面不提供独立的常驻连接状态。

盘存参数：

- 天线和逐天线功率/灵敏度；
- RF Mode、Session、Target、Population；
- 报告触发、开始触发和停止触发；
- 最多两个 Gen2 Filter；
- Impinj Search Mode；
- FastID/Serialized TID、Peak RSSI、相位角、Doppler、Tx Power、XPC；
- Optimized Read、Tag Population Estimation、Filter Verification。

只有 SDK 能力目录确认支持的 Impinj 选项才默认开放，不自动发送未验证参数。

标签表显示：

- EPC、TID；
- 累计次数、首次/最后时间；
- Reader、天线、RSSI、信道；
- 已请求并返回的 Impinj 扩展字段。

支持全部、按 Reader、按天线视图，以及过滤、排序、列选择和 TOI 分组。顶部显示运行时间、唯一标签、总读取数、滚动读取率、每 Reader/天线读取率和 TOI 已见/未见统计。

### 5.5 TOI

- 支持 ItemTest 风格 JSON 和兼容 CSV 导入；
- 支持启用、停用、颜色、分组和统计；
- 同时启用的清单存在重复 EPC 时阻止启用并列出冲突；
- TOI 只影响展示和统计，不隐式改变 Reader Filter；
- 可从 SQLite 导出为独立 JSON 文件。

### 5.6 日志与诊断

每次盘存开始前由用户显式选择是否记录原始报告。

`tags.csv` 保存：

- 主机时间、Reader；
- EPC/TID、天线、RSSI、SeenCount、信道；
- 已启用的 Impinj 扩展字段。

`session.json` 保存：

- Reader 身份和固件；
- 使用的 Preset 与实际设置；
- 开始、结束时间和结束原因；
- 汇总统计和日志丢失数量。

日志使用独立有界队列。队列饱和时不得阻塞 LLRP 回调，应用记录并醒目显示丢失数量。

诊断时间线记录连接、重连、天线、GPI/GPO、Keepalive、缓冲区和错误事件。诊断包不包含 RDD、FDD 或 RShell 数据。

### 5.7 Tag Memory

- 一次只操作一台 Reader；
- 可从盘存结果带入完整 EPC 或 TID；
- 只允许精确选择，禁止模糊目标写入；
- 支持 Reserved/EPC/TID/User；
- 支持 0～32 words、word offset 和 32 位密码；
- Hex word 与扩展 ASCII 双向同步；
- 操作期间锁定输入，默认 5 秒超时；
- 返回中文状态并保留当前会话操作历史。

R700 与兼容 M700 标签显示 Protected Mode 操作。必须启用 FastID、提供完整目标和密码，并进行二次确认。Speedway 或能力不满足时不显示。

不向 UI 暴露 Kill、Lock、BlockErase、Permalock 或 QT。

在相邻 LLRPCSharp 中补充高层 `ProtectedModeRequest` 和 `SetProtectedModeAsync`，由 SDK 组合标准 Select/Access/Write，负责能力、密码、目标和响应校验；WPF 不接触生成协议类型。

## 6. 实施顺序

1. 重构应用分层、依赖注入、中文导航和现有 MVP 功能；
2. 建立 EF Core SQLite、迁移、Repository、备份和 JSON 导入导出；
3. 集成 Zeroconf、数据源管理、Impinj 身份验证和能力快照；
4. 完成 Reader 设置页及 Reader Preset；
5. 完成多 Reader 盘存、Impinj 高级字段、Inventory Preset 和统计；
6. 完成 TOI、CSV 日志、运行摘要和诊断时间线；
7. 完成 Tag Memory，并在 LLRPCSharp 增加 Protected Mode API；
8. 完成长稳优化、真机验收、诊断包和交付文档。

每个阶段必须保持解决方案可构建、测试可运行且既有功能可用，不长期保留新旧两套页面。

## 7. 测试与验收

### 7.1 自动化测试

- Reader 状态机、取消、异常、重连和多 Reader 操作隔离；
- 聚合、SeenCount、Reader/天线维度、TOI 匹配和 UI 批量更新；
- EF Repository CRUD、唯一约束、级联删除和并发更新；
- SQLite 首次创建、逐版本迁移、迁移备份和失败恢复；
- Preset/TOI JSON 导入导出与 schemaVersion 兼容；
- Zeroconf Host、Service、地址、端口、多网卡和离线判定映射；
- CSV 转义、扩展字段、队列饱和和会话摘要；
- Impinj 能力驱动设置编译与不支持功能拒绝；
- Protected Mode 开启、关闭、错误密码、非 R700、缺少 FastID 和超时；
- WPF 导航、命令状态、中文校验、能力驱动显隐和关闭取消。

### 7.2 真机验收

- R700 与至少一台 Speedway 完成发现、连接、设置、盘存、GPI/GPO 和 Tag Memory；
- Windows 环境通过 `_llrp._tcp.local.` 发现两类 Reader；
- 两台 Reader、2,000 个唯一标签、连续运行 1 小时；
- 运行期间 UI 保持可交互，无持续内存增长或 Dispatcher 堵塞；
- R700 + M700 验证 Protected Mode 开启、带密码盘存和关闭；
- 验证 mDNS 禁用、防火墙阻止、断网、Reader 重启、错误密码、无匹配标签、数据库迁移失败和日志目录不可写等故障。

## 8. 默认决策

- 使用 .NET 10、WPF 和相邻 LLRPCSharp 源码引用；
- 使用 `Zeroconf` 3.7.16；
- 使用 EF Core SQLite 作为主持久化机制；
- 使用 JSON 作为 Preset/TOI 交换和诊断快照格式；
- 使用 CSV 保存原始 TagReport；
- 默认 LLRP 端口为 5084，并启用 Impinj 扩展；
- 默认不记录原始日志；
- 默认不在自动重连后恢复 RF；
- 首版只提供中文资源；
- 密码默认不持久化。

## 9. 已识别的后续优化（暂缓 / 有意不做）

以下条目经审查识别，当前明确不实施，条件成熟后再评估：

- **合并单 Serilog 管线**：当前实现为两个独立 `ILoggerFactory`（`studio-*.log` 应用日志、`sdk-*.log` SDK 日志），通过 `IsSdkLogEvent` 类别过滤避免串扰。职责边界清楚、生命周期正确（容器/Session 先释放，SDK 日志工厂最后释放，异步 sink 先 flush）。项目规模不大时保持双工厂，可读性更好；若未来日志需求复杂化（多文件分级、动态级别切换），再重构为单管线 + 分类过滤写多文件。
- **SDK 日志区分多 Reader**：当前输出模板含 `{SourceContext}`（如 `LlrpSdk.Reader.LlrpReader`），但 SDK 内部 logger 不带 endpoint/ReaderId，多设备同时操作时无法从 sdk-*.log 直接区分。需要在 SDK 侧（LlrpReader 的 logger 上下文）或 ReaderSession 层附加 reader 信息，依赖 SDK 版本迭代，暂不实施。
- **SDK `RxSensitivityEntry` 偏移模型改造**：SDK 的 `ReceiveSensitivityDbm = value/100` 是偏移量而非绝对 dBm；UI 已对齐为显示 SDK 原始 `ReceiveSensitivityValue`（0/10/11…50）。SDK 侧模型修正待 SDK 迭代时一并处理。
- **WPF 侧设置转换逻辑抽到 Core 层加单测**：`DataSourceSettingsViewModel` 中的 UI↔settings 转换（天线配置、GPI、Filter、SearchMode 等）目前无单测，逻辑稳定后抽取到 Core 层补覆盖。

## 10. 设备范围与后续（P2/P3）

- **设备范围**：当前软件仅支持 Impinj 设备。`ReaderProfile.EnableImpinjExtensions` 默认 true，不做按厂商 ID 的自动判定（P3 不做）；mDNS 只扫描 `_llrp._tcp.local.`（Impinj 设备均广播），非广播设备通过手动 IP 添加。
- **P1 已实施**：添加数据源先临时探测（`ReaderFleetService.ProbeAsync`，成功后才保存 + 注册 + 建 UI，失败完全回滚）；启动对 Enable=true 设备检查一次、失败自动禁用并保存；Enable 开关空闲时开→验证（防重入、失败回滚）、关→断开；选择设备不隐式连接（设置页显式 REFRESH SETTINGS 才查询）。
- **P2 待做**：Availability 状态机（Unknown/Checking/Reachable/Unreachable/Inventorying）、UI 显示最后检查时间/错误/型号固件、设置页显示最近一次缓存配置、停止寻卡时断开连接。

## 11. 归档说明

- 项目定位为 **Impinj-only** 工具：`ReaderProfile.EnableImpinjExtensions` 默认 true、不做厂商判定、mDNS 仅扫 `_llrp._tcp.local.`。标准 LLRP 设备支持不在范围内。
- 当前调整完成后项目**归档**：不再规划新功能（P2 剩余项按需处理，P3 明确不做）。
- 侧边导航栏：StatusMessage / Software Settings / About 固定在侧边栏底部。
