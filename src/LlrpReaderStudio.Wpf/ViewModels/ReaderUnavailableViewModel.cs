using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LlrpReaderStudio.ViewModels;

/// <summary>
/// Placeholder page shown when a reader's configuration could not be synced to the local cache
/// (startup connect/query failed). The settings page is not shown for such a reader; the user can
/// retry, which re-runs the connect + query + cache-sync flow before switching back to settings.
/// </summary>
public partial class ReaderUnavailableViewModel : PageViewModelBase
{
    [ObservableProperty]
    private string readerName = string.Empty;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    public ReaderUnavailableViewModel()
    {
        PageTitle = "Reader Unavailable";
    }

    public event Action? RetryRequested;

    public void Show(string readerName, string errorMessage)
    {
        ReaderName = readerName;
        ErrorMessage = errorMessage;
    }

    [RelayCommand]
    private void Retry()
    {
        RetryRequested?.Invoke();
    }
}
