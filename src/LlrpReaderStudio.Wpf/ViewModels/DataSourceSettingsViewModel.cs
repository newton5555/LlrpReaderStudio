using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Data;
using LlrpNet.Protocol.Impinj.Enumerations.V1_0_1;
using LlrpSdk;
using LlrpSdk.Extensions.Impinj;

namespace LlrpReaderStudio.ViewModels;

public partial class DataSourceSettingsViewModel : PageViewModelBase
{
    private readonly ReaderFleetService fleet;
    private readonly InventoryPresetRepository inventoryPresets;
    private ReaderSettings? settingsDraft = new();
    private bool suppressGpoUpdate;
    private int gpoOperationsInFlight;

    /// <summary>Last capabilities synced from the device; retained in memory so options (RF mode,
    /// Tx/Rx, frequency) survive a disconnect and can populate the cache-loaded settings page.</summary>
    private ReaderCapabilities? capabilities;
    private readonly Dictionary<string, ushort> txPowerIndexesByDisplay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ushort> rxSensitivityIndexesByDisplay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, string> txPowerDisplaysByIndex = [];
    private readonly Dictionary<ushort, string> rxSensitivityDisplaysByIndex = [];
    private IReadOnlyList<TxPowerEntry> txPowerEntries = Array.Empty<TxPowerEntry>();
    private IReadOnlyList<RxSensitivityEntry> rxSensitivityEntries = Array.Empty<RxSensitivityEntry>();
    private ushort? maxRxSensitivityIndex;
    private string? maxRxSensitivityDisplay;
    private ushort maxAntennas = 4;

    [ObservableProperty]
    private string settingsOrigin = "No reader settings loaded";

    [ObservableProperty]
    private string selectedReaderName = "No reader selected";

    [ObservableProperty]
    private string selectedReaderHost = "-";

    [ObservableProperty]
    private string selectedReaderModel = "-";

    [ObservableProperty]
    private string selectedReaderRegion = "Reader default";

    [ObservableProperty]
    private string preset = "Default";

    [ObservableProperty]
    private bool includeInventoryDraft;

    [ObservableProperty]
    private string antennas = "1, 2, 3, 4";

    [ObservableProperty]
    private bool useIndividualAntennaSettings;

    [ObservableProperty]
    private bool isIndividualAntennasExpanded;

    [ObservableProperty]
    private bool isGlobalAntennaSettingsEnabled = true;

    [ObservableProperty]
    private string rfMode = "0";

    [ObservableProperty]
    private string searchMode = "Reader Selected";

    [ObservableProperty]
    private bool impinjExtensionsAvailable = true;

    [ObservableProperty]
    private bool enableFastId;

    [ObservableProperty]
    private string session = "Session 1";

    [ObservableProperty]
    private bool reportPhaseAngle;

    [ObservableProperty]
    private bool reportDopplerFrequency;

    [ObservableProperty]
    private string powerDbm = "30";

    [ObservableProperty]
    private string rxSensitivity = "0";

    [ObservableProperty]
    private string filterMode = "None";

    [ObservableProperty]
    private string filterVerification = "Reader Default";

    [ObservableProperty]
    private string filter1 = string.Empty;

    [ObservableProperty]
    private bool filter1Enabled;

    [ObservableProperty]
    private bool filter2Enabled;

    [ObservableProperty]
    private string filter2 = string.Empty;

    [ObservableProperty]
    private string filter1BitLength = "0";

    [ObservableProperty]
    private string filter2BitLength = "0";

    [ObservableProperty]
    private string filter1Offset = "32";

    [ObservableProperty]
    private string filter2Offset = "32";

    [ObservableProperty]
    private string filter1MemoryBank = "EPC";

    [ObservableProperty]
    private string filter1Target = "Session0";

    [ObservableProperty]
    private string filter1Action = "Assert A/Deassert B";

    [ObservableProperty]
    private string filter2MemoryBank = "EPC";

    [ObservableProperty]
    private string filter1MatchAction = "Select";

    [ObservableProperty]
    private string filter1NonMatchAction = "Unselect";

    [ObservableProperty]
    private string filter2MatchAction = "Select";

    [ObservableProperty]
    private string filter2NonMatchAction = "Unselect";

    [ObservableProperty]
    private string filter2Target = "Session0";

