# LLRP Reader Studio WPF reader/UI handoff

本文档记录当前 WPF 端 reader、Data Source、Inventory、设备配置页之间的逻辑边界，方便后续继续开发时不把 UI 选择、设备连接和 LLRP 配置写混。

## 核心原则

- 本应用只管 LLRP reader。
- 左侧 `DATA SOURCES` 表示已添加的 reader profile。
- Data Source 的选中状态只表示“右侧打开这个 reader 的设备配置 UI”。
- Data Source 的 switch `ON/OFF` 才表示该 reader 是否参与寻卡、Tag Memory 等实际操作。
- 软件级设置在 `Software Settings` 页面；设备级设置通过选中 Data Source 打开。
- `Diagnostics` 暂时灰化；当前只实现 GPO 控制。

## 启动与 IoC

`App.xaml` 不使用 `StartupUri`，启动时由 `App.xaml.cs` 建立 IoC：

- `ReaderFleetService`：单例，管理当前进程内 reader session；
- `ReaderProfileRepository`：保存/读取 Data Source profile 和 switch 状态；
- `InventoryPresetRepository`：保存/读取每个 reader 的默认 Inventory 配置；
- `MainViewModel`、`InventoryViewModel`：单例；
- 普通页面 ViewModel 按当前项目约定注入。

启动流程：

1. 构建 DI 容器；
2. 解析 `MainViewModel`；
3. 调用 `LoadSavedDataSourcesAsync()`；
4. 从容器解析并显示 `MainWindow`。

`LoadSavedDataSourcesAsync()` 会：

- 从 SQLite 本地库读取保存过的 reader profile；
- 恢复每个 reader 的 switch 状态；
- 将 profile 加入 `ReaderFleetService`；
- 默认选中第一个 reader；
- 后台初始化 switch=ON 的 reader。

本地 DB 路径：

```text
%APPDATA%\LlrpReaderStudio\studio.db
```

旧库如果没有 `ReaderProfiles.IsEnabled` 字段，`ReaderProfileRepository` 会自动补列。

## Data Source 列表语义

左侧 Data Source 行由 `ReaderItemViewModel` 驱动：

- `IsEnabled`：绑定 MahApps `ToggleSwitch`，表示是否参与实际操作；
- `DeleteCommand`：删除该 reader profile，并从本地 DB 删除；
- 点击中间文本区域：打开该 reader 的设备配置页；
- 选中 Data Source：不会直接影响寻卡目标，只影响右侧配置页显示。

`MainViewModel.OnReaderItemPropertyChanged()` 监听 `IsEnabled`：

- 持久化 switch 状态；
- 同步 Tag Memory 可操作 reader；
- 如果 Inventory 正在运行，开启 reader 时会连接并启动该 reader；关闭 reader 时会停止并断开该 reader。

## 设备配置页

设备配置页为：

```text
Views/DataSourceSettingsView.xaml
ViewModels/DataSourceSettingsViewModel.cs
```

打开设备配置页时：

1. 设置当前 reader；
2. 连接设备并加载 capabilities；
3. 查询当前 reader 配置和 SDK-managed ROSpec/Inventory；
4. 有设备 Inventory 配置时优先显示设备配置；
5. 否则读取本地历史配置；
6. 再没有则加载 SDK default。

下拉选项来源：

- Tx power：从 `ReaderCapabilities.TxPowers` 生成，UI 显示 dBm，保存时转回 SDK index；
- Rx sensitivity：从 `ReaderCapabilities.RxSensitivities` 生成，UI 显示 dBm，保存时转回 SDK index；
- RF mode：从 `ReaderCapabilities.RfModes` 生成。

`SAVE` 行为：

- 保存前先连接设备并刷新 capabilities；
- 根据 UI 构造 `ReaderSettings.Inventory`；
- 调用 `ReaderFleetService.ApplySettingsAsync()`，通过 SDK 写入 reader；
- 保存 Inventory 配置到本地历史；
- 文案显示为已保存到 reader 和本地历史。

