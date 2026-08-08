using System.Threading.Channels;
using LlrpSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlrpReaderStudio.Core;

public sealed class ReaderStatusChangedEventArgs(ReaderStatus status) : EventArgs
{
    public ReaderStatus Status { get; } = status;
}

public sealed class FleetTagObservedEventArgs(ReaderProfile profile, TagReport report, TagObservation aggregate) : EventArgs
{
    public ReaderProfile Profile { get; } = profile;
    public TagReport Report { get; } = report;
    public TagObservation Aggregate { get; } = aggregate;
}

public sealed class ReaderFleetService : IAsyncDisposable
{
    /// <summary>
    /// Bounded cap for the tag-report hand-off channel. The SDK message-pump thread only writes to
    /// this channel (O(1), non-blocking); aggregation + the TagObserved event run on a background
    /// consumer task. If a report storm outpaces the consumer, writes are refused (and counted) rather
    /// than stalling the pump (which would delay KEEPALIVE_ACKs and look like a dead UI).
    /// </summary>
    private const int TagChannelCapacity = 100_000;

    private readonly IReaderSessionFactory sessionFactory;
    private readonly ILogger<ReaderFleetService> logger;
    private readonly Dictionary<Guid, ManagedReader> readers = [];
    private readonly TagAggregateStore aggregates = new();
    private readonly Channel<TagWorkItem> tagChannel;
    private readonly Task tagConsumer;
    private long tagsDropped;

    public ReaderFleetService(IReaderSessionFactory? sessionFactory = null, ILogger<ReaderFleetService>? logger = null)
    {
        this.sessionFactory = sessionFactory ?? new LlrpReaderSessionFactory(NullLoggerFactory.Instance);
        this.logger = logger ?? NullLogger<ReaderFleetService>.Instance;
        tagChannel = Channel.CreateBounded<TagWorkItem>(new BoundedChannelOptions(TagChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        tagConsumer = Task.Run(ConsumeTagReportsAsync);
    }

    public event EventHandler<ReaderStatusChangedEventArgs>? ReaderStatusChanged;
    public event EventHandler<FleetTagObservedEventArgs>? TagObserved;
    public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderDeviceExceptionOccurred;

    public IReadOnlyList<ReaderStatus> Readers =>
        readers.Values.Select(static reader => reader.Status).OrderBy(static reader => reader.Profile.Name).ToArray();

    public IReadOnlyList<TagObservation> Tags => aggregates.Snapshot();

    public ReaderStatus Add(ReaderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (readers.ContainsKey(profile.Id))
        {
            throw new InvalidOperationException($"Reader profile '{profile.Id}' is already registered.");
        }

        IReaderSession session = sessionFactory.Create(profile);
        var managed = new ManagedReader(profile, session);
        session.TagReported += (_, args) => OnTagReported(managed, args.Report);
        session.ReaderExceptionOccurred += (_, args) => OnReaderExceptionOccurred(managed, args);
        session.DeviceInitiatedClosed += (_, _) => OnDeviceInitiatedClosed(managed);
        readers.Add(profile.Id, managed);
        Publish(managed);
        return managed.Status;
    }

    public async Task RemoveAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        ManagedReader managed = Get(profileId);
        await managed.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (managed.Session.IsConnected)
            {
                try
                {
                    await managed.Session.StopInventoryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Disconnect and dispose remain authoritative during removal.
                }

                try
                {
                    await managed.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Dispose below still releases the transport.
                }
            }

            await managed.Session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            readers.Remove(profileId);
            managed.Gate.Release();
            managed.Gate.Dispose();
        }
    }

