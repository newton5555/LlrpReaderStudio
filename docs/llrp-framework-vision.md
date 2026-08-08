# LLRP 应用服务层 + WPF UI 新项目框架与开发计划

> 状态：规划稿（2026-08）
> 背景：`LlrpReaderStudio` 是现有实现，**新软件（完整支持多厂商 LLRP reader 的 UI 软件）将在此基础上演化**，而非另起炉灶。本规划定义正式的**应用服务层（类库）+ WPF UI**。
> 当前约束：**服务层当前先只给 WPF 用**；服务层独立成库（避免 demo 胖 VM/耦合），不强求跨 UI 框架纯净，未来需要多 UI 时再拆分。

## 0. 目标与定位

- 新建**厂商无关的 LLRP 应用服务层（独立类库）**，作为正式产品长期维护；
- 新建 **WPF UI 应用**（`App.Wpf`），消费服务层，作为当前唯一 UI；
- 依赖底层 `LlrpSdk`（标准 LLRP 核心 + 厂商扩展架构），**不重造协议层**；
- 服务层**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过扩展模块接入；
- 未来若出现第二个 UI 消费者，再评估服务层跨框架化（当前不做）。

## 1. 关键前提（已核实）

| 前提 | 事实 |
|---|---|
| 底层 SDK | `LlrpSdk 1.2.0` 已是"标准 LLRP 核心 + `LlrpSdk.Extensions.Abstractions`（IReaderExtension 等）+ Impinj/Seuic 扩展"架构 |
| SDK 引用方式 | 目标用 `PackageReference`（本机 NuGet cache 有 `llrpsdk`、`llrpsdk.extensions.impinj` 1.2.0） |
| 标准 LLRP 支持 | 已实测支持 Impinj 与标准 LLRP 1.0.1；LLRP 版本策略（Auto/Force101/Force11）已在 demo 打通 |
| demo 角色 | 现有实现的**直接基础**：完整支持多厂商 LLRP reader 的 UI 软件将从它演化，部分代码（生命周期/防卡死/隔离）可直接迁移，不是"只抄设计" |

## 2. 项目结构（一个解决方案、多个职责分离项目）

```
LLRP.Framework.slnx（新仓库）
├── LlrpReaderStudio.Services/            ★ 应用服务层（独立类库，正式产品）
│     定位：当前先给 WPF 用；独立成库是为了避免 demo 的胖 VM/耦合，不强求跨 UI 框架
│     依赖: LlrpSdk (PackageReference) + LlrpSdk.Extensions.Abstractions
│     ├── Lifecycle/      Reader 注册 / 激活 / 短连接 / Enable 语义
│     ├── Settings/       能力驱动设置模型（ReaderFeatureCatalog / EffectiveSettingsLayout / SettingsCompiler）
│     ├── Inventory/      标准 Inventory / TagReport / TagAccess 协调层
│     ├── Capabilities/   ReaderCapabilities 内存缓存（不从 DB 持久化）
│     └── Modules/        IVendorExtensionModule 抽象（不直接依赖厂商包）
├── LlrpReaderStudio.App.Wpf/            ★ 新 UI 应用（WPF，引用服务层，当前唯一 UI）
│     依赖: LlrpReaderStudio.Services + CommunityToolkit.Mvvm + MahApps.Metro
│     ├── Views/          页面视图（纯 UI）
│     ├── ViewModels/     页面状态 / 命令（只消费服务层，不直接碰 SDK）
│     ├── Messages/       ViewModel 间消息
│     ├── Converters/     值转换
│     └── Assets/         图标等
├── LlrpReaderStudio.Services.Tests/     xunit + LlrpVirtualReader（虚拟 reader 测试）
└── (可选) LlrpReaderStudio.Extensions.Impinj/   Impinj 扩展模块（服务层外置）
```

**设计铁律**：
- 服务层**只依赖 `LlrpSdk` + `LlrpSdk.Extensions.Abstractions`**（不依赖 UI 框架）；当前 UI 是 WPF，但服务层本身不引用 `UseWPF`；
- 服务层**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过注册 `IVendorExtensionModule` 加载，杜绝 demo 中"Core 直接 using Impinj"的病；
- UI 应用只负责展示与交互，设备生命周期/能力聚合/设置编译全部在服务层；**ViewModel 不直接碰 SDK**（demo 的 Service Locator / 直接 new 禁止）。