注意：`SAVE` 部署的是 SDK-managed inventory intent。实际启动寻卡时会重新按当前表格 report metadata 组装并启动，避免 UI 列和 reader 上报字段脱节。

### 天线配置

`Antennas` 对应 LLRP `AISpec.AntennaIDs`：

- `0` 表示所有天线；
- `1, 2, 3, 4` 表示具体启用天线；
- `0` 不能和具体天线混用。

`Individual Antennas` 是一个 `Expander`：

- 折叠时使用全局 Tx/Rx 参数；
- 展开时才写逐天线 `InventoryAntennaConfiguration`；
- 逐天线配置只会写入 `Antennas` 中启用的天线，避免 SDK 校验错误：`antenna configuration must target an antenna selected for inventory`。

### FastID / TID

`Enable Fast ID` 不使用 `AttachedDataOptions`。

当前逻辑：

- 设备配置页 `EnableFastId = true` 时，保存为 Impinj inventory report 扩展：

```text
ImpinjInventoryReportOptions.IncludeSerializedTid = true
```

- SDK 收到 Impinj `ImpinjSerializedTID` 后，会投影到：

```text
TagReport.Extensions["impinj.serializedTid"]
```

- Core 聚合层从该 extension 取 TID，并显示到 Inventory 表格。

也就是说：

- 表格显示 TID 列，只控制 UI 是否显示；
- TID 是否真正有数据，由设备配置页的 FastID 和 reader/firmware 支持情况决定；
- 不要因为用户显示 TID 列就自动启用 attached TID read。

## Inventory 表格与 report metadata

Inventory 表格在：

```text
Views/InventoryView.xaml
ViewModels/InventoryViewModel.cs
ViewModels/TagRowViewModel.cs
```

表格特性：

- 数据竖直居中；
- 表头右键菜单可多选显示列；
- 当前列包括 `#`、`EPC`、`TID`、`Count`、`First Seen`、`Last Seen`、`Reader`、`Antenna`、`Peak RSSI`、`Channel`。

开始寻卡时，`InventoryViewModel.ApplyReportOptions()` 会把当前列显示状态写入标准 report metadata：

- `Antenna` -> `Report.IncludeAntennaId`;
- `Peak RSSI` -> `Report.IncludePeakRssi`;
- `Channel` -> `Report.IncludeChannelIndex`;
- `First Seen` -> `Report.IncludeFirstSeenTimestamp`;
- `Last Seen` -> `Report.IncludeLastSeenTimestamp`;
- `Count` -> `Report.IncludeTagSeenCount`。

`TID` 例外：只控制列显示，不自动改 LLRP attached data，也不自动请求 FastID。

启动寻卡时，`MainViewModel.StartReaderInventoryAsync()`：

1. 优先读取本地保存的 Inventory 配置；
2. 没有本地配置则查询设备当前配置；
3. 再没有则用 SDK default；
4. 调用 `InventoryViewModel.ApplyReportOptions()` 叠加当前表格列选择；
5. 调用 `ReaderFleetService.StartInventoryAsync()` 启动。

这样即使设备断开后没有保留 stopped ROSpec，再次 Start 也会优先使用本地保存配置，而不是直接掉回默认。

## Tag 聚合

Core 聚合在：

```text
src/LlrpReaderStudio.Core/TagAggregation.cs
```

聚合 key 当前为 EPC。

`TagObservation` 保存：

- EPC；
- TID；
- read count；
- first/last seen；
- last RSSI；
- last channel；
- reader names；
- antenna ids。

TID 来源：

```text
TagReport.Extensions["impinj.serializedTid"] as IReadOnlyList<ushort>
```

显示时转换为连续大写 hex word 字符串。

## 需要注意的后续工作

