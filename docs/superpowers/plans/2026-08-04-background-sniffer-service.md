# Background Sniffer Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move packet capture and gathering-session processing out of `AlbionCompanion.App`'s
process into a standalone Windows Service that runs continuously from boot, so gathering activity
is captured no matter what order Albion Online and the App are started in.

**Architecture:** A new `AlbionCompanion.Service` Worker Service hosts the existing
`AppHostBuilder`-wired sniffer/gathering pipeline, gated on/off by whether `Albion-Online.exe` is
running. It exposes live gathering events to `AlbionCompanion.App` over a named pipe. The App
becomes a thin client: it reads history/characters straight from the shared database (unchanged)
and receives live updates over the pipe instead of in-process C# events. A small
`AlbionCompanion.ServiceInstaller` console app does the one-time install (copy files, migrate the
DB from `%APPDATA%` to `%ProgramData%`, register + start the Windows Service, grant the current
user start/stop rights on it).

**Tech Stack:** .NET 10, `Microsoft.Extensions.Hosting.WindowsServices`, `System.IO.Pipes`,
`System.ServiceProcess.ServiceController`, `System.Text.Json`, EF Core + SQLite (WAL mode), xUnit.

## Global Constraints

- Personal single-machine use only - no MSI/wizard, no code signing, no auto-update (per spec's
  Non-goals).
- Database and log paths move from `%APPDATA%\AlbionCompanion` to `%ProgramData%\AlbionCompanion`
  (a `LocalSystem`-run service cannot read a per-user `%APPDATA%`).
- Wire protocol: newline-delimited JSON over `System.IO.Pipes`, pipe name
  `AlbionCompanionLiveEvents`. No versioning - Service and App are always deployed together.
- Client retry policy: 5 attempts, 3 seconds apart, then stop and require a manual retry trigger.
- Game process names confirmed on this machine: `Albion-Online.exe` and `Albion-Online_BE.exe`.
- Follow this codebase's existing test style: real objects over mocks/fakes wherever practical;
  fakes only at true OS boundaries (`ServiceController`, `Process.GetProcessesByName`) that can't
  be exercised from xUnit.

---

### Task 1: `ICharacterService.NotifyCharactersChanged` - single invocation point for the change event

**Files:**
- Modify: `AlbionCompanion.Gathering/ICharacterService.cs`
- Modify: `AlbionCompanion.Gathering/CharacterService.cs`
- Test: `AlbionCompanion.Gathering.Tests/CharacterServiceTests.cs` (check if this file exists first: if not, add a new minimal test file)

**Interfaces:**
- Produces: `ICharacterService.NotifyCharactersChanged()` - callable from outside `CharacterService`
  itself (needed later by `LiveEventPipeServer`, running in a different process than whoever calls
  `AddAsync`/`DeleteAsync`/`RenameAsync`, to re-raise the same event locally when told a change
  happened elsewhere).

This is a pure refactor (no behavior change from the current three call sites) that adds one new
public method other tasks depend on.

- [ ] **Step 1: Check for an existing `CharacterServiceTests.cs` and read it**

Run: `ls AlbionCompanion.Gathering.Tests/CharacterServiceTests.cs 2>/dev/null || echo "does not exist"`

If it exists, read it fully before continuing - this task adds one more test to whatever's there,
following its existing helper/setup patterns instead of introducing a second style.

- [ ] **Step 2: Write the failing test**

If `CharacterServiceTests.cs` doesn't exist, create it with an in-memory SQLite context matching
the pattern in `GatheringSessionServiceTests.cs` (`CreateInMemoryContext`). Add:

```csharp
[Fact]
public async Task NotifyCharactersChanged_RaisesCharactersChanged()
{
    using var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
    using var context = new AppDbContext(options);
    context.Database.EnsureCreated();
    var factory = new PooledDbContextFactory<AppDbContext>(options);
    var service = new CharacterService(factory);
    var raiseCount = 0;
    service.CharactersChanged += (_, _) => raiseCount++;

    service.NotifyCharactersChanged();

    Assert.Equal(1, raiseCount);
}
```

(If `CharacterServiceTests.cs` already exists with different constructor/setup conventions for
`CharacterService`, match those instead of the snippet above - the point of Step 1 is to avoid
duplicating a second, inconsistent test-setup style.)

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~NotifyCharactersChanged_RaisesCharactersChanged"`
Expected: FAIL - `CharacterService` has no method `NotifyCharactersChanged`.

- [ ] **Step 4: Add the method to the interface and implementation**

In `AlbionCompanion.Gathering/ICharacterService.cs`, add alongside the existing
`event EventHandler? CharactersChanged;`:

```csharp
    // Re-raises CharactersChanged locally without itself changing any character data - used by
    // LiveEventPipeServer to propagate a change that happened in a different process (the App
    // writes characters, but LocalPlayerTracker's cache lives in the Service process) into this
    // process's own event, exactly as if AddAsync/DeleteAsync/RenameAsync had run here.
    void NotifyCharactersChanged();
```

In `AlbionCompanion.Gathering/CharacterService.cs`, replace the three inline
`CharactersChanged?.Invoke(this, EventArgs.Empty);` call sites (in `AddAsync`, `DeleteAsync`,
`RenameAsync`) with calls to a new method, and implement the interface member:

```csharp
    public void NotifyCharactersChanged() => CharactersChanged?.Invoke(this, EventArgs.Empty);
```

Each of the three methods now ends with `NotifyCharactersChanged();` instead of the raw
`?.Invoke(...)`.

- [ ] **Step 5: Update every `FakeCharacterService` in the test suite**

`GatheringSessionServiceTests.cs`, `ZoneTrackerTests.cs`, `RawEventRecorderTests.cs`,
`GatheringEventRouterTests.cs`, `LocalPlayerTrackerTests.cs` each declare a private
`FakeCharacterService : ICharacterService`. Add to each:

```csharp
        public void NotifyCharactersChanged() => CharactersChanged?.Invoke(this, EventArgs.Empty);
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test AlbionCompanion.Gathering.Tests`
Expected: all pass, including the new test.

- [ ] **Step 7: Commit**

```bash
git add AlbionCompanion.Gathering/ICharacterService.cs AlbionCompanion.Gathering/CharacterService.cs AlbionCompanion.Gathering.Tests/
git commit -m "refactor(gathering): add ICharacterService.NotifyCharactersChanged"
```

---

### Task 2: `LiveEventMessage` - wire protocol DTOs and (de)serialization

**Files:**
- Create: `AlbionCompanion.Gathering/LiveEvents/LiveEventMessage.cs`
- Test: `AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventMessageTests.cs`

**Interfaces:**
- Produces: `LiveEventMessage` (abstract base with a `Type` discriminator), one concrete record per
  event (`SessionStartedMessage`, `SessionEndedMessage`, `LocationChangedMessage`,
  `ItemAddedMessage`, `FameAddedMessage`, `SilverAddedMessage`, `CharacterRegistryChangedMessage`),
  and `LiveEventMessageSerializer.Serialize(LiveEventMessage) : string` /
  `Deserialize(string) : LiveEventMessage`.

This task is pure data modeling + JSON round-trip - no pipes yet, so it's fully unit-testable
without any OS resources.

- [ ] **Step 1: Write the failing test**

```csharp
using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventMessageTests
{
    [Fact]
    public void SessionStartedMessage_RoundTripsThroughSerializer()
    {
        var original = new SessionStartedMessage(Guid.NewGuid(), "Martlock", Guid.NewGuid());

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<SessionStartedMessage>(result);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.StartLocation, deserialized.StartLocation);
        Assert.Equal(original.CharacterId, deserialized.CharacterId);
    }

    [Fact]
    public void ItemAddedMessage_RoundTripsThroughSerializer()
    {
        var original = new ItemAddedMessage("T4_ORE", 5, "Martlock");

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<ItemAddedMessage>(result);
        Assert.Equal("T4_ORE", deserialized.ItemId);
        Assert.Equal(5, deserialized.Amount);
        Assert.Equal("Martlock", deserialized.Location);
    }

    [Fact]
    public void CharacterRegistryChangedMessage_RoundTripsThroughSerializer()
    {
        var original = new CharacterRegistryChangedMessage();

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        Assert.IsType<CharacterRegistryChangedMessage>(result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventMessageTests"`
Expected: FAIL to compile - none of these types exist yet.

