using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Data;
using LlrpReaderStudio.Models;
using LlrpSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlrpReaderStudio.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ReaderFleetService fleet;
    private readonly ReaderProfileRepository readerProfiles;
    private readonly InventoryPresetRepository inventoryPresets;
    private readonly ILogger<MainViewModel> logger;
    private readonly Dictionary<Guid, ReaderItemViewModel> readerIndex = [];
    private readonly Dictionary<Guid, DataSourceSettingsViewModel> settingsVms = [];
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

    /// <summary>True while startup is syncing each reader's configuration; blocks UI interaction.</summary>
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = string.Empty;

    public MainViewModel(
        ReaderFleetService fleet,
        ReaderProfileRepository readerProfiles,
        InventoryPresetRepository inventoryPresets,
        InventoryViewModel inventoryViewModel,
        AddDataSourceViewModel addDataSourceViewModel,
        ReaderUnavailableViewModel readerUnavailableViewModel,
        TagMemoryViewModel tagMemoryViewModel,
        SettingsViewModel settingsViewModel,
        AboutViewModel aboutViewModel,
        ILogger<MainViewModel>? logger = null)
    {
        this.fleet = fleet;
        this.readerProfiles = readerProfiles;
        this.inventoryPresets = inventoryPresets;
        this.logger = logger ?? NullLogger<MainViewModel>.Instance;
        InventoryVM = inventoryViewModel;
        AddDataSourceVM = addDataSourceViewModel;
        ReaderUnavailableVM = readerUnavailableViewModel;
        TagMemoryVM = tagMemoryViewModel;
        SettingsVM = settingsViewModel;
        AboutVM = aboutViewModel;

        currentPage = InventoryVM;

        NavigationItems =
        [
            new NavigationItem { Title = "寻卡 / Inventory", PageName = "Inventory", Glyph = "#", ViewModel = InventoryVM },
            new NavigationItem { Title = "Tag Memory", PageName = "TagMemory", Glyph = "M", ViewModel = TagMemoryVM },
            new NavigationItem { Title = "Software Settings", PageName = "Settings", Glyph = "S", ViewModel = SettingsVM },
            new NavigationItem { Title = "About Studio", PageName = "About", Glyph = "i", ViewModel = AboutVM },
        ];

        selectedNavigationItem = NavigationItems[0];

        this.fleet.ReaderStatusChanged += OnReaderStatusChanged;
        this.fleet.TagObserved += OnTagObserved;
        this.fleet.ReaderDeviceExceptionOccurred += OnReaderDeviceExceptionOccurred;

        InventoryVM.ToggleInventoryRequested += OnToggleInventoryRequested;
        InventoryVM.ClearTagsRequested += OnClearTagsRequested;
        AddDataSourceVM.DataSourceSubmitted += OnAddDataSourceSubmitted;
        AddDataSourceVM.CancelRequested += OnCancelToInventoryRequested;
        ReaderUnavailableVM.RetryRequested += OnReaderUnavailableRetryRequested;
    }

    public InventoryViewModel InventoryVM { get; }
    public AddDataSourceViewModel AddDataSourceVM { get; }
    public ReaderUnavailableViewModel ReaderUnavailableVM { get; }
    public TagMemoryViewModel TagMemoryVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public AboutViewModel AboutVM { get; }

    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];
    public ObservableCollection<ReaderItemViewModel> Readers { get; } = [];

    /// <summary>
    /// Returns (creating on first use) the per-reader settings view-model. Each data source keeps
    /// its own instance so switching readers never mixes configuration state between devices.
    /// </summary>
    private DataSourceSettingsViewModel SettingsFor(Guid readerId)
    {
        if (!settingsVms.TryGetValue(readerId, out DataSourceSettingsViewModel? vm))
        {
            vm = new DataSourceSettingsViewModel(fleet, inventoryPresets);
            vm.CancelRequested += OnCancelToInventoryRequested;
            settingsVms[readerId] = vm;
        }

        return vm;
    }

    public async Task LoadSavedDataSourcesAsync()
    {
        try
        {
            IReadOnlyList<SavedReaderProfile> savedProfiles = await readerProfiles.LoadAsync(CancellationToken.None);
            foreach (SavedReaderProfile saved in savedProfiles)
            {
                ReaderProfile profile = saved.Profile;
                if (readerIndex.ContainsKey(profile.Id))
                {
                    continue;
                }

                ReaderStatus status = fleet.Add(profile);
                var item = new ReaderItemViewModel(status, saved.IsEnabled, onDeleteRequested: item => _ = RemoveSpecificReaderAsync(item));
                item.SetLastKnownState(saved.LastCheckedAtUtc, saved.Model, saved.Firmware, saved.LastError);
                readerIndex[profile.Id] = item;
                item.PropertyChanged += OnReaderItemPropertyChanged;
                Readers.Add(item);
            }

            SyncOperationReaders();
            SelectedReader = Readers.FirstOrDefault();
            _ = InitializeEnabledReadersAsync();
            StatusMessage = Readers.Count == 0
                ? "Click (+) under DATA SOURCES to add a reader."
                : $"Loaded {Readers.Count} saved data source(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load saved data sources: {ex.Message}";
        }
    }

    private async Task InitializeEnabledReadersAsync()
    {
        IsBusy = true;
        BusyText = "Syncing reader configurations...";
        try
        {
            foreach (ReaderItemViewModel reader in Readers.Where(static reader => reader.IsEnabled).ToArray())
            {
                try
                {
                    BusyText = $"Syncing configuration for '{reader.Name}'...";
                    await fleet.ConnectAsync(reader.Id, CancellationToken.None);
                    ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == reader.Id);

                    // Retain the reader's capabilities in its per-reader settings VM so the cache-loaded
                    // page can populate RF mode / Tx/Rx / frequency dropdowns without a live connection.
                    if (fleet.GetCapabilities(reader.Id) is { } caps)
                    {
                        SettingsFor(reader.Id).ApplyCapabilities(caps);
                    }

                    // Sync the reader's current configuration to the local cache, then drop the
                    // connection (short-lived probe session, like the reference tool): later pages read
                    // the cache instead of holding a connection open. Reads/saves (SAVE/REFRESH) and
                    // inventory re-establish a connection on demand.
                    ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(reader.Id, CancellationToken.None);
                    await inventoryPresets.SaveDefaultAsync(reader.Id, snapshot.Settings, CancellationToken.None);
                    await fleet.DisconnectAsync(reader.Id, CancellationToken.None);

                    reader.ConfigSynced = true;
                    await readerProfiles.UpdateStatusAsync(reader.Id, DateTime.UtcNow, status.Model, status.Firmware, null, CancellationToken.None);

                    // The reader may have been auto-selected at startup before sync finished; refresh
                    // the page so the settings view (backed by the now-fresh cache) appears — but only
                    // if the user has not already navigated away from the reader pages.
                    if (ReferenceEquals(SelectedReader, reader) &&
                        CurrentPage is DataSourceSettingsViewModel or ReaderUnavailableViewModel)
                    {
                        DataSourceSettingsViewModel vm = SettingsFor(reader.Id);
                        await vm.LoadCachedSettingsAsync(reader, CancellationToken.None);
                        CurrentPage = vm;
                    }
                }
                catch (Exception ex)
                {
                    // A reader that cannot be reached at startup is disabled automatically so it does
                    // not keep being selected for operations; its settings page is replaced by the
                    // "unavailable" placeholder until a retry succeeds.
                    reader.ConfigSynced = false;
                    reader.IsEnabled = false;
                    await readerProfiles.SetEnabledAsync(reader.Id, false, CancellationToken.None);
                    await readerProfiles.UpdateStatusAsync(reader.Id, DateTime.UtcNow, null, null, ex.Message, CancellationToken.None);
                    StatusMessage = $"Could not initialize '{reader.Name}': {ex.Message} (disabled).";

                    if (ReferenceEquals(SelectedReader, reader) &&
                        CurrentPage is DataSourceSettingsViewModel or ReaderUnavailableViewModel)
                    {
                        CurrentPage = ShowReaderUnavailable(reader);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }
    partial void OnCurrentPageChanged(PageViewModelBase value)
    {
        ActiveTabName = value switch
        {
            InventoryViewModel => "Inventory",
            AddDataSourceViewModel => "AddDataSource",
            DataSourceSettingsViewModel => "DataSourceSettings",
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
        // Selecting a reader never implicitly connects. When its configuration was synced to the
        // cache at startup, the settings page shows those cached values; otherwise the "unavailable"
        // placeholder (with Retry) is shown instead of a half-empty settings page.
        if (value is null)
        {
            return;
        }

        DataSourceSettingsViewModel vm = SettingsFor(value.Id);
        vm.SetSelectedReader(value);

        CurrentPage = value.ConfigSynced ? vm : ShowReaderUnavailable(value);
    }

    [RelayCommand]
    private void Navigate(string pageName)
    {
        CurrentPage = pageName switch
        {
            "Inventory" => InventoryVM,
            "AddDataSource" => AddDataSourceVM,
            "DataSourceSettings" => SelectedReader is { } sr ? SettingsFor(sr.Id) : CurrentPage,
            "TagMemory" => TagMemoryVM,
            "Settings" => SettingsVM,
            "About" => AboutVM,
            _ => InventoryVM
        };
    }

    [RelayCommand]
    private async Task OpenDataSourceSettingsAsync(ReaderItemViewModel? reader)
    {
        if (reader is null)
        {
            return;
        }

        if (SelectedReader != reader)
        {
            SelectedReader = reader;
        }

        if (!reader.ConfigSynced)
        {
            // Startup sync failed for this reader: show the placeholder (hint + Retry) instead of
            // a settings page with no real data.
            CurrentPage = ShowReaderUnavailable(reader);
            return;
        }

        // Startup already synced the reader's configuration to the local cache, so opening the
        // settings page is instant and offline. REFRESH SETTINGS re-queries the device on demand.
        DataSourceSettingsViewModel vm = SettingsFor(reader.Id);
        await vm.LoadCachedSettingsAsync(reader, CancellationToken.None);
        CurrentPage = vm;
    }

    private PageViewModelBase ShowReaderUnavailable(ReaderItemViewModel reader)
    {
        ReaderUnavailableVM.Show(reader.Name, string.IsNullOrWhiteSpace(reader.LastError)
            ? "The reader could not be reached during startup."
            : reader.LastError);
        return ReaderUnavailableVM;
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
        await readerProfiles.DeleteAsync(reader.Id, CancellationToken.None);
        reader.PropertyChanged -= OnReaderItemPropertyChanged;
        readerIndex.Remove(reader.Id);
        if (settingsVms.Remove(reader.Id, out DataSourceSettingsViewModel? removedVm))
        {
            removedVm.CancelRequested -= OnCancelToInventoryRequested;
        }
        Readers.Remove(reader);
        SyncOperationReaders();
        SelectedReader = Readers.FirstOrDefault();
        // If the settings page was showing the reader just removed (e.g. the last one), fall back to
        // Inventory instead of leaving the editor bound to a reader that no longer exists.
        if (ReferenceEquals(CurrentPage, removedVm))
        {
            CurrentPage = InventoryVM;
        }
        StatusMessage = $"Removed data source '{reader.Name}'.";
    }

    private async Task OnAddDataSourceSubmitted(string host, string name, int port, LlrpProtocolVersionOption llrpVersion)
    {
        ReaderProfile? profile = null;
        try
        {
            profile = new ReaderProfile
            {
                Name = name,
                Host = host,
                Port = port,
                LlrpVersion = llrpVersion,
            };

            // Probe first: nothing is registered or persisted until connectivity is verified.
            StatusMessage = $"Probing '{profile.Name}' ({profile.Host}:{profile.Port})...";
            ReaderProbeResult probe = await fleet.ProbeAsync(profile, CancellationToken.None);

            await readerProfiles.SaveAsync(profile, isEnabled: true, CancellationToken.None);
            ReaderStatus status = fleet.Add(profile);
            var item = new ReaderItemViewModel(status, isEnabled: true, onDeleteRequested: item => _ = RemoveSpecificReaderAsync(item));
            readerIndex[profile.Id] = item;
            item.PropertyChanged += OnReaderItemPropertyChanged;
            Readers.Add(item);
            SyncOperationReaders();

            // Connect and load the reader's current configuration + capabilities into its per-reader VM so
            // the settings page (opened below) shows real values immediately, not just after REFRESH.
            DataSourceSettingsViewModel settingsVm = SettingsFor(item.Id);
            try
            {
                await settingsVm.InitializeForReaderAsync(item, CancellationToken.None);
            }
            catch
            {
                // Best-effort load; the settings page still opens and a later REFRESH SETTINGS will
                // reconnect and populate values.
            }

            // Selecting the reader updates the settings page from the probe result; it does not
            // connect again (selecting never implicitly connects).
            settingsVm.SetSelectedReader(item);
            CurrentPage = settingsVm;
            StatusMessage = $"Added data source '{profile.Name}' ({profile.Host}); connection verified"
                + (probe.Model is null ? "." : $" ({probe.Model}).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add data source '{name}': {ex.Message}";
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

        var targets = enabledReaders
            .Select(reader => new ReaderStartTarget(reader.Id, reader.State))
            .ToArray();

        var connected = new List<Guid>();
        try
        {
            // UI-side state first. Every Start begins from an empty table (rows, pending queue, and
            // the upstream aggregate store), then the timer/drain is armed BEFORE any reader starts
            // reporting so early observations are consumed rather than discarded.
            InventoryVM.ResetTable();
            InventoryVM.StartTimer();

            // Device I/O (connect + start ROSpec) runs off the UI thread: these wait on the session
            // gate and network RPCs and must never block the UI (a stalled device made "Stop" freeze
            // the whole window). The reader ids/states were snapshotted on the UI thread; the pool
            // thread only drives fleet operations and never re-queries the (unsafe) status collection.
            connected = await Task.Run(async () =>
            {
                var ids = new List<Guid>();
                foreach (ReaderStartTarget target in targets)
                {
                    if (target.State is StudioReaderState.Inventorying or StudioReaderState.Connecting)
                    {
                        continue;
                    }

                    if (target.State != StudioReaderState.Connected)
                    {
                        await fleet.ConnectAsync(target.Id, CancellationToken.None);
                    }

                    await StartReaderInventoryAsync(target.Id, CancellationToken.None);
                    ids.Add(target.Id);
                }

                return ids;
            }).ConfigureAwait(true);

            if (connected.Count == 0)
            {
                InventoryVM.StopTimer();
                StatusMessage = "No reader could be connected for inventory.";
                return;
            }

            StatusMessage = $"Started inventory on {connected.Count} reader(s).";
        }
        catch (Exception ex)
        {
            // Best-effort stop only for the readers this run tried, so a failure on one reader does
            // not tear down readers that were already inventorying before this Start.
            await Task.Run(async () =>
            {
                foreach (ReaderStartTarget target in targets)
                {
                    try
                    {
                        await fleet.StopInventoryAsync(target.Id, CancellationToken.None);
                    }
                    catch
                    {
                        // Best effort during a failed start.
                    }

                    try
                    {
                        await fleet.DisconnectAsync(target.Id, CancellationToken.None);
                    }
                    catch
                    {
                        // Best effort during a failed start.
                    }
                }
            }).ConfigureAwait(true);

            InventoryVM.StopTimer();
            StatusMessage = $"Could not start inventory; reader connections were closed: {ex.Message}";
        }
    }

    private readonly record struct ReaderStartTarget(Guid Id, StudioReaderState State);

    private async Task StopAllInventoryAsync()
    {
        // Snapshot the UI collection on the UI thread; the device I/O below runs on a pool thread.
        ReaderItemViewModel[] readers = Readers.ToArray();
        List<Exception> errors = await Task.Run(async () =>
        {
            var collected = new List<Exception>();
            foreach (ReaderItemViewModel reader in readers)
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
                    collected.Add(ex);
                }
            }

            return collected;
        }).ConfigureAwait(true);

        // Stop the reader(s) BEFORE flipping IsInventoryRunning off: reports arriving between the
        // stop and the stopwatch/timer teardown are real reads the user expects to see; stopping the
        // timer first would drop them (EnqueueTag discards while IsInventoryRunning is false).
        InventoryVM.StopTimer();

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
                if (SelectedReader == item)
                {
                    SettingsFor(item.Id).SetSelectedReader(item);
                }
            }
        });
    }

    private void OnReaderDeviceExceptionOccurred(object? sender, ReaderDeviceExceptionEventArgs args)
    {
        logger.LogWarning("Reader reported an internal exception: {Message} (ROSpec {ROSpecId}, Antenna {AntennaId})",
            args.Message, args.ROSpecId, args.AntennaId);
        PostToUi(() => StatusMessage = $"Reader reported an exception: {args.Message}");
    }

    private void OnTagObserved(object? sender, FleetTagObservedEventArgs args)
    {
        if (isDisposing)
        {
            return;
        }

        // Do not hop to the UI thread per report: the SDK/message-pump thread only enqueues here
        // (O(1)) and InventoryViewModel drains the queue in batches on its UI timer. Per-report
        // InvokeAsync calls are what flooded the dispatcher under high-frequency reports.
        InventoryVM.EnqueueTag(args.Aggregate);
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
            sender is not ReaderItemViewModel reader)
        {
            return;
        }

        SyncOperationReaders();
        _ = readerProfiles.SetEnabledAsync(reader.Id, reader.IsEnabled, CancellationToken.None);

        if (!InventoryVM.IsInventoryRunning)
        {
            // Idle toggle: re-enabling verifies connectivity (guarded against re-entrancy from the
            // automatic rollback); disabling drops an established connection.
            if (reader.IsEnabled)
            {
                if (!readerToggleOperations.Add(reader.Id))
                {
                    return;
                }

                try
                {
                    await fleet.ConnectAsync(reader.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    reader.IsEnabled = false;
                    StatusMessage = $"Could not enable '{reader.Name}': {ex.Message} (reverted).";
                }
                finally
                {
                    readerToggleOperations.Remove(reader.Id);
                }
            }
            else
            {
                try
                {
                    ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
                    if (status.State is StudioReaderState.Connected or StudioReaderState.Connecting or StudioReaderState.Inventorying)
                    {
                        await fleet.DisconnectAsync(reader.Id, CancellationToken.None);
                    }
                }
                catch
                {
                    // Best effort; the reader stays disabled regardless.
                }
            }

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

                await StartReaderInventoryAsync(reader.Id, CancellationToken.None);
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
            SyncOperationReaders();
        }
    }

    private void OnToggleInventoryRequested()
    {
        _ = ToggleInventoryAsync();
    }

    private void OnClearTagsRequested()
    {
        if (isDisposing)
        {
            return;
        }

        fleet.ClearTags();
    }

    private async void OnReaderUnavailableRetryRequested()
    {
        if (isDisposing || SelectedReader is not ReaderItemViewModel reader)
        {
            return;
        }

        // Guard against the IsEnabled change below re-entering OnReaderItemPropertyChanged (which
        // would start its own connect and could roll back IsEnabled on failure). Holding the toggle
        // slot makes that handler bail out immediately.
        if (!readerToggleOperations.Add(reader.Id))
        {
            return;
        }

        try
        {
            ReaderUnavailableVM.Show(reader.Name, "Connecting and syncing configuration...");
            await fleet.ConnectAsync(reader.Id, CancellationToken.None);
            ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == reader.Id);
            ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(reader.Id, CancellationToken.None);
            await inventoryPresets.SaveDefaultAsync(reader.Id, snapshot.Settings, CancellationToken.None);
            await fleet.DisconnectAsync(reader.Id, CancellationToken.None);

            reader.ConfigSynced = true;
            reader.IsEnabled = true;
            await readerProfiles.SetEnabledAsync(reader.Id, true, CancellationToken.None);
            await readerProfiles.UpdateStatusAsync(reader.Id, DateTime.UtcNow, status.Model, status.Firmware, null, CancellationToken.None);

            DataSourceSettingsViewModel vm = SettingsFor(reader.Id);
            await vm.LoadCachedSettingsAsync(reader, CancellationToken.None);
            CurrentPage = vm;
        }
        catch (Exception ex)
        {
            ReaderUnavailableVM.Show(reader.Name, ex.Message);
            StatusMessage = $"Retry failed for '{reader.Name}': {ex.Message}";
        }
        finally
        {
            readerToggleOperations.Remove(reader.Id);
        }
    }

    private async Task StartReaderInventoryAsync(Guid readerId, CancellationToken cancellationToken)
    {
        InventorySettings settings = await ResolveInventorySettingsForStartAsync(readerId, cancellationToken);
        settings = InventoryVM.ApplyReportOptions(settings);
        await fleet.StartInventoryAsync(readerId, settings, cancellationToken);
    }

    private async Task<InventorySettings> ResolveInventorySettingsForStartAsync(Guid readerId, CancellationToken cancellationToken)
    {
        ReaderSettings? saved = await inventoryPresets.LoadDefaultAsync(readerId, cancellationToken);
        if (saved?.Inventory is { } inventory)
        {
            return inventory;
        }

        try
        {
            ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(readerId, cancellationToken);
            return snapshot.ManagedRoSpec?.Inventory
                ?? snapshot.Settings.Inventory
                ?? new InventorySettings();
        }
        catch
        {
            return new InventorySettings();
        }
    }

    private void OnCancelToInventoryRequested()
    {
        CurrentPage = InventoryVM;
    }

    private void SyncOperationReaders()
    {
        TagMemoryVM.SetOperationReaders(Readers);
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
        fleet.ReaderDeviceExceptionOccurred -= OnReaderDeviceExceptionOccurred;
        InventoryVM.ToggleInventoryRequested -= OnToggleInventoryRequested;
        InventoryVM.ClearTagsRequested -= OnClearTagsRequested;
        AddDataSourceVM.DataSourceSubmitted -= OnAddDataSourceSubmitted;
        AddDataSourceVM.CancelRequested -= OnCancelToInventoryRequested;
        foreach (DataSourceSettingsViewModel vm in settingsVms.Values)
        {
            vm.CancelRequested -= OnCancelToInventoryRequested;
        }
        settingsVms.Clear();
        ReaderUnavailableVM.RetryRequested -= OnReaderUnavailableRetryRequested;
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