- 如果后续支持非 Impinj reader，TID/FastID 需要单独建 vendor capability 判断，不要默认打开 Impinj 扩展字段。
- 如果要支持 attached data/TID read，应放在单独配置项，不要和表格 TID 列显示绑定。
- 如果要让已部署 ROSpec 在设备端长期保留，需要明确设备断开后的资源生命周期，不要依赖 UI 状态推断。
- 表格列显示状态目前是内存状态；如需持久化，可加 `AppSettingEntity` 或单独 UI settings repository。




## SDK 接口同步（待办，2026-08-04 记录）

SDK（LLRPCSharp，2026-08 P0 修复后）已新增便捷成员，本仓库当前仍用手写等价逻辑，后续替换：

- `TagReport.EpcHex`（普通属性）——替换 `HexCodec.FormatBytes(report.ElectronicProductCode)`（`TagAggregation.cs:25`）
- `ImpinjTagReportExtensions.SerializedTidHex`（C# 14 扩展属性，命名空间 `LlrpSdk.Extensions.Impinj`）——替换 `FormatAttachedReadData`（`TagAggregation.cs:55-66`）

两者与现有手写逻辑逐位等价（`Extensions["impinj.serializedTid"] as IReadOnlyList<ushort>` + `X4` 拼接）。替换时需 `using LlrpSdk.Extensions.Impinj;`（`DataSourceSettingsViewModel` 已 using）。SDK 通过 ProjectReference（`..\..\..\LLRPCSharp\src\...`）引用，编译即最新，无需等 NuGet 发布。替换后建议在真实 WPF 应用中顺带验证 C# 14 扩展属性可用性（本项目 `LangVersion=latest`，`TreatWarningsAsErrors=true`）。


## 待办：Inventory 高频报告下 UI 卡死调查（2026-08-08 记录）

现象：连接 R420（固件 6.4.1.240）后点击开始寻卡，UI 卡死/无响应。
设备侧排查已排除：ROSpec 会留在设备上 Active（`inventory stop` 只停 SDK 托管会话内部状态，不删设备资源；需用托管 clear 或 `EnterManualResourceModeAsync` 后手动 STOP/DELETE，见 SDK 侧文档 `docs/coverage/llrp101-sdk-coverage.md`）。

### 已确认的链路（SDK → UI，2026-08-08 Core 去耦后）

```text
SDK 消息泵线程: TagsReported?.Invoke(report)          // 每报告同步触发，但只做 O(1) 入队
  → ReaderSession.OnTagsReported                      // 同步转发
  → ReaderFleetService.OnTagReported (泵线程)          // 仅 tagChannel.Writer.TryWrite（有界全 DropWrite，非阻塞）
  → [[后台消费任务]]                                    // 聚合 Add(lock(gate)) + TagObserved?.Invoke
  → MainViewModel.OnTagObserved(后台线程)              // InventoryVM.EnqueueTag(lock，直接入队，非 UI)
  → InventoryViewModel (UI 线程 DispatcherTimer 50ms)  // DrainPendingTags：批量+去重+快速路径+O(N²)去化
```

SDK 侧无死锁：报告走 `Channel.Writer.TryWrite` 非阻塞；`TagsReported` 事件包 try/catch。
Core 侧在 `ReaderFleetService` 内新增有界 Channel + 单读者后台消费任务，聚合从泵线程移走，泵线程绝不阻塞在 lock/gc 上。

### 疑点（供 WPF 侧深入）

1. 报告触发 `Upon_N_Tags_Or_End_Of_AISpec, N=1`：每读到 1 个 tag 即发一次报告，高频下 `InvokeAsync` 向 UI 队列投递大量委托。
2. `InventoryViewModel.OnTagObserved`（UI 线程）每次 `Tags.Insert(0,row)+ReindexRows()` 为 O(N²)；`DispatcherTimer`(50ms) 每 tick 遍历全部 Tags —— 报告风暴下 UI 线程过载，表现为假死/无响应。
3. 聚合 `TagAggregation.Add` 有 `lock(gate)`，在消息泵线程每报告唤醒一次 —— 高频下锁竞争放大概率。
4. 设备报告里出现过超长 EPC（`202203020000...` 及 `000...` 整零），若进入表格会放大 UI 处理成本。