- [ ] **Step 3: Implement the DTOs and serializer**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionCompanion.Gathering.LiveEvents;

// One line of newline-delimited JSON per message, sent both directions over the same named pipe:
// Service -> App carries the six gathering-session events IGatheringSessionService already
// raises in-process; App -> Service carries CharacterRegistryChanged (the one thing the App still
// writes directly to the database, so the Service's LocalPlayerTracker cache needs telling).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStartedMessage), "SessionStarted")]
[JsonDerivedType(typeof(SessionEndedMessage), "SessionEnded")]
[JsonDerivedType(typeof(LocationChangedMessage), "LocationChanged")]
[JsonDerivedType(typeof(ItemAddedMessage), "ItemAdded")]
[JsonDerivedType(typeof(FameAddedMessage), "FameAdded")]
[JsonDerivedType(typeof(SilverAddedMessage), "SilverAdded")]
[JsonDerivedType(typeof(CharacterRegistryChangedMessage), "CharacterRegistryChanged")]
public abstract record LiveEventMessage;

public sealed record SessionStartedMessage(Guid SessionId, string StartLocation, Guid? CharacterId) : LiveEventMessage;
public sealed record SessionEndedMessage(Guid SessionId) : LiveEventMessage;
public sealed record LocationChangedMessage(Guid SessionId, string CurrentLocation) : LiveEventMessage;
public sealed record ItemAddedMessage(string ItemId, int Amount, string Location) : LiveEventMessage;
public sealed record FameAddedMessage(int Amount, string Location) : LiveEventMessage;
public sealed record SilverAddedMessage(int Amount, string Location) : LiveEventMessage;
public sealed record CharacterRegistryChangedMessage : LiveEventMessage;

public static class LiveEventMessageSerializer
{
    public static string Serialize(LiveEventMessage message) =>
        JsonSerializer.Serialize(message, typeof(LiveEventMessage));