    [ObservableProperty]
    private string filter2Action = "Assert A/Deassert B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsStateAwareFiltersVisibility))]
    [NotifyPropertyChangedFor(nameof(EnableStateAwareFiltersVisibility))]
    [NotifyPropertyChangedFor(nameof(NonStateAwareFiltersVisibility))]
    private bool supportsStateAwareFilters;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnableStateAwareFiltersVisibility))]
    [NotifyPropertyChangedFor(nameof(NonStateAwareFiltersVisibility))]
    private bool enableStateAwareFilters;

    public Visibility SupportsStateAwareFiltersVisibility =>
        SupportsStateAwareFilters ? Visibility.Visible : Visibility.Collapsed;

    // State-aware Target/Action rows are visible only while the state-aware switch is on.
    public Visibility EnableStateAwareFiltersVisibility =>
        EnableStateAwareFilters ? Visibility.Visible : Visibility.Collapsed;

    // Non-state-aware Match/Non-Match Action rows are visible while the switch is off
    // (or the reader does not support state-aware at all).
    public Visibility NonStateAwareFiltersVisibility =>
        EnableStateAwareFilters ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    private string filter1Option = "Match";

    [ObservableProperty]
    private string filter2Option = "Match";

    [ObservableProperty]
    private string population = "32";

    [ObservableProperty]
    private string reportEvery = "1";

    [ObservableProperty]
    private string useSpecifiedFrequencies = "Disabled";

    [ObservableProperty]
    private ObservableCollection<FrequencyChannelRow> frequencyChannelOptions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyChannelsVisibility))]
    private bool isFrequencyChannelsEnabled;

    public Visibility FrequencyChannelsVisibility =>
        IsFrequencyChannelsEnabled ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool lowDutyCycleEnabled;

    [ObservableProperty]
    private string emptyFieldTimeoutMs = "500";

    [ObservableProperty]
    private string fieldPingIntervalMs = "200";

    [ObservableProperty]
    private bool gpo1;

    [ObservableProperty]
    private bool gpo2;

    [ObservableProperty]
    private bool gpo3;

    [ObservableProperty]
    private bool gpo4;

    [ObservableProperty]
    private string statusMessage = "Query, configure or load SDK default settings.";

    public DataSourceSettingsViewModel(ReaderFleetService fleet, InventoryPresetRepository inventoryPresets)
    {
        this.fleet = fleet;
        this.inventoryPresets = inventoryPresets;
        PageTitle = "Inventory Settings";
        AntennaSettings =
        [
            new AntennaSettingsRow("Antenna 1"),
            new AntennaSettingsRow("Antenna 2"),
            new AntennaSettingsRow("Antenna 3"),
            new AntennaSettingsRow("Antenna 4"),
        ];
        GpiSettings =
        [
            new GpiSettingsRow(1),
            new GpiSettingsRow(2),
            new GpiSettingsRow(3),
            new GpiSettingsRow(4),
        ];
    }

    public Guid? SelectedReaderId { get; private set; }
    public ObservableCollection<string> TxPowerOptions { get; } = [];
    public ObservableCollection<string> RxSensitivityOptions { get; } = [];
    public ObservableCollection<string> RfModeOptions { get; } = [];
    public ObservableCollection<AntennaSettingsRow> AntennaSettings { get; }
    public ObservableCollection<GpiSettingsRow> GpiSettings { get; }

    public event Action? CancelRequested;

    public void SetSelectedReader(ReaderItemViewModel? reader)
    {
        SelectedReaderId = reader?.Id;
        SelectedReaderName = reader?.Name ?? "No reader selected";
        SelectedReaderHost = reader?.Host ?? "-";
        SelectedReaderModel = string.IsNullOrWhiteSpace(reader?.Details) ? "-" : reader.Details;
        SelectedReaderRegion = "Reader default";
    }

    /// <summary>
    /// Shows the last saved settings for the reader without connecting. Live data requires an
    /// explicit REFRESH SETTINGS (QuerySettingsAsync).
    /// </summary>
    public async Task LoadCachedSettingsAsync(ReaderItemViewModel reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        SetSelectedReader(reader);

        try
        {
            ReaderSettings? saved = await inventoryPresets.LoadDefaultAsync(reader.Id, cancellationToken);
            if (saved is not null)
            {
                settingsDraft = saved;
                ApplySettingsToUi(settingsDraft);
                // If capabilities were synced earlier (startup/sync), repopulate the capability
                // dropdowns for the cached page without needing a live connection.
                if (capabilities is not null)
                {
                    RefreshOptions(capabilities);
                }
                SettingsOrigin = $"Cached preset ({DateTime.Now:HH:mm:ss})";
                StatusMessage = $"{reader.Name}: Showing last saved settings; REFRESH SETTINGS for live data.";
            }
            else
            {
                StatusMessage = $"{reader.Name}: No cached settings yet; use REFRESH SETTINGS to query the reader.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load cached settings: {ex.Message}";
        }
    }

    public async Task InitializeForReaderAsync(ReaderItemViewModel reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        SetSelectedReader(reader);

        try
        {
            StatusMessage = $"{reader.Name}: Connecting and loading capabilities...";
            await EnsureConnectedAndLoadCapabilitiesAsync(reader.Id, cancellationToken);

            ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(reader.Id, cancellationToken);
            InventorySettings? inventory = snapshot.ManagedRoSpec?.Inventory ?? snapshot.Settings.Inventory;
            if (inventory is not null)
            {
                settingsDraft = snapshot.Settings with { Inventory = inventory };
                ApplySettingsToUi(settingsDraft);
                SettingsOrigin = $"Loaded from reader at {DateTime.Now:HH:mm:ss}";
                StatusMessage = $"{reader.Name}: Loaded current reader settings.";
                return;
            }

            ReaderSettings? saved = await inventoryPresets.LoadDefaultAsync(reader.Id, cancellationToken);
            if (saved is not null)
            {
                settingsDraft = saved;
                ApplySettingsToUi(settingsDraft);
                SettingsOrigin = "Loaded from local history";
                StatusMessage = $"{reader.Name}: No reader inventory was found; loaded local history.";
                return;
            }

            ReaderSettingsDefaults defaults = await fleet.GetDefaultSettingsAsync(reader.Id, cancellationToken);
            settingsDraft = defaults.Settings;
            ApplySettingsToUi(settingsDraft);

            SettingsOrigin = "SDK defaults";
            StatusMessage = $"{reader.Name}: No reader inventory or local history was found; loaded SDK defaults.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"{reader.Name}: Initialization failed: {ex.Message}";
        }
    }

    partial void OnUseIndividualAntennaSettingsChanged(bool value)
    {
        if (!value)
        {
            foreach (AntennaSettingsRow row in AntennaSettings)
            {
                row.TxPower = powerDbm;
                row.RxSensitivity = rxSensitivity;
            }
        }
    }

    partial void OnIsIndividualAntennasExpandedChanged(bool value)
    {
        UseIndividualAntennaSettings = value;
        // In per-antenna mode the outer (all-antenna) power/sensitivity fields are disabled.
        IsGlobalAntennaSettingsEnabled = !value;
        if (value)
        {
            // Rows initialize from the all-antenna values when expanded. When the expansion is driven
            // by a reader read-back, the per-antenna loop that follows overwrites the rows with the
            // actual per-antenna values, so this only seeds rows on a manual expansion.
            foreach (AntennaSettingsRow row in AntennaSettings)
            {
                row.TxPower = PowerDbm;
                row.RxSensitivity = RxSensitivity;
            }
        }
    }

    [RelayCommand]
    private async Task QuerySettingsAsync()
    {
        if (SelectedReaderId is not Guid readerId)
        {
            StatusMessage = "Select a reader first.";
            return;
        }

        try
        {
            await EnsureConnectedAndLoadCapabilitiesAsync(readerId);
            ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(readerId, CancellationToken.None);
            settingsDraft = snapshot.Settings;
            ApplySettingsToUi(settingsDraft);
            SettingsOrigin = $"Queried from {SelectedReaderName} at {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"{SelectedReaderName}: Settings queried successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Query failed: {ex.Message}";
        }
        finally
        {
            await DisconnectIfNotInventoryingAsync(readerId);
        }
    }

    [RelayCommand]
    private async Task DefaultSettingsAsync()
    {
        if (SelectedReaderId is not Guid readerId)
        {
            StatusMessage = "Select a reader first.";
            return;
        }

        try
        {
            await EnsureConnectedAndLoadCapabilitiesAsync(readerId);
            ReaderSettingsDefaults defaults = await fleet.GetDefaultSettingsAsync(readerId, CancellationToken.None);
            settingsDraft = defaults.Settings;
            ApplySettingsToUi(settingsDraft);
            SettingsOrigin = $"SDK Defaults for {SelectedReaderName}";
            StatusMessage = $"{SelectedReaderName}: SDK default settings loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load defaults failed: {ex.Message}";
        }
        finally
        {
            await DisconnectIfNotInventoryingAsync(readerId);
        }
    }

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        if (SelectedReaderId is not Guid readerId)
        {
            StatusMessage = "Select a reader first.";
            return;
        }

        if (settingsDraft is null)
        {
            StatusMessage = "Load or query settings draft first.";
            return;
        }

        try
        {
            StatusMessage = $"{SelectedReaderName}: Connecting before applying settings...";
            await EnsureConnectedAndLoadCapabilitiesAsync(readerId);
            ushort? hopTableId = fleet.GetCapabilities(readerId)?.HopTables.FirstOrDefault()?.HopTableId;
            ReaderSettings settings = BuildSettingsFromUi(hopTableId);
            await fleet.ApplySettingsAsync(readerId, settings, CancellationToken.None);
            await inventoryPresets.SaveDefaultAsync(readerId, settings, CancellationToken.None);
            settingsDraft = settings;
            SettingsOrigin = $"Saved to reader at {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"{SelectedReaderName}: Settings saved to reader and local history.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply settings failed: {ex.Message}";
        }
        finally
        {
            await DisconnectIfNotInventoryingAsync(readerId);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }

    partial void OnGpo1Changed(bool oldValue, bool newValue) => _ = SetGpoAsync(1, oldValue, newValue);
    partial void OnGpo2Changed(bool oldValue, bool newValue) => _ = SetGpoAsync(2, oldValue, newValue);
    partial void OnGpo3Changed(bool oldValue, bool newValue) => _ = SetGpoAsync(3, oldValue, newValue);
    partial void OnGpo4Changed(bool oldValue, bool newValue) => _ = SetGpoAsync(4, oldValue, newValue);

    private async Task SetGpoAsync(ushort portNumber, bool oldValue, bool newValue)
    {
        if (suppressGpoUpdate)
        {
            return;
        }

        if (SelectedReaderId is not Guid readerId)
        {
            StatusMessage = "Select a data source before setting GPO.";
            RevertGpo(portNumber, oldValue);
            return;
        }

        // Multiple GPO switches can be toggled quickly; only disconnect once the last in-flight
        // operation finishes, otherwise one GPO's disconnect kills another GPO's session.
        int inFlight = Interlocked.Increment(ref gpoOperationsInFlight);
        try
        {
            StatusMessage = $"{SelectedReaderName}: Connecting before setting GPO {portNumber}...";
            await EnsureConnectedAndLoadCapabilitiesAsync(readerId);
            await fleet.SetGpoAsync(readerId, portNumber, newValue, CancellationToken.None);
            StatusMessage = $"{SelectedReaderName}: GPO {portNumber} set to {(newValue ? "ON" : "OFF")}.";
        }
        catch (Exception ex)
        {
            RevertGpo(portNumber, oldValue);
            StatusMessage = $"GPO {portNumber} update failed: {ex.Message}";
        }
        finally
        {
            if (Interlocked.Decrement(ref gpoOperationsInFlight) == 0)
            {
                await DisconnectIfNotInventoryingAsync(readerId);
            }
        }
    }

    private void RevertGpo(ushort portNumber, bool value)
    {
        suppressGpoUpdate = true;
        try
        {
            switch (portNumber)
            {
                case 1:
                    Gpo1 = value;
                    break;
                case 2:
                    Gpo2 = value;
                    break;
                case 3:
                    Gpo3 = value;
                    break;
                case 4:
                    Gpo4 = value;
                    break;
            }
        }
        finally
        {
            suppressGpoUpdate = false;
        }
    }

    private ReaderSettings BuildSettingsFromUi(ushort? hopTableId)
    {
        InventorySettings baseInventory = settingsDraft?.Inventory ?? new InventorySettings();
        ReaderSettings baseSettings = settingsDraft ?? new ReaderSettings();
        ushort[] antennaIds = ParseAntennaIds(Antennas);
        byte session = ParseSession(Session);
        ushort population = ParseUShort(Population, nameof(Population));
        ushort reportEvery = ParseUShort(ReportEvery, nameof(ReportEvery));
        ushort modeIndex = ParseModeIndex(RfMode);

        List<InventoryAntennaConfiguration> antennaConfigurations = BuildAntennaConfigurations(antennaIds, hopTableId);
        IReadOnlyList<InventorySelectFilter> filters = BuildFiltersFromUi();
        IReadOnlyDictionary<string, object?> extensions = BuildInventoryExtensions(baseInventory.Extensions);

        InventorySettings inventory = baseInventory with
        {
            AntennaIds = Array.AsReadOnly(antennaIds),
            AntennaConfigurations = antennaConfigurations,
            Session = session,
            TagPopulationEstimate = population,
            ReportEveryNTags = reportEvery,
            Report = baseInventory.Report with { Trigger = InventoryReportTrigger.UponNTagsOrEndOfAiSpec },
            ModeIndex = modeIndex,
            Filters = filters,
            // State-aware filters require an explicit state-aware singulation on readers that support it.
            StateAwareSingulation = filters.Any(static filter => filter.StateAwareAction is not null)
                ? new InventoryStateAwareSingulation()
                : null,
            StartTrigger = BuildStartTrigger(),
            StopTrigger = BuildStopTrigger(),
            Extensions = extensions,
        };

        return baseSettings with
        {
            Inventory = inventory,
            Configuration = baseSettings.Configuration with
            {
                Extensions = BuildReaderExtensions(baseSettings.Configuration.Extensions),
            },
        };
    }

    private IReadOnlyDictionary<string, object?> BuildInventoryExtensions(IReadOnlyDictionary<string, object?> source)
    {
        var extensions = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        ImpinjInventoryReportOptions existing =
            extensions.TryGetValue(ImpinjInventoryReportOptions.ExtensionKey, out object? value) &&
            value is ImpinjInventoryReportOptions options
                ? options
                : new ImpinjInventoryReportOptions();
        ImpinjInventoryReportOptions requested = existing with
        {
            IncludeSerializedTid = EnableFastId,
            IncludeRfPhaseAngle = ReportPhaseAngle,
            IncludeRfDopplerFrequency = ReportDopplerFrequency,
        };

        if (EnableFastId || ReportPhaseAngle || ReportDopplerFrequency ||
            extensions.ContainsKey(ImpinjInventoryReportOptions.ExtensionKey))
        {
            extensions[ImpinjInventoryReportOptions.ExtensionKey] = requested;
        }

        // Search Mode / Fixed Frequency / Low Duty Cycle are inventory-command extensions
        // (allowedIn C1G2InventoryCommand per Impinjdef.xml), so they travel with the inventory
        // settings rather than the reader configuration.
        ImpinjInventoryControlOptions existingControl =
            extensions.TryGetValue(ImpinjInventoryControlOptions.ExtensionKey, out object? controlValue) &&
            controlValue is ImpinjInventoryControlOptions controlOptions
                ? controlOptions
                : new ImpinjInventoryControlOptions();
        ImpinjInventoryControlOptions requestedControl = existingControl with
        {
            InventorySearchMode = ParseSearchMode(SearchMode),
            FixedFrequency = BuildFixedFrequency(),
            LowDutyCycle = LowDutyCycleEnabled
                ? new ImpinjLowDutyCycleSettings(
                    ImpinjLowDutyCycleMode.Enabled,
                    ParseUShort(EmptyFieldTimeoutMs, nameof(EmptyFieldTimeoutMs)),
                    ParseUShort(FieldPingIntervalMs, nameof(FieldPingIntervalMs)))
                : null,
        };
        extensions[ImpinjInventoryControlOptions.ExtensionKey] = requestedControl;

        return extensions.Count == 0
            ? new InventorySettings().Extensions
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(extensions);
    }

    private IReadOnlyDictionary<string, object?> BuildReaderExtensions(IReadOnlyDictionary<string, object?> source)
    {
        var extensions = new Dictionary<string, object?>(source, StringComparer.Ordinal);

        // Only the fields the UI manages are sent. Fields echoed back from a query (AccessSpec,
        // AdvancedGpos, LinkMonitor, ReportBufferMode, ReducedPowerFrequency) are left null/empty so
        // SET_READER_CONFIG does not carry parameters some firmware rejects (M_UnsupportedParameter).
        ImpinjReaderConfiguration requested = new()
        {
            GpiDebounce = HasGpiConfiguration()
                ? GpiSettings
                    .Select(static row => new ImpinjGpiDebounceSetting((ushort)row.Port, ParseDebounceMs(row.DebounceMs)))
                    .ToArray()
                : [],
        };
        extensions[ImpinjReaderConfiguration.ExtensionKey] = requested;

        return extensions.Count == 0
            ? new ReaderSettings().Extensions
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(extensions);
    }

    private bool HasGpiConfiguration() =>
        GpiSettings.Any(static row => row.StartEnabled || row.StopEnabled);

    private ImpinjFixedFrequencySettings? BuildFixedFrequency() =>
        UseSpecifiedFrequencies.ToUpperInvariant() switch
        {
            "CHANNEL LIST" => BuildChannelListFrequency(),
            "AUTO SELECT" => new ImpinjFixedFrequencySettings(ImpinjFixedFrequencyMode.Auto_Select, []),
            _ => null,
        };

    private ImpinjFixedFrequencySettings BuildChannelListFrequency()
    {
        ushort[] channels = FrequencyChannelOptions
            .Where(row => row.IsSelected)
            .Select(row => row.ChannelIndex)
            .ToArray();

        // Impinj requires 1-50 channels; an empty ChannelList fails with
        // "//ImpinjFixedFrequencyList/ChannelList : invalid number of channels".
        if (channels.Length == 0)
        {
            throw new InvalidOperationException(
                "Channel List 模式需要至少勾选 1 个频道（Impinj 要求 1-50 个频道）。");
        }

        return new ImpinjFixedFrequencySettings(ImpinjFixedFrequencyMode.Channel_List, channels);
    }

    private static ImpinjInventorySearchType? ParseSearchMode(string value) =>
        value.Trim() switch
        {
            "" or "Reader Selected" => null,
            "Single Target" => ImpinjInventorySearchType.Single_Target,
            "Dual Target" => ImpinjInventorySearchType.Dual_Target,
            "TagFocus" => ImpinjInventorySearchType.Single_Target_With_Suppression,
            "No Target" => ImpinjInventorySearchType.No_Target,
            "Single Target Reset" => ImpinjInventorySearchType.Single_Target_BtoA,
            "Dual Target Select B to A" => ImpinjInventorySearchType.Dual_Target_with_BtoASelect,
            _ => null,
        };

    private IReadOnlyList<InventorySelectFilter> BuildFiltersFromUi()
    {
        var filters = new List<InventorySelectFilter>(2);
        AddFilter(filters, Filter1Enabled, Filter1, Filter1BitLength, Filter1Offset, Filter1MemoryBank, Filter1Target, Filter1Action, Filter1MatchAction, Filter1NonMatchAction, EnableStateAwareFilters);
        AddFilter(filters, Filter2Enabled, Filter2, Filter2BitLength, Filter2Offset, Filter2MemoryBank, Filter2Target, Filter2Action, Filter2MatchAction, Filter2NonMatchAction, EnableStateAwareFilters);
        return filters;
    }

    private static void AddFilter(
        List<InventorySelectFilter> filters,
        bool enabled,
        string value,
        string bitLength,
        string offset,
        string memoryBank,
        string target,
        string action,
        string matchAction,
        string nonMatchAction,
        bool enableStateAware)
    {
        if (!enabled || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        InventorySelectFilter filter = new()
        {
            MemoryBank = ParseMemoryBank(memoryBank),
            BitPointer = ParseUShort(offset, "Filter offset"),
            Mask = HexCodec.ParseBytes(value),
            BitLength = ParseUShort(bitLength, "Filter bit length"),
            MatchAction = ParseSelectAction(matchAction),
            NonMatchAction = ParseSelectAction(nonMatchAction),
        };

        // State-aware filters are only valid on readers that advertise state-aware singulation support;
        // the compiler also requires StateAwareSingulation (set in BuildSettingsFromUi).
        if (enableStateAware)
        {
            filter = filter with
            {
                StateAwareAction = new InventoryStateAwareFilterAction
                {
                    Target = ParseFilterTarget(target),
                    Action = ParseFilterAction(action),
                },
            };
        }

        filters.Add(filter);
    }

    private static InventorySelectAction ParseSelectAction(string value) => value.Trim() switch
    {
        "Do Nothing" => InventorySelectAction.DoNothing,
        "Unselect" => InventorySelectAction.Unselect,
        _ => InventorySelectAction.Select,
    };

    private static string FormatSelectAction(InventorySelectAction action) => action switch
    {
        InventorySelectAction.DoNothing => "Do Nothing",
        InventorySelectAction.Unselect => "Unselect",
        _ => "Select",
    };

    private static InventoryFilterTarget ParseFilterTarget(string value) => value.Trim() switch
    {
        "Selected Flag" => InventoryFilterTarget.SelectedFlag,
        "Session1" => InventoryFilterTarget.Session1,
        "Session2" => InventoryFilterTarget.Session2,
        "Session3" => InventoryFilterTarget.Session3,
        _ => InventoryFilterTarget.Session0,
    };

    private static string FormatFilterTarget(InventoryFilterTarget target) => target switch
    {
        InventoryFilterTarget.SelectedFlag => "Selected Flag",
        InventoryFilterTarget.Session1 => "Session1",
        InventoryFilterTarget.Session2 => "Session2",
        InventoryFilterTarget.Session3 => "Session3",
        _ => "Session0",
    };

    private static InventoryFilterAction ParseFilterAction(string value) => value.Trim() switch
    {
        "Assert A/No Op" => InventoryFilterAction.AssertSelectedOrStateAAndNoOperation,
        "No Op/Deassert B" => InventoryFilterAction.NoOperationAndDeassertSelectedOrStateB,
        "Negate/No Op" => InventoryFilterAction.NegateSelectedOrStateAndNoOperation,
        "Deassert B/Assert A" => InventoryFilterAction.DeassertSelectedOrStateBAndAssertSelectedOrStateA,
        "Deassert B/No Op" => InventoryFilterAction.DeassertSelectedOrStateBAndNoOperation,
        "No Op/Assert A" => InventoryFilterAction.NoOperationAndAssertSelectedOrStateA,
        "No Op/Negate" => InventoryFilterAction.NoOperationAndNegateSelectedOrState,
        _ => InventoryFilterAction.AssertSelectedOrStateAAndDeassertSelectedOrStateB,
    };

    private static string FormatFilterAction(InventoryFilterAction action) => action switch
    {
        InventoryFilterAction.AssertSelectedOrStateAAndNoOperation => "Assert A/No Op",
        InventoryFilterAction.NoOperationAndDeassertSelectedOrStateB => "No Op/Deassert B",
        InventoryFilterAction.NegateSelectedOrStateAndNoOperation => "Negate/No Op",
        InventoryFilterAction.DeassertSelectedOrStateBAndAssertSelectedOrStateA => "Deassert B/Assert A",
        InventoryFilterAction.DeassertSelectedOrStateBAndNoOperation => "Deassert B/No Op",
        InventoryFilterAction.NoOperationAndAssertSelectedOrStateA => "No Op/Assert A",
        InventoryFilterAction.NoOperationAndNegateSelectedOrState => "No Op/Negate",
        _ => "Assert A/Deassert B",
    };

    private static ushort ParseMemoryBank(string value) => value.Trim().ToUpperInvariant() switch
    {
        "RESERVED" => 0,
        "TID" => 2,
        "USER" => 3,
        _ => 1,
    };

    private InventoryStartTrigger BuildStartTrigger()
    {
        GpiSettingsRow? row = GpiSettings.FirstOrDefault(static setting => setting.StartEnabled);
        if (row is null)
        {
            return new InventoryStartTrigger();
        }

        return new InventoryStartTrigger
        {
            Type = InventoryStartTriggerType.Gpi,
            GpiPortNumber = (ushort)row.Port,
            GpiState = IsHighLevel(row.StartLevel),
        };
    }

    private InventoryStopTrigger BuildStopTrigger()
    {
        GpiSettingsRow? row = GpiSettings.FirstOrDefault(static setting => setting.StopEnabled);
        if (row is null)
        {
            return new InventoryStopTrigger();
        }

        return new InventoryStopTrigger
        {
            Type = InventoryStopTriggerType.GpiWithTimeout,
            GpiPortNumber = (ushort)row.Port,
            GpiState = IsHighLevel(row.StopLevel),
        };
    }

    private static bool IsHighLevel(string level) =>
        level.Trim().Equals("High", StringComparison.OrdinalIgnoreCase);

    private static uint ParseDebounceMs(string value)
    {
        if (!uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint result))
        {
            throw new InvalidOperationException("GPI debounce must be a non-negative number of milliseconds.");
        }

        return result;
    }

    private List<InventoryAntennaConfiguration> BuildAntennaConfigurations(ushort[] antennaIds, ushort? hopTableId)
    {
        bool selectsAllAntennas = antennaIds.Length == 1 && antennaIds[0] == 0;
        HashSet<ushort> selected = antennaIds.ToHashSet();
        List<InventoryAntennaConfiguration> antennaConfigurations = [];

        if (!UseIndividualAntennaSettings)
        {
            ushort? txPower = TryParseNullableTxPowerIndex(PowerDbm);
            // A reader that exposes a sensitivity table always gets an RFReceiver; an empty or invalid
            // sensitivity falls back to the most sensitive level instead of being cleared.
            ushort? rxSensitivity = TryParseNullableRxSensitivityIndex(RxSensitivity) ?? maxRxSensitivityIndex;
            if (txPower is null && rxSensitivity is null)
            {
                return antennaConfigurations;
            }

            // AntennaId 0 applies this configuration to every antenna selected for inventory, so the
            // power/sensitivity settings stay independent of which antennas are enabled.
            antennaConfigurations.Add(CreateAntennaConfiguration(0, txPower, rxSensitivity, hopTableId));
            return antennaConfigurations;
        }

        for (int i = 0; i < AntennaSettings.Count; i++)
        {
            ushort antennaId = checked((ushort)(i + 1));
            if (!selectsAllAntennas && !selected.Contains(antennaId))
            {
                continue;
            }

            AntennaSettingsRow row = AntennaSettings[i];
            ushort? txPower = TryParseNullableTxPowerIndex(row.TxPower);
            ushort? rxSensitivity = TryParseNullableRxSensitivityIndex(row.RxSensitivity) ?? maxRxSensitivityIndex;
            if (txPower is null && rxSensitivity is null)
            {
                continue;
            }

            antennaConfigurations.Add(CreateAntennaConfiguration(antennaId, txPower, rxSensitivity, hopTableId));
        }

        return antennaConfigurations;
    }

    private static InventoryAntennaConfiguration CreateAntennaConfiguration(
        ushort antennaId,
        ushort? txPower,
        ushort? rxSensitivity,
        ushort? hopTableId)
    {
        return new InventoryAntennaConfiguration
        {
            AntennaId = antennaId,
            ReceiverSensitivityIndex = rxSensitivity,
            TransmitPowerIndex = txPower,
            // The LLRP RFTransmitter requires a hop table reference together with power. Use the first
            // hop table the reader actually reported when available; fall back to the conventional id 1.
            // FrequencyInformation reports either a hop table list or a fixed frequency table (never both),
            // so for fixed-frequency readers the Impinj FixedFrequencyList extension governs channels and
            // this reference is ignored by the reader firmware.
            HopTableId = hopTableId ?? (ushort)1,
            ChannelIndex = txPower is null ? null : (ushort)1,
        };
    }

    private async Task EnsureConnectedAndLoadCapabilitiesAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        // Never steal a connection that is mid-inventory: ConnectAsync would overwrite the
        // Inventorying state and the disconnect below would tear down the running ROSpec.
        ReaderStatus before = fleet.Readers.First(current => current.Profile.Id == readerId);
        if (before.State is StudioReaderState.Inventorying or StudioReaderState.Stopping or StudioReaderState.Connecting)
        {
            throw new InvalidOperationException("The reader is busy (inventory running); stop it before changing settings.");
        }

        await fleet.ConnectAsync(readerId, cancellationToken);
        ReaderCapabilities? capabilities = fleet.GetCapabilities(readerId);
        if (capabilities is null)
        {
            return;
        }

        ApplyCapabilities(capabilities);
    }

    /// <summary>
    /// Drops the connection after a settings operation unless the reader is mid-inventory, where
    /// disconnecting would tear down the running ROSpec and stop tag reports.
    /// </summary>
    private async Task DisconnectIfNotInventoryingAsync(Guid readerId)
    {
        try
        {
            ReaderStatus status = fleet.Readers.First(current => current.Profile.Id == readerId);
            if (status.State is StudioReaderState.Inventorying or StudioReaderState.Stopping or StudioReaderState.Connecting)
            {
                return;
            }

            await fleet.DisconnectAsync(readerId, CancellationToken.None);
        }
        catch
        {
            // Best effort; the reader stays connected if the state could not be determined.
        }
    }

    /// <summary>
    /// Records capabilities retrieved from the device (from a connect path / startup sync) and
    /// rebuilds the capability-derived dropdown options. The value is kept in memory so a later
    /// cache-loaded settings page can still populate RF mode / Tx/Rx / frequency without reconnecting.
    /// </summary>
    public void ApplyCapabilities(ReaderCapabilities capabilities)
    {
        this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        RefreshOptions(capabilities);
    }

    private void RefreshOptions(ReaderCapabilities capabilities)
    {
        // Remember the current UI values so a collection rebuild (which clears two-way bound ComboBox
        // text back to the binding source) can restore the still-valid selection afterwards.
        string preservedTxPower = PowerDbm;
        string preservedRxSensitivity = RxSensitivity;
        string preservedRfMode = RfMode;
        HashSet<ushort> preservedChannels = FrequencyChannelOptions
            .Where(row => row.IsSelected)
            .Select(row => row.ChannelIndex)
            .ToHashSet();

        txPowerIndexesByDisplay.Clear();
        txPowerDisplaysByIndex.Clear();
        txPowerEntries = capabilities.TxPowers;
        string[] txPowerOptions = capabilities.TxPowers
            .OrderBy(static value => value.TransmitPowerValue)
            .Select(FormatTxPowerOption)
            .ToArray();
        ReplaceOptions(TxPowerOptions, txPowerOptions);

        rxSensitivityIndexesByDisplay.Clear();
        rxSensitivityDisplaysByIndex.Clear();
        rxSensitivityEntries = capabilities.RxSensitivities;
        string[] rxSensitivityOptions = capabilities.RxSensitivities
            .OrderBy(static value => value.ReceiveSensitivityValue)
            .Select(FormatRxSensitivityOption)
            .ToArray();
        ReplaceOptions(RxSensitivityOptions, rxSensitivityOptions);
        if (capabilities.RxSensitivities.Count > 0)
        {
            // The most sensitive entry is the one with the smallest (most negative) reported value.
            RxSensitivityEntry mostSensitive = capabilities.RxSensitivities
                .OrderBy(static value => value.ReceiveSensitivityValue)
                .First();
            maxRxSensitivityIndex = mostSensitive.Index;
            maxRxSensitivityDisplay = FormatRxSensitivityIndex(mostSensitive.Index);
        }
        else
        {
            maxRxSensitivityIndex = null;
            maxRxSensitivityDisplay = null;
        }

        string[] rfModeOptions = capabilities.RfModes
            .Select(static mode => $"{mode.ModeIdentifier}({FormatRfModeLink(mode)})")
            .ToArray();
        ReplaceOptions(RfModeOptions, rfModeOptions);

        rfModeLinkByModeId.Clear();
        foreach (C1G2RfModeEntry mode in capabilities.RfModes)
        {
            rfModeLinkByModeId[mode.ModeIdentifier] = FormatRfModeLink(mode);
        }

        SupportsStateAwareFilters = capabilities.CanDoTagInventoryStateAwareSingulation;

        // Frequency channel table comes from standard LLRP capabilities. Prefer the hop table
        // (FrequencyInformation.HopTable); fall back to the fixed-frequency table (TxFrequencies),
        // which some readers report instead. The channel number written to ImpinjFixedFrequencyList
        // is the 1-based position within the chosen table.
        FrequencyChannelOptions.Clear();
        IReadOnlyList<uint> frequencies = capabilities.HopTables.FirstOrDefault()?.Frequencies
            ?? capabilities.TxFrequencies;
        if (frequencies.Count > 0)
        {
            for (int i = 0; i < frequencies.Count; i++)
            {
                FrequencyChannelOptions.Add(new FrequencyChannelRow((ushort)(i + 1), frequencies[i]));
            }
        }

        // Restore the channel selections the user made before the rebuild (e.g. ApplySettingsAsync
        // re-runs RefreshOptions right before building the settings, which would otherwise drop them).
        foreach (FrequencyChannelRow row in FrequencyChannelOptions)
        {
            row.IsSelected = preservedChannels.Contains(row.ChannelIndex);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[DataSourceSettings] Frequency options: hopTables={capabilities.HopTables.Count}, " +
            $"txFrequencies={capabilities.TxFrequencies.Count}, shown={FrequencyChannelOptions.Count}");

        // Restore selections that are still present in the (possibly rebuilt) option lists.
        if (txPowerIndexesByDisplay.ContainsKey(preservedTxPower))
        {
            PowerDbm = preservedTxPower;
        }

        if (rxSensitivityIndexesByDisplay.ContainsKey(preservedRxSensitivity))
        {
            RxSensitivity = preservedRxSensitivity;
        }

        if (!string.IsNullOrEmpty(preservedRfMode) &&
            rfModeLinkByModeId.ContainsKey(ParseModeIndex(preservedRfMode)))
        {
            RfMode = preservedRfMode;
        }

        ushort resolvedMaxAntennas = capabilities.MaxNumberOfAntennas == 0 ? (ushort)4 : capabilities.MaxNumberOfAntennas;
        maxAntennas = resolvedMaxAntennas;
        bool shouldResetAntennas = string.IsNullOrWhiteSpace(Antennas);
        if (!shouldResetAntennas)
        {
            try
            {
                shouldResetAntennas = ParseAntennaIds(Antennas).Any(id => id > maxAntennas);
            }
            catch
            {
                shouldResetAntennas = true;
            }
        }

        if (shouldResetAntennas)
        {
            Antennas = string.Join(", ", Enumerable.Range(1, maxAntennas).Select(static value => value.ToString(CultureInfo.InvariantCulture)));
        }
        while (AntennaSettings.Count < maxAntennas)
        {
            AntennaSettings.Add(new AntennaSettingsRow($"Antenna {AntennaSettings.Count + 1}"));
        }

        while (AntennaSettings.Count > maxAntennas)
        {
            AntennaSettings.RemoveAt(AntennaSettings.Count - 1);
        }
    }

    private readonly Dictionary<uint, string> rfModeLinkByModeId = [];

    private string FormatRfMode(uint mode) =>
        rfModeLinkByModeId.TryGetValue(mode, out string? link)
            ? $"{mode}({link})"
            : mode.ToString(CultureInfo.InvariantCulture);

    private static string FormatRfModeLink(C1G2RfModeEntry mode)
    {
        int bdrKbps = (int)(mode.BdrValue / 1000.0);
        double tariUs = mode.MinTariValue / 1000.0;
        double pieUs = mode.PieValue / 1000.0;
        return $"M{mode.MValue}/{bdrKbps}K Tari: {tariUs:0.#} uS, (PIE: {pieUs:F1})";
    }

    private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> values)
    {
        string[] nextValues = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (nextValues.Length == 0)
        {
            return;
        }

        // Rebuilding the collection while a two-way bound ComboBox is open clears its Text back to the
        // binding source (empty), so skip the rebuild when nothing actually changed.
        if (nextValues.Length == target.Count &&
            target.SequenceEqual(nextValues, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        target.Clear();
        foreach (string value in nextValues)
        {
            target.Add(value);
        }
    }

    private string FormatTxPowerOption(TxPowerEntry entry)
    {
        string display = FormatDbm(entry.TransmitPowerDbm);
        txPowerIndexesByDisplay[display] = entry.Index;
        txPowerDisplaysByIndex[entry.Index] = display;
        return display;
    }

    private string FormatRxSensitivityOption(RxSensitivityEntry entry)
    {
        // Show the raw capability value (0, 10, 11, ...) so the UI matches what the SDK exposes;
        // the SDK's ReceiveSensitivityDbm (value/100) is an offset, not an absolute dBm.
        string display = entry.ReceiveSensitivityValue.ToString(CultureInfo.InvariantCulture);
        rxSensitivityIndexesByDisplay[display] = entry.Index;
        rxSensitivityDisplaysByIndex[entry.Index] = display;
        return display;
    }

    private static string FormatDbm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private void ApplyInventoryToUi(InventorySettings inventory)
    {
        if (inventory.AntennaIds.Contains((ushort)0))
        {
            // AntennaIds 0 means "all antennas"; show the concrete list for the UI.
            Antennas = string.Join(", ", Enumerable.Range(1, Math.Max(1, (int)maxAntennas)));
        }
        else
        {
            Antennas = string.Join(", ", inventory.AntennaIds);
        }

        Session = $"Session {inventory.Session}";
        Population = inventory.TagPopulationEstimate.ToString(CultureInfo.InvariantCulture);
        ReportEvery = inventory.ReportEveryNTags.ToString(CultureInfo.InvariantCulture);
        RfMode = FormatRfMode(inventory.ModeIndex);

        InventoryAntennaConfiguration? global = inventory.AntennaConfigurations.FirstOrDefault(static value => value.AntennaId == 0);
        if (global is not null)
        {
            UseIndividualAntennaSettings = false;
            IsIndividualAntennasExpanded = false;
            if (global.TransmitPowerIndex is ushort txPower)
            {
                PowerDbm = FormatTxPowerIndex(txPower);
            }

            if (global.ReceiverSensitivityIndex is ushort rxSensitivity)
            {
                RxSensitivity = FormatRxSensitivityIndex(rxSensitivity);
            }

            // A single AntennaId-0 configuration applies to every inventory antenna; mirror it into
            // the per-antenna rows so expanding and saving in per-antenna mode keeps the same values.
            foreach (AntennaSettingsRow row in AntennaSettings)
            {
                row.TxPower = PowerDbm;
                row.RxSensitivity = RxSensitivity;
            }
        }
        else
        {
            UseIndividualAntennaSettings = inventory.AntennaConfigurations.Count > 0;
            // Per-antenna configurations on the reader expand the per-antenna editor.
            IsIndividualAntennasExpanded = inventory.AntennaConfigurations.Count > 0;
        }

        for (int i = 0; i < AntennaSettings.Count; i++)
        {
            AntennaSettingsRow row = AntennaSettings[i];
            InventoryAntennaConfiguration? configuration = inventory.AntennaConfigurations
                .FirstOrDefault(candidate => candidate.AntennaId == i + 1);
            if (configuration is null)
            {
                continue;
            }

            if (configuration.TransmitPowerIndex is ushort txPower)
            {
                row.TxPower = FormatTxPowerIndex(txPower);
            }

            if (configuration.ReceiverSensitivityIndex is ushort rxSensitivity)
            {
                row.RxSensitivity = FormatRxSensitivityIndex(rxSensitivity);
            }
        }

        // Antennas without a per-antenna configuration inherit the global baseline so a later save in
        // per-antenna mode writes the same power/sensitivity instead of stale row defaults.
        for (int i = 0; i < AntennaSettings.Count; i++)
        {
            if (inventory.AntennaConfigurations.Any(candidate => candidate.AntennaId == i + 1))
            {
                continue;
            }

            AntennaSettingsRow row = AntennaSettings[i];
            row.TxPower = PowerDbm;
            row.RxSensitivity = RxSensitivity;
        }

        ApplyFiltersToUi(inventory.Filters);
        ApplyGpiTriggersToUi(inventory.StartTrigger, inventory.StopTrigger);
        ApplyInventoryReportOptionsToUi(inventory.Extensions);
        ApplyInventoryControlToUi(inventory.Extensions);
    }

    private void ApplyInventoryControlToUi(IReadOnlyDictionary<string, object?> extensions)
    {
        ImpinjInventoryControlOptions? control =
            extensions.TryGetValue(ImpinjInventoryControlOptions.ExtensionKey, out object? value) &&
            value is ImpinjInventoryControlOptions controlOptions
                ? controlOptions
                : null;
        SearchMode = control?.InventorySearchMode is { } mode
            ? FormatSearchMode(mode)
            : "Reader Selected";

        LowDutyCycleEnabled = control?.LowDutyCycle?.Mode == ImpinjLowDutyCycleMode.Enabled;
        if (control?.LowDutyCycle is { } lowDutyCycle)
        {
            EmptyFieldTimeoutMs = lowDutyCycle.EmptyFieldTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
            FieldPingIntervalMs = lowDutyCycle.FieldPingIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
        }

        if (control?.FixedFrequency is { } fixedFrequency)
        {
            UseSpecifiedFrequencies = fixedFrequency.Mode switch
            {
                ImpinjFixedFrequencyMode.Channel_List => "Channel List",
                ImpinjFixedFrequencyMode.Auto_Select => "Auto Select",
                _ => "Disabled",
            };

            ApplyFrequencySelection(fixedFrequency.Mode == ImpinjFixedFrequencyMode.Channel_List
                ? fixedFrequency.ChannelList
                : null);
        }
        else
        {
            UseSpecifiedFrequencies = "Disabled";
            ApplyFrequencySelection(null);
        }
    }

    private void ApplySettingsToUi(ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Inventory is InventorySettings inventory)
        {
            ApplyInventoryToUi(inventory);
        }

        ApplyReaderConfigurationToUi(settings.Configuration.Extensions);
    }

    private void ApplyFiltersToUi(IReadOnlyList<InventorySelectFilter> filters)
    {
        bool stateAware1 = filters.Count > 0 && filters[0].StateAwareAction is not null;
        bool stateAware2 = filters.Count > 1 && filters[1].StateAwareAction is not null;
        EnableStateAwareFilters = stateAware1 || stateAware2;

        Filter1 = filters.Count > 0 ? FormatFilterMask(filters[0]) : string.Empty;
        Filter1Enabled = filters.Count > 0;
        Filter1BitLength = filters.Count > 0 ? filters[0].BitLength.ToString(CultureInfo.InvariantCulture) : "0";
        Filter1Offset = filters.Count > 0 ? filters[0].BitPointer.ToString(CultureInfo.InvariantCulture) : "32";
        Filter1MemoryBank = filters.Count > 0 ? FormatMemoryBank(filters[0].MemoryBank) : "EPC";
        Filter1Target = filters.Count > 0 && filters[0].StateAwareAction is { } action1
            ? FormatFilterTarget(action1.Target)
            : "Session0";
        Filter1Action = filters.Count > 0 && filters[0].StateAwareAction is { } action1b
            ? FormatFilterAction(action1b.Action)
            : "Assert A/Deassert B";
        Filter1MatchAction = filters.Count > 0 ? FormatSelectAction(filters[0].MatchAction) : "Select";
        Filter1NonMatchAction = filters.Count > 0 ? FormatSelectAction(filters[0].NonMatchAction) : "Unselect";

        Filter2 = filters.Count > 1 ? FormatFilterMask(filters[1]) : string.Empty;
        Filter2Enabled = filters.Count > 1;
        Filter2BitLength = filters.Count > 1 ? filters[1].BitLength.ToString(CultureInfo.InvariantCulture) : "0";
        Filter2Offset = filters.Count > 1 ? filters[1].BitPointer.ToString(CultureInfo.InvariantCulture) : "32";
        Filter2MemoryBank = filters.Count > 1 ? FormatMemoryBank(filters[1].MemoryBank) : "EPC";
        Filter2Target = filters.Count > 1 && filters[1].StateAwareAction is { } action2
            ? FormatFilterTarget(action2.Target)
            : "Session0";
        Filter2Action = filters.Count > 1 && filters[1].StateAwareAction is { } action2b
            ? FormatFilterAction(action2b.Action)
            : "Assert A/Deassert B";
        Filter2MatchAction = filters.Count > 1 ? FormatSelectAction(filters[1].MatchAction) : "Select";
        Filter2NonMatchAction = filters.Count > 1 ? FormatSelectAction(filters[1].NonMatchAction) : "Unselect";
    }

    private static string FormatFilterMask(InventorySelectFilter filter) =>
        filter.Mask.IsEmpty ? string.Empty : Convert.ToHexString(filter.Mask.Span);

    private static string FormatMemoryBank(ushort bank) => bank switch
    {
        0 => "Reserved",
        2 => "TID",
        3 => "User",
        _ => "EPC",
    };

    private void ApplyGpiTriggersToUi(InventoryStartTrigger start, InventoryStopTrigger stop)
    {
        foreach (GpiSettingsRow row in GpiSettings)
        {
            bool isStart = start.Type == InventoryStartTriggerType.Gpi && start.GpiPortNumber == row.Port;
            bool isStop = stop.Type == InventoryStopTriggerType.GpiWithTimeout && stop.GpiPortNumber == row.Port;
            row.StartEnabled = isStart;
            row.StartLevel = isStart && start.GpiState ? "High" : "Low";
            row.StopEnabled = isStop;
            row.StopLevel = isStop && stop.GpiState ? "High" : "Low";
        }
    }

    private void ApplyInventoryReportOptionsToUi(IReadOnlyDictionary<string, object?> extensions)
    {
        ImpinjInventoryReportOptions? options =
            extensions.TryGetValue(ImpinjInventoryReportOptions.ExtensionKey, out object? value) &&
            value is ImpinjInventoryReportOptions reportOptions
                ? reportOptions
                : null;
        EnableFastId = options?.IncludeSerializedTid ?? false;
        ReportPhaseAngle = options?.IncludeRfPhaseAngle ?? false;
        ReportDopplerFrequency = options?.IncludeRfDopplerFrequency ?? false;
    }

    private void ApplyReaderConfigurationToUi(IReadOnlyDictionary<string, object?> extensions)
    {
        // The Impinj reader configuration is only real when the reader actually reported the
        // impinj.configuration extension; otherwise the Search Mode / frequency / low duty cycle /
        // GPI debounce controls would show stale defaults, so they are disabled instead.
        bool available =
            extensions.TryGetValue(ImpinjReaderConfiguration.ExtensionKey, out object? value) &&
            value is ImpinjReaderConfiguration;
        ImpinjExtensionsAvailable = available;
        foreach (GpiSettingsRow row in GpiSettings)
        {
            row.IsDebounceEnabled = available;
        }

        if (!available || value is not ImpinjReaderConfiguration configuration)
        {
            return;
        }

        foreach (ImpinjGpiDebounceSetting debounce in configuration.GpiDebounce)
        {
            GpiSettingsRow? row = GpiSettings.FirstOrDefault(candidate => candidate.Port == debounce.GpiPortNumber);
            if (row is not null)
            {
                row.DebounceMs = debounce.DebounceMilliseconds.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static string FormatSearchMode(ImpinjInventorySearchType? mode) => mode switch
    {
        ImpinjInventorySearchType.Single_Target => "Single Target",
        ImpinjInventorySearchType.Dual_Target => "Dual Target",
        ImpinjInventorySearchType.Single_Target_With_Suppression => "TagFocus",
        ImpinjInventorySearchType.No_Target => "No Target",
        ImpinjInventorySearchType.Single_Target_BtoA => "Single Target Reset",
        ImpinjInventorySearchType.Dual_Target_with_BtoASelect => "Dual Target Select B to A",
        _ => "Reader Selected",
    };

    private void ApplyFrequencySelection(IReadOnlyList<ushort>? channelList)
    {
        foreach (FrequencyChannelRow row in FrequencyChannelOptions)
        {
            row.IsSelected = channelList?.Contains(row.ChannelIndex) == true;
        }
    }

    partial void OnUseSpecifiedFrequenciesChanged(string value) => UpdateFrequencyChannelsEnabled();

    partial void OnImpinjExtensionsAvailableChanged(bool value) => UpdateFrequencyChannelsEnabled();

    private void UpdateFrequencyChannelsEnabled() =>
        IsFrequencyChannelsEnabled = ImpinjExtensionsAvailable &&
            UseSpecifiedFrequencies.Equals("Channel List", StringComparison.OrdinalIgnoreCase);

    private string FormatTxPowerIndex(ushort index) =>
        txPowerDisplaysByIndex.TryGetValue(index, out string? display)
            ? display
            : index.ToString(CultureInfo.InvariantCulture);

    private string FormatRxSensitivityIndex(ushort index) =>
        rxSensitivityDisplaysByIndex.TryGetValue(index, out string? display)
            ? display
            : index.ToString(CultureInfo.InvariantCulture);

    [RelayCommand]
    private void FillAllAntennas()
    {
        if (maxAntennas == 0)
        {
            return;
        }

        Antennas = string.Join(", ", Enumerable.Range(1, maxAntennas)
            .Select(static value => value.ToString(CultureInfo.InvariantCulture)));
    }

    [RelayCommand]
    private void ClearAntennas() => Antennas = string.Empty;

    private static ushort[] ParseAntennaIds(string value)
    {
        ushort[] ids = value
            .Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ParseUShort(part, "Antennas"))
            .ToArray();

        if (ids.Length == 0)
        {
            throw new InvalidOperationException("Antennas must not be empty; use ALL to select every antenna.");
        }

        return ids;
    }

    private static byte ParseSession(string value)
    {
        string normalized = value.Replace("Session", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        byte session = byte.Parse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (session > 3)
        {
            throw new InvalidOperationException("Session must be 0, 1, 2, or 3.");
        }

        return session;
    }

    private static ushort ParseModeIndex(string value)
    {
        string firstToken = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
        string digits = new string(firstToken.TakeWhile(char.IsDigit).ToArray());
        return ushort.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort result)
            ? result
            : (ushort)0;
    }

    private static ushort ParseUShort(string value, string fieldName)
    {
        if (!ushort.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort result))
        {
            throw new InvalidOperationException($"{fieldName} must be a number from 0 to 65535.");
        }

        return result;
    }

    private ushort? TryParseNullableTxPowerIndex(string value)
    {
        string trimmed = value.Trim();
        if (txPowerIndexesByDisplay.TryGetValue(trimmed, out ushort index))
        {
            return index;
        }

        // The UI edits dBm values, never raw table indexes; an unmatched free-form value is resolved
        // against the capability table by nearest dBm instead of being sent as an index.
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbm))
        {
            TxPowerEntry? nearest = txPowerEntries
                .OrderBy(entry => Math.Abs(entry.TransmitPowerDbm - dbm))
                .FirstOrDefault();
            if (nearest is not null)
            {
                return nearest.Index;
            }
        }

        return null;
    }

    private ushort? TryParseNullableRxSensitivityIndex(string value)
    {
        string trimmed = value.Trim();
        if (rxSensitivityIndexesByDisplay.TryGetValue(trimmed, out ushort index))
        {
            return index;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedValue))
        {
            RxSensitivityEntry? nearest = rxSensitivityEntries
                .OrderBy(entry => Math.Abs(entry.ReceiveSensitivityValue - parsedValue))
                .FirstOrDefault();
            if (nearest is not null)
            {
                return nearest.Index;
            }
        }

        return null;
    }
}

public sealed partial class AntennaSettingsRow : ObservableObject
{
    [ObservableProperty]
    private string txPower = "30";

    [ObservableProperty]
    private string rxSensitivity = "0";

    public AntennaSettingsRow(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public sealed partial class FrequencyChannelRow : ObservableObject
{
    public ushort ChannelIndex { get; }
    public uint FrequencyKHz { get; }
    public string FrequencyDisplay { get; }

    [ObservableProperty]
    private bool isSelected;

    public FrequencyChannelRow(ushort channelIndex, uint frequencyKHz)
    {
        ChannelIndex = channelIndex;
        FrequencyKHz = frequencyKHz;
        FrequencyDisplay = (frequencyKHz / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed partial class GpiSettingsRow : ObservableObject
{
    [ObservableProperty]
    private bool startEnabled;

    [ObservableProperty]
    private string startLevel = "Low";

    [ObservableProperty]
    private bool stopEnabled;

    [ObservableProperty]
    private string stopLevel = "Low";

    [ObservableProperty]
    private string debounceMs = "20";

    [ObservableProperty]
    private bool isDebounceEnabled = true;

    public GpiSettingsRow(int port)
    {
        Port = port;
    }

    public int Port { get; }
}
