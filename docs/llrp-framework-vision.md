# LLRP Framework 新框架项目规划

> 状态：规划稿（2026-08）
> 背景：`LlrpReaderStudio` 为一次性 demo / 测试软件，后续不维护。本规划定义正式的多厂商 LLRP 应用框架库。

## 0. 目标与定位

- 新建一个**厂商无关的 LLRP 应用框架库**，作为正式产品长期维护；
- 依赖底层 `LlrpSdk`（标准 LLRP 核心 + 厂商扩展架构），**不重造协议层**；
- 框架库**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过扩展模块接入；
- 未来 UI（WPF/Maui/控制台）作为独立消费者引用框架库，与框架分离。

## 1. 关键前提（已核实）

| 前提 | 事实 |
|---|---|
| 底层 SDK | `LlrpSdk 1.2.0` 已是"标准 LLRP 核心 + `LlrpSdk.Extensions.Abstractions`（IReaderExtension 等）+ Impinj/Seuic 扩展"架构 |
| SDK 引用方式 | 目标用 `PackageReference`（本机 NuGet cache 有 `llrpsdk`、`llrpsdk.extensions.impinj` 1.2.0） |
| 标准 LLRP 支持 | 已实测支持 Impinj 与标准 LLRP 1.0.1；LLRP 版本策略（Auto/Force101/Force11）已在 demo 打通 |
| demo 角色 | 一次性测试软件，贡献"设计经验"而非可抄代码 |

## 2. 项目结构（一个解决方案、多个职责分离项目）

```
LLRP.Framework.slnx（新仓库）
├── LlrpReaderFramework/                  ★ 厂商无关应用框架库（正式产品）
│     依赖: LlrpSdk (PackageReference) + LlrpSdk.Extensions.Abstractions
│     ├── Lifecycle/      Reader 注册 / 激活 / 短连接 / Enable 语义
│     ├── Settings/       能力驱动设置模型（ReaderFeatureCatalog / EffectiveSettingsLayout）
│     ├── Inventory/      标准 Inventory / TagReport / TagAccess 协调层
│     ├── Capabilities/   ReaderCapabilities 内存缓存（不从 DB 持久化）
│     └── Modules/        IVendorExtensionModule 抽象（不直接依赖厂商包）
├── LlrpReaderFramework.Demo.Wpf/        新 UI 演示应用（引用框架库，可弃/换）
├── LlrpReaderFramework.Tests/           xunit + LlrpVirtualReader（虚拟 reader 测试）
└── (可选) LlrpReaderFramework.Extensions.Impinj/    Impinj 扩展模块（从框架库外置）
```

**设计铁律**：框架库只依赖 `LlrpSdk` + `LlrpSdk.Extensions.Abstractions`；厂商能力通过注册 `IVendorExtensionModule` 加载，杜绝 demo 中"Core 直接 using Impinj"的病。

## 3. 模块职责与核心 API 草案

### 3.1 Lifecycle —— Reader 生命周期

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

### 3.2 Capabilities —— 能力与缓存

- `ReaderCapabilities` 只能连接时获取，**不持久化到 DB**；
- 每次激活/连接时存进对应 ReaderHandle 的内存字段；
- 能力驱动的 UI 选项（RF mode / Tx/Rx / 频率）从此内存缓存填充，避免"读缓存页下拉空"。

### 3.3 Settings —— 能力驱动设置模型（核心创新，demo 没有）

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

- 标准 LLRP 设置为框架核心；Impinj Search Mode / FastID / Phase / Doppler / 定频 / Low Duty / GPI debounce 等由扩展模块贡献；
- 未知厂商设备：只显示标准设置（L1/L2/L3 按能力），不出现厂商项。

### 3.4 Modules —— 厂商扩展模块抽象

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

- `LlrpReaderFramework.Extensions.Impinj`（独立项目）实现 Impinj 模块；
- 未来 Seuic/其他厂商新增独立模块，不改框架核心。

### 3.5 Inventory / TagReport / TagAccess