## 3. 模块职责与核心 API 草案

### 3.1 Lifecycle —— Reader 生命周期（服务层）

从 demo 的 `ReaderFleetService` 提炼并厂商无关化：

```csharp
public sealed class ReaderManager
{
    // 注册/注销（原子性：先 probe 成功再注册）
    Task<ReaderProbeResult> ProbeAsync(ReaderProfile profile, CancellationToken ct);
    ReaderHandle Add(ReaderProfile profile);
    Task RemoveAsync(Guid readerId, CancellationToken ct);

    // 激活（短连接：连接→读身份/能力/配置→写缓存→断开）
    Task<ReaderActivationResult> ActivateAsync(Guid readerId, CancellationToken ct);
    Task DeactivateAsync(Guid readerId, CancellationToken ct);

    // Enable 语义：IsEnabled（用户意图，持久化）与连接状态（Disconnected/Connecting/Connected/...）分离
    event EventHandler<ReaderStateChangedEventArgs> StateChanged;
}
```

- **短连接**：启动/新增/设置操作用完即断，仅寻卡运行时保持连接（沿用 demo 已验证方向）；
- **Enable=true**：启动时自动激活同步到缓存，不保持 Session（demo 修正后的语义）。

### 3.2 Capabilities —— 能力与缓存（服务层）

- `ReaderCapabilities` 只能连接时获取，**不持久化到 DB**；
- 每次激活/连接时存进对应 ReaderHandle 的内存字段；
- 能力驱动的 UI 选项（RF mode / Tx/Rx / 频率）从此内存缓存填充，避免"读缓存页下拉空"。

### 3.3 Settings —— 能力驱动设置模型（服务层，核心创新，demo 没有）

```csharp
public sealed class ReaderFeatureCatalog
{
    // 由 ReaderCapabilities + 激活的扩展模块聚合出"该设备支持什么"
    IReadOnlyList<Feature> SupportedFeatures { get; }
    bool Supports(Feature feature);
}

public sealed class EffectiveSettingsLayout
{
    // 根据 catalog 决定：哪些设置项显示/隐藏/只读/可选值/校验规则
    IReadOnlyList<SettingsEntry> Entries { get; }
}

public sealed class SettingsCompiler
{
    // 把 UI 布局编译为厂商无关的 ReaderSettings + 扩展参数
    CompiledSettings Compile(EffectiveSettingsLayout layout, CancellationToken ct);
}
```

- 标准 LLRP 设置为服务层核心；Impinj Search Mode / FastID / Phase / Doppler / 定频 / Low Duty / GPI debounce 等由扩展模块贡献；
- 未知厂商设备：只显示标准设置（L1/L2/L3 按能力），不出现厂商项。

### 3.4 Modules —— 厂商扩展模块抽象（服务层）

```csharp
public interface IVendorExtensionModule
{
    string Id { get; }
    // 在 ReaderCapabilities 中识别本厂商能力（如 impinj.configuration 扩展存在）
    bool CanHandle(ReaderCapabilities capabilities);
    // 贡献能力驱动的设置项 / TagReport 投影 / Preset 序列化
    void ContributeSettings(ISettingsCatalogBuilder builder);
    void ContributeTagProjection(ITagReportBuilder builder);
}
```

- `LlrpReaderStudio.Extensions.Impinj`（独立项目）实现 Impinj 模块；
- 未来 Seuic/其他厂商新增独立模块，不改服务层核心。

### 3.5 Inventory / TagReport / TagAccess（服务层）

- 标准 Inventory 启动/停止（ROSpec 部署）；
- TagReport 经有界 Channel 后台聚合（沿用 demo 的防卡死方案：泵线程 O(1) 入队 + 后台聚合 + 定时批量）；
- TagAccess（读/写 TagMemory）标准封装；
- GPI/GPO 能力控制。

### 3.6 UI 层架构（App.Wpf，当前唯一 UI）

