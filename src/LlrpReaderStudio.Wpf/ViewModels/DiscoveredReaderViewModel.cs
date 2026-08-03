namespace LlrpReaderStudio.ViewModels;

public sealed record DiscoveredReaderViewModel(
    string DisplayName,
    string Host,
    string IpAddress,
    int Port,
    string ModelName)
{
    public string DisplayEndpoint => string.Equals(Host, IpAddress, StringComparison.OrdinalIgnoreCase)
        ? $"{IpAddress}:{Port}"
        : $"{Host} ({IpAddress}:{Port})";
}
