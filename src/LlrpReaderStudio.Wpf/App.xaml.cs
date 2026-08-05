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

namespace LlrpReaderStudio;

public partial class App : Application
{
    private ServiceProvider? serviceProvider;

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
        // Logging Infrastructure: Debug (VS output window) + rolling file under %AppData%\LlrpReaderStudio\logs.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDirectory = Path.Combine(appData, "LlrpReaderStudio", "logs");
        Directory.CreateDirectory(logDirectory);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(logDirectory, "studio-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger());
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (serviceProvider is not null)
        {
            serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