**技术栈**：WPF + `CommunityToolkit.Mvvm`（`[ObservableProperty]`/`[RelayCommand]`）+ `MahApps.Metro`（窗口/控件/ProgressRing）。沿用 demo 已验证的组合，不引入新 UI 库。

**页面结构（ViewModel-first 导航）**：

```text
Views/  +  ViewModels/
├── DataSourcesView / DataSourcesViewModel      设备列表：名称/端点/开关(Enable)/状态/删除/新增入口
├── AddDataSourceView / AddDataSourceViewModel  新增：Host/Name/Port/LLRP版本/厂商选项
├── ReaderSettingsView / ReaderSettingsViewModel 能力驱动设置页（由 EffectiveSettingsLayout 生成）
├── InventoryView / InventoryViewModel           寻卡：Start/Stop、读速率/唯一tag计数、Tag表格
├── TagMemoryView / TagMemoryViewModel           Tag 读写
├── DiagnosticsView / DiagnosticsViewModel       GPO/状态诊断
└── Shell（MainWindow/MainViewModel）           左侧设备列表 + 右侧 ContentControl 页面
```

**能力驱动设置页（关键设计）**：
- `ReaderSettingsViewModel` **不手写字段**，而是绑定 `EffectiveSettingsLayout.Entries`（服务层生成）；
- 每个 `SettingsEntry` 描述：标题、控件类型（TextBox/ComboBox/CheckBox）、可选值、可见性、只读、校验；
- UI 用 `DataTemplate` 按控件类型渲染 → 不同设备能力自动生成不同设置布局，**不再有 demo 的 1613 行胖 VM**；
- 保存时 `SettingsCompiler.Compile(layout)` 编译回 `ReaderSettings` + 扩展参数。

**与服务层的对接规则**：
- `MainViewModel` 消费 `ReaderManager`（列表/开关/状态）；`ReaderSettingsViewModel` 消费 `ReaderFeatureCatalog`/`EffectiveSettingsLayout`/`SettingsCompiler`；
- ViewModel **不直接引用 `LlrpSdk` 类型**（除非服务层暴露的 DTO）；
- 页面间共享状态用 Singleton 服务 + 强类型消息（`WeakReferenceMessenger`），沿用 demo 模式但更收敛。

**UI 经验（从 demo 继承，避免重蹈覆辙）**：
- per-reader 状态隔离（每设备独立设置 VM）；
- capabilities 内存缓存填充下拉；
- 短连接 + 显式刷新（REFRESH）；
- 高频 TagReport 批量渲染（不逐条刷新 UI）；
- 启动/同步时忙碌遮罩（MahApps ProgressRing）。

## 4. 与 demo（LlrpReaderStudio）的迁移边界

**迁移原则**：现有实现是基础，**能直接迁移的直接迁移，该重写的重写**（最终产出是完整的支持多厂商 LLRP reader 的 UI 软件）：

| demo 项 | 迁移方式 |
|---|---|
| ReaderFleetService 生命周期 | **直接迁移**，提炼为 ReaderManager（厂商无关化） |
| 短连接 / Enable 语义 | **直接沿用**（已修正文档语义） |
| 有界 Channel 防卡死 | **直接沿用**（泵线程入队 + 后台聚合） |
| capabilities 内存缓存 | **直接沿用** |
| per-reader 状态隔离 | **直接沿用**（字典 ReaderId→Handle） |
| Inventory / TagMemory / Diagnostics 页面 | **可迁移**，随 UI 重构落位 |
| DataSourceSettingsViewModel（1613 行） | **不迁移**，重写为能力驱动设置模型（EffectiveSettingsLayout） |
| MainWindow/导航/MahApps 风格 | 可沿用主题与导航模式，结构重构 |
| LlrpSdk 引用 | 用 PackageReference，不用 demo 的引用方式 |

## 5. 分阶段实施计划

### F1：骨架与依赖验证（0.5~1 人日）
- 建 `LLRP.Framework.slnx` + 空服务层类库 + 空测试；
- `PackageReference LlrpSdk`，验证独立于 demo/LLRPCSharp 源码可构建；
- **风险点**：`LlrpSdk.Extensions.Abstractions` 是否有 NuGet 包？若没有，需临时 ProjectReference 或本地 feed（见 §7）。

