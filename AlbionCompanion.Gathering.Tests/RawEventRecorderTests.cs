using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class RawEventRecorderTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    private sealed class SingleConnectionDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public SingleConnectionDbContextFactory(SqliteConnection connection)
        {
            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static (GatheringSessionService Service, AppDbContext Context, IDbContextFactory<AppDbContext> Factory) CreateServiceWithContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context);
        var factory = new SingleConnectionDbContextFactory(connection);
        return (service, context, factory);
    }

    [Fact]
    public async Task EventWithActiveSession_RecordsRowWithSessionId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        await service.StartSessionAsync("4213");
        var session = await service.GetActiveSessionAsync();
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);

        await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 535802, [252] = (byte)59 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Equal(session!.Id, stored.SessionId);
        Assert.Equal((byte)1, stored.PhotonCode);
        Assert.Equal((byte)59, stored.SemanticEventCode);
    }

    [Fact]
    public async Task EventWithNoActiveSession_RecordsRowWithNullSessionId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);

        await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 535802 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SessionId);
    }

    [Fact]
    public async Task EventWithoutSemanticCodeParameter_RecordsNullSemanticEventCode()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);

        await recorder.HandleEventAsync(new PhotonEvent(3, new Dictionary<byte, object?> { [0] = 535802, [1] = 100 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SemanticEventCode);
    }

    [Fact]
    public async Task SemanticCodeOutOfByteRange_RecordsNullSemanticEventCode()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);

        await recorder.HandleEventAsync(new PhotonEvent(3, new Dictionary<byte, object?> { [252] = 99999 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SemanticEventCode);
    }

    [Fact]
    public async Task ParametersJson_RoundTripsOriginalDictionary()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);

        await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 535802, [3] = 2955, [4] = 27, [252] = (byte)59 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(stored.ParametersJson);
        Assert.Equal(535802, roundTripped!["0"].GetInt32());
        Assert.Equal(2955, roundTripped["3"].GetInt32());
        Assert.Equal(27, roundTripped["4"].GetInt32());
        Assert.Equal(59, roundTripped["252"].GetInt32());
    }

    [Fact]
    public async Task EventReceivedThroughParserSubscription_IsRecorded()
    {
        // Confirms the constructor actually subscribes to OnEventReceived, not just that
        // HandleEventAsync works when called directly.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        _ = new RawEventRecorder(parser, factory);

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object?> { [252] = (byte)59 }));
        await Task.Delay(50); // event handler is fire-and-forget (async void-like dispatch)

        Assert.Single(context.RawGatheringEvents);
    }

    [Fact]
    public async Task TwoEventsRaisedInQuickSuccession_BothPersistedWithoutThrowing()
    {
        // Regression test: RawEventRecorder used to do its write against a single shared
        // AppDbContext instance. Because dispatch is fire-and-forget, two events raised without
        // awaiting between them could both be mid-flight on that same instance at once, and EF
        // Core throws "A second operation was started on this context instance before a previous
        // operation completed." Each event now gets its own AppDbContext via IDbContextFactory,
        // so overlapping events must no longer throw and both rows must be persisted.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context, factory) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);
        var failures = new List<Exception>();
        recorder.OnRecordFailure += (_, ex) => failures.Add(ex);

        var task1 = recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [252] = (byte)59 }));
        var task2 = recorder.HandleEventAsync(new PhotonEvent(2, new Dictionary<byte, object?> { [252] = (byte)60 }));
        await Task.WhenAll(task1, task2);

        Assert.Empty(failures);
        Assert.Equal(2, context.RawGatheringEvents.Count());
    }

    // AppDbContext subclass that lets a test hold a SaveChangesAsync call open until explicitly
    // released, so a concurrent caller on the SAME context instance is guaranteed to observe it
    // as "mid-flight" - this is what forces a genuine overlap instead of relying on incidental
    // timing (in-memory SQLite awaits normally complete synchronously, which is why the older
    // TwoEventsRaisedInQuickSuccession_BothPersistedWithoutThrowing test never actually exercised
    // the race).
    private sealed class BlockingSaveDbContext : AppDbContext
    {
        private readonly TaskCompletionSource _saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingSaveDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public Task SaveStarted => _saveStarted.Task;

        public void ReleaseSave() => _releaseSave.TrySetResult();

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveStarted.TrySetResult();
            await _releaseSave.Task;
            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task RawEventRecorder_NeverTouchesSharedContext_EvenWhileSharedContextIsMidOperation()
    {
        // Proves RawEventRecorder's HandleEventAsync is fully independent of the shared scoped
        // AppDbContext that GatheringSessionService/GatheringEventRouter use. We deliberately hold
        // the shared context "busy" mid-SaveChangesAsync (simulating GatheringEventRouter's
        // in-flight write) for the whole duration of a RawEventRecorder call. Before the fix,
        // RawEventRecorder read the active session via IGatheringSessionService - i.e. against
        // this same busy shared context - which EF Core would reject with "a second operation was
        // started on this context instance before a previous operation completed." After the fix,
        // RawEventRecorder never touches the shared context at all, so it must complete without
        // error regardless of what the shared context is doing.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var sharedContext = new BlockingSaveDbContext(options);
        sharedContext.Database.EnsureCreated();
        var sharedService = new GatheringSessionService(sharedContext);
        var factory = new SingleConnectionDbContextFactory(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, factory);
        var failures = new List<Exception>();
        recorder.OnRecordFailure += (_, ex) => failures.Add(ex);

        // Start a genuine in-flight operation on the shared context and wait until it is
        // confirmed mid-SaveChangesAsync (guaranteed real overlap, not a timing guess).
        var routerStyleWrite = sharedService.StartSessionAsync("4213");
        await sharedContext.SaveStarted;

        try
        {
            // While the shared context is still busy, RawEventRecorder must be able to look up
            // the active session and persist its row using only its own factory-created context.
            await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [252] = (byte)59 }));
        }
        finally
        {
            sharedContext.ReleaseSave();
            await routerStyleWrite;
        }

        Assert.Empty(failures);
        using var verifyContext = new AppDbContext(options);
        Assert.Single(verifyContext.RawGatheringEvents);
    }
}
