using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Data;
using LlrpSdk;
using LlrpSdk.Extensions.Impinj;

namespace LlrpReaderStudio.ViewModels;

public partial class DataSourceSettingsViewModel : PageViewModelBase
{
    private readonly ReaderFleetService fleet;
    private readonly InventoryPresetRepository inventoryPresets;
    private ReaderSettings? settingsDraft = new();
    private bool suppressGpoUpdate;
    private readonly Dictionary<string, ushort> txPowerIndexesByDisplay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ushort> rxSensitivityIndexesByDisplay = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, string> txPowerDisplaysByIndex = [];
    private readonly Dictionary<ushort, string> rxSensitivityDisplaysByIndex = [];

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
    private string rfMode = "Auto Set Dense Reader Deep Scan";

    [ObservableProperty]
    private string searchMode = "Dual Target";

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
    private string rxSensitivity = "-80";

    [ObservableProperty]
    private bool enableMaxSensitivity = true;

    [ObservableProperty]
    private string filterMode = "None";

    [ObservableProperty]
    private string filterVerification = "Reader Default";

    [ObservableProperty]
    private string filter1 = string.Empty;

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
    private string filter2MemoryBank = "EPC";

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

    public async Task InitializeForReaderAsync(ReaderItemViewModel reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        SetSelectedReader(reader);

        try
        {
            StatusMessage = $"{reader.Name}: Connecting and loading capabilities...";
            await EnsureConnectedAndLoadCapabilitiesAsync(reader.Id, cancellationToken);

            ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(reader.Id, cancellationToken);
            InventorySettings? inventory = snapshot.Inventory?.Settings ?? snapshot.Settings.Inventory;
            if (inventory is not null)
            {
                settingsDraft = snapshot.Settings with { Inventory = inventory };
                ApplyInventoryToUi(inventory);
                SettingsOrigin = $"Loaded from reader at {DateTime.Now:HH:mm:ss}";
                StatusMessage = $"{reader.Name}: Loaded current reader settings.";
                return;
            }

            InventorySettings? saved = await inventoryPresets.LoadDefaultAsync(reader.Id, cancellationToken);
            if (saved is not null)
            {
                settingsDraft = new ReaderSettings { Inventory = saved };
                ApplyInventoryToUi(saved);
                SettingsOrigin = "Loaded from local history";
                StatusMessage = $"{reader.Name}: No reader inventory was found; loaded local history.";
                return;
            }

            ReaderSettingsDefaults defaults = await fleet.GetDefaultSettingsAsync(reader.Id, cancellationToken);
            settingsDraft = defaults.Settings;
            if (defaults.Settings.Inventory is not null)
            {
                ApplyInventoryToUi(defaults.Settings.Inventory);
            }

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
            if ((snapshot.Inventory?.Settings ?? snapshot.Settings.Inventory) is InventorySettings inventory)
            {
                ApplyInventoryToUi(inventory);
            }
            SettingsOrigin = $"Queried from {SelectedReaderName} at {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"{SelectedReaderName}: Settings queried successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Query failed: {ex.Message}";
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
            if (defaults.Settings.Inventory is InventorySettings inventory)
            {
                ApplyInventoryToUi(inventory);
            }
            SettingsOrigin = $"SDK Defaults for {SelectedReaderName}";
            StatusMessage = $"{SelectedReaderName}: SDK default settings loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load defaults failed: {ex.Message}";
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
            ReaderSettings settings = BuildSettingsFromUi();
            await fleet.ApplySettingsAsync(readerId, settings, CancellationToken.None);
            if (settings.Inventory is not null)
            {
                await inventoryPresets.SaveDefaultAsync(readerId, settings.Inventory, CancellationToken.None);
            }
            settingsDraft = settings;
            SettingsOrigin = $"Saved to reader at {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"{SelectedReaderName}: Settings saved to reader and local history.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply settings failed: {ex.Message}";
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

    private ReaderSettings BuildSettingsFromUi()
    {
        InventorySettings baseInventory = settingsDraft?.Inventory ?? new InventorySettings();
        ushort[] antennaIds = ParseAntennaIds(Antennas);
        byte session = ParseSession(Session);
        ushort population = ParseUShort(Population, nameof(Population));
        ushort reportEvery = ParseUShort(ReportEvery, nameof(ReportEvery));
        ushort modeIndex = ParseModeIndex(RfMode);

        List<InventoryAntennaConfiguration> antennaConfigurations = BuildAntennaConfigurations(antennaIds);

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
            Extensions = extensions,
        };

        return (settingsDraft ?? new ReaderSettings()) with { Inventory = inventory };
    }

    private IReadOnlyDictionary<string, object?> BuildInventoryExtensions(IReadOnlyDictionary<string, object?> source)
    {
        var extensions = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        if (EnableFastId)
        {
            extensions[ImpinjInventoryReportOptions.ExtensionKey] =
                extensions.TryGetValue(ImpinjInventoryReportOptions.ExtensionKey, out object? value) &&
                value is ImpinjInventoryReportOptions existing
                    ? existing with { IncludeSerializedTid = true }
                    : new ImpinjInventoryReportOptions { IncludeSerializedTid = true };
        }
        else
        {
            extensions.Remove(ImpinjInventoryReportOptions.ExtensionKey);
        }

        return extensions.Count == 0
            ? new InventorySettings().Extensions
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(extensions);
    }

    private List<InventoryAntennaConfiguration> BuildAntennaConfigurations(ushort[] antennaIds)
    {
        bool selectsAllAntennas = antennaIds.Length == 1 && antennaIds[0] == 0;
        HashSet<ushort> selected = antennaIds.ToHashSet();
        List<InventoryAntennaConfiguration> antennaConfigurations = [];

        if (!UseIndividualAntennaSettings)
        {
            ushort? txPower = TryParseNullableTxPowerIndex(PowerDbm);
            ushort? rxSensitivity = TryParseNullableRxSensitivityIndex(RxSensitivity);
            if (txPower is null && rxSensitivity is null)
            {
                return antennaConfigurations;
            }

            IEnumerable<ushort> targets = selectsAllAntennas
                ? new[] { (ushort)0 }
                : antennaIds;

            foreach (ushort antennaId in targets)
            {
                antennaConfigurations.Add(CreateAntennaConfiguration(antennaId, txPower, rxSensitivity));
            }

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
            ushort? rxSensitivity = TryParseNullableRxSensitivityIndex(row.RxSensitivity);
            if (txPower is null && rxSensitivity is null)
            {
                continue;
            }

            antennaConfigurations.Add(CreateAntennaConfiguration(antennaId, txPower, rxSensitivity));
        }

        return antennaConfigurations;
    }

    private static InventoryAntennaConfiguration CreateAntennaConfiguration(ushort antennaId, ushort? txPower, ushort? rxSensitivity)
    {
        return new InventoryAntennaConfiguration
        {
            AntennaId = antennaId,
            ReceiverSensitivityIndex = rxSensitivity,
            TransmitPowerIndex = txPower,
            HopTableId = txPower is null ? null : (ushort)1,
            ChannelIndex = txPower is null ? null : (ushort)1,
        };
    }

    private async Task EnsureConnectedAndLoadCapabilitiesAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        await fleet.ConnectAsync(readerId, cancellationToken);
        ReaderCapabilities? capabilities = fleet.GetCapabilities(readerId);
        if (capabilities is null)
        {
            return;
        }

        RefreshOptions(capabilities);
    }