### F2：生命周期与 Capabilities（服务层，2~3 人日）
- `ReaderProfile`（厂商无关，含 LLRP 版本策略）、`ReaderManager`、`ReaderHandle`；
- 短连接激活、Enable 分离、状态事件；
- capabilities 内存缓存 + 激活填充。

### F3：能力驱动设置模型（服务层，4~6 人日，核心）
- `ReaderFeatureCatalog` / `EffectiveSettingsLayout` / `SettingsCompiler`；
- 标准 LLRP 设置编译（Tx/Rx/RF mode/频率/Gen2 Filter/天线）；
- 未知厂商设备的降级显示。

### F4：Inventory / TagReport / TagAccess（服务层，3~4 人日）
- 标准盘存、TagReport 有界 Channel 聚合、TagAccess、GPI/GPO。

### F5：Impinj 扩展模块（3~4 人日）
- `LlrpReaderStudio.Extensions.Impinj` 独立项目；
- 迁移 Search Mode/FastID/Phase/Doppler/定频/Low Duty/GPI debounce/Preset/TagReport 投影。

### F6：WPF UI 骨架与设备管理（2~3 人日）
- App.Wpf 骨架（MahApps 主题、导航、DI）；
- DataSources 页（列表/开关/状态）+ AddDataSource 页 + 服务层 `ReaderManager` 对接；
- 忙碌遮罩、状态展示。

### F7：能力驱动设置页 UI（3~4 人日）
- `ReaderSettingsViewModel` 绑定 `EffectiveSettingsLayout`；
- 按 `SettingsEntry` 类型渲染的 DataTemplate；
- 保存走 `SettingsCompiler`；REFRESH/短连接交互。

### F8：Inventory / TagMemory / Diagnostics UI（3~4 人日）
- 寻卡页（Start/Stop、速率/计数、Tag 表格批量渲染）；
- Tag Memory 读写页；
- GPO/状态诊断页。

### F9：测试与实机验收（2~3 人日）
- `LlrpVirtualReader` 虚拟 reader 单测（服务层）；
- Impinj 与标准 LLRP 1.0.1 设备实机验收。

## 6. 验收标准

- 至少一台 Impinj 和一台标准 LLRP 1.0.1 设备可分别及同时运行；
- 未知厂商设备：连接、显示身份/能力、标准盘存，不出现 Impinj 参数；
- 能力驱动 UI：不同能力快照生成不同设置布局，无法选择/发送不支持参数；
- 服务层与 UI 解耦：服务层独立成库且不引用 `UseWPF`，为未来可能的其他 UI 留余地（当前先 WPF）；
- UI 无 demo 的胖 VM：设置页由 `EffectiveSettingsLayout` 驱动，无手写 1613 行单 VM。

## 7. 风险与对策

| 风险 | 对策 |
|---|---|
| `LlrpSdk.Extensions.Abstractions` 无 NuGet 包 | F1 先验证；若无，用本地 feed / ProjectReference 临时引用，或向 SDK 仓库推动发版 |
| Seuic 等厂商扩展未发 NuGet | 服务层不依赖它们，按需加载；缺失则跳过该厂商能力 |
| 标准 LLRP 设备实机差异大（不同厂商实现偏差） | 用 L1~L4 能力分级 + 实测矩阵（demo 已验证 1.0.1 基本链路） |
| 能力驱动 UI 过度设计 | 先做固定 L1~L3 布局，再逐步抽象；不一开始追求全动态 |
| 重构 Scope 膨胀 | 按 F1~F9 分批，每阶段有独立验收，不一次铺开 |

## 8. 文档关系

- 本规划为**新服务层 + WPF UI 项目**的完整开发计划；
- `docs/multi-vendor-llrp-refactor-plan.md` 中的 P1~P6 可作为本规划的技术细节参考（P3 能力驱动设置、P4 模块化直接对应 F3/F5）；
- 经验沉淀见该文档 §3.1；
- 本规划随开发推进持续更新（每阶段完成回填状态）。
