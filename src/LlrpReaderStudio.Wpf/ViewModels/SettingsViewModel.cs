using CommunityToolkit.Mvvm.ComponentModel;

namespace LlrpReaderStudio.ViewModels;

public partial class SettingsViewModel : PageViewModelBase
{
    [ObservableProperty]
    private bool enableTagLogging;

    [ObservableProperty]
    private string tagLogDirectory = string.Empty;

    [ObservableProperty]
    private string statusMessage = "Reader-specific data source settings are shown when selecting a reader under DATA SOURCES.";

    public SettingsViewModel()
    {
        PageTitle = "Application Settings";
    }
}
