using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;

namespace LlrpReaderStudio.ViewModels;

public partial class ReaderItemViewModel : ObservableObject
{
    private readonly Action<ReaderItemViewModel>? onDeleteRequested;

    [ObservableProperty]
    private bool isEnabled = true;

    [ObservableProperty]
    private StudioReaderState state;

    [ObservableProperty]
    private string details = string.Empty;

    public ReaderItemViewModel(ReaderStatus status, Action<ReaderItemViewModel>? onDeleteRequested = null)
    {
        Id = status.Profile.Id;
        Name = status.Profile.Name;
        Host = status.Profile.Host;
        Port = status.Profile.Port;
        Endpoint = $"{Host}:{Port}";
        this.onDeleteRequested = onDeleteRequested;
        Update(status);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Host { get; }
    public int Port { get; }
    public string Endpoint { get; }

    public void Update(ReaderStatus status)
    {
        State = status.State;
        Details = status.Error ?? string.Join(" · ", new[] { status.Model, status.Firmware }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    [RelayCommand]
    private void Delete()
    {
        onDeleteRequested?.Invoke(this);
    }
}
