# Dynamic Zone IDs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `ZoneTracker` from throwing on non-numeric zone-change IDs (dungeons/hideouts/Mists), and give dynamic-instance and Mists zone changes sensible location names instead of crashing or showing a raw ID.

**Architecture:** Task 1 adds a pure-logic `ZoneIdParser`/`ParsedZoneId` to `AlbionCompanion.Gathering`, defensively classifying a raw zone-id value with no dependency on `ZoneTracker` or `IZoneCatalog`. Task 2 rewrites `ZoneTracker.HandleResponseAsync` to use it instead of the unguarded `Convert.ToInt32` that can throw today.

**Tech Stack:** C# / .NET 10, xUnit.

## Global Constraints

- `ZoneIdParser.Parse` must never throw, for any input shape.
- A numeric-prefixed instance id (`"1234-5"`) resolves to the base zone's catalog name with no "(Instance)" annotation.
- `"@MISTS@..."`-shaped ids get the fixed location name `"Mists"` and are always treated as open world (a session starts) — no `IZoneCatalog` lookup.
- Any other unrecognized shape falls back to the existing "open world, raw value as name" default — no exception, no behavior change from today's already-existing fallback for unrecognized *numeric* ids.
- No change to `IZoneCatalog`/`ZoneCatalog`/`GatheringSessionService`.

---

### Task 1: `ZoneIdParser` / `ParsedZoneId`

**Files:**
- Create: `AlbionCompanion.Gathering/ZoneIdParser.cs`
- Test: `AlbionCompanion.Gathering.Tests/ZoneIdParserTests.cs`

**Interfaces:**
- Produces: `AlbionCompanion.Gathering.ZoneIdParser.Parse(object zoneIdValue)` → `ParsedZoneId`; `AlbionCompanion.Gathering.ParsedZoneId(int? NumericZoneId, bool IsMists, string RawValue)`. Consumed by Task 2 (`ZoneTracker.HandleResponseAsync`).

The full new `AlbionCompanion.Gathering/ZoneIdParser.cs`:

```csharp
namespace AlbionCompanion.Gathering;

public sealed record ParsedZoneId(int? NumericZoneId, bool IsMists, string RawValue);

// Defensively classifies the raw "current zone" value from a Photon zone-change response
// (parameter 8 - see ZoneTracker). Every numeric zone id observed so far fits the first branch;
// the second and third branches exist for dynamic instances (dungeons, hideouts, the Mists),
// which per specs/albion-companion-context.md use non-numeric ids in practice - no live-capture
// sample confirms the exact shape, so this never throws regardless of what shows up: an
// unrecognized shape simply falls through to the last, safe branch instead of failing.
public static class ZoneIdParser
{
    private const string MistsPrefix = "@MISTS@";

    public static ParsedZoneId Parse(object zoneIdValue)
    {
        if (zoneIdValue is int numeric)
        {
            return new ParsedZoneId(numeric, IsMists: false, RawValue: numeric.ToString());
        }

        var raw = zoneIdValue.ToString() ?? string.Empty;

        if (raw.StartsWith(MistsPrefix, StringComparison.Ordinal))
        {
            return new ParsedZoneId(NumericZoneId: null, IsMists: true, RawValue: raw);
        }

        var dashIndex = raw.IndexOf('-');
        if (dashIndex > 0 && int.TryParse(raw[..dashIndex], out var prefixZoneId))
        {
            return new ParsedZoneId(prefixZoneId, IsMists: false, RawValue: raw);
        }

        return new ParsedZoneId(NumericZoneId: null, IsMists: false, RawValue: raw);
    }
}
```

- [ ] **Step 1: Write the failing tests**

Create `AlbionCompanion.Gathering.Tests/ZoneIdParserTests.cs`:

```csharp
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ZoneIdParserTests
{
    [Fact]
    public void BoxedInt_ReturnsNumericZoneId()
    {
        var result = ZoneIdParser.Parse(4213);

        Assert.Equal(4213, result.NumericZoneId);
        Assert.False(result.IsMists);
    }

    [Fact]
    public void MistsPrefixedString_ReturnsIsMists()
    {
        var result = ZoneIdParser.Parse("@MISTS@some-guid-looking-string");

        Assert.True(result.IsMists);
        Assert.Null(result.NumericZoneId);
    }

    [Fact]
    public void NumericPrefixedInstanceId_ReturnsBaseZoneId()
    {
        var result = ZoneIdParser.Parse("1234-5");

        Assert.Equal(1234, result.NumericZoneId);
        Assert.False(result.IsMists);
    }

    [Fact]
    public void CompletelyUnrecognizedString_ReturnsNullWithRawValue()
    {
        var result = ZoneIdParser.Parse("garbage");

        Assert.Null(result.NumericZoneId);
        Assert.False(result.IsMists);
        Assert.Equal("garbage", result.RawValue);
    }

    [Fact]
    public void NonNumericPrefixBeforeDash_FallsThroughToUnrecognized()
    {
        var result = ZoneIdParser.Parse("abc-5");

        Assert.Null(result.NumericZoneId);
        Assert.False(result.IsMists);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter ZoneIdParserTests`
