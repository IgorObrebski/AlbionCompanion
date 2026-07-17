# Raw Gathering Event Retention — Design

## Problem

`RawGatheringEvent` (see
[2026-07-17-raw-gathering-event-log-design.md](2026-07-17-raw-gathering-event-log-design.md))
records every Photon event unfiltered and forever. That log was built purely for debugging
unrecognized/unexpected events, not as a permanent record — left as-is, `RawGatheringEvents` (and
its `ParametersJson` column) grows without bound in the SQLite file for the life of the
installation.

## Goals

- Bound the size of `RawGatheringEvents` by age: rows older than a fixed threshold are deleted.
- Keep this simple — no configuration surface, no background scheduling infrastructure.

## Non-goals

- Making the retention period configurable. A fixed constant is enough for the debugging use case
  this log exists for.
- Retention/cleanup for any other table. This only targets `RawGatheringEvents`.
- Continuous/periodic cleanup while the app is running. The app is typically restarted often
  enough (once per play session) that a startup-time sweep is sufficient.

## Design

### Retention threshold

A constant, `TimeSpan.FromDays(7)`, defined alongside the cleanup code. Rows with
`Timestamp < DateTime.UtcNow - retention` are deleted.

### Cleanup trigger: on startup

`AlbionCompanion.ConsoleHost/Program.cs` already runs one-time startup work inside the
`migrationScope` block (`Database.MigrateAsync()`, then `IItemDictionaryService.SeedFromJsonAsync()`
— see `Program.cs:93-98`). The retention sweep is added as a third step in that same block, using
the same `AppDbContext` instance already resolved there:

```csharp
using (var migrationScope = provider.CreateScope())
{
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
    Console.WriteLine("Checking item dictionary...");
    await migrationScope.ServiceProvider.GetRequiredService<IItemDictionaryService>().SeedFromJsonAsync();

    var cutoff = DateTime.UtcNow - RawGatheringEventRetention.Period;
    await dbContext.RawGatheringEvents
        .Where(e => e.Timestamp < cutoff)
        .ExecuteDeleteAsync();
}
```

The retention constant lives in a small static holder (e.g.
`AlbionCompanion.Gathering/RawGatheringEventRetention.cs`, `public const` or `static readonly
TimeSpan Period = TimeSpan.FromDays(7);`) so the test project and `Program.cs` share the same value
rather than duplicating the literal.

### Error handling

No special handling. If the delete throws (e.g. a locked database file), it propagates and fails
startup the same way a failed `MigrateAsync()` would today — this matches existing behavior for
other startup-time database operations in this block, and a silently-failing cleanup is worse than
a loud one.

### Explicitly unchanged

`RawEventRecorder`'s write path is untouched — it keeps writing every event unfiltered, exactly as
designed. This spec only adds a delete step that runs once at startup, after migration and before
event capture begins.

## Testing

- A new integration test (in `AlbionCompanion.Gathering.Tests`, alongside the existing
  `AppDbContextTests` style) that:
  - Seeds `RawGatheringEvents` rows with timestamps older and newer than the retention threshold.
  - Runs the same delete query used in `Program.cs`.
  - Asserts only the newer rows remain.
