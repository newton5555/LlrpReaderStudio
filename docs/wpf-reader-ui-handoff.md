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