Expected: FAIL — build error, `ZoneIdParser`/`ParsedZoneId` do not exist.

- [ ] **Step 3: Create `ZoneIdParser.cs` with the exact content above**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter ZoneIdParserTests`
Expected: PASS — all 5 facts pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests across the solution PASS (this task only adds one new file plus one new test file).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/ZoneIdParser.cs AlbionCompanion.Gathering.Tests/ZoneIdParserTests.cs
git commit -m "feat(gathering): add ZoneIdParser for safely classifying dynamic zone ids"
```

---

### Task 2: Rewrite `ZoneTracker.HandleResponseAsync` to use `ZoneIdParser`

**Files:**
- Modify: `AlbionCompanion.Gathering/ZoneTracker.cs`
- Modify: `AlbionCompanion.Gathering.Tests/ZoneTrackerTests.cs`

**Interfaces:**
- Consumes: `AlbionCompanion.Gathering.ZoneIdParser.Parse(object)` / `ParsedZoneId` (Task 1).
- Produces: nothing consumed by a later task — this is the final task.

The full new `AlbionCompanion.Gathering/ZoneTracker.cs`:

```csharp
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Drives gathering-session start/end from zone changes. Confirmed via live capture on
// 2026-07-16: the outer PhotonResponse.OperationCode is always 1 (a generic wrapper, same
// pattern as PhotonEvent.Code); the real sub-operation lives in parameter 253, and the
// "you joined a zone" response (253 == 2) carries the new zone's numeric id in parameter 8.
//
// Originally this tracked "returned to the zone id first seen at app start" as a home-zone
// heuristic, but that broke as soon as the player visited their city's bank/market: those are
// separate zoneIds from the main city, so they looked like "left home" and spuriously started a
// session. IZoneCatalog (ao-bin-dumps zones.json) now classifies each zone as city/safe-area vs.
// open world directly, which handles bank/market/portals correctly without needing to remember
// where the player started.
//
// Parameter 8's value isn't always numeric: dynamic instances (dungeons, hideouts, the Mists) use
// non-numeric ids in practice (see docs/superpowers/specs/2026-07-17-dynamic-zone-ids-design.md).
// ZoneIdParser classifies the raw value defensively so this method never throws on a shape it
// doesn't recognize.
public class ZoneTracker
{
    private const byte ZoneResponseSubCodeKey = 253;
    private const byte ZoneResponseSubCode = 2;
    private const byte CurrentZoneIdParameterKey = 8;

    private readonly IGatheringSessionService _sessionService;
    private readonly IZoneCatalog _zoneCatalog;

    public ZoneTracker(IPhotonParser photonParser, IGatheringSessionService sessionService, IZoneCatalog zoneCatalog)
    {
        _sessionService = sessionService;
        _zoneCatalog = zoneCatalog;
        photonParser.OnResponseReceived += (_, response) => _ = HandleResponseAsync(response);
    }

    internal async Task HandleResponseAsync(PhotonResponse response)
    {
        if (!response.Parameters.TryGetValue(ZoneResponseSubCodeKey, out var subCode) ||
            Convert.ToInt32(subCode) != ZoneResponseSubCode)
        {
            return;
        }

        if (!response.Parameters.TryGetValue(CurrentZoneIdParameterKey, out var zoneIdValue) || zoneIdValue is null)
        {
            return;
        }

        var parsed = ZoneIdParser.Parse(zoneIdValue);

        if (parsed.IsMists)
        {
            await _sessionService.StartSessionAsync("Mists");
            return;
        }

        if (parsed.NumericZoneId is { } numericZoneId)
        {
            if (await _zoneCatalog.IsCityOrSafeAreaAsync(numericZoneId))
            {
                await _sessionService.EndSessionAsync();
                return;
            }

            var zone = await _zoneCatalog.GetZoneAsync(numericZoneId);
            await _sessionService.StartSessionAsync(zone?.Name ?? numericZoneId.ToString());
            return;
        }

        await _sessionService.StartSessionAsync(parsed.RawValue);
    }
}
```

