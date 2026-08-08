using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpReaderStudio.Infrastructure.Discovery;

namespace LlrpReaderStudio.ViewModels;

public partial class AddDataSourceViewModel : PageViewModelBase
{
    private readonly IReaderDiscoveryService discoveryService;

    [ObservableProperty]
    private string hostName = string.Empty;

    [ObservableProperty]
    private string nickname = string.Empty;

    [ObservableProperty]
    private string portText = "5084";

    [ObservableProperty]
    private LlrpProtocolVersionOption llrpVersion = LlrpProtocolVersionOption.Auto;

    public IReadOnlyList<LlrpProtocolVersionOption> LlrpVersionValues { get; } =
        Enum.GetValues<LlrpProtocolVersionOption>();

    [ObservableProperty]
    private bool isAdvancedExpanded;

    [ObservableProperty]
    private bool isDiscovering;

    [ObservableProperty]
    private bool isDiscoveringPopupOpen;

    [ObservableProperty]
    private string statusMessage = "Provide a hostname or scan the network for LLRP readers.";

    public AddDataSourceViewModel(IReaderDiscoveryService discoveryService)
    {
        this.discoveryService = discoveryService;
        PageTitle = "Add Data Source";
    }

    public ObservableCollection<DiscoveredReaderViewModel> DiscoveredDevices { get; } = [];
    public event Func<string, string, int, LlrpProtocolVersionOption, Task>? DataSourceSubmitted;
    public event Action? CancelRequested;

    [RelayCommand]
    private async Task Submit()
    {
        string host = HostName.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusMessage = "Host Name cannot be empty.";
            return;
        }

        string name = string.IsNullOrWhiteSpace(Nickname) ? host : Nickname.Trim();
        int port = int.TryParse(PortText, out int parsedPort) ? parsedPort : 5084;

        HostName = string.Empty;
        Nickname = string.Empty;
        if (DataSourceSubmitted is { } handler)
        {
            await handler(host, name, port, LlrpVersion);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        HostName = string.Empty;
        Nickname = string.Empty;
        IsDiscoveringPopupOpen = false;
        CancelRequested?.Invoke();
    }

    [RelayCommand]
    private async Task DiscoverDevicesAsync()
    {
        IsDiscovering = true;
        IsDiscoveringPopupOpen = true;
        StatusMessage = "Scanning network for LLRP readers via mDNS (_llrp._tcp.local.)...";
        DiscoveredDevices.Clear();

        try
        {
            IReadOnlyList<DiscoveredReader> results = await discoveryService.DiscoverAsync(TimeSpan.FromSeconds(3));
            foreach (DiscoveredReader reader in results)
            {
                string modelName = "LLRP Reader";
                if (reader.DisplayName.StartsWith("impinj-", StringComparison.OrdinalIgnoreCase))
                {
                    modelName = "LLRP Reader";
                }
                else if (reader.DisplayName.StartsWith("speedwayr-", StringComparison.OrdinalIgnoreCase))
                {
                    modelName = "LLRP Reader";
                }

                DiscoveredDevices.Add(new DiscoveredReaderViewModel(
                    DisplayName: reader.DisplayName,
                    Host: reader.Host,
                    IpAddress: reader.IpAddress,
                    Port: reader.Port,
                    ModelName: modelName));
            }

            StatusMessage = DiscoveredDevices.Count == 0
                ? "No LLRP mDNS readers found. You can still enter a hostname or IP address manually."
                : $"Discovered {DiscoveredDevices.Count} LLRP reader(s) via mDNS.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"mDNS Discovery failed: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    private void SelectDiscoveredDevice(DiscoveredReaderViewModel? device)
    {
        if (device is not null)
        {
            HostName = device.Host;
            if (string.IsNullOrWhiteSpace(Nickname))
            {
                Nickname = device.DisplayName;
            }
            IsDiscoveringPopupOpen = false;
            StatusMessage = $"Selected discovered reader: {device.DisplayName} ({device.Host}).";
        }
    }

    [RelayCommand]
    private void ClosePopup()
    {
        IsDiscoveringPopupOpen = false;
    }
}
