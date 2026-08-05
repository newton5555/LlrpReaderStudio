using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderStudio.Core;
using LlrpSdk;

namespace LlrpReaderStudio.ViewModels;

public partial class InventoryViewModel : PageViewModelBase
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch stopwatch = new();
    private long lastTotalReads;
    private DateTimeOffset lastRateCheckTime = DateTimeOffset.UtcNow;

    [ObservableProperty]
    private bool isInventoryRunning;

    [ObservableProperty]
    private string elapsedTimeText = "00:00:00.000";

    [ObservableProperty]
    private double currentReadRate;

    [ObservableProperty]
    private string readRateText = "0.000 reads/s";

    [ObservableProperty]
    private bool showIndexColumn = true;

    [ObservableProperty]
    private bool showEpcColumn = true;

    [ObservableProperty]
    private bool showTidColumn;

    [ObservableProperty]
    private bool showCountColumn = true;

    [ObservableProperty]
    private bool showFirstSeenColumn = true;

    [ObservableProperty]
    private bool showLastSeenColumn = true;

    [ObservableProperty]
    private bool showReaderColumn = true;

    [ObservableProperty]
    private bool showAntennaColumn = true;

    [ObservableProperty]
    private bool showPeakRssiColumn = true;

    [ObservableProperty]
    private bool showChannelColumn = true;

    public InventoryViewModel()
    {
        PageTitle = "寻卡 / Inventory";
        timer.Tick += OnTimerTick;
    }

    public ObservableCollection<TagRowViewModel> Tags { get; } = [];

    public int UniqueTagCount => Tags.Count;

    public event Action? ToggleInventoryRequested;

    public event Action? ClearTagsRequested;

    public InventorySettings ApplyReportOptions(InventorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings with
        {
            Report = settings.Report with
            {
                IncludeAntennaId = ShowAntennaColumn,
                IncludeChannelIndex = ShowChannelColumn,
                IncludePeakRssi = ShowPeakRssiColumn,
                IncludeFirstSeenTimestamp = ShowFirstSeenColumn,
                IncludeLastSeenTimestamp = ShowLastSeenColumn,
                IncludeTagSeenCount = ShowCountColumn,
            },
        };
    }

    [RelayCommand]
    private void ToggleInventory()
    {
        ToggleInventoryRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearTags()
    {
        Tags.Clear();
        if (IsInventoryRunning)
        {
            // Restart keeps the stopwatch running so the elapsed timer keeps counting from zero.
            stopwatch.Restart();
        }
        else
        {
            stopwatch.Reset();
        }

        ElapsedTimeText = "00:00:00.000";
        CurrentReadRate = 0;
        lastTotalReads = 0;
        lastRateCheckTime = DateTimeOffset.UtcNow;
        ReadRateText = "0.000 reads/s";
        OnPropertyChanged(nameof(UniqueTagCount));
        ClearTagsRequested?.Invoke();
    }

    public void OnTagObserved(TagObservation aggregate)
    {
        TagRowViewModel? existing = Tags.FirstOrDefault(tag =>
            string.Equals(tag.Epc, aggregate.Epc, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var row = new TagRowViewModel(Tags.Count + 1, aggregate);
            Tags.Insert(0, row);
            ReindexRows();
        }
        else
        {
            existing.Update(aggregate);
            int index = Tags.IndexOf(existing);
            if (index > 0)
            {
                Tags.Move(index, 0);
                ReindexRows();
            }
        }
        OnPropertyChanged(nameof(UniqueTagCount));
    }

    public void StartTimer()
    {
        IsInventoryRunning = true;
        if (!stopwatch.IsRunning)
        {
            stopwatch.Start();
            timer.Start();
        }
    }

    public void StopTimer()
    {
        IsInventoryRunning = false;
        stopwatch.Stop();
        timer.Stop();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        TimeSpan elapsed = stopwatch.Elapsed;
        ElapsedTimeText = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}";

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double seconds = (now - lastRateCheckTime).TotalSeconds;
        if (seconds >= 0.5)
        {
            long currentReads = Tags.Sum(t => t.ReadCount);
            long diff = currentReads - lastTotalReads;
            CurrentReadRate = diff / seconds;
            ReadRateText = $"{CurrentReadRate:F3} reads/s";
            lastTotalReads = currentReads;
            lastRateCheckTime = now;
            OnPropertyChanged(nameof(UniqueTagCount));
        }

        foreach (TagRowViewModel tag in Tags)
        {
            tag.RefreshTimeSinceLastSeen();
        }
    }

    private void ReindexRows()
    {
        for (int i = 0; i < Tags.Count; i++)
        {
            Tags[i].Index = i + 1;
        }
    }
}
