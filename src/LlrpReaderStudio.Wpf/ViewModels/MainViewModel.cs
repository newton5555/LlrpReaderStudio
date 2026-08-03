using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Models;
using LlrpSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlrpReaderStudio.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ReaderFleetService fleet;
    private readonly ILogger<MainViewModel> logger;
    private readonly Dictionary<Guid, ReaderItemViewModel> readerIndex = [];
    private readonly HashSet<Guid> readerToggleOperations = [];
    private bool isDisposing;
    private Task? disposeTask;

    [ObservableProperty]
    private PageViewModelBase currentPage;

    [ObservableProperty]
    private NavigationItem? selectedNavigationItem;

    [ObservableProperty]
    private string activeTabName = "Inventory";

    [ObservableProperty]
    private ReaderItemViewModel? selectedReader;

    [ObservableProperty]
    private string statusMessage = "Click (+) under DATA SOURCES to add a reader.";

    public MainViewModel(
        ReaderFleetService fleet,
        InventoryViewModel inventoryViewModel,
        AddDataSourceViewModel addDataSourceViewModel,
        TagMemoryViewModel tagMemoryViewModel,
        SettingsViewModel settingsViewModel,
        AboutViewModel aboutViewModel,
        ILogger<MainViewModel>? logger = null)
    {
        this.fleet = fleet;
        this.logger = logger ?? NullLogger<MainViewModel>.Instance;
        InventoryVM = inventoryViewModel;
        AddDataSourceVM = addDataSourceViewModel;
        TagMemoryVM = tagMemoryViewModel;
        SettingsVM = settingsViewModel;
        AboutVM = aboutViewModel;

        currentPage = InventoryVM;

        NavigationItems =
        [
            new NavigationItem { Title = "寻卡 / Inventory", PageName = "Inventory", Glyph = "#", IconBrush = CreateBrush("#10B981"), ViewModel = InventoryVM },
            new NavigationItem { Title = "Tag Memory", PageName = "TagMemory", Glyph = "M", IconBrush = CreateBrush("#F97316"), ViewModel = TagMemoryVM },
            new NavigationItem { Title = "Tag Logging & Settings", PageName = "Settings", Glyph = "S", IconBrush = CreateBrush("#8B5CF6"), ViewModel = SettingsVM },
            new NavigationItem { Title = "About Studio", PageName = "About", Glyph = "i", IconBrush = CreateBrush("#38BDF8"), ViewModel = AboutVM },
        ];

        selectedNavigationItem = NavigationItems[0];

        this.fleet.ReaderStatusChanged += OnReaderStatusChanged;
        this.fleet.TagObserved += OnTagObserved;

        InventoryVM.ToggleInventoryRequested += OnToggleInventoryRequested;
        AddDataSourceVM.DataSourceSubmitted += OnAddDataSourceSubmitted;
        AddDataSourceVM.CancelRequested += () => CurrentPage = InventoryVM;
    }

    public InventoryViewModel InventoryVM { get; }
    public AddDataSourceViewModel AddDataSourceVM { get; }
    public TagMemoryViewModel TagMemoryVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public AboutViewModel AboutVM { get; }

    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];
    public ObservableCollection<ReaderItemViewModel> Readers { get; } = [];
    partial void OnCurrentPageChanged(PageViewModelBase value)
    {
        ActiveTabName = value switch
        {
            InventoryViewModel => "Inventory",
            AddDataSourceViewModel => "AddDataSource",
            TagMemoryViewModel => "TagMemory",
            SettingsViewModel => "Settings",
            AboutViewModel => "About",
            _ => "Inventory"
        };

        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.ViewModel == value);
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value?.ViewModel is PageViewModelBase page && CurrentPage != page)
        {
            CurrentPage = page;
        }
    }

    partial void OnSelectedReaderChanged(ReaderItemViewModel? value)
    {
        TagMemoryVM.SelectedReaderId = value?.Id;
        TagMemoryVM.SelectedReaderName = value?.Name;
        SettingsVM.SelectedReaderId = value?.Id;
        SettingsVM.SelectedReaderName = value?.Name;
    }

    [RelayCommand]
    private void Navigate(string pageName)
    {
        CurrentPage = pageName switch
        {
            "Inventory" => InventoryVM,
            "AddDataSource" => AddDataSourceVM,
            "TagMemory" => TagMemoryVM,
            "Settings" => SettingsVM,
            "About" => AboutVM,
            _ => InventoryVM
        };
    }

    [RelayCommand]
    private async Task RemoveReaderAsync()
    {
        if (SelectedReader is not ReaderItemViewModel reader)
        {
            StatusMessage = "Select a reader to remove.";
            return;
        }

        await RemoveSpecificReaderAsync(reader);
    }

    public async Task RemoveSpecificReaderAsync(ReaderItemViewModel reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        logger.LogInformation("Removing data source: {Name} ({Endpoint})", reader.Name, reader.Endpoint);
        await fleet.RemoveAsync(reader.Id, CancellationToken.None);
        reader.PropertyChanged -= OnReaderItemPropertyChanged;
        readerIndex.Remove(reader.Id);
        Readers.Remove(reader);
        SelectedReader = Readers.FirstOrDefault();
        StatusMessage = $"Removed data source '{reader.Name}'.";
    }

    private async Task OnAddDataSourceSubmitted(string host, string name, int port)
    {
        ReaderProfile? profile = null;
        try
        {
            profile = new ReaderProfile
            {
                Name = name,
                Host = host,
                Port = port
            };

            ReaderStatus status = fleet.Add(profile);
            var item = new ReaderItemViewModel(status, onDeleteRequested: item => _ = RemoveSpecificReaderAsync(item));
            readerIndex[profile.Id] = item;
            item.PropertyChanged += OnReaderItemPropertyChanged;
            Readers.Add(item);
            SelectedReader = item;

            StatusMessage = $"Checking TCP connectivity for '{profile.Name}'...";
            await fleet.ValidateConnectionAsync(profile.Id, CancellationToken.None);

            CurrentPage = InventoryVM;
            StatusMessage = $"Added data source '{profile.Name}' ({profile.Host}); TCP connection verified.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Data source was added, but TCP validation failed: {ex.Message}";
        }
    }

    private async Task ToggleInventoryAsync()
    {
        if (InventoryVM.IsInventoryRunning)
        {
            await StopAllInventoryAsync();
        }
        else
        {
            await StartAllInventoryAsync();
        }
    }

    private async Task StartAllInventoryAsync()
    {
        ReaderItemViewModel[] enabledReaders = Readers.Where(static reader => reader.IsEnabled).ToArray();
        if (enabledReaders.Length == 0)
        {
            StatusMessage = Readers.Count == 0
                ? "Add a data source before starting inventory."
                : "Enable at least one reader before starting inventory.";
            return;
        }

        var started = new List<Guid>();
        try
        {
            foreach (ReaderItemViewModel reader in enabledReaders)
            {
                ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
                if (status.State == StudioReaderState.Inventorying)
                {
                    continue;
                }

                if (status.State != StudioReaderState.Connected)
                {
                    await fleet.ConnectAsync(reader.Id, CancellationToken.None);
                }

                await fleet.StartInventoryAsync(reader.Id, new InventorySettings(), CancellationToken.None);
                started.Add(reader.Id);
            }

            if (started.Count == 0)
            {
                StatusMessage = "No reader could be connected for inventory.";
                return;
            }

            InventoryVM.StartTimer();
            StatusMessage = $"Started inventory on {started.Count} reader(s).";
        }
        catch (Exception ex)
        {
            await StopAllInventoryAsync();
            StatusMessage = $"Could not start inventory; all reader connections were closed: {ex.Message}";
        }
    }

    private async Task StopAllInventoryAsync()
    {
        InventoryVM.StopTimer();
        List<Exception> errors = [];
        foreach (ReaderItemViewModel reader in Readers.ToArray())
        {
            try
            {
                ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
                if (status.State is StudioReaderState.Inventorying or StudioReaderState.Stopping)
                {
                    await fleet.StopInventoryAsync(reader.Id, CancellationToken.None);
                }

                status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
                if (status.State is not StudioReaderState.Disconnected)
                {
                    await fleet.DisconnectAsync(reader.Id, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        StatusMessage = errors.Count == 0
            ? "Stopped inventory and disconnected all readers."
            : $"Inventory stopped with {errors.Count} cleanup error(s); check reader status.";
    }

    private void OnReaderStatusChanged(object? sender, ReaderStatusChangedEventArgs args)
    {
        if (isDisposing)
        {
            return;
        }

        PostToUi(() =>
        {
            if (readerIndex.TryGetValue(args.Status.Profile.Id, out ReaderItemViewModel? item))
            {
                item.Update(args.Status);
            }
        });
    }

    private void OnTagObserved(object? sender, FleetTagObservedEventArgs args)
    {
        if (isDisposing)
        {
            return;
        }

        PostToUi(() =>
        {
            InventoryVM.OnTagObserved(args.Aggregate);
        });
    }

    private void PostToUi(Action action)
    {
        if (isDisposing || Application.Current?.Dispatcher is not Dispatcher dispatcher ||
            dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (!isDisposing)
                {
                    action();
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The dispatcher may begin shutting down between the checks above.
        }
    }

    private async void OnReaderItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (isDisposing || args.PropertyName != nameof(ReaderItemViewModel.IsEnabled) ||
            sender is not ReaderItemViewModel reader || !InventoryVM.IsInventoryRunning)
        {
            return;
        }

        if (!readerToggleOperations.Add(reader.Id))
        {
            return;
        }

        try
        {
            ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
            if (reader.IsEnabled && status.State != StudioReaderState.Inventorying)
            {
                if (status.State != StudioReaderState.Connected)
                {
                    await fleet.ConnectAsync(reader.Id, CancellationToken.None);
                }

                await fleet.StartInventoryAsync(reader.Id, new InventorySettings(), CancellationToken.None);
                StatusMessage = $"Enabled reader '{reader.Name}' for the active inventory.";
            }
            else if (!reader.IsEnabled)
            {
                if (status.State is StudioReaderState.Inventorying or StudioReaderState.Stopping)
                {
                    await fleet.StopInventoryAsync(reader.Id, CancellationToken.None);
                }

                status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
                if (status.State is not StudioReaderState.Disconnected)
                {
                    await fleet.DisconnectAsync(reader.Id, CancellationToken.None);
                }

                StatusMessage = $"Disabled reader '{reader.Name}' for the active inventory.";
            }
        }
        catch (Exception ex)
        {
            reader.IsEnabled = !reader.IsEnabled;
            StatusMessage = $"Could not change reader '{reader.Name}': {ex.Message}";
        }
        finally
        {
            readerToggleOperations.Remove(reader.Id);
        }
    }

    private void OnToggleInventoryRequested()
    {
        _ = ToggleInventoryAsync();
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public async ValueTask DisposeAsync()
    {
        disposeTask ??= DisposeCoreAsync();
        await disposeTask.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        isDisposing = true;
        fleet.ReaderStatusChanged -= OnReaderStatusChanged;
        fleet.TagObserved -= OnTagObserved;
        InventoryVM.ToggleInventoryRequested -= OnToggleInventoryRequested;
        AddDataSourceVM.DataSourceSubmitted -= OnAddDataSourceSubmitted;
        foreach (ReaderItemViewModel reader in Readers)
        {
            reader.PropertyChanged -= OnReaderItemPropertyChanged;
        }
        readerToggleOperations.Clear();
        if (InventoryVM.IsInventoryRunning || Readers.Any(static reader => reader.State is not StudioReaderState.Disconnected))
        {
            await StopAllInventoryAsync();
        }
        InventoryVM.StopTimer();
        await fleet.DisposeAsync().ConfigureAwait(false);
    }
}
