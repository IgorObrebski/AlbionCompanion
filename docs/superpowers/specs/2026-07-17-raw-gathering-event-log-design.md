# Raw Gathering Event Log — Design

## Problem

`GatheringEventRouter` currently interprets `HarvestStart` events on the fly and persists only the
derived result (`GatheredItem.ItemId` + `Amount`). Every other field on the raw Photon event
(`NodeId`, raw category code, actor entity id, and the full parameter set of every other event
type we don't yet interpret) is discarded the moment the event is handled. If it later turns out
another field carries useful information (e.g. `UpdateFame`'s layout, or a currently-unknown
event), that data is gone — the only way to get it back is to re-run a live capture.

## Goals

- Persist every Photon event we receive, in raw form, so nothing is lost to a bad interpretation
  decision made today.
- Do this without touching the existing, working interpretation logic
  (`GatheringEventRouter`, `HarvestableNodeTracker`, `GatheringSessionService`) or the tables it
  writes to (`GatheredItem`, `FameLog`).
- Make the raw log queryable later by session and by time, for after-the-fact analysis.

## Non-goals

- Interpreting or backfilling any new event types now. This spec only captures raw data; analysis
  of it is future work.
- Filtering which events get recorded. Every event the Photon parser raises is recorded — no
  allow-list of event codes, no actor filtering.

## Design

### New model: `RawGatheringEvent`

`AlbionCompanion.Core/Models/RawGatheringEvent.cs`:

```csharp
public class RawGatheringEvent
{
    public long Id { get; set; }
    public Guid? SessionId { get; set; }
    public GatheringSession? Session { get; set; }
    public byte PhotonCode { get; set; }
    public byte? SemanticEventCode { get; set; }
    public string ParametersJson { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
```

- `PhotonCode` is `PhotonEvent.Code` as received from the Photon parser.
- `SemanticEventCode` is the value of parameter `252` (the `AlbionEventCode`) when present and
  representable as a `byte`; `null` otherwise. This mirrors the same "try to fit into a byte, skip
  if not" logic `GatheringEventRouter` already uses for its own dispatch, kept here purely as a
  convenience column for filtering — it does not gate whether the row is written.
- `ParametersJson` is `PhotonEvent.Parameters` (a `Dictionary<byte, object?>`) serialized whole via
  `System.Text.Json`. Byte keys become string keys in the JSON object; values that aren't directly
  JSON-representable (e.g. `byte[]`) serialize however `System.Text.Json` naturally renders them
  (byte arrays as base64) — no custom converter is required for this spec's purpose of "don't lose
  the data," and a converter can be added later if a specific field needs nicer round-tripping.
- `SessionId` is nullable: set to the currently active `GatheringSession.Id` when one exists,
  `null` otherwise (e.g. the player is in town, or between sessions). This is a deliberate change
  from how `GatheredItem`/`FameLog` behave today (they simply drop activity with no active
  session) — the raw log's purpose is to lose nothing, so out-of-session events are still recorded,
  just unattributed.

### New service: `RawEventRecorder`

`AlbionCompanion.Gathering/RawEventRecorder.cs` (+ `IRawEventRecorder` interface, matching the
existing pattern of `IHarvestableNodeTracker` / `ILocalPlayerTracker`):

- Subscribes to `IPhotonParser.OnEventReceived` independently of `GatheringEventRouter` — a
  separate subscriber, not a modification to the router. Both subscribers receive every event;
  neither is aware of the other.
- On each event: reads `IGatheringSessionService.GetActiveSessionAsync()` for `SessionId`,
  extracts `SemanticEventCode` from parameter `252` if present, serializes `Parameters`, and saves
  a new `RawGatheringEvent` row via `AppDbContext`.
- Registered in DI in `AlbionCompanion.ConsoleHost/Program.cs` alongside `GatheringEventRouter`,
  as its own independent registration.

### Database

- New table `RawGatheringEvents` via an EF Core migration.
- FK `SessionId` → `GatheringSessions.Id`, nullable.
- Indexes on `SessionId` and `Timestamp` to support future analytical queries (e.g. "show me every
  raw event from session X" or "show me every event in this time window").

### Explicitly unchanged

`GatheredItem`, `FameLog`, `GatheringSession`, `GatheringSessionService`, `GatheringEventRouter`,
`HarvestableNodeTracker` — all keep working exactly as they do today. `RawGatheringEvent` is a
parallel, independent log, not a replacement.

## Testing

- `RawEventRecorder` unit tests (in `AlbionCompanion.Gathering.Tests`), following the existing
  style of `GatheringEventRouterTests`/`HarvestableNodeTrackerTests`:
  - Event received while a session is active → row written with that `SessionId`.
  - Event received with no active session → row written with `SessionId = null`.
  - `SemanticEventCode` populated when parameter 252 is present and byte-sized; `null` when
    absent or out of byte range.
  - `ParametersJson` round-trips the original parameter dictionary.
- `AppDbContextTests` extended to cover the new `DbSet<RawGatheringEvent>` and the migration,
  matching the existing coverage style for other entities.
