using LlrpReaderStudio.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeroconf;

namespace LlrpReaderStudio.Infrastructure.Discovery;

public sealed record DiscoveredReader(
    string DisplayName,
    string Host,
    string IpAddress,
    int Port,
    IReadOnlyDictionary<string, string> Properties);

public interface IReaderDiscoveryService
{
    Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(TimeSpan scanDuration, CancellationToken cancellationToken = default);
}

public sealed class ZeroconfReaderDiscoveryService(ILogger<ZeroconfReaderDiscoveryService>? logger = null) : IReaderDiscoveryService
{
    public const string LlrpServiceType = "_llrp._tcp.local.";
    private readonly ILogger<ZeroconfReaderDiscoveryService> logger = logger ?? NullLogger<ZeroconfReaderDiscoveryService>.Instance;

    public async Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(TimeSpan scanDuration, CancellationToken cancellationToken = default)
    {
        var discoveredList = new List<DiscoveredReader>();

        try
        {
            logger.LogDebug("Starting Zeroconf reader discovery scan for service '{ServiceType}' (duration: {Duration}s)...", LlrpServiceType, scanDuration.TotalSeconds);

            IReadOnlyList<IZeroconfHost> results = await ZeroconfResolver.ResolveAsync(
                LlrpServiceType,
                scanTime: scanDuration,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (IZeroconfHost result in results)
            {
                // Find service matching _llrp._tcp regardless of trailing dots or casing
                KeyValuePair<string, IService> serviceKvp = result.Services
                    .FirstOrDefault(s => s.Key.Contains("_llrp._tcp", StringComparison.OrdinalIgnoreCase));

                IService? service = serviceKvp.Value;
                int port = service?.Port ?? 5084;

                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (service?.Properties != null)
                {
                    foreach (IDictionary<string, string> dict in service.Properties)
                    {
                        foreach (KeyValuePair<string, string> kvp in dict)
                        {
                            properties[kvp.Key] = kvp.Value;
                        }
                    }
                }

                // DisplayName in Zeroconf often contains the mDNS hostname (e.g. impinj-89-ab-cd.local)
                string ip = result.IPAddress ?? string.Empty;
                string displayName = !string.IsNullOrWhiteSpace(result.DisplayName) ? result.DisplayName : (!string.IsNullOrWhiteSpace(ip) ? ip : "Unknown Reader");
                string host = !string.IsNullOrWhiteSpace(result.DisplayName) ? result.DisplayName : (!string.IsNullOrWhiteSpace(ip) ? ip : "localhost");
                string ipAddress = !string.IsNullOrWhiteSpace(ip) ? ip : host;

                logger.LogInformation("Discovered LLRP reader: {DisplayName} ({IpAddress}:{Port})", displayName, ipAddress, port);

                discoveredList.Add(new DiscoveredReader(
                    DisplayName: displayName,
                    Host: host,
                    IpAddress: ipAddress,
                    Port: port > 0 ? port : 5084,
                    Properties: properties));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Zeroconf reader discovery encountered an exception");
        }

        return discoveredList.AsReadOnly();
    }
}
