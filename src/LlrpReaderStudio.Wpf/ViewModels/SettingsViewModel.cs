using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpSdk;

namespace LlrpReaderStudio.ViewModels;

public partial class SettingsViewModel : PageViewModelBase
{
    private readonly ReaderFleetService fleet;
    private ReaderSettings? settingsDraft = new();

    [ObservableProperty]
    private string settingsOrigin = "No reader settings loaded";

    [ObservableProperty]
    private bool includeInventoryDraft;

    [ObservableProperty]
    private string antennas = "0";

    [ObservableProperty]
    private string session = "0";

    [ObservableProperty]
    private string population = "32";

    [ObservableProperty]
    private string reportEvery = "1";

    [ObservableProperty]
    private string statusMessage = "Query, configure or load SDK default settings.";

    public SettingsViewModel(ReaderFleetService fleet)
    {
        this.fleet = fleet;
        PageTitle = "Settings Workspace";
    }

    public Guid? SelectedReaderId { get; set; }
    public string? SelectedReaderName { get; set; }

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
            ReaderSettingsSnapshot snapshot = await fleet.QuerySettingsAsync(readerId, CancellationToken.None);
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
            ReaderSettingsDefaults defaults = await fleet.GetDefaultSettingsAsync(readerId, CancellationToken.None);
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
            await fleet.ApplySettingsAsync(readerId, settingsDraft, CancellationToken.None);
            SettingsOrigin = $"Applied to {SelectedReaderName} at {DateTime.Now:HH:mm:ss}";
            StatusMessage = $"{SelectedReaderName}: Applied settings draft.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply settings failed: {ex.Message}";
        }
    }
}