    public static LiveEventMessage Deserialize(string line) =>
        (LiveEventMessage)JsonSerializer.Deserialize(line, typeof(LiveEventMessage))!;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventMessageTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.Gathering/LiveEvents/ AlbionCompanion.Gathering.Tests/LiveEvents/
git commit -m "feat(gathering): add LiveEventMessage wire protocol DTOs"
```

---

### Task 3: `IGatheringLiveEventSource` - shared event shape for in-process and piped sources

**Files:**
- Create: `AlbionCompanion.Gathering/LiveEvents/IGatheringLiveEventSource.cs`
- Modify: `AlbionCompanion.Gathering/IGatheringSessionService.cs`

**Interfaces:**
- Produces: `IGatheringLiveEventSource` with the six events, signatures identical to
  `IGatheringSessionService`'s existing ones.
- `IGatheringSessionService` now extends `IGatheringLiveEventSource` (no behavior change - the
  events it already declares satisfy the new base interface as-is).

This lets `GatheringLiveState` (Task 9) subscribe to *either* a real `IGatheringSessionService`
(used today, still used inside the Service process) or a `LiveEventPipeClient` (Task 5, used by
the App) through one shared shape.

- [ ] **Step 1: Read `IGatheringSessionService.cs` in full**

```bash
cat AlbionCompanion.Gathering/IGatheringSessionService.cs
```

Confirm the six event signatures before copying them - they must match exactly or the interface
won't be satisfied.

- [ ] **Step 2: Create the interface**

```csharp
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Common event shape shared by IGatheringSessionService (the real, in-process source - used
// directly inside AlbionCompanion.Service) and LiveEventPipeClient (the App's piped source).
// GatheringLiveState subscribes to whichever one it's given without caring which.
public interface IGatheringLiveEventSource
{
    event EventHandler<GatheringSession>? OnSessionStarted;
    event EventHandler<GatheringSession>? OnSessionEnded;
    event EventHandler<GatheringSession>? OnLocationChanged;
    event EventHandler<GatheredItem>? OnItemAdded;
    event EventHandler<FameLog>? OnFameAdded;
    event EventHandler<SilverLog>? OnSilverAdded;
}
```

- [ ] **Step 3: Make `IGatheringSessionService` extend it**

In `AlbionCompanion.Gathering/IGatheringSessionService.cs`, change:

```csharp
public interface IGatheringSessionService
```

to:

```csharp
public interface IGatheringSessionService : LiveEvents.IGatheringLiveEventSource
```

Remove the now-duplicated six `event EventHandler<...>? On...;` declarations from
`IGatheringSessionService` itself (they're inherited from `IGatheringLiveEventSource` now) -
`GatheringSessionService`'s existing field-like event implementations don't need to change at all,
since C# lets a class satisfy an inherited interface event with the same declaration it already
has.

- [ ] **Step 4: Build to confirm nothing broke**

Run: `dotnet build AlbionCompanion.Gathering`
Expected: builds clean - this is a pure interface refactor, no call site should need changes.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test AlbionCompanion.Gathering.Tests`
Expected: all still pass (no behavior changed).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/LiveEvents/IGatheringLiveEventSource.cs AlbionCompanion.Gathering/IGatheringSessionService.cs
git commit -m "refactor(gathering): extract IGatheringLiveEventSource from IGatheringSessionService"
```

---

### Task 4: `LiveEventPipeServer` - broadcasts session events to connected pipe clients

**Files:**
- Create: `AlbionCompanion.Gathering/LiveEvents/LiveEventPipeServer.cs`
- Test: `AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventPipeServerTests.cs`

**Interfaces:**
- Consumes: `IGatheringLiveEventSource` (Task 3), `ICharacterService.NotifyCharactersChanged()`
  (Task 1), `LiveEventMessageSerializer` (Task 2).
- Produces: `LiveEventPipeServer(string pipeName, ICharacterService characterService)` with
  `void AttachSource(IGatheringLiveEventSource source)` / `void DetachSource()` (called by
  `Worker` in Task 12 whenever the gathering pipeline starts/stops) and `Task
  RunAsync(CancellationToken)` that accepts connections forever.

Uses real `System.IO.Pipes.NamedPipeServerStream`/`NamedPipeClientStream` in the test - both ends
run in the same test process, so no fakes or actual Windows Service are needed to prove the
protocol works.

- [ ] **Step 1: Write the failing test**

```csharp
using System.IO.Pipes;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventPipeServerTests
{
    private sealed class FakeCharacterService : ICharacterService
    {
        public int NotifyCount { get; private set; }
        public event EventHandler? CharactersChanged;
        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Array.Empty<Character>());
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
        public void NotifyCharactersChanged() { NotifyCount++; CharactersChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeEventSource : IGatheringLiveEventSource
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<GatheringSession>? OnLocationChanged;
        public event EventHandler<GatheredItem>? OnItemAdded;
        public event EventHandler<FameLog>? OnFameAdded;
        public event EventHandler<SilverLog>? OnSilverAdded;

        public void RaiseSessionStarted(GatheringSession session) => OnSessionStarted?.Invoke(this, session);
        public void RaiseItemAdded(GatheredItem item) => OnItemAdded?.Invoke(this, item);
    }

    [Fact]
    public async Task ConnectedClient_ReceivesSessionStartedMessage()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        var source = new FakeEventSource();
        server.AttachSource(source);
        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(client);

        var characterId = Guid.NewGuid();
        var session = new GatheringSession { StartLocation = "Martlock", CharacterId = characterId };
        source.RaiseSessionStarted(session);

        var readTask = reader.ReadLineAsync();
        var line = await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(line);
        var message = Assert.IsType<SessionStartedMessage>(LiveEventMessageSerializer.Deserialize(line!));
        Assert.Equal("Martlock", message.StartLocation);
        Assert.Equal(characterId, message.CharacterId);

        cts.Cancel();
    }

    [Fact]
    public async Task ClientSendingCharacterRegistryChanged_NotifiesCharacterService()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
        using var writer = new StreamWriter(client) { AutoFlush = true };

        await writer.WriteLineAsync(LiveEventMessageSerializer.Serialize(new CharacterRegistryChangedMessage()));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (characterService.NotifyCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, characterService.NotifyCount);

        cts.Cancel();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventPipeServerTests"`
Expected: FAIL to compile - `LiveEventPipeServer` doesn't exist yet.

- [ ] **Step 3: Implement `LiveEventPipeServer`**

```csharp
using System.IO.Pipes;
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Runs inside AlbionCompanion.Service. Stays alive for the whole Service lifetime (accepting
// connections) independent of whether the gathering pipeline is currently running - AttachSource/
// DetachSource is how Worker (Task 12) plugs the pipeline's IGatheringSessionService in and out as
// Albion Online starts/stops, without the pipe connections themselves dropping.
public class LiveEventPipeServer
{
    private readonly string _pipeName;
    private readonly ICharacterService _characterService;
    private readonly List<StreamWriter> _writers = new();
    private readonly object _writersLock = new();
    private IGatheringLiveEventSource? _source;

    public LiveEventPipeServer(string pipeName, ICharacterService characterService)
    {
        _pipeName = pipeName;
        _characterService = characterService;
    }

    public void AttachSource(IGatheringLiveEventSource source)
    {
        _source = source;
        source.OnSessionStarted += (_, s) => Broadcast(new SessionStartedMessage(s.Id, s.StartLocation, s.CharacterId));
        source.OnSessionEnded += (_, s) => Broadcast(new SessionEndedMessage(s.Id));
        source.OnLocationChanged += (_, s) => Broadcast(new LocationChangedMessage(s.Id, s.CurrentLocation));
        source.OnItemAdded += (_, i) => Broadcast(new ItemAddedMessage(i.ItemId, i.Amount, i.Location));
        source.OnFameAdded += (_, f) => Broadcast(new FameAddedMessage(f.Amount, f.Location));
        source.OnSilverAdded += (_, s) => Broadcast(new SilverAddedMessage(s.Amount, s.Location));
    }

    public void DetachSource() => _source = null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken);
            _ = HandleClientAsync(pipe, cancellationToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        lock (_writersLock)
        {
            _writers.Add(writer);
        }

        try
        {
            var reader = new StreamReader(pipe);
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (LiveEventMessageSerializer.Deserialize(line) is CharacterRegistryChangedMessage)
                {
                    _characterService.NotifyCharactersChanged();
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected - fall through to cleanup below.
        }
        finally
        {
            lock (_writersLock)
            {
                _writers.Remove(writer);
            }

            pipe.Dispose();
        }
    }

    private void Broadcast(LiveEventMessage message)
    {
        var line = LiveEventMessageSerializer.Serialize(message);
        List<StreamWriter> snapshot;
        lock (_writersLock)
        {
            snapshot = new List<StreamWriter>(_writers);
        }

        foreach (var writer in snapshot)
        {
            try
            {
                writer.WriteLine(line);
            }
            catch (IOException)
            {
                // A dead client is cleaned up by its own HandleClientAsync loop noticing the
                // broken pipe on its next read - nothing to do here but skip this one write.
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventPipeServerTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.Gathering/LiveEvents/LiveEventPipeServer.cs AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventPipeServerTests.cs
git commit -m "feat(gathering): add LiveEventPipeServer"
```

---

### Task 5: `LiveEventPipeClient` - connects with a capped retry policy, exposes live events

**Files:**
- Create: `AlbionCompanion.Gathering/LiveEvents/LiveEventPipeClient.cs`
- Test: `AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventPipeClientTests.cs`

**Interfaces:**
- Consumes: `LiveEventMessageSerializer` (Task 2).
- Produces: `LiveEventPipeClient(string pipeName)` implementing `IGatheringLiveEventSource`
  (Task 3), plus:
  - `ConnectionStatus` enum: `Disconnected`, `Connecting`, `Connected`, `Exhausted`
  - `ConnectionStatus Status { get; }` and `event EventHandler? OnStatusChanged`
  - `Task StartAsync(CancellationToken)` - begins the connect loop
  - `Task RetryNowAsync()` - resets the attempt counter and connects immediately, for the
    Settings page's "start service" button (Task 11) to call after starting the Windows Service
  - `Task SendCharacterRegistryChangedAsync()` - used by `ICharacterService` write paths in the
    App (Task 10)

Real `NamedPipeClientStream` pointed at a pipe name nobody is listening on genuinely fails to
connect - no fake connector needed for the retry-cap test.

- [ ] **Step 1: Write the failing test for the retry cap**

```csharp
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventPipeClientTests
{
    [Fact]
    public async Task StartAsync_WhenNobodyIsListening_GivesUpAfterFiveAttemptsAndReportsExhausted()
    {
        var client = new LiveEventPipeClient("NobodyIsListeningOnThisPipe_" + Guid.NewGuid(), retryDelay: TimeSpan.FromMilliseconds(10));
        var statuses = new List<LiveEventPipeClient.ConnectionStatus>();
        client.OnStatusChanged += (_, _) => statuses.Add(client.Status);

        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);
        Assert.Contains(LiveEventPipeClient.ConnectionStatus.Connecting, statuses);
    }

    [Fact]
    public async Task RetryNowAsync_AfterExhaustion_MakesOneImmediateAttempt()
    {
        var client = new LiveEventPipeClient("NobodyIsListeningOnThisPipe_" + Guid.NewGuid(), retryDelay: TimeSpan.FromMilliseconds(10));
        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);

        var retryTask = client.RetryNowAsync();
        // A fresh attempt means status flips back to Connecting at least once before failing again.
        var sawConnecting = false;
        client.OnStatusChanged += (_, _) => sawConnecting |= client.Status == LiveEventPipeClient.ConnectionStatus.Connecting;
        await retryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventPipeClientTests"`
Expected: FAIL to compile - `LiveEventPipeClient` doesn't exist yet.

- [ ] **Step 3: Implement `LiveEventPipeClient`**

```csharp
using System.IO.Pipes;
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

public class LiveEventPipeClient : IGatheringLiveEventSource
{
    public enum ConnectionStatus { Disconnected, Connecting, Connected, Exhausted }

    private const int MaxAttempts = 5;
    private readonly string _pipeName;
    private readonly TimeSpan _retryDelay;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    public event EventHandler<GatheringSession>? OnSessionStarted;
    public event EventHandler<GatheringSession>? OnSessionEnded;
    public event EventHandler<GatheringSession>? OnLocationChanged;
    public event EventHandler<GatheredItem>? OnItemAdded;
    public event EventHandler<FameLog>? OnFameAdded;
    public event EventHandler<SilverLog>? OnSilverAdded;
    public event EventHandler? OnStatusChanged;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public LiveEventPipeClient(string pipeName, TimeSpan? retryDelay = null)
    {
        _pipeName = pipeName;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            SetStatus(ConnectionStatus.Connecting);
            if (await TryConnectAsync(cancellationToken))
            {
                return;
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        SetStatus(ConnectionStatus.Exhausted);
    }

    public Task RetryNowAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    public async Task SendCharacterRegistryChangedAsync()
    {
        if (_writer is null)
        {
            return;
        }

        await _writer.WriteLineAsync(LiveEventMessageSerializer.Serialize(new CharacterRegistryChangedMessage()));
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, cancellationToken);
            _pipe = pipe;
            _writer = new StreamWriter(pipe) { AutoFlush = true };
            SetStatus(ConnectionStatus.Connected);
            _ = ReadLoopAsync(pipe, cancellationToken);
            return true;
        }
        catch (Exception) when (Status != ConnectionStatus.Exhausted)
        {
            // Timeout, no listener, or the pipe was busy - treated identically as "this attempt
            // failed," the caller's loop decides whether to retry.
            return false;
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var reader = new StreamReader(pipe);
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                Dispatch(LiveEventMessageSerializer.Deserialize(line));
            }
        }
        catch (IOException)
        {
            // Server-side disconnect - fall through, mark disconnected below.
        }
        finally
        {
            SetStatus(ConnectionStatus.Disconnected);
        }
    }

    private void Dispatch(LiveEventMessage message)
    {
        switch (message)
        {
            case SessionStartedMessage m:
                OnSessionStarted?.Invoke(this, new GatheringSession { Id = m.SessionId, StartLocation = m.StartLocation, CurrentLocation = m.StartLocation, CharacterId = m.CharacterId });
                break;
            case SessionEndedMessage m:
                OnSessionEnded?.Invoke(this, new GatheringSession { Id = m.SessionId });
                break;
            case LocationChangedMessage m:
                OnLocationChanged?.Invoke(this, new GatheringSession { Id = m.SessionId, CurrentLocation = m.CurrentLocation });
                break;
            case ItemAddedMessage m:
                OnItemAdded?.Invoke(this, new GatheredItem { ItemId = m.ItemId, Amount = m.Amount, Location = m.Location });
                break;
            case FameAddedMessage m:
                OnFameAdded?.Invoke(this, new FameLog { Amount = m.Amount, Location = m.Location });
                break;
            case SilverAddedMessage m:
                OnSilverAdded?.Invoke(this, new SilverLog { Amount = m.Amount, Location = m.Location });
                break;
        }
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventPipeClientTests"`
Expected: PASS (2 tests). If the timing-sensitive `RetryNowAsync` test flakes, increase its
`WaitAsync` timeout rather than removing the assertion.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.Gathering/LiveEvents/LiveEventPipeClient.cs AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventPipeClientTests.cs
git commit -m "feat(gathering): add LiveEventPipeClient with capped retry"
```

---

### Task 6: End-to-end pipe integration test (Server + Client together)

**Files:**
- Test: `AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventPipeIntegrationTests.cs`

**Interfaces:**
- Consumes: `LiveEventPipeServer` (Task 4), `LiveEventPipeClient` (Task 5).

Tasks 4 and 5 each tested their own side against a raw pipe stream. This task proves they
correctly talk to *each other*, catching any protocol mismatch before Task 12 wires them into two
separate real processes (where a bug would be much harder to diagnose).

- [ ] **Step 1: Write the failing test**

```csharp
using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventPipeIntegrationTests
{
    private sealed class FakeCharacterService : ICharacterService
    {
        public int NotifyCount { get; private set; }
        public event EventHandler? CharactersChanged;
        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Array.Empty<Character>());
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
        public void NotifyCharactersChanged() { NotifyCount++; CharactersChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeEventSource : IGatheringLiveEventSource
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<GatheringSession>? OnLocationChanged;
        public event EventHandler<GatheredItem>? OnItemAdded;
        public event EventHandler<FameLog>? OnFameAdded;
        public event EventHandler<SilverLog>? OnSilverAdded;

        public void RaiseItemAdded(GatheredItem item) => OnItemAdded?.Invoke(this, item);
    }

    [Fact]
    public async Task ItemAddedOnServerSide_ArrivesOnClientSide()
    {
        var pipeName = "IntegrationTestPipe_" + Guid.NewGuid();
        var server = new LiveEventPipeServer(pipeName, new FakeCharacterService());
        var source = new FakeEventSource();
        server.AttachSource(source);
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10));
        GatheredItem? received = null;
        var tcs = new TaskCompletionSource();
        client.OnItemAdded += (_, item) => { received = item; tcs.TrySetResult(); };
        await client.StartAsync(cts.Token);
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Connected, client.Status);