    public Task ConnectAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, StudioReaderState.Connecting, async managed =>
        {
            await managed.Session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            managed.Status = managed.Status with
            {
                State = StudioReaderState.Connected,
                Model = managed.Session.Identity is null
                    ? null
                    : $"{managed.Session.Identity.ManufacturerId}:{managed.Session.Identity.ModelId}",
                Firmware = managed.Session.Identity?.FirmwareVersion,
                Error = null,
            };
        }, cancellationToken);

    /// <summary>
    /// Probes a not-yet-registered data source: creates a temporary session, connects, reads the
    /// reader identity, then disconnects and disposes. Nothing is registered or persisted; callers
    /// decide whether to save and add the profile based on the result.
    /// </summary>
    public async Task<ReaderProbeResult> ProbeAsync(ReaderProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        IReaderSession session = sessionFactory.Create(profile);
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            ReaderIdentity? identity = session.Identity;
            return new ReaderProbeResult(
                identity is null ? null : $"{identity.ManufacturerId}:{identity.ModelId}",
                identity?.FirmwareVersion);
        }
        finally
        {
            try
            {
                await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Disposal below remains authoritative.
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task DisconnectAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, StudioReaderState.Disconnecting, async managed =>
        {
            await managed.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            managed.Status = managed.Status with { State = StudioReaderState.Disconnected, Error = null };
        }, cancellationToken);

    public Task StartInventoryAsync(Guid profileId, InventorySettings settings, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, StudioReaderState.Inventorying, async managed =>
        {
            await managed.Session.StartInventoryAsync(settings, cancellationToken).ConfigureAwait(false);
            managed.Status = managed.Status with { State = StudioReaderState.Inventorying, Error = null };
        }, cancellationToken);

    public Task StartConfiguredInventoryAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, StudioReaderState.Inventorying, async managed =>
        {
            await managed.Session.StartConfiguredInventoryAsync(cancellationToken).ConfigureAwait(false);
            managed.Status = managed.Status with { State = StudioReaderState.Inventorying, Error = null };
        }, cancellationToken);

    public Task StopInventoryAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        RunAsync(profileId, StudioReaderState.Stopping, async managed =>
        {
            await managed.Session.StopInventoryAsync(cancellationToken).ConfigureAwait(false);
            managed.Status = managed.Status with { State = StudioReaderState.Connected, Error = null };
        }, cancellationToken);

    public Task<ReaderSettingsSnapshot> QuerySettingsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        UseAsync(profileId, (session, token) => session.QuerySettingsAsync(token), cancellationToken);

    public Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        UseAsync(profileId, (session, token) => session.GetDefaultSettingsAsync(token), cancellationToken);

    public ReaderCapabilities? GetCapabilities(Guid profileId) => Get(profileId).Session.Capabilities;

    public Task ApplySettingsAsync(Guid profileId, ReaderSettings settings, CancellationToken cancellationToken = default) =>
        UseAsync(profileId, (session, token) => session.ApplySettingsAsync(settings, token), cancellationToken);

    public Task<TagAccessResult> ReadTagMemoryAsync(Guid profileId, ReadTagRequest request, CancellationToken cancellationToken = default) =>
        UseAsync(profileId, (session, token) => session.ReadTagMemoryAsync(request, token), cancellationToken);

    public Task<TagAccessResult> WriteTagMemoryAsync(Guid profileId, WriteTagRequest request, CancellationToken cancellationToken = default) =>
        UseAsync(profileId, (session, token) => session.WriteTagMemoryAsync(request, token), cancellationToken);

    public Task SetGpoAsync(Guid profileId, ushort portNumber, bool state, CancellationToken cancellationToken = default) =>
        UseAsync(profileId, (session, token) => session.SetGpoAsync(portNumber, state, token), cancellationToken);

    public void ClearTags() => aggregates.Clear();

    /// <summary>Number of tag reports dropped because the consumer could not keep up with the pump.</summary>
    public long TagsDropped => Interlocked.Read(ref tagsDropped);

    public async ValueTask DisposeAsync()
    {
        foreach (ManagedReader managed in readers.Values.ToArray())
        {
            if (managed.Session.IsConnected)
            {
                try
                {
                    await managed.Session.StopInventoryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Continue closing the remaining readers.
                }

                try
                {
                    await managed.Session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Continue disposing the session.
                }
            }

            await managed.Session.DisposeAsync().ConfigureAwait(false);
            managed.Gate.Dispose();
        }

        readers.Clear();

        // Stop the consumer and let it drain anything already queued before the pump stopped.
        tagChannel.Writer.TryComplete();
        try
        {
            await tagConsumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Consumer was shut down with the fleet; nothing left to drain.
        }
    }

    private async Task RunAsync(
        Guid profileId,
        StudioReaderState interimState,
        Func<ManagedReader, Task> operation,
        CancellationToken cancellationToken)
    {
        ManagedReader managed = Get(profileId);
        await managed.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            managed.Status = managed.Status with { State = interimState, Error = null };
            Publish(managed);
            await operation(managed).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            managed.Status = managed.Status with { State = StudioReaderState.Faulted, Error = exception.Message };
            throw;
        }
        finally
        {
            Publish(managed);
            managed.Gate.Release();
        }
    }

    private async Task<T> UseAsync<T>(
        Guid profileId,
        Func<IReaderSession, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ManagedReader managed = Get(profileId);
        await managed.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(managed.Session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    private async Task UseAsync(
        Guid profileId,
        Func<IReaderSession, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ManagedReader managed = Get(profileId);
        await managed.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(managed.Session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    private ManagedReader Get(Guid id) =>
        readers.TryGetValue(id, out ManagedReader? managed)
            ? managed
            : throw new KeyNotFoundException($"Reader profile '{id}' is not registered.");

    private void OnTagReported(ManagedReader managed, TagReport report)
    {
        // Runs on the SDK message-pump thread: only hand off to the consumer (O(1), non-blocking).
        // Aggregation and the TagObserved event never run on the pump, so a report flood cannot
        // stall KEEPALIVE_ACK handling on that thread. If the consumer lags, TryWrite is refused
        // (and counted) so the pump never blocks.
        if (!tagChannel.Writer.TryWrite(new TagWorkItem(managed, report)))
        {
            long dropped = Interlocked.Increment(ref tagsDropped);
            logger.LogWarning("Tag report dropped (consumer saturated); total dropped so far: {Dropped}", dropped);
        }
    }

    private async Task ConsumeTagReportsAsync()
    {
        try
        {
            await foreach (TagWorkItem item in tagChannel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    OnTagProcessed(item.Managed, item.Report);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to aggregate a tag report for reader {Name}.",
                        item.Managed.Status.Profile.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fleet disposal completed the channel; the loop is done.
        }
    }

    private void OnTagProcessed(ManagedReader managed, TagReport report)
    {
        TagObservation aggregate = aggregates.Add(managed.Status.Profile, report);
        TagObserved?.Invoke(this, new FleetTagObservedEventArgs(managed.Status.Profile, report, aggregate));
    }

    private void OnReaderExceptionOccurred(ManagedReader managed, ReaderDeviceExceptionEventArgs args)
    {
        ReaderDeviceExceptionOccurred?.Invoke(this, args);
    }

    private async void OnDeviceInitiatedClosed(ManagedReader managed)
    {
        // Serialize the status update against in-flight operations (RunAsync / UseAsync) so the
        // device-initiated close cannot be clobbered by a concurrent op's unconditional Publish.
        await managed.Gate.WaitAsync(CancellationToken.None);
        try
        {
            managed.Status = managed.Status with
            {
                State = StudioReaderState.Faulted,
                Error = "Reader closed the connection (device-initiated).",
            };
            Publish(managed);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    private void Publish(ManagedReader managed) =>
        ReaderStatusChanged?.Invoke(this, new ReaderStatusChangedEventArgs(managed.Status));

    private sealed class ManagedReader(ReaderProfile profile, IReaderSession session)
    {
        public IReaderSession Session { get; } = session;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ReaderStatus Status { get; set; } =
            new(profile, StudioReaderState.Disconnected, null, null, null);
    }

    private readonly record struct TagWorkItem(ManagedReader Managed, TagReport Report);
}