### 建议方向（未验证）

- 报告到 UI 的投递限流/批量（例如聚合后按批次 InvokeAsync，而非每报告一次）。
- `InventoryViewModel` 集合操作去 O(N²)：索引字典 + 有限列表上限，避免无限增长。
- 先复现并抓 UI 线程栈（WPF 卡死时在 VS 暂停看 Dispatcher 队列深度与 OnTagObserved 耗时）。
- 若确认与 SDK 报告节流相关，SDK 侧可提供 `TagsReported` 事件与 `ReadReportsAsync` 之外的合并/降频选项（回 SDK 仓库立项）。

### 已实施修复（2026-08-08，WPF 侧）

按上面"建议方向"的前两条实施，未改 SDK 侧：

- **投递去 PostToUi**：`MainViewModel.OnTagObserved` 不再每报告 `Dispatcher.InvokeAsync`，改为直接调用 `InventoryViewModel.EnqueueTag`（lock + `Queue<TagObservation>` 入队，O(1)），消除 UI 队列被高频报告淹没的问题。
- **批量 drain**：`InventoryViewModel` 的 50ms UI timer 每 tick `DrainPendingTags()` 批量取队列（单 tick 上限 1000 条，剩余留待下 tick），批内按 EPC 去重（保留最后一次观察）。
- **集合操作去 O(N²)**：`Dictionary<string, TagRowViewModel>`（`tagIndex`，OrdinalIgnoreCase）替代 `Tags.FirstOrDefault` 线性查找；已存在行优先走 `Tags[0]` 快速路径，避免每报告一次 `IndexOf`；`ReindexRows` 由每报告一次降为每批最多一次；批内按 `LastSeen` 升序处理，保证最新行在顶部。
- **有限列表上限**：`MaxTagRows = 10_000`，超限从尾部裁剪（`RemoveAt` 尾部 O(1)），同时同步 `tagIndex` 与 `totalReadCount`。
- **统计去全量遍历**：读速率改用增量维护的 `totalReadCount`，不再每 0.5s `Tags.Sum`。
- **TimeSinceLastSeen 降频降范围**：timer tick 只每 250ms 刷新前 200 行（可见区），不再每 50ms 全量刷新。
- **TagRowViewModel.Update**：`Readers`/`Antennas` 字符串先比较再赋值，避免已存在行每次更新都触发 PropertyChanged。

残余取舍：满表（10k 行）且持续有新 tag 时，每批一次全表 `ReindexRows` 仍会产生较多 `Index` 通知；高频真实场景（tag 数量有限、反复读到）下为 O(1) 快速路径，不构成问题。设备侧行为（ROSpec 保留、FastID 语义）未变。

### Core 层去耦补充（2026-08-08，仍卡死二次定位后）

首轮 WPF 改动后实机仍卡死，二次回溯 SDK 泵线程定位到真正的根治点：**聚合仍发生在 SDK 消息泵线程的 tag 回调里**（`ReaderFleetService.OnTagReported` → `TagAggregateStore.Add(lock(gate))` + `TagObserved?.Invoke`）。报告风暴（`Upon_N_Tags, N=1`）下泵线程被自身的 lock+快照分配压住，KEEPALIVE_ACK 无法及时发送，表现为"开始后仍卡死 / 开始不了 / stop→start 异常"。

在 Core `ReaderFleetService` 内新增有界 Channel（`TagChannelCapacity = 100_000`，`FullMode = DropWrite`，单读者）+ 后台 `ConsumeTagReportsAsync` 任务：

- 泵线程 `OnTagReported` 只做 `tagChannel.Writer.TryWrite`（O(1)、非阻塞）；满时 `TryWrite` 拒绝并递增 `TagsDropped`，泵线程绝不阻塞在 lock/gc；
- 后台任务 `ReadAllAsync` 消费，在那里做 `TagAggregateStore.Add` + `TagObserved?.Invoke`；单条异常包 try/catch 记日志；
- `DisposeAsync` 先停 sessions，再 `TryComplete` channel 并 `await` 消费任务排空已入队项；
- WPF 侧 `MainViewModel.OnTagObserved` 相应也改为非 UI 线程直接调用 `InventoryVM.EnqueueTag`（有界：`PendingTagCap = 10_000`，超限丢最旧）。`TagObserved`/`EnqueueTag` 每段都是非阻塞队列交接。