        source.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5, Location = "Martlock" });
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Equal("T4_ORE", received!.ItemId);
        Assert.Equal(5, received.Amount);

        cts.Cancel();
    }

    [Fact]
    public async Task CharacterRegistryChangedFromClient_ReachesServersCharacterService()
    {
        var pipeName = "IntegrationTestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10));
        await client.StartAsync(cts.Token);
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Connected, client.Status);

        await client.SendCharacterRegistryChangedAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (characterService.NotifyCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, characterService.NotifyCount);

        cts.Cancel();
    }
}
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~LiveEventPipeIntegrationTests"`
Expected: given Tasks 4 and 5 are already implemented, this should PASS immediately - it exists to
*prove* that, not to drive new code. If it fails, that's a real protocol mismatch between the
server and client implementations from Tasks 4/5 - fix whichever side is wrong before continuing.

- [ ] **Step 3: Commit**

```bash
git add AlbionCompanion.Gathering.Tests/LiveEvents/LiveEventPipeIntegrationTests.cs
git commit -m "test(gathering): add end-to-end LiveEventPipeServer/Client integration test"
```

---

### Task 7: `IGameProcessWatcher` - detects whether Albion Online is running

**Files:**
- Create: `AlbionCompanion.Gathering/IGameProcessWatcher.cs`
- Create: `AlbionCompanion.Gathering/GameProcessWatcher.cs`
- Test: `AlbionCompanion.Gathering.Tests/GameProcessWatcherTests.cs`

**Interfaces:**
- Produces: `IGameProcessWatcher.IsGameRunning() : bool`. Real implementation checks
  `Process.GetProcessesByName("Albion-Online")` and `Process.GetProcessesByName("Albion-Online_BE")`.

Only the interface's contract (used by `Worker`, Task 12) is unit-tested via a fake; the real
`Process.GetProcessesByName`-based implementation is verified manually (per the spec) by actually
starting/stopping the game.

- [ ] **Step 1: Write the failing test for the interface shape**

This test exists to lock in the exact method name/signature `Worker` will depend on - not to
exercise the real process-scanning logic.

```csharp
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class GameProcessWatcherTests
{
    private sealed class FakeGameProcessWatcher : IGameProcessWatcher
    {
        public bool Running { get; set; }
        public bool IsGameRunning() => Running;
    }

    [Fact]
    public void FakeWatcher_ReflectsRunningFlag()
    {
        var watcher = new FakeGameProcessWatcher { Running = true };

        Assert.True(watcher.IsGameRunning());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~GameProcessWatcherTests"`
Expected: FAIL to compile - `IGameProcessWatcher` doesn't exist yet.

- [ ] **Step 3: Implement the interface and real implementation**

```csharp
namespace AlbionCompanion.Gathering;

public interface IGameProcessWatcher
{
    bool IsGameRunning();
}
```

```csharp
using System.Diagnostics;

namespace AlbionCompanion.Gathering;

// Confirmed on this machine 2026-08-04: Albion Online runs as two processes, "Albion-Online.exe"
// (the game client) and "Albion-Online_BE.exe" (a helper/backend process) - watch for either so a
// launch sequence that starts them in either order still counts as "the game is running."
public class GameProcessWatcher : IGameProcessWatcher
{
    private static readonly string[] ProcessNames = { "Albion-Online", "Albion-Online_BE" };

    public bool IsGameRunning() =>
        ProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~GameProcessWatcherTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.Gathering/IGameProcessWatcher.cs AlbionCompanion.Gathering/GameProcessWatcher.cs AlbionCompanion.Gathering.Tests/GameProcessWatcherTests.cs
git commit -m "feat(gathering): add IGameProcessWatcher"
```

---

### Task 8: WAL mode for shared SQLite access

**Files:**
- Modify: `AlbionCompanion.Gathering/AppHostBuilder.cs`

**Interfaces:**
- No new public members - this changes `RunStartupSequenceAsync`'s existing migration step.

- [ ] **Step 1: Add the PRAGMA to the existing migration scope**

In `AppHostBuilder.RunStartupSequenceAsync`, inside the existing
`using (var migrationScope = provider.CreateScope())` block, right after
`await dbContext.Database.MigrateAsync();`, add:

```csharp
            // Two OS processes (AlbionCompanion.Service and AlbionCompanion.App) now share this
            // one SQLite file - WAL allows one writer plus many concurrent readers without
            // SQLITE_BUSY/"database is locked", which the default rollback-journal mode doesn't.
            await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build AlbionCompanion.Gathering`
Expected: builds clean.

- [ ] **Step 3: Run the full Gathering test suite**

Run: `dotnet test AlbionCompanion.Gathering.Tests`
Expected: all pass (in-memory SQLite tests are unaffected by WAL mode, which only matters for
real on-disk files with concurrent processes).

- [ ] **Step 4: Commit**

```bash
git add AlbionCompanion.Gathering/AppHostBuilder.cs
git commit -m "feat(gathering): enable SQLite WAL mode for cross-process access"
```

---

### Task 9: `GatheringLiveState` - attach to either a live session service or a piped source

**Files:**
- Modify: `AlbionCompanion.Gathering/IGatheringLiveState.cs`
- Modify: `AlbionCompanion.Gathering/GatheringLiveState.cs`
- Test: `AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs` (check if it exists first)

