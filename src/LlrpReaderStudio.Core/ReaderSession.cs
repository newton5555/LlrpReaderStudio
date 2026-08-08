using LlrpSdk;
using LlrpSdk.Extensions.Impinj;
using Microsoft.Extensions.Logging;

namespace LlrpReaderStudio.Core;

public sealed class StudioTagReportEventArgs(TagReport report) : EventArgs
{
    public TagReport Report { get; } = report;
}

public sealed class ReaderDeviceExceptionEventArgs(
    string message,
    uint? roSpecId,
    ushort? antennaId,
    DateTimeOffset timestamp) : EventArgs
{
    public string Message { get; } = message;
    public uint? ROSpecId { get; } = roSpecId;
    public ushort? AntennaId { get; } = antennaId;
    public DateTimeOffset Timestamp { get; } = timestamp;
}

public interface IReaderSession : IAsyncDisposable
{
    public bool IsConnected { get; }
    public ReaderIdentity? Identity { get; }
    public ReaderCapabilities? Capabilities { get; }
    public event EventHandler<StudioTagReportEventArgs>? TagReported;
    public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
    public event EventHandler<EventArgs>? DeviceInitiatedClosed;
    public Task ConnectAsync(CancellationToken cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken);
    public Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken);
    public Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken);
    public Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken);
    public Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken);
    public Task StartConfiguredInventoryAsync(CancellationToken cancellationToken);
    public Task StopInventoryAsync(CancellationToken cancellationToken);
    public Task<TagAccessResult> ReadTagMemoryAsync(ReadTagRequest request, CancellationToken cancellationToken);
    public Task<TagAccessResult> WriteTagMemoryAsync(WriteTagRequest request, CancellationToken cancellationToken);
    public Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken);
}

public interface IReaderSessionFactory
{
    public IReaderSession Create(ReaderProfile profile);
}

public sealed class LlrpReaderSessionFactory : IReaderSessionFactory
{
    private readonly ILoggerFactory loggerFactory;

    public LlrpReaderSessionFactory(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
    }

    public IReaderSession Create(ReaderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var builder = new LlrpReaderBuilder(profile.Host)
            .WithPort(profile.Port)
            .WithProtocolVersionPolicy(profile.LlrpVersion switch
            {
                LlrpProtocolVersionOption.Force101 => LlrpProtocolVersionPolicy.Force101,
                LlrpProtocolVersionOption.Force11 => LlrpProtocolVersionPolicy.Force11,
                _ => LlrpProtocolVersionPolicy.Auto,
            });
        builder.WithLoggerFactory(loggerFactory);
        if (profile.EnableImpinjExtensions)
        {
            builder.UseImpinj();
        }

        return new LlrpReaderSession(builder.Build());
    }
}

internal sealed class LlrpReaderSession : IReaderSession
{
    private readonly LlrpReader reader;
    private InventorySession? inventorySession;

    public LlrpReaderSession(LlrpReader reader)
    {
        this.reader = reader;
        reader.TagsReported += OnTagsReported;
        reader.ReaderExceptionOccurred += OnReaderExceptionOccurred;
        reader.ConnectionChanged += OnConnectionChanged;
    }

    public bool IsConnected => reader.IsConnected;
    public ReaderIdentity? Identity => reader.Identity;
    public ReaderCapabilities? Capabilities => reader.Capabilities;
    public event EventHandler<StudioTagReportEventArgs>? TagReported;
    public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
    public event EventHandler<EventArgs>? DeviceInitiatedClosed;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await reader.ConnectAsync(cancellationToken).ConfigureAwait(false);
        // A failed managed operation (for example ApplySettingsAsync) marks the SDK-managed state as
        // unknown; resynchronize so the next managed call does not throw
        // "SDK-managed reader state is unknown after raw protocol access".
        if (!reader.IsManagedStateSynchronized)
        {
            await reader.SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SynchronizeIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!reader.IsManagedStateSynchronized)
        {
            await reader.SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await reader.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inventorySession = null;
        }
    }

    public async Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await reader.QuerySettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await reader.GetDefaultSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await reader.ApplySettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken)
    {
        if (inventorySession is not null)
        {
            throw new InvalidOperationException("Inventory is already running for this reader.");
        }

        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        inventorySession = await reader.StartInventoryAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartConfiguredInventoryAsync(CancellationToken cancellationToken)
    {
        if (inventorySession is not null)
        {
            throw new InvalidOperationException("Inventory is already running for this reader.");
        }

        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        inventorySession = await reader.StartInventoryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken)
    {
        InventorySession? session = inventorySession;
        if (session is null)
        {
            await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
            await reader.StopAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        inventorySession = null;
        await session.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TagAccessResult> ReadTagMemoryAsync(ReadTagRequest request, CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadTagMemoryAsync(request, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TagAccessResult> WriteTagMemoryAsync(WriteTagRequest request, CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await reader.WriteTagMemoryAsync(request, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await reader.SetGpoAsync(portNumber, state, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        reader.TagsReported -= OnTagsReported;
        reader.ReaderExceptionOccurred -= OnReaderExceptionOccurred;
        reader.ConnectionChanged -= OnConnectionChanged;
        if (inventorySession is not null)
        {
            try
            {
                await inventorySession.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Reader disposal remains authoritative when the connection is already gone.
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTagsReported(object? sender, TagReportEventArgs args) =>
        TagReported?.Invoke(this, new StudioTagReportEventArgs(args.Report));

    private void OnReaderExceptionOccurred(object? sender, ReaderExceptionEventArgs args) =>
        ReaderExceptionOccurred?.Invoke(this, new ReaderDeviceExceptionEventArgs(
            args.Message,
            args.ROSpecId,
            args.AntennaId,
            args.Timestamp));

    private void OnConnectionChanged(object? sender, ReaderConnectionChangedEventArgs args)
    {
        if (args.CurrentState == ReaderConnectionState.Faulted && args.DeviceInitiatedClose)
        {
            DeviceInitiatedClosed?.Invoke(this, EventArgs.Empty);
        }
    }
}
