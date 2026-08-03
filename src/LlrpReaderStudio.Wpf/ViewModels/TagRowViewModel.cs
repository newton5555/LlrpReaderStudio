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
        Tid = "--";
        XpcWords = "--";
        Update(observation);
    }

    public string Epc { get; }
    public string Tid { get; }
    public string XpcWords { get; }
    public DateTimeOffset FirstSeen { get; }

    public void Update(TagObservation observation)
    {
        ReadCount = observation.ReadCount;
        LastSeen = observation.LastSeen;
        LastRssi = observation.LastRssi;
        Readers = string.Join(", ", observation.Readers);
        Antennas = string.Join(", ", observation.Antennas);
        RefreshTimeSinceLastSeen();
    }

    public void RefreshTimeSinceLastSeen()
    {
        TimeSpan span = DateTimeOffset.UtcNow - LastSeen;
        TimeSinceLastSeen = $"{span.TotalSeconds:F1}s";
    }
}