- 标准 Inventory 启动/停止（ROSpec 部署）；
- TagReport 经有界 Channel 后台聚合（沿用 demo 的防卡死方案：泵线程 O(1) 入队 + 后台聚合 + 定时批量）；
- TagAccess（读/写 TagMemory）标准封装；
- GPI/GPO 能力控制。

## 4. 与 demo（LlrpReaderStudio）的迁移边界

**不迁移代码，只迁移设计经验**：

| demo 项 | 迁移方式 |
|---|---|
| ReaderFleetService 生命周期 | 提炼为 ReaderManager（厂商无关化） |
| 短连接 / Enable 语义 | 直接沿用（已修正文档语义） |
| 有界 Channel 防卡死 | 沿用（泵线程入队 + 后台聚合） |
| capabilities 内存缓存 | 沿用 |
| per-reader 状态隔离 | 沿用（字典 ReaderId→Handle） |
| DataSourceSettingsViewModel（1613 行） | **不迁移**，重写为能力驱动设置模型 |
| LlrpSdk 引用 | 用 PackageReference，不用 demo 的引用方式 |

## 5. 分阶段实施计划

### F1：骨架与依赖验证（0.5~1 人日）
- 建 `LLRP.Framework.slnx` + 空框架库 + 空测试；
- `PackageReference LlrpSdk`，验证独立于 demo/LLRPCSharp 源码可构建；
- **风险点**：`LlrpSdk.Extensions.Abstractions` 是否有 NuGet 包？若没有，需临时 ProjectReference 或本地 feed（见 §7）。

### F2：生命周期与 Capabilities（2~3 人日）
- `ReaderProfile`（厂商无关，含 LLRP 版本策略）、`ReaderManager`、`ReaderHandle`；
- 短连接激活、Enable 分离、状态事件；
- capabilities 内存缓存 + 激活填充。

### F3：能力驱动设置模型（4~6 人日，核心）
- `ReaderFeatureCatalog` / `EffectiveSettingsLayout` / `SettingsCompiler`；
- 标准 LLRP 设置编译（Tx/Rx/RF mode/频率/Gen2 Filter/天线）；
- 未知厂商设备的降级显示。

### F4：Inventory / TagReport / TagAccess（3~4 人日）
- 标准盘存、TagReport 有界 Channel 聚合、TagAccess、GPI/GPO。

### F5：Impinj 扩展模块（3~4 人日）
- `LlrpReaderFramework.Extensions.Impinj` 独立项目；
- 迁移 Search Mode/FastID/Phase/Doppler/定频/Low Duty/GPI debounce/Preset/TagReport 投影。

### F6：UI Demo + 测试（2~3 人日）
- `Demo.Wpf` 引用框架库，展示能力驱动设置；
- 测试：`LlrpVirtualReader` 虚拟 reader + xunit。

## 6. 验收标准

- 至少一台 Impinj 和一台标准 LLRP 1.0.1 设备可分别及同时运行；
- 未知厂商设备：连接、显示身份/能力、标准盘存，不出现 Impinj 参数；
- 能力驱动 UI：不同能力快照生成不同设置布局，无法选择/发送不支持参数；
- demo 与框架解耦：换 UI 不改框架。

## 7. 风险与对策

| 风险 | 对策 |
|---|---|
| `LlrpSdk.Extensions.Abstractions` 无 NuGet 包 | F1 先验证；若无，用本地 feed / ProjectReference 临时引用，或向 SDK 仓库推动发版 |
| Seuic 等厂商扩展未发 NuGet | 框架不依赖它们，按需加载；缺失则跳过该厂商能力 |
| 标准 LLRP 设备实机差异大（不同厂商实现偏差） | 用 L1~L4 能力分级 + 实测矩阵（demo 已验证 1.0.1 基本链路） |
| 重构 Scope 膨胀 | 按 F1~F6 分批，每阶段有独立验收，不一次铺开 |

## 8. 文档关系

- 本规划为**新框架项目**的总体方案；
- `docs/multi-vendor-llrp-refactor-plan.md` 中的 P1~P6 可作为本规划的技术细节参考（P3 能力驱动设置、P4 模块化直接对应 F3/F5）；
- 经验沉淀见该文档 §3.1。
