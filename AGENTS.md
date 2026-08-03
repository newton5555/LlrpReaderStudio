# WPF MVVM 项目模板

## 目标

创建或维护一个结构清晰、可扩展的 WPF 桌面项目。默认采用：

- WPF；
- CommunityToolkit.Mvvm；
- Microsoft.Extensions.DependencyInjection；
- MahApps.Metro；
- FontAwesome.Sharp。

库和 .NET 版本不在本模板中约束。使用项目已有配置；创建新项目时选择彼此兼容的版本即可。

除非用户明确要求，不实现或复制：

- 自定义 UI 资源和复杂 ResourceDictionary；
- 明暗主题或运行时主题切换；
- 多语言、本地化或运行时语言切换；
- 参考项目中的具体业务代码。

## 项目结构

默认使用一个 WPF 项目，按职责组织目录：

```text
<Project>/
  App.xaml
  App.xaml.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  Views/
  ViewModels/
  Models/
  Services/
  Messages/
  Converters/
  Assets/
```

各目录职责：

- `App.xaml.cs`：唯一组合根，负责 IoC 注册、应用启动和退出清理；
- `Views/`：页面、对话框和纯 UI 行为；
- `ViewModels/`：页面状态、命令和导航逻辑；
- `Models/`：简单数据模型、导航项和共享状态；
- `Services/`：业务服务接口及实现；
- `Messages/`：ViewModel 间的强类型消息；
- `Converters/`：无状态值转换器；
- `Assets/`：图标等静态文件。

不要编辑或提交 `bin/`、`obj/`、临时 `*_wpftmp.csproj` 文件。

## IoC 与启动

`App.xaml` 不设置 `StartupUri`。应用启动时在 `App.xaml.cs` 中：

1. 创建 `ServiceCollection`；
2. 集中注册 Service、共享状态、ViewModel 和 Window；
3. 构建根容器；
4. 从容器解析并显示 `MainWindow`；
5. 退出时释放容器。

参考骨架：

```csharp
public partial class App : Application
{
    private ServiceProvider? serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<MainWindow>().Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppService, AppService>();
        services.AddSingleton<AppState>();

        services.AddTransient<HomeViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
```

IoC 规则：

- ViewModel 和 Service 使用构造函数注入；
- 只允许 `App.xaml.cs` 直接访问根容器；
- View、ViewModel 和 Service 中禁止使用 Service Locator；
- 跨页面共享的状态或服务注册为 `Singleton`；
- 普通页面 ViewModel 默认注册为 `Transient`；
- 主窗口和主 ViewModel 注册为 `Singleton`；
- 不在 ViewModel 中直接 `new` Service、Window 或其他 ViewModel。

## MVVM 约定

ViewModel 使用 CommunityToolkit.Mvvm：

```csharp
public partial class HomeViewModel : ObservableObject
{
    private readonly IAppService appService;

    [ObservableProperty]
    private string statusText = string.Empty;

    public HomeViewModel(IAppService appService)
    {
        this.appService = appService;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusText = await appService.GetStatusAsync();
    }
}
```

基本规则：

- 通知属性优先使用 `[ObservableProperty]`；
- 用户操作优先使用 `[RelayCommand]`；
- 异步命令返回 `Task`，避免 `async void`；
- 动态列表使用 `ObservableCollection<T>`；
- 业务逻辑放在 ViewModel 或 Service，不放在 code-behind；
- code-behind 仅保留控件焦点、选择、快捷键等纯 UI 行为；
- ViewModel 不直接操作 `Window`、`MessageBox`、`Clipboard` 或文件选择器，需要时以 Service 封装。

View 的 code-behind 默认只初始化组件：

```csharp
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}
```

主窗口通过构造函数注入 ViewModel：

```csharp
public partial class MainWindow : MetroWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
```

不要添加会绕过 `InitializeComponent()` 的无参 Window 构造函数。XAML 设计时上下文使用 `d:DataContext`，不影响运行时 IoC。

## 页面导航

使用 ViewModel-first 导航：

- `MainViewModel` 维护 `NavigationItems`；
- 每个导航项保存标题、图标和页面 ViewModel；
- `SelectedNavigationItem` 变化时更新 `CurrentPageViewModel`；
- `MainWindow.xaml` 使用隐式 `DataTemplate` 建立 ViewModel 到 View 的映射；
- 页面区域使用 `ContentControl` 显示当前 ViewModel；
- 不在 ViewModel 中创建 UserControl。

示例：

```xml
<DataTemplate DataType="{x:Type vm:HomeViewModel}">
    <views:HomeView />
</DataTemplate>

<ContentControl Content="{Binding CurrentPageViewModel}" />
```

新增页面时同步完成：

1. 创建 `<Feature>View.xaml`；
2. 创建 `<Feature>ViewModel.cs`；
3. 注册 ViewModel；
4. 添加 DataTemplate；
5. 添加导航项。

## ViewModel 间通信

有直接依赖关系时优先使用构造函数和普通属性。多个无直接引用的 ViewModel 需要广播轻量状态变化时，可使用 `WeakReferenceMessenger` 和强类型消息。

```csharp
public sealed class ConnectionStateChangedMessage(bool isConnected)
    : ValueChangedMessage<bool>(isConnected);
```

消息规则：

- 消息放在 `Messages/`；
- 名称描述事实或明确请求；
- 负载保持小且不可变；
- 持续共享的数据放入 Singleton 状态对象，不把 Messenger 当数据仓库。

## UI 库

- 主窗口继承 `MahApps.Metro.Controls.MetroWindow`；
- 使用 MahApps.Metro 的标准控件和官方资源；
- 图标使用 FontAwesome.Sharp 的 WPF 控件；
- 普通按钮绑定 Command，不编写 Click 业务处理器；
- 页面应支持窗口缩放，长内容使用 ScrollViewer；
- 没有明确需求时，不创建自定义控件库或全局样式系统；
- 不创建主题服务、语言服务及对应 ViewModel；
- 不添加主题或语言切换按钮。

`App.xaml` 只合并 MahApps 正常使用所需的官方资源。可以使用一个固定主题作为基础显示，但不得实现运行时主题切换。

## 编码约定

- View 与 ViewModel 成对命名：`<Feature>View`、`<Feature>ViewModel`；
- 接口以 `I` 开头；
- 一个文件通常只放一个主要类型；
- 尊重项目现有命名空间、格式和包管理方式；
- 不复制参考项目名称、命名空间、设备型号或业务常量；
- 不为了“结构完整”创建当前需求用不到的空目录、接口或类。

## 验证

每次变更后至少：

```powershell
dotnet build <SolutionFile>
dotnet test <SolutionFile> --no-build
```

涉及启动、IoC 或导航时还要确认：

- `MainWindow` 能由容器创建；
- 所有构造函数依赖均已注册；
- 每个页面 ViewModel 都有对应 DataTemplate；
- 页面切换正常；
- 调试输出中没有新增的 XAML Binding 错误。

## 代理执行原则

- 修改前先读取解决方案、项目文件和现有目录；
- 只实现当前任务需要的最小结构；
- 版本选择服从项目现状，不在本模板中固定；
- 用户未明确要求时，始终排除自定义 UI 资源、主题切换和多语言功能；
- 用户的明确要求优先于本模板。