- [ ] **Step 1: Write the failing tests**

Add to `AlbionCompanion.Gathering.Tests/ZoneTrackerTests.cs`, inside the existing `ZoneTrackerTests`
class (keep every existing member — `FakePhotonParser`, `FakeZoneCatalog`, `SampleZones`,
`CreateService`, the existing `ZoneResponse(int)` helper and existing `[Fact]`s — and add a second
overload plus three new facts):

```csharp
private static PhotonResponse ZoneResponse(object zoneIdValue) =>
    new(OperationCode: 1, ReturnCode: 0, DebugMessage: string.Empty,
        Parameters: new Dictionary<byte, object?> { [253] = 2, [8] = zoneIdValue });

[Fact]
public async Task EnteringMists_StartsSessionNamedMists()
{
    using var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();
    var service = CreateService(connection);
    var parser = new FakePhotonParser();
    _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

    parser.RaiseResponse(ZoneResponse("@MISTS@abc-guid-looking-string"));
    await Task.Delay(20);

    var active = await service.GetActiveSessionAsync();
    Assert.NotNull(active);
    Assert.Equal("Mists", active!.StartLocation);
}

[Fact]
public async Task EnteringNumericPrefixedInstance_ResolvesBaseZoneName()
{
    using var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();
    var service = CreateService(connection);
    var parser = new FakePhotonParser();
    _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

    parser.RaiseResponse(ZoneResponse("4213-5")); // dynamic instance of Cairn Camain
    await Task.Delay(20);

    var active = await service.GetActiveSessionAsync();
    Assert.NotNull(active);
    Assert.Equal("Cairn Camain", active!.StartLocation);
}

[Fact]
public async Task EnteringUnrecognizedStringZoneId_DoesNotThrowAndUsesRawValue()
{
    // Regression: ZoneTracker used to call Convert.ToInt32(zoneIdValue) unconditionally, which
    // threw FormatException on any non-numeric zone id - lost silently since dispatch is
    // fire-and-forget. This must no longer throw, and the fire-and-forget dispatch must still
    // reach GatheringSessionService.StartSessionAsync with the raw value.
    using var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();
    var service = CreateService(connection);
    var parser = new FakePhotonParser();
    _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

    parser.RaiseResponse(ZoneResponse("some-unrecognized-format"));
    await Task.Delay(20);

    var active = await service.GetActiveSessionAsync();
    Assert.NotNull(active);
    Assert.Equal("some-unrecognized-format", active!.StartLocation);
}
```

- [ ] **Step 2: Run tests to verify the new ones fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter "EnteringMists_StartsSessionNamedMists | EnteringNumericPrefixedInstance_ResolvesBaseZoneName | EnteringUnrecognizedStringZoneId_DoesNotThrowAndUsesRawValue"`
Expected: FAIL — `ZoneTracker` still uses `Convert.ToInt32(zoneIdValue)` directly, so
`EnteringMists_StartsSessionNamedMists` and `EnteringUnrecognizedStringZoneId_DoesNotThrowAndUsesRawValue`
throw `FormatException` inside the fire-and-forget task (surfacing as the active session staying
null / the assertion failing), and `EnteringNumericPrefixedInstance_ResolvesBaseZoneName` fails the
same way for `"4213-5"`.

- [ ] **Step 3: Replace `ZoneTracker.cs` with the exact content above**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter ZoneTrackerTests`
Expected: PASS — all 8 facts in `ZoneTrackerTests` (5 existing + 3 new) pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests across the solution PASS.

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/ZoneTracker.cs AlbionCompanion.Gathering.Tests/ZoneTrackerTests.cs
git commit -m "fix(gathering): stop ZoneTracker crashing on non-numeric dynamic zone ids"
```

---

## Self-Review Notes

- Spec coverage: `ZoneIdParser`'s four branches (numeric, Mists, numeric-prefixed instance,
  unrecognized) exactly match the spec's Design section, tested individually in Task 1;
  `ZoneTracker` rewritten to use it with the exact three-way branch (Mists → "Mists" name, numeric
  → existing catalog flow unchanged, fallback → raw value), tested end-to-end in Task 2 including
  a regression test for the original crash. Non-goals respected: no live-capture confirmation
  attempted, no dungeon/hideout distinction in naming, `IZoneCatalog`/`GatheringSessionService`
  untouched.
- No placeholders — every step has literal file contents, exact commands, and expected output.
- Type/name consistency: `ParsedZoneId(int? NumericZoneId, bool IsMists, string RawValue)` used
  identically in Task 1's tests and Task 2's `ZoneTracker` consumption.