**Interfaces:**
- Consumes: `IGatheringLiveEventSource` (Task 3).
- Produces: `IGatheringLiveState.Attach(IGatheringSessionService sessionService,
  IGatheringLiveEventSource eventSource)` - the rehydration snapshot still comes from
  `sessionService` (a direct DB read, unaffected by which process is running); the six live events
  are now wired from the separately-passed `eventSource` instead of always being the same object as
  `sessionService`.

Today's only call site (`App.xaml.cs`) passes the *same* object for both parameters currently -
after Task 10 rewrites `App.xaml.cs`, it will pass `sessionService` (App's own DB-reading instance)
and a `LiveEventPipeClient` (the piped source) separately.

- [ ] **Step 1: Check for an existing `GatheringLiveStateTests.cs` and read it if present**

Run: `ls AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs 2>/dev/null || echo "does not exist"`

- [ ] **Step 2: Write the failing test**

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering.LiveEvents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class GatheringLiveStateTests
{
    private sealed class FakeCharacterService : ICharacterService
    {
        public event EventHandler? CharactersChanged;
        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Array.Empty<Character>());
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
        public void NotifyCharactersChanged() => CharactersChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeLocalPlayerTracker : ILocalPlayerTracker
    {
        public int? CurrentEntityId { get; set; }
        public string? CurrentCharacterName { get; set; }
        public event EventHandler<Exception>? OnError;
    }

    private sealed class FakeEventSource : IGatheringLiveEventSource
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<GatheringSession>? OnLocationChanged;
        public event EventHandler<GatheredItem>? OnItemAdded;
        public event EventHandler<FameLog>? OnFameAdded;
        public event EventHandler<SilverLog>? OnSilverAdded;

        public void RaiseSessionStarted(GatheringSession session) => OnSessionStarted?.Invoke(this, session);
    }

    [Fact]
    public async Task Attach_WiresLiveEventsFromTheGivenEventSource_NotFromSessionService()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var sessionService = new GatheringSessionService(context, new FakeLocalPlayerTracker(), new FakeCharacterService());
        var eventSource = new FakeEventSource();
        var liveState = new GatheringLiveState();

        await liveState.Attach(sessionService, eventSource);
        var session = new GatheringSession { StartLocation = "Martlock", CharacterId = Guid.NewGuid() };
        eventSource.RaiseSessionStarted(session);

        Assert.True(liveState.IsActive);
        Assert.Equal("Martlock", liveState.StartLocation);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~GatheringLiveStateTests"`
Expected: FAIL to compile - `Attach` doesn't take two parameters yet.

- [ ] **Step 4: Update the interface and implementation**

In `IGatheringLiveState.cs`, change:

```csharp
    Task Attach(IGatheringSessionService sessionService);
```

to:

```csharp
    Task Attach(IGatheringSessionService sessionService, LiveEvents.IGatheringLiveEventSource eventSource);
```

In `GatheringLiveState.cs`, change the method signature to
`Attach(IGatheringSessionService sessionService, IGatheringLiveEventSource eventSource)` and
replace every `sessionService.On...` subscription in the body with the identical subscription on
`eventSource` instead (the rehydration snapshot call, `sessionService.GetActiveSessionSnapshotAsync()`,
stays on `sessionService` - only the six event-subscription lines move to `eventSource`). Add
`using AlbionCompanion.Gathering.LiveEvents;` at the top of the file.

- [ ] **Step 5: Update the one existing call site**

`App.xaml.cs`'s `await liveState.Attach(sessionService);` will be rewritten in Task 10 as part of
that task's larger changes - no action needed here, but note it will not compile until Task 10
lands. That's expected for this task in isolation; if running this task standalone rather than as
part of the full plan, temporarily change the call site to
`liveState.Attach(sessionService, sessionService)` (passing the same object twice) purely to keep
the App project compiling until Task 10 replaces it properly.

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~GatheringLiveStateTests"`
Expected: PASS.

- [ ] **Step 7: Run the full Gathering test suite**

Run: `dotnet test AlbionCompanion.Gathering.Tests`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add AlbionCompanion.Gathering/IGatheringLiveState.cs AlbionCompanion.Gathering/GatheringLiveState.cs AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs
git commit -m "refactor(gathering): GatheringLiveState.Attach takes a separate live event source"
```

---

### Task 10: `IServiceStatusProvider` - wraps `ServiceController` for the Settings page

**Files:**
- Create: `AlbionCompanion.Gathering/IServiceStatusProvider.cs`
- Create: `AlbionCompanion.Gathering/WindowsServiceStatusProvider.cs`
- Test: `AlbionCompanion.Gathering.Tests/ServiceStatusProviderTests.cs` (tests the interface
  contract via a fake, not the real `ServiceController`-based implementation)

**Interfaces:**
- Produces: `ServiceStatus` enum (`Running`, `Stopped`, `NotInstalled`), `IServiceStatusProvider
  .GetStatusAsync() : Task<ServiceStatus>`, `IServiceStatusProvider.StartAsync() : Task`.

- [ ] **Step 1: Write the failing test for the interface shape**

```csharp
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ServiceStatusProviderTests
{
    private sealed class FakeServiceStatusProvider : IServiceStatusProvider
    {
        public ServiceStatus Status { get; set; } = ServiceStatus.Stopped;
        public int StartCallCount { get; private set; }
        public Task<ServiceStatus> GetStatusAsync() => Task.FromResult(Status);
        public Task StartAsync() { StartCallCount++; Status = ServiceStatus.Running; return Task.CompletedTask; }
    }

    [Fact]
    public async Task StartAsync_TransitionsStoppedToRunning()
    {
        var provider = new FakeServiceStatusProvider();

        await provider.StartAsync();

        Assert.Equal(ServiceStatus.Running, await provider.GetStatusAsync());
        Assert.Equal(1, provider.StartCallCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~ServiceStatusProviderTests"`
Expected: FAIL to compile - none of these types exist yet.

- [ ] **Step 3: Implement the interface, enum, and real implementation**

```csharp
namespace AlbionCompanion.Gathering;

public enum ServiceStatus { Running, Stopped, NotInstalled }

public interface IServiceStatusProvider
{
    Task<ServiceStatus> GetStatusAsync();
    Task StartAsync();
}
```

```csharp
using System.ServiceProcess;

namespace AlbionCompanion.Gathering;

// Wraps ServiceController for the one Windows Service this app cares about. The installer (a
// separate console app) grants the interactive user START/STOP rights on this specific service
// via `sc sdset`, so StartAsync below never triggers a UAC prompt when run from the App.
public class WindowsServiceStatusProvider : IServiceStatusProvider
{
    private const string ServiceName = "AlbionCompanionService";

    public Task<ServiceStatus> GetStatusAsync()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            var status = controller.Status switch
            {
                System.ServiceProcess.ServiceControllerStatus.Running => ServiceStatus.Running,
                _ => ServiceStatus.Stopped,
            };
            return Task.FromResult(status);
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws this when the named service isn't registered at all.
            return Task.FromResult(ServiceStatus.NotInstalled);
        }
    }

    public Task StartAsync()
    {
        using var controller = new ServiceController(ServiceName);
        controller.Start();
        return controller.WaitForStatusAsync(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
    }
}
```

Note: `ServiceController` doesn't natively expose `WaitForStatusAsync` - use its synchronous
`WaitForStatus(status, timeout)` wrapped in `Task.Run` if targeting an older TFM lacks the async
overload; confirm which is available for this project's `net10.0-windows` target before finalizing
this method's body.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "FullyQualifiedName~ServiceStatusProviderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.Gathering/IServiceStatusProvider.cs AlbionCompanion.Gathering/WindowsServiceStatusProvider.cs AlbionCompanion.Gathering.Tests/ServiceStatusProviderTests.cs
git commit -m "feat(gathering): add IServiceStatusProvider wrapping ServiceController"
```

---

### Task 11: `AlbionCompanion.Service` project - Worker hosts the gathering pipeline, gated by game presence

**Files:**
- Create: `AlbionCompanion.Service/AlbionCompanion.Service.csproj`
- Create: `AlbionCompanion.Service/Program.cs`
- Create: `AlbionCompanion.Service/Worker.cs`
- Modify: `AlbionCompanion.Companion.sln` (or whatever the solution file is named - confirm with
  `ls *.sln`)

**Interfaces:**
- Consumes: `AppHostBuilder` (existing), `LiveEventPipeServer` (Task 4), `IGameProcessWatcher`
  (Task 7).

This task is OS-process scaffolding, not unit-testable logic on its own (it depends on real
Windows Service hosting and a real game process) - verified manually per the spec. Every step
still has concrete, real content; there's just no failing-test-first cycle for "create a new
Worker Service project."

- [ ] **Step 1: Scaffold the project**

```bash
dotnet new worker -n AlbionCompanion.Service -o AlbionCompanion.Service
dotnet add AlbionCompanion.Service package Microsoft.Extensions.Hosting.WindowsServices
dotnet add AlbionCompanion.Service reference AlbionCompanion.Core AlbionCompanion.Sniffer AlbionCompanion.Gathering
dotnet sln add AlbionCompanion.Service
```

Edit the generated `.csproj`'s `<TargetFramework>` to `net10.0-windows` (matching every other
project in this solution - `Process.GetProcessesByName` and `ServiceController` are Windows-only,
and this whole app already targets Windows exclusively).

- [ ] **Step 2: Write `Program.cs`**

```csharp
using AlbionCompanion.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "AlbionCompanionService");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
```

- [ ] **Step 3: Write `Worker.cs`**

```csharp
using AlbionCompanion.Gathering;
using AlbionCompanion.Gathering.LiveEvents;
using AlbionCompanion.Sniffer.PacketCapture;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.Service;

// Registered as always-Running/Automatic in the SCM, but internally idles between polls when
// Albion Online isn't running - see docs/superpowers/specs/2026-08-04-background-sniffer-service-design.md's
// "Process gating" section. GameCheckInterval matches that spec's 10-15s guidance.
public class Worker : BackgroundService
{
    private static readonly TimeSpan GameCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly string ProgramDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");

    private readonly IGameProcessWatcher _gameProcessWatcher = new GameProcessWatcher();
    private readonly LiveEventPipeServer _pipeServer;
    private ServiceProvider? _pipelineProvider;
    private IServiceScope? _pipelineScope;
    private bool _pipelineRunning;

    public Worker()
    {
        Directory.CreateDirectory(ProgramDataPath);
        // The pipe server's own ICharacterService instance is separate from the gathering
        // pipeline's - it only needs write-free read access for NotifyCharactersChanged's
        // side-effect-free re-raise, and must stay alive across pipeline start/stop cycles
        // (unlike the pipeline's own scoped services), so it gets a small dedicated provider.
        var statusProvider = AppHostBuilder.BuildServiceProvider(ProgramDataPath);
        _pipeServer = new LiveEventPipeServer("AlbionCompanionLiveEvents", statusProvider.GetRequiredService<ICharacterService>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _pipeServer.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var gameRunning = _gameProcessWatcher.IsGameRunning();

            if (gameRunning && !_pipelineRunning)
            {
                await StartPipelineAsync();
            }
            else if (!gameRunning && _pipelineRunning)
            {
                StopPipeline();
            }

            await Task.Delay(GameCheckInterval, stoppingToken);
        }

        if (_pipelineRunning)
        {
            StopPipeline();
        }
    }

    private async Task StartPipelineAsync()
    {
        _pipelineProvider = AppHostBuilder.BuildServiceProvider(ProgramDataPath);
        _pipelineScope = await AppHostBuilder.RunStartupSequenceAsync(_pipelineProvider);
        var sessionService = _pipelineScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
        _pipeServer.AttachSource(sessionService);
        _pipelineRunning = true;
    }

    private void StopPipeline()
    {
        _pipeServer.DetachSource();
        _pipelineProvider?.GetRequiredService<IPacketSniffer>().Stop();
        _pipelineScope?.Dispose();
        _pipelineProvider?.Dispose();
        _pipelineProvider = null;
        _pipelineScope = null;
        _pipelineRunning = false;
    }
}
```

- [ ] **Step 4: Build the new project**

Run: `dotnet build AlbionCompanion.Service`
Expected: builds clean.

- [ ] **Step 5: Manual verification (per spec - not unit-testable)**

Run the service in console mode (Worker Services run as a normal console app when not launched by
the SCM):

```bash
dotnet run --project AlbionCompanion.Service
```

With Albion Online closed, confirm no `debug_packets.log` appears under
`%ProgramData%\AlbionCompanion`. Launch Albion Online, wait up to 15s, confirm the log file
appears and grows. Close Albion Online, wait up to 15s, confirm the log stops growing (the
pipeline tore down) while the process itself keeps running (Ctrl+C to stop the console run).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Service/ AlbionCompanion.Companion.sln
git commit -m "feat: add AlbionCompanion.Service, gated on Albion Online's process presence"
```

---

### Task 12: `AlbionCompanion.App` - become a thin client (remove the in-process sniffer)

**Files:**
- Create: `AlbionCompanion.Gathering/AppClientHostBuilder.cs`
- Modify: `AlbionCompanion.App/MauiProgram.cs`
- Modify: `AlbionCompanion.App/App.xaml.cs`
- Modify: `AlbionCompanion.App/AlbionCompanion.App.csproj` (remove the `AlbionCompanion.Sniffer`
  project reference if present directly, not just transitively via `AlbionCompanion.Gathering`)

**Interfaces:**
- Consumes: `LiveEventPipeClient` (Task 5), `IGatheringLiveState.Attach` new signature (Task 9).
- Produces: `AppClientHostBuilder.BuildServiceProvider(string programDataPath) : ServiceProvider`
  registering only the read/write-DB services the App needs (no sniffer).

This task has no new business logic to TDD (it's wiring) - verified by building the App and
manually confirming it still shows history/characters, plus the live-view behavior described in
Task 11's manual verification section (now from the App's side).

- [ ] **Step 1: Write `AppClientHostBuilder`**

```csharp
using AlbionCompanion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.Gathering;

// AlbionCompanion.App's DI wiring - unlike AppHostBuilder (used by AlbionCompanion.Service), this
// never touches IPacketSniffer/AlbionPhotonParser/ZoneTracker/GatheringEventRouter/
// ILocalPlayerTracker/IRawEventRecorder. The App only reads/writes the shared database and talks
// to the Service over LiveEventPipeClient.
public static class AppClientHostBuilder
{
    public static ServiceProvider BuildServiceProvider(string programDataPath)
    {
        var dbPath = Path.Combine(programDataPath, "albion.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<ICharacterService, CharacterService>();
        services.AddScoped<IGatheringSessionService, GatheringSessionService>();
        services.AddSingleton<IItemDictionaryService, ItemDictionaryService>();
        services.AddSingleton<IServiceStatusProvider, WindowsServiceStatusProvider>();
        services.AddSingleton(_ => new LiveEvents.LiveEventPipeClient("AlbionCompanionLiveEvents"));

        // GatheringSessionService needs an ILocalPlayerTracker to satisfy its constructor even
        // though the App never starts a session itself - a no-op stand-in is enough since
        // StartSessionAsync is never called from this process.
        services.AddSingleton<ILocalPlayerTracker, NullLocalPlayerTracker>();

        return services.BuildServiceProvider();
    }
}

// See AppClientHostBuilder's comment - the App reads GatheringSessionService but never starts
// sessions with it, so this satisfies the constructor dependency without a real Photon connection.
internal class NullLocalPlayerTracker : ILocalPlayerTracker
{
    public int? CurrentEntityId => null;
    public string? CurrentCharacterName => null;
    public event EventHandler<Exception>? OnError { add { } remove { } }
}
```

- [ ] **Step 2: Rewrite `MauiProgram.cs`**

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Gathering;
using ApexCharts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlbionCompanion.App;

public static class MauiProgram
{
    public static ServiceProvider? GatheringProvider { get; private set; }
    public static IServiceScope? GatheringSessionScope { get; set; }
    public static IServiceProvider? Services { get; private set; }
    public static string? ProgramDataPath { get; private set; }

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddApexCharts();
        builder.Services.AddSingleton<IGatheringLiveState, GatheringLiveState>();
        builder.Services.AddSingleton<ISessionHistoryService>(_ =>
            new SessionHistoryService(GatheringProvider!.GetRequiredService<IDbContextFactory<AppDbContext>>()));
        builder.Services.AddSingleton<IItemDictionaryService>(_ =>
            GatheringProvider!.GetRequiredService<IItemDictionaryService>());
        builder.Services.AddSingleton<ICharacterService>(_ =>
            GatheringProvider!.GetRequiredService<ICharacterService>());
        builder.Services.AddSingleton<IServiceStatusProvider>(_ =>
            GatheringProvider!.GetRequiredService<IServiceStatusProvider>());
        builder.Services.AddSingleton(_ =>
            GatheringProvider!.GetRequiredService<AlbionCompanion.Gathering.LiveEvents.LiveEventPipeClient>());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        ProgramDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");
        Directory.CreateDirectory(ProgramDataPath);
        GatheringProvider = AppClientHostBuilder.BuildServiceProvider(ProgramDataPath);

        var app = builder.Build();
        Services = app.Services;
        return app;
    }
}
```

(`AppDataPath` is renamed to `ProgramDataPath` here to match the relocated storage - check for any
other reference to `MauiProgram.AppDataPath` elsewhere in the App project, e.g.
`App.xaml.cs`'s failure-log path, and rename those call sites too.)

- [ ] **Step 3: Rewrite `App.xaml.cs`**

```csharp
using AlbionCompanion.Gathering;
using AlbionCompanion.Gathering.LiveEvents;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "AlbionCompanion" };
        var startupTask = ConnectAsync();

        window.Destroying += async (_, _) =>
        {
            await startupTask;
            MauiProgram.GatheringSessionScope?.Dispose();
        };

        return window;
    }

    private static async Task ConnectAsync()
    {
        if (MauiProgram.GatheringProvider is null)
        {
            return;
        }

        try
        {
            var sessionScope = MauiProgram.GatheringProvider.CreateScope();
            MauiProgram.GatheringSessionScope = sessionScope;
            var sessionService = sessionScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
            var pipeClient = MauiProgram.GatheringProvider.GetRequiredService<LiveEventPipeClient>();

            if (MauiProgram.Services?.GetRequiredService<IGatheringLiveState>() is { } liveState)
            {
                await liveState.Attach(sessionService, pipeClient);
            }

            _ = pipeClient.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (MauiProgram.ProgramDataPath is not null)
            {
                var logPath = Path.Combine(MauiProgram.ProgramDataPath, "debug_maui_startup_failures.log");
                await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}
```

- [ ] **Step 4: Wire `CharacterService`'s write paths to notify the pipe**

The App's `ICharacterService` (`CharacterService`, resolved via `MauiProgram.GatheringProvider`)
still runs `AddAsync`/`DeleteAsync`/`RenameAsync` directly against the shared DB. Wherever the
Character hub UI (`CharacterHub.razor` or similar) calls these methods, follow each successful call
with:

```csharp
await ServiceProvider.GetRequiredService<LiveEventPipeClient>().SendCharacterRegistryChangedAsync();
```

(Inject `LiveEventPipeClient` into whichever component/service currently calls
`ICharacterService.AddAsync`/etc. - find these call sites with
`grep -rn "characterService.AddAsync\|characterService.DeleteAsync\|characterService.RenameAsync" AlbionCompanion.App` before editing.)

- [ ] **Step 5: Remove the now-unused direct `AlbionCompanion.Sniffer` reference from the App project**

Run: `grep -n "AlbionCompanion.Sniffer" AlbionCompanion.App/AlbionCompanion.App.csproj`

If present, remove that `<ProjectReference>` line - the App no longer calls into `IPacketSniffer`
directly (it did in the old `App.xaml.cs`'s `window.Destroying` handler, now removed in Step 3).

- [ ] **Step 6: Build the App project**

Run: `dotnet build AlbionCompanion.App`
Expected: builds clean. Fix any remaining reference to the old `AppHostBuilder`/`AppDataPath`
names this task didn't already catch.

- [ ] **Step 7: Manual verification**

With `AlbionCompanion.Service` running (Task 11's console-mode run is fine for this), launch the
App. Confirm Sessions/CharacterHub still show existing history. Confirm the live-view banner shows
"Connected" once the pipe connects (Task 13 adds the visible banner/Settings page - for now,
confirming `LiveEventPipeClient.Status` reaches `Connected` via a debugger/log line is enough).

- [ ] **Step 8: Commit**

```bash
git add AlbionCompanion.Gathering/AppClientHostBuilder.cs AlbionCompanion.App/
git commit -m "refactor(app): remove in-process sniffer, become a LiveEventPipeClient reader"
```

---

### Task 13: `Settings.razor` - service status, manual start, connection banner

**Files:**
- Create: `AlbionCompanion.App/Components/Pages/Settings.razor`
- Modify: `AlbionCompanion.App/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `IServiceStatusProvider` (Task 10), `LiveEventPipeClient` (Task 5).

UI-only task - no new business logic to unit test (the logic being displayed was already tested in
Tasks 5 and 10). Verified by running the App and clicking through the states, per the "For UI or
frontend changes" rule in this project's standing instructions.

- [ ] **Step 1: Add the nav link**

In `NavMenu.razor`, add after the existing `Sessions` `NavLink` and before the theme toggle
button:

```razor
<NavLink class="ac-navlink" href="settings">
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" /></svg>
    Settings
</NavLink>
```

- [ ] **Step 2: Write `Settings.razor`**

```razor
@page "/settings"
@using AlbionCompanion.Gathering
@using AlbionCompanion.Gathering.LiveEvents
@inject IServiceStatusProvider ServiceStatusProvider
@inject LiveEventPipeClient PipeClient
@implements IDisposable

<h1>Settings</h1>

<div class="ac-card">
    <h2>Sniffer service</h2>
    <p>Status: <strong>@_serviceStatus</strong></p>
    @if (_serviceStatus == ServiceStatus.Stopped)
    {
        <button class="ac-button" @onclick="StartServiceAsync">Uruchom serwis</button>
    }
    else if (_serviceStatus == ServiceStatus.NotInstalled)
    {
        <p>Serwis nie jest zainstalowany - uruchom installer.exe.</p>
    }

    <h2>Połączenie live</h2>
    <p>Status: <strong>@PipeClient.Status</strong></p>
</div>

@code {
    private ServiceStatus _serviceStatus = ServiceStatus.Stopped;
    private Timer? _pollTimer;

    protected override async Task OnInitializedAsync()
    {
        await RefreshStatusAsync();
        _pollTimer = new Timer(async _ => await InvokeAsync(RefreshStatusAsync), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        PipeClient.OnStatusChanged += OnPipeStatusChanged;
    }

    private async Task RefreshStatusAsync()
    {
        _serviceStatus = await ServiceStatusProvider.GetStatusAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task StartServiceAsync()
    {
        await ServiceStatusProvider.StartAsync();
        await RefreshStatusAsync();
        _ = PipeClient.RetryNowAsync();
    }

    private void OnPipeStatusChanged(object? sender, EventArgs e) => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        _pollTimer?.Dispose();
        PipeClient.OnStatusChanged -= OnPipeStatusChanged;
    }
}
```

- [ ] **Step 3: Build the App project**

Run: `dotnet build AlbionCompanion.App`
Expected: builds clean.

- [ ] **Step 4: Manual verification**

Run the App with the Service stopped - confirm Settings shows "Stopped" and a working "Uruchom
serwis" button with no UAC prompt (requires Task 14's installer to have already granted the ACL -
if run before Task 14, a UAC prompt or an access-denied exception is expected and not a bug in
this task). With the Service running, confirm status flips to "Running" and the button disappears.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.App/Components/Pages/Settings.razor AlbionCompanion.App/Components/Layout/NavMenu.razor
git commit -m "feat(app): add Settings page with sniffer service status and manual start"
```

---

### Task 14: Connection banner on Home/Broadcast

**Files:**
- Modify: `AlbionCompanion.App/Components/Pages/Home.razor` (or wherever the live session card
  lives - confirm exact filename with `ls AlbionCompanion.App/Components/Pages/`)
- Modify: `AlbionCompanion.App/Components/Pages/Broadcast.razor`

**Interfaces:**
- Consumes: `LiveEventPipeClient.Status`/`OnStatusChanged` (Task 5).

- [ ] **Step 1: Add the banner markup and code to each page**

In both `Home.razor` and `Broadcast.razor`, inject `LiveEventPipeClient` and add near the top of
the page markup:

```razor
@if (PipeClient.Status == LiveEventPipeClient.ConnectionStatus.Exhausted)
{
    <div class="ac-banner ac-banner-warning">
        Brak połączenia z serwisem nasłuchującym. <a href="/settings">Sprawdź Ustawienia</a>.
    </div>
}
else if (PipeClient.Status != LiveEventPipeClient.ConnectionStatus.Connected)
{
    <div class="ac-banner ac-banner-info">Łączenie z serwisem...</div>
}
```

Add to each page's `@code` block:

```csharp
protected override void OnInitialized()
{
    PipeClient.OnStatusChanged += (_, _) => InvokeAsync(StateHasChanged);
}
```

(If either page already has an `OnInitialized`/`OnInitializedAsync` override, add the
subscription line to the existing method instead of adding a second override.)

- [ ] **Step 2: Add banner styles**

In `AlbionCompanion.App/wwwroot/app.css`, add (matching the existing `.ac-card`/`.ac-navlink`
token-based style):

```css
.ac-banner {
    padding: var(--ac-space-3) var(--ac-space-4);
    border-radius: var(--ac-radius-md);
    margin-bottom: var(--ac-space-4);
}

.ac-banner-warning {
    background: color-mix(in srgb, #eab308 20%, transparent);
    color: var(--ac-text);
}

.ac-banner-info {
    background: color-mix(in srgb, var(--ac-text) 8%, transparent);
    color: var(--ac-text-muted);
}
```

- [ ] **Step 3: Build the App project**

Run: `dotnet build AlbionCompanion.App`
Expected: builds clean.

- [ ] **Step 4: Manual verification**

With the Service stopped, launch the App, wait ~15s (5 retries x 3s) - confirm the warning banner
appears on Home and Broadcast. Start the Service via Settings - confirm the banner disappears
within a few seconds on both pages without needing to navigate away and back.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.App/Components/Pages/Home.razor AlbionCompanion.App/Components/Pages/Broadcast.razor AlbionCompanion.App/wwwroot/app.css
git commit -m "feat(app): show a connection banner when the sniffer pipe is disconnected"
```

---

### Task 15: `AlbionCompanion.ServiceInstaller` - one-click install

**Files:**
- Create: `AlbionCompanion.ServiceInstaller/AlbionCompanion.ServiceInstaller.csproj`
- Create: `AlbionCompanion.ServiceInstaller/Program.cs`

**Interfaces:**
- None consumed from other tasks' code - this is a standalone console app that operates on
  published binaries and OS state.

Entirely OS-boundary work (file copy, `sc.exe`, service start) - verified manually per the spec,
same treatment as `NpcapInstaller` gets today. Every step still has real, runnable content.

- [ ] **Step 1: Scaffold the project**

```bash
dotnet new console -n AlbionCompanion.ServiceInstaller -o AlbionCompanion.ServiceInstaller
dotnet sln add AlbionCompanion.ServiceInstaller
```

Set `<TargetFramework>net10.0-windows</TargetFramework>` and add
`<UseWindowsForms>false</UseWindowsForms>` is unnecessary; just ensure the TFM matches the rest of
the solution.

- [ ] **Step 2: Write `Program.cs`**

```csharp
using System.Diagnostics;

var programDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");
var serviceInstallPath = Path.Combine(programDataPath, "service");
Directory.CreateDirectory(serviceInstallPath);

Console.WriteLine("Installing AlbionCompanion sniffer service...");

// Step 1: migrate the old per-user database/logs to the shared location, if not already done.
var oldAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
var oldDbPath = Path.Combine(oldAppDataPath, "albion.db");
var newDbPath = Path.Combine(programDataPath, "albion.db");
if (File.Exists(oldDbPath) && !File.Exists(newDbPath))
{
    Console.WriteLine($"Migrating existing database from {oldDbPath} to {newDbPath}...");
    File.Copy(oldDbPath, newDbPath);
}

// Step 2: copy the published Service binaries (this installer expects to be run from the same
// folder as a `dotnet publish` output of AlbionCompanion.Service, or that output copied alongside
// this exe under a "service-publish" subfolder).
var sourcePublishPath = Path.Combine(AppContext.BaseDirectory, "service-publish");
if (!Directory.Exists(sourcePublishPath))
{
    Console.WriteLine($"ERROR: expected published service output at {sourcePublishPath} - run `dotnet publish AlbionCompanion.Service -o <this exe's folder>/service-publish` first.");
    return 1;
}

foreach (var file in Directory.GetFiles(sourcePublishPath, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(sourcePublishPath, file);
    var destination = Path.Combine(serviceInstallPath, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(file, destination, overwrite: true);
}

var serviceExePath = Path.Combine(serviceInstallPath, "AlbionCompanion.Service.exe");

// Step 3: register the service (idempotent - delete first if it already exists, e.g. a reinstall).
RunScAndWait($"delete AlbionCompanionService");
RunScAndWait($"create AlbionCompanionService binPath= \"{serviceExePath}\" start= auto");

// Step 4: grant the current interactive user START/STOP rights, so the App's Settings page never
// needs a UAC prompt to start the service.
var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
RunScAndWait($"sdset AlbionCompanionService D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWRPWPDCLOCRRC;;;{GetCurrentUserSid()})(A;;CCLCSWLOCRRC;;;SU)");

RunScAndWait("start AlbionCompanionService");

Console.WriteLine("Done.");
return 0;

static string GetCurrentUserSid()
{
    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return identity.User!.Value;
}

static void RunScAndWait(string arguments)
{
    var process = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true })!;
    Console.WriteLine(process.StandardOutput.ReadToEnd());
    process.WaitForExit();
}
```

Note: the exact `sdset` SDDL string above grants `SY` (LocalSystem), `BA` (Administrators), and the
current user's SID `CCLCSWRPWPDCLOCRRC` (start/stop/query/change-config rights) - verify this
string against `sc sdshow` output on a freshly-created service before relying on it, and adjust the
ACE list if `sc.exe`'s SDDL parser rejects it (SDDL syntax is notoriously easy to get subtly wrong;
test this specific step manually rather than trusting it blind).

- [ ] **Step 3: Build the installer**

Run: `dotnet build AlbionCompanion.ServiceInstaller`
Expected: builds clean.

- [ ] **Step 4: Manual verification (must be run as Administrator)**

```bash
dotnet publish AlbionCompanion.Service -o AlbionCompanion.ServiceInstaller/bin/Debug/net10.0-windows/service-publish
dotnet run --project AlbionCompanion.ServiceInstaller
```

Confirm via `services.msc` or `sc query AlbionCompanionService` that the service exists, is
`Automatic`, and is `Running`. Confirm `%ProgramData%\AlbionCompanion\albion.db` exists and (if a
prior `%APPDATA%` database existed) has the migrated history. Log out and back in (or reboot) to
confirm it starts automatically without the App or installer running.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.ServiceInstaller/ AlbionCompanion.Companion.sln
git commit -m "feat: add AlbionCompanion.ServiceInstaller for one-click service registration"
```

---

### Task 16: Full-solution build and regression pass

**Files:** none (verification-only task)

- [ ] **Step 1: Confirm both new projects are in the solution**

Run: `dotnet sln list`
Expected: includes `AlbionCompanion.Service` and `AlbionCompanion.ServiceInstaller` alongside every
pre-existing project.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: 0 errors. (Close `AlbionCompanion.App.exe`/`AlbionCompanion.Service.exe` first if either
is running, or the build will fail on a file-lock error unrelated to the code itself.)

- [ ] **Step 3: Run every test project**

Run: `dotnet test`
Expected: every test passes, including every test added across Tasks 1-15.

- [ ] **Step 4: Full manual scenario - the original bug**

With the Service installed and running (Task 15 already done), launch Albion Online, enter the
open world, wait a few seconds, **then** launch `AlbionCompanion.App`. Confirm: a gathering session
is already active (visible on Home) and correctly attributed to the right character - the original
2026-08-04 bug this whole feature exists to fix.

- [ ] **Step 5: Commit (only if this task's verification uncovered fixes)**

If Step 2-4 required any code changes, commit them individually with a message describing what was
wrong. If everything passed as-is, there's nothing to commit for this task.
