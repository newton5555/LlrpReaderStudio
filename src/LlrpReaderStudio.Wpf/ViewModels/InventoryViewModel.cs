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
    /// <summary>
    /// Upper bound for rows kept in the table. Newest rows win; older rows are trimmed from the
    /// tail so an unbounded list cannot grow forever under a sustained report stream.
    /// </summary>
    private const int MaxTagRows = 10_000;

    /// <summary>
    /// Maximum raw observations drained per timer tick. Under a report flood a single tick must
    /// not stall the UI thread, so the remainder stays queued for the next tick.
    /// </summary>
    private const int MaxDrainPerTick = 1_000;

    /// <summary>Only the newest rows are visible in a virtualized grid; refresh just those.</summary>
    private const int VisibleRowsForRefresh = 200;

    /// <summary>
    /// Max pending observations buffered between UI timer ticks. If a flood outpaces the drain,
    /// the oldest pending observation is dropped so memory stays bounded (mirrors the bounded,
    /// drop-oldest channel used upstream for the same reason).
    /// </summary>
    private const int PendingTagCap = 10_000;

    /// <summary>Refresh "time since last seen" on the visible rows every N ticks (50 ms * 5 = 250 ms).</summary>
    private const int TimeSinceRefreshEveryTicks = 5;

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch stopwatch = new();
    private readonly object pendingGate = new();
    private readonly Queue<TagObservation> pendingTags = [];
    private readonly Dictionary<string, TagRowViewModel> tagIndex = new(StringComparer.OrdinalIgnoreCase);
    private long lastTotalReads;
    private long totalReadCount;
    private int tickCounter;
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

    [ObservableProperty]
    private bool showPcBitsColumn;

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
                IncludePcBits = ShowPcBitsColumn,
            },
        };
    }

    [RelayCommand]
    private void ToggleInventory()
    {
        ToggleInventoryRequested?.Invoke();
    }

    /// <summary>
    /// Clears all table state and the upstream aggregate store. Called by the main shell at the
    /// start of each inventory run so a new Start always begins from an empty table.
    /// </summary>
    public void ResetTable()
    {
        lock (pendingGate)
        {
            pendingTags.Clear();
        }

        tagIndex.Clear();
        Tags.Clear();
        totalReadCount = 0;
        stopwatch.Reset();

        ElapsedTimeText = "00:00:00.000";
        CurrentReadRate = 0;
        lastTotalReads = 0;
        lastRateCheckTime = DateTimeOffset.UtcNow;
        ReadRateText = "0.000 reads/s";
        OnPropertyChanged(nameof(UniqueTagCount));
        ClearTagsRequested?.Invoke();
    }

    /// <summary>
    /// Called from the SDK/message-pump thread. Only enqueues the observation (O(1)); the UI timer
    /// drains the queue in batches so a high-frequency report stream cannot flood the dispatcher.
    /// </summary>
    public void EnqueueTag(TagObservation aggregate)
    {
        lock (pendingGate)
        {
            if (!IsInventoryRunning)
            {
                return;
            }

            // Keep the buffer bounded: if a flood outpaces the UI drain, drop the oldest instead of
            // letting the queue grow without limit.
            if (pendingTags.Count >= PendingTagCap)
            {
                pendingTags.Dequeue();
            }

            pendingTags.Enqueue(aggregate);
        }
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
        lock (pendingGate)
        {
            pendingTags.Clear();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        DrainPendingTags();

        TimeSpan elapsed = stopwatch.Elapsed;
        ElapsedTimeText = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}";

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double seconds = (now - lastRateCheckTime).TotalSeconds;
        if (seconds >= 0.5)
        {
            long currentReads = totalReadCount;
            long diff = currentReads - lastTotalReads;
            CurrentReadRate = diff / seconds;
            ReadRateText = $"{CurrentReadRate:F3} reads/s";
            lastTotalReads = currentReads;
            lastRateCheckTime = now;
            OnPropertyChanged(nameof(UniqueTagCount));
        }

        // Refresh "time since last seen" only for the newest (visible) rows, and less often than
        // every tick; a full-table pass at 50 ms is what overloaded the UI thread under floods.
        if (++tickCounter % TimeSinceRefreshEveryTicks == 0)
        {
            int count = Math.Min(Tags.Count, VisibleRowsForRefresh);
            for (int i = 0; i < count; i++)
            {
                Tags[i].RefreshTimeSinceLastSeen();
            }
        }
    }

    private void DrainPendingTags()
    {
        List<TagObservation> batch;
        lock (pendingGate)
        {
            if (pendingTags.Count == 0)
            {
                return;
            }

            int take = Math.Min(pendingTags.Count, MaxDrainPerTick);
            batch = new List<TagObservation>(take);
            for (int i = 0; i < take; i++)
            {
                batch.Add(pendingTags.Dequeue());
            }
        }

        // Collapse the batch by EPC (keep the last observation of each tag) so repeated reports of
        // the same tag within one tick update the row once instead of moving it to the top each time.
        var lastByEpc = new Dictionary<string, TagObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (TagObservation observation in batch)
        {
            lastByEpc[observation.Epc] = observation;
        }

        bool reindex = false;

        // Process oldest-first so that after every insert/move the newest observation ends up on
        // top; within one tick the top rows then match "most recently seen first".
        foreach (TagObservation aggregate in lastByEpc.Values.OrderBy(static observation => observation.LastSeen))
        {
            if (tagIndex.TryGetValue(aggregate.Epc, out TagRowViewModel? existing))
            {
                long previousReadCount = existing.ReadCount;
                existing.Update(aggregate);
                totalReadCount += aggregate.ReadCount - previousReadCount;

                // Fast path: the row is usually the most recently read one, which already sits at
                // the top; only fall back to a position search when it does not.
                if (!ReferenceEquals(Tags[0], existing))
                {
                    int index = Tags.IndexOf(existing);
                    if (index > 0)
                    {
                        Tags.Move(index, 0);
                        reindex = true;
                    }
                }
            }
            else
            {
                var row = new TagRowViewModel(1, aggregate);
                tagIndex.Add(aggregate.Epc, row);
                Tags.Insert(0, row);
                totalReadCount += aggregate.ReadCount;
                reindex = true;
                TrimToLimit();
            }
        }

        if (reindex)
        {
            ReindexRows();
        }

        if (lastByEpc.Count > 0)
        {
            OnPropertyChanged(nameof(UniqueTagCount));
        }
    }

    private void TrimToLimit()
    {
        if (Tags.Count <= MaxTagRows)
        {
            return;
        }

        int excess = Tags.Count - MaxTagRows;
        for (int i = 0; i < excess; i++)
        {
            int last = Tags.Count - 1;
            TagRowViewModel removed = Tags[last];
            tagIndex.Remove(removed.Epc);
            totalReadCount -= removed.ReadCount;
            Tags.RemoveAt(last);
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
