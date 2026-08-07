using CommunityToolkit.Mvvm.ComponentModel;
using LlrpReaderStudio.Core;

namespace LlrpReaderStudio.ViewModels;

public partial class TagRowViewModel : ObservableObject
{
    [ObservableProperty]
    private int index;

    [ObservableProperty]
    private long readCount;

    [ObservableProperty]
    private DateTimeOffset lastSeen;

    [ObservableProperty]
    private sbyte? lastRssi;

    [ObservableProperty]
    private ushort? lastChannelIndex;

    [ObservableProperty]
    private string tid = "--";

    [ObservableProperty]
    private string pcBits = "--";

    [ObservableProperty]
    private string readers = string.Empty;

    [ObservableProperty]
    private string antennas = string.Empty;

    [ObservableProperty]
    private string timeSinceLastSeen = "0.0s";

    public TagRowViewModel(int index, TagObservation observation)
    {
        this.index = index;
        Epc = observation.Epc;
        FirstSeen = observation.FirstSeen;
        XpcWords = "--";
        Update(observation);
    }

    public string Epc { get; }
    public string XpcWords { get; }
    public DateTimeOffset FirstSeen { get; }
    public long SeenCount => ReadCount;
    public string ReaderName => Readers;
    public string AntennaId => Antennas;
    public string FirstSeenTimeText => FirstSeen.ToLocalTime().ToString("HH:mm:ss.fff");
    public string LastSeenTimeText => LastSeen.ToLocalTime().ToString("HH:mm:ss.fff");
    public string PeakRssiText => LastRssi is null ? "--" : LastRssi.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ChannelIndexText => LastChannelIndex is null ? "--" : LastChannelIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string PcBitsText => PcBits;

    public void Update(TagObservation observation)
    {
        ReadCount = observation.ReadCount;
        LastSeen = observation.LastSeen;
        LastRssi = observation.LastRssi;
        LastChannelIndex = observation.LastChannelIndex;
        Tid = string.IsNullOrWhiteSpace(observation.Tid) ? Tid : observation.Tid;
        if (!string.IsNullOrWhiteSpace(observation.PcBitsHex))
        {
            PcBits = observation.PcBitsHex;
        }
        Readers = string.Join(", ", observation.Readers);
        Antennas = string.Join(", ", observation.Antennas);
        OnPropertyChanged(nameof(SeenCount));
        OnPropertyChanged(nameof(ReaderName));
        OnPropertyChanged(nameof(AntennaId));
        OnPropertyChanged(nameof(LastSeenTimeText));
        OnPropertyChanged(nameof(PeakRssiText));
        OnPropertyChanged(nameof(ChannelIndexText));
        OnPropertyChanged(nameof(PcBitsText));
        RefreshTimeSinceLastSeen();
    }

    public void RefreshTimeSinceLastSeen()
    {
        TimeSpan span = DateTimeOffset.UtcNow - LastSeen;
        TimeSinceLastSeen = $"{span.TotalSeconds:F1}s";
    }
}
