using LlrpSdk;
using LlrpSdk.Extensions.Impinj;

namespace LlrpReaderStudio.Core;

public sealed record TagObservation(
    string Epc,
    string Tid,
    ushort? PcBits,
    string? PcBitsHex,
    long ReadCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    sbyte? LastRssi,
    ushort? LastChannelIndex,
    IReadOnlyList<string> Readers,
    IReadOnlyList<ushort> Antennas);

public sealed class TagAggregateStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, MutableObservation> observations = new(StringComparer.OrdinalIgnoreCase);

    public TagObservation Add(ReaderProfile profile, TagReport report, DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(report);
        string epc = HexCodec.FormatBytes(report.ElectronicProductCode);
        string tid = FormatAttachedReadData(report);
        DateTimeOffset timestamp = observedAt ?? DateTimeOffset.Now;

        lock (gate)
        {
            if (!observations.TryGetValue(epc, out MutableObservation? observation))
            {
                observation = new MutableObservation(epc, timestamp);
                observations.Add(epc, observation);
            }

            observation.ReadCount += Math.Max(1, (int)(report.SeenCount ?? 1));
            if (!string.IsNullOrWhiteSpace(tid))
            {
                observation.Tid = tid;
            }
            if (report.PcBits is ushort pcBits)
            {
                observation.PcBits = pcBits;
                observation.PcBitsHex = pcBits.ToString("X4");
            }
            observation.LastSeen = timestamp;
            observation.LastRssi = report.PeakRssi;
            observation.LastChannelIndex = report.ChannelIndex;
            observation.Readers.Add(profile.Name);
            if (report.AntennaId is ushort antenna)
            {
                observation.Antennas.Add(antenna);
            }

            return observation.Snapshot();
        }
    }

    private static string FormatAttachedReadData(TagReport report) =>
        report.GetSerializedTidHex() ?? string.Empty;

    public IReadOnlyList<TagObservation> Snapshot()
    {
        lock (gate)
        {
            return observations.Values
                .Select(static observation => observation.Snapshot())
                .OrderByDescending(static observation => observation.LastSeen)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            observations.Clear();
        }
    }

    private sealed class MutableObservation(string epc, DateTimeOffset firstSeen)
    {
        public string Epc { get; } = epc;
        public string Tid { get; set; } = string.Empty;
        public ushort? PcBits { get; set; }
        public string? PcBitsHex { get; set; }
        public long ReadCount { get; set; }
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public DateTimeOffset LastSeen { get; set; } = firstSeen;
        public sbyte? LastRssi { get; set; }
        public ushort? LastChannelIndex { get; set; }
        public HashSet<string> Readers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<ushort> Antennas { get; } = [];

        public TagObservation Snapshot() => new(
            Epc,
            Tid,
            PcBits,
            PcBitsHex,
            ReadCount,
            FirstSeen,
            LastSeen,
            LastRssi,
            LastChannelIndex,
            Readers.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            Antennas.OrderBy(static value => value).ToArray());
    }
}
