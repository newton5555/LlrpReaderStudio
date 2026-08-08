using LlrpReaderStudio.Core;
using LlrpSdk;

namespace LlrpReaderStudio.Tests;

public sealed class ReaderFleetServiceTests
{
    [Fact]
    public async Task ConnectAndInventory_RouteToProfileSession()
    {
        var session = new FakeSession();
        await using var fleet = new ReaderFleetService(new FakeFactory(session));
        var profile = new ReaderProfile { Name = "Reader 1", Host = "192.0.2.10" };
        fleet.Add(profile);

        await fleet.ConnectAsync(profile.Id);
        await fleet.StartInventoryAsync(profile.Id, new InventorySettings { Session = 2 });

        Assert.True(session.ConnectCalled);
        Assert.Equal((byte)2, session.StartedInventory?.Session);
        Assert.Equal(StudioReaderState.Inventorying, Assert.Single(fleet.Readers).State);
    }

    [Fact]
    public async Task ProbeAsync_ConnectsAndDisposesWithoutRegistration()
    {
        var session = new FakeSession();
        await using var fleet = new ReaderFleetService(new FakeFactory(session));
        var profile = new ReaderProfile { Name = "Reader 1", Host = "192.0.2.10" };

        ReaderProbeResult result = await fleet.ProbeAsync(profile);

        Assert.True(session.ConnectCalled);
        Assert.True(session.DisconnectCalled);
        Assert.False(session.IsConnected);
        Assert.Empty(fleet.Readers);
        Assert.Null(session.StartedInventory);
    }

    [Fact]
    public async Task Reports_ArePublishedAsFleetAggregates()
    {
        var session = new FakeSession();
        await using var fleet = new ReaderFleetService(new FakeFactory(session));
        var profile = new ReaderProfile { Name = "Reader 1", Host = "192.0.2.10" };
        fleet.Add(profile);
        FleetTagObservedEventArgs? observed = null;
        fleet.TagObserved += (_, args) => observed = args;

        session.Emit(new TagReport(
            Convert.FromHexString("300833B2"),
            14150,
            1,
            1,
            1,
            -50,
            1,
            null,
            null,
            4,
            null));

        // Aggregation now runs on a background consumer task (the pump thread only hands the report
        // off), so the event is published asynchronously; wait for it within the test timeout.
        await WaitUntilAsync(() => observed is not null);

        Assert.NotNull(observed);
        Assert.Equal("300833B2", observed.Aggregate.Epc);
        Assert.Equal(4, observed.Aggregate.ReadCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for condition.");
    }

    [Fact]
    public async Task DeviceInitiatedClose_MarksReaderFaulted()
    {
        var session = new FakeSession();
        await using var fleet = new ReaderFleetService(new FakeFactory(session));
        var profile = new ReaderProfile { Name = "Reader 1", Host = "192.0.2.10" };
        fleet.Add(profile);
        ReaderStatus? published = null;
        fleet.ReaderStatusChanged += (_, args) => published = args.Status;

        session.EmitDeviceInitiatedClosed();

        Assert.Equal(StudioReaderState.Faulted, Assert.Single(fleet.Readers).State);
        Assert.NotNull(published);
        Assert.Equal(StudioReaderState.Faulted, published.State);
        Assert.Contains("device-initiated", published.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReaderException_IsForwarded()
    {
        var session = new FakeSession();
        await using var fleet = new ReaderFleetService(new FakeFactory(session));
        var profile = new ReaderProfile { Name = "Reader 1", Host = "192.0.2.10" };
        fleet.Add(profile);
        ReaderDeviceExceptionEventArgs? observed = null;
        fleet.ReaderDeviceExceptionOccurred += (_, args) => observed = args;

        session.EmitReaderException(new ReaderDeviceExceptionEventArgs("op failed", 5, 1, DateTimeOffset.UtcNow));

        Assert.NotNull(observed);
        Assert.Equal("op failed", observed.Message);
        Assert.Equal((uint)5, observed.ROSpecId);
        Assert.Equal((ushort)1, observed.AntennaId);
    }

    private sealed class FakeFactory(FakeSession session) : IReaderSessionFactory
    {
        public IReaderSession Create(ReaderProfile profile) => session;
    }

    private sealed class FakeSession : IReaderSession
    {
        public bool ConnectCalled { get; private set; }
        public bool DisconnectCalled { get; private set; }
        public InventorySettings? StartedInventory { get; private set; }
        public bool IsConnected { get; private set; }
        public ReaderIdentity? Identity => null;
        public ReaderCapabilities? Capabilities => null;
        public event EventHandler<StudioTagReportEventArgs>? TagReported;
        public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
        public event EventHandler<EventArgs>? DeviceInitiatedClosed;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCalled = true;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            DisconnectCalled = true;
            IsConnected = false;
            return Task.CompletedTask;
        }
        public Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ReaderSettingsSnapshot(new ReaderSettings(), null));
        public Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ReaderSettingsDefaults.CreateGeneric());
        public Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken)
        {
            StartedInventory = settings;
            return Task.CompletedTask;
        }

        public Task StartConfiguredInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopInventoryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TagAccessResult> ReadTagMemoryAsync(ReadTagRequest request, CancellationToken cancellationToken) =>
            Task.FromException<TagAccessResult>(new NotSupportedException());
        public Task<TagAccessResult> WriteTagMemoryAsync(WriteTagRequest request, CancellationToken cancellationToken) =>
            Task.FromException<TagAccessResult>(new NotSupportedException());
        public Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(TagReport report) =>
            TagReported?.Invoke(this, new StudioTagReportEventArgs(report));

        public void EmitReaderException(ReaderDeviceExceptionEventArgs args) =>
            ReaderExceptionOccurred?.Invoke(this, args);

        public void EmitDeviceInitiatedClosed() =>
            DeviceInitiatedClosed?.Invoke(this, EventArgs.Empty);
    }
}
