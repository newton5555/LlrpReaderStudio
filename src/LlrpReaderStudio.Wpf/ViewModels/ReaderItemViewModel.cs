using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;

namespace LlrpReaderStudio.ViewModels;

public enum ReaderAvailability
{
    Unknown,
    Checking,
    Reachable,
    Unreachable,
    Inventorying,
}

public partial class ReaderItemViewModel : ObservableObject
{
    private readonly Action<ReaderItemViewModel>? onDeleteRequested;

    [ObservableProperty]
    private bool isEnabled = true;

    [ObservableProperty]
    private StudioReaderState state;

    [ObservableProperty]
    private ReaderAvailability availability = ReaderAvailability.Unknown;

    [ObservableProperty]
    private string details = string.Empty;

    [ObservableProperty]
    private string model = string.Empty;

    [ObservableProperty]
    private string firmware = string.Empty;

    [ObservableProperty]
    private string lastError = string.Empty;

    [ObservableProperty]
    private string lastCheckedText = string.Empty;

    public ReaderItemViewModel(ReaderStatus status, bool isEnabled = true, Action<ReaderItemViewModel>? onDeleteRequested = null)
    {
        Id = status.Profile.Id;
        Name = status.Profile.Name;
        Host = status.Profile.Host;
        Port = status.Profile.Port;
        Endpoint = $"{Host}:{Port}";
        this.isEnabled = isEnabled;
        this.onDeleteRequested = onDeleteRequested;
        Update(status);
    }

    /// <summary>Shows the last known connectivity outcome from persistence (no connection made).</summary>
    public void SetLastKnownState(DateTime? checkedAtUtc, string? model, string? firmware, string? error)
    {
        if (checkedAtUtc is { } utc)
        {
            LastCheckedText = utc.ToLocalTime().ToString("HH:mm:ss");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            Model = model;
        }

        if (!string.IsNullOrWhiteSpace(firmware))
        {
            Firmware = firmware;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            LastError = error;
            if (Availability == ReaderAvailability.Unknown)
            {
                Availability = ReaderAvailability.Unreachable;
            }
        }
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
        Model = status.Model ?? string.Empty;
        Firmware = status.Firmware ?? string.Empty;
        LastError = status.Error ?? string.Empty;
        LastCheckedText = DateTime.Now.ToString("HH:mm:ss");

        Availability = status.State switch
        {
            StudioReaderState.Connecting or StudioReaderState.Disconnecting => ReaderAvailability.Checking,
            StudioReaderState.Inventorying or StudioReaderState.Stopping => ReaderAvailability.Inventorying,
            StudioReaderState.Faulted => ReaderAvailability.Unreachable,
            StudioReaderState.Connected => ReaderAvailability.Reachable,
            _ => ReaderAvailability.Unknown,
        };
    }

    [RelayCommand]
    private void Delete()
    {
        onDeleteRequested?.Invoke(this);
    }
}