    private void RefreshOptions(ReaderCapabilities capabilities)
    {
        txPowerIndexesByDisplay.Clear();
        txPowerDisplaysByIndex.Clear();
        string[] txPowerOptions = capabilities.TxPowers
            .OrderBy(static value => value.TransmitPowerValue)
            .Select(FormatTxPowerOption)
            .ToArray();
        ReplaceOptions(TxPowerOptions, txPowerOptions);

        rxSensitivityIndexesByDisplay.Clear();
        rxSensitivityDisplaysByIndex.Clear();
        string[] rxSensitivityOptions = capabilities.RxSensitivities
            .OrderBy(static value => value.ReceiveSensitivityValue)
            .Select(FormatRxSensitivityOption)
            .ToArray();
        ReplaceOptions(RxSensitivityOptions, rxSensitivityOptions);

        string[] rfModeOptions = capabilities.RfModes
            .Select(static value => value.ModeIdentifier.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        ReplaceOptions(RfModeOptions, rfModeOptions);

        ushort maxAntennas = capabilities.MaxNumberOfAntennas == 0 ? (ushort)4 : capabilities.MaxNumberOfAntennas;
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
        string display = FormatDbm(entry.ReceiveSensitivityDbm);
        rxSensitivityIndexesByDisplay[display] = entry.Index;
        rxSensitivityDisplaysByIndex[entry.Index] = display;
        return display;
    }

    private static string FormatDbm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private void ApplyInventoryToUi(InventorySettings inventory)
    {
        Antennas = string.Join(", ", inventory.AntennaIds);
        Session = $"Session {inventory.Session}";
        Population = inventory.TagPopulationEstimate.ToString(CultureInfo.InvariantCulture);
        ReportEvery = inventory.ReportEveryNTags.ToString(CultureInfo.InvariantCulture);
        RfMode = inventory.ModeIndex == 0
            ? RfMode
            : inventory.ModeIndex.ToString(CultureInfo.InvariantCulture);

        InventoryAntennaConfiguration? global = inventory.AntennaConfigurations.FirstOrDefault(static value => value.AntennaId == 0);
        if (global is not null)
        {
            UseIndividualAntennaSettings = false;
            if (global.TransmitPowerIndex is ushort txPower)
            {
                PowerDbm = FormatTxPowerIndex(txPower);
            }

            if (global.ReceiverSensitivityIndex is ushort rxSensitivity)
            {
                RxSensitivity = FormatRxSensitivityIndex(rxSensitivity);
            }
        }
        else if (inventory.AntennaConfigurations.Count > 0)
        {
            UseIndividualAntennaSettings = true;
        }

        foreach (InventoryAntennaConfiguration configuration in inventory.AntennaConfigurations.Where(static value => value.AntennaId > 0))
        {
            int index = configuration.AntennaId - 1;
            if (index < 0 || index >= AntennaSettings.Count)
            {
                continue;
            }

            AntennaSettingsRow row = AntennaSettings[index];
            if (configuration.TransmitPowerIndex is ushort txPower)
            {
                row.TxPower = FormatTxPowerIndex(txPower);
            }

            if (configuration.ReceiverSensitivityIndex is ushort rxSensitivity)
            {
                row.RxSensitivity = FormatRxSensitivityIndex(rxSensitivity);
            }
        }
    }

    private string FormatTxPowerIndex(ushort index) =>
        txPowerDisplaysByIndex.TryGetValue(index, out string? display)
            ? display
            : index.ToString(CultureInfo.InvariantCulture);

    private string FormatRxSensitivityIndex(ushort index) =>
        rxSensitivityDisplaysByIndex.TryGetValue(index, out string? display)
            ? display
            : index.ToString(CultureInfo.InvariantCulture);

    private static ushort[] ParseAntennaIds(string value)
    {
        ushort[] ids = value
            .Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ParseUShort(part, "Antennas"))
            .ToArray();

        return ids.Length == 0 ? [0] : ids;
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
        return ushort.TryParse(firstToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort result)
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

        return ushort.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort result)
            ? result
            : null;
    }

    private ushort? TryParseNullableRxSensitivityIndex(string value)
    {
        string trimmed = value.Trim();
        if (rxSensitivityIndexesByDisplay.TryGetValue(trimmed, out ushort index))
        {
            return index;
        }

        return ushort.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort result)
            ? result
            : null;
    }
}

public sealed partial class AntennaSettingsRow : ObservableObject
{
    [ObservableProperty]
    private string txPower = "30";

    [ObservableProperty]
    private string rxSensitivity = "-80";

    public AntennaSettingsRow(string name)
    {
        Name = name;
    }

    public string Name { get; }
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

    public GpiSettingsRow(int port)
    {
        Port = port;
    }

    public int Port { get; }
}
