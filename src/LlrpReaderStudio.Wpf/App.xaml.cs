using System.IO;
using System.Windows;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Data;
using LlrpReaderStudio.Infrastructure.Discovery;
using LlrpReaderStudio.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LlrpReaderStudio;

public partial class App : Application
{
    private ServiceProvider? serviceProvider;
    private static ILoggerFactory? sdkLoggerFactory;

    private const string LogOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    // Single-file cap before rolling; Serilog's default is 1 GB, which is too large for a busy day.
    private const long LogFileSizeLimitBytes = 50L * 1024 * 1024;

    private static LogEventLevel DefaultLogLevel =>
#if DEBUG
        LogEventLevel.Debug;
#else
        LogEventLevel.Information;
#endif

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider provider = services.BuildServiceProvider();
        serviceProvider = provider;

        MainViewModel mainViewModel = provider.GetRequiredService<MainViewModel>();
        await mainViewModel.LoadSavedDataSourcesAsync();

        provider.GetRequiredService<MainWindow>().Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDirectory = Path.Combine(appData, "LlrpReaderStudio", "logs");
        Directory.CreateDirectory(logDirectory);

        // App log: Debug (VS output window) + async rolling file studio-yyyyMMdd.log.
        // SDK categories are excluded here because they go to the dedicated SDK log below.
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Is(DefaultLogLevel)
                .Filter.ByExcluding(IsSdkLogEvent)
                .WriteTo.Async(configuration => configuration
                    .File(
                        Path.Combine(logDirectory, "studio-.log"),
                        outputTemplate: LogOutputTemplate,
                        fileSizeLimitBytes: LogFileSizeLimitBytes,
                        rollOnFileSizeLimit: true,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14))
                .CreateLogger(),
                dispose: true);
        });

        // SDK log: LLRP SDK and wire/session categories go to a separate async rolling file
        // sdk-yyyyMMdd.log so SDK traffic does not mix with app logs nor block protocol threads.
        ILoggerFactory dedicatedSdkLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Is(DefaultLogLevel)
                .WriteTo.Async(configuration => configuration
                    .File(
                        Path.Combine(logDirectory, "sdk-.log"),
                        outputTemplate: LogOutputTemplate,
                        fileSizeLimitBytes: LogFileSizeLimitBytes,
                        rollOnFileSizeLimit: true,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14))
                .CreateLogger(),
                dispose: true);
        });

        // Infrastructure & Core Services
        services.AddDbContextFactory<StudioDbContext>(options =>
        {
            string dataDirectory = Path.Combine(appData, "LlrpReaderStudio");
            Directory.CreateDirectory(dataDirectory);
            options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "studio.db")}");
        });
        services.AddTransient<ReaderProfileRepository>();
        services.AddTransient<InventoryPresetRepository>();
        services.AddSingleton<ReaderFleetService>();
        services.AddSingleton<IReaderDiscoveryService, ZeroconfReaderDiscoveryService>();

        // SDK logging: the dedicated SDK logger factory is explicitly handed to the reader session
        // factory here (not the DI ILoggerFactory), so SDK log lines land in sdk-*.log while app
        // components keep logging to studio-*.log.
        services.AddSingleton<IReaderSessionFactory>(new LlrpReaderSessionFactory(dedicatedSdkLoggerFactory));

        // Keep the SDK logger factory alive for the app lifetime; disposed in OnExit.
        sdkLoggerFactory = dedicatedSdkLoggerFactory;

        // Page ViewModels
        services.AddSingleton<InventoryViewModel>();
        services.AddTransient<AddDataSourceViewModel>();
        services.AddTransient<DataSourceSettingsViewModel>();
        services.AddTransient<TagMemoryViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AboutViewModel>();

        // Main Shell ViewModel & Window (Singleton)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static bool IsSdkLogEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContext))
        {
            return false;
        }

        string category = sourceContext.ToString().Trim('"');
        return category.StartsWith("LlrpSdk", StringComparison.Ordinal)
            || category.StartsWith("LlrpNet", StringComparison.Ordinal);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (serviceProvider is not null)
        {
            serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        sdkLoggerFactory?.Dispose();
        base.OnExit(e);
    }
}