一处行为修正：`StopAllInventoryAsync` 先停读卡器、再 `StopTimer()`，避免停止窗口内真实报告因 `IsInventoryRunning=false` 被丢弃。`StartAllInventoryAsync` 先 `StartTimer()` 再逐个启动 reader，避免启动最早一批 tag 被丢。

测试 `ReaderFleetServiceTests.Reports_ArePublishedAsFleetAggregates` 因聚合异步化改为 `WaitUntilAsync` 等待事件（其余用例不受影响）。

已知残余：`Gate.Dispose` 与 `OnDeviceInitiatedClosed`（async void）的边界竞态为既有问题、非本次引入，未在本轮改动。

### 短连接模式（2026-08-08，阶段1+2：启动同步 + 设置操作用完即断）

参考 itemtest 抓包（连接→读能力→读配置→断开）调整设备交互为**短连接**：

**阶段1 — 启动同步配置 + 占位页**
- `InitializeEnabledReadersAsync`（启动）：对每台 enabled reader 改为 `connect → QuerySettingsAsync → SaveDefaultAsync 写本地缓存 → DisconnectAsync 断开`（不再保持长连接）；成功 `ReaderItemViewModel.ConfigSynced=true`，失败 `ConfigSynced=false` + `IsEnabled=false`。
- 打开配置页（`OpenDataSourceSettingsAsync`）：`ConfigSynced=true` → `LoadCachedSettingsAsync` 读缓存**秒显**（零网络）；`false` → `ReaderUnavailableView` 占位页（提示失败原因 + Retry 按钮）。
- `OnReaderUnavailableRetryRequested`：重跑同步流，成功 `ConfigSynced=true`、`IsEnabled=true`、切回配置页；失败留在占位页。用 `readerToggleOperations` 防 `IsEnabled` 变更重入开关 handler。
- 同步完成后的页面刷新有保护：仅当用户仍在 reader 页面（`DataSourceSettingsViewModel`/`ReaderUnavailableViewModel`）时才自动切页，避免覆盖用户导航。

**阶段2 — 设置类操作用完即断**
- `DataSourceSettingsViewModel` 新增 `DisconnectIfNotInventoryingAsync`：设置操作 finally 中断开连接，除非 reader 处于 `Inventorying/Stopping/Connecting`（绝不杀寻卡）。
- `EnsureConnectedAndLoadCapabilitiesAsync` 开头新增寻卡状态检查：正在寻卡时抛 `InvalidOperationException`（"The reader is busy..."），避免 `ConnectAsync` 覆盖 Inventorying 状态后 finally 误断开寻卡连接。
- 应用到的操作：REFRESH（`QuerySettingsAsync`）、`DefaultSettingsAsync`、SAVE（`ApplySettingsAsync`）、GPO（`SetGpoAsync`）。GPO 用 `gpoOperationsInFlight` 计数，仅最后一个 in-flight 操作完成时断开。
- 寻卡（Start/Stop）保持既有行为：Start 时连接，Stop 时断开。

已知残余（低优先，非阻断）：① `EnsureConnected` 状态检查与 `ConnectAsync` 间有 TOCTOU 窗口（需精确时序才可达）；② `gpoOperationsInFlight` 为全局计数不区分 readerId（切换 reader 并发 GPO 时可能泄漏连接，可复用）；③ 非 GPO 操作与 GPO 开关并发时 REFRESH 的断开可能抢在 GPO 前（罕见，GPO 失败会 RevertGpo + 提示）。`InitializeForReaderAsync` 当前无调用者（MainViewModel 走 `LoadCachedSettingsAsync`）。
