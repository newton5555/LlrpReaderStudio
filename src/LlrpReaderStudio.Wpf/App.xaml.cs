using System.Windows;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Discovery;
using LlrpReaderStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LlrpReaderStudio;

public partial class App : Application
{
    private ServiceProvider? serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider provider = services.BuildServiceProvider();
        serviceProvider = provider;

        provider.GetRequiredService<MainWindow>().Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging Infrastructure
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Infrastructure & Core Services
        services.AddSingleton<ReaderFleetService>();
        services.AddSingleton<IReaderDiscoveryService, ZeroconfReaderDiscoveryService>();

        // Page ViewModels
        services.AddSingleton<InventoryViewModel>();
        services.AddTransient<AddDataSourceViewModel>();
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
