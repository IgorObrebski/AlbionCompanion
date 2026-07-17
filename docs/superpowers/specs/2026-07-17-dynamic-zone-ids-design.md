# Dynamic Zone IDs — Design

## Problem

`ZoneTracker.HandleResponseAsync` reads the "current zone" Photon response parameter (key `8`)
with `Convert.ToInt32(zoneIdValue)`, assuming it is always numeric. Per
`specs/albion-companion-context.md`'s open item, dynamic instances (dungeons, hideouts, the Mists)
in practice use non-numeric IDs — a numeric-prefixed instance id like `"1234-5"`, or a synthetic
key like `"@MISTS@<guid>"` for the Mists. `Convert.ToInt32` on either of those throws
`FormatException`. Because `ZoneTracker`'s dispatch is fire-and-forget
(`photonParser.OnResponseReceived += (_, response) => _ = HandleResponseAsync(response);`), that
exception is never observed — the gathering session for that zone entry silently never starts, with
no log, no crash, nothing to indicate why. This is a correctness bug, not merely a missing
"nice name" feature.

No live-capture sample data of an actual dungeon/hideout/Mists zone-change response is available to
confirm the exact wire format, so this design is built defensively: it must never throw regardless
of what shape parameter 8 actually takes, and it degrades gracefully for shapes it can't fully
interpret.

## Goals

- `ZoneTracker` never throws when parameter 8 is a non-numeric string, in any shape.
- A numeric-prefixed dynamic instance id (e.g. `"1234-5"`) resolves to the base zone's name via the
  existing `IZoneCatalog` lookup, same as a plain numeric zone id — the instance suffix is not
  reflected in the displayed location name.
- The Mists (`"@MISTS@<guid>"`-shaped ids) get a fixed, readable location name (`"Mists"`) instead
  of a raw ID string, without needing a catalog entry (there is none — the Mists have no static
  zones.json listing).
- Any zoneId shape not covered by the above still falls back to the existing safe behavior: treated
  as open world (gathering-eligible), location name is the raw value as a string — matching
  `IZoneCatalog.IsCityOrSafeAreaAsync`'s existing "unrecognized ⇒ open world" philosophy for a
  numeric id not in the catalog.

## Non-goals

- Confirming the exact real wire format via live capture. This design is intentionally defensive
  because no sample data is available; if the real format turns out to differ from the two shapes
  assumed here (`"<number>-<suffix>"`, `"@MISTS@<rest>"`), the fallback branch (Non-goal-safe,
  no-throw, raw-string display) still applies — nothing crashes, worst case is a less pretty name.
- Distinguishing dungeon vs. hideout vs. other instance types in the displayed name. All
  numeric-prefixed instances resolve to their base zone's catalog name, with no "(Instance)"
  annotation (per the resolved design question) — a dungeon entrance and its open-world parent zone
  show the same name today.
- Any change to `IZoneCatalog`/`ZoneCatalog` itself — this only changes how `ZoneTracker` derives
  the integer id (or decides it can't) before calling into the existing catalog API.

## Design

### New helper: `ZoneIdParser`

`AlbionCompanion.Gathering/ZoneIdParser.cs`:

```csharp
public static class ZoneIdParser
{
    public static ParsedZoneId Parse(object zoneIdValue);
}

public sealed record ParsedZoneId(int? NumericZoneId, bool IsMists, string RawValue);
```

`Parse` never throws for any input shape. Its logic, in order:

1. If `zoneIdValue` is already a numeric type (as it is for every zone id observed so far via
   `Convert.ToInt32`-compatible values), `NumericZoneId` is that value, `IsMists = false`.
2. Else, if `zoneIdValue.ToString()` starts with `"@MISTS@"`, `IsMists = true`, `NumericZoneId =
   null`.
3. Else, if `zoneIdValue.ToString()` contains a `-` and the substring before the first `-` parses
   as an `int` (`int.TryParse`), `NumericZoneId` is that parsed value, `IsMists = false` — this is
   the `"1234-5"` dynamic-instance shape.
4. Else, `NumericZoneId = null`, `IsMists = false` — an unrecognized shape; no exception, just no
   numeric id to look up.

`RawValue` is always `zoneIdValue.ToString() ?? string.Empty`, used as the final fallback display
name in case 4.

### `ZoneTracker.HandleResponseAsync` rewritten

Replaces the current `var zoneId = Convert.ToInt32(zoneIdValue);` and its two subsequent calls
(`IsCityOrSafeAreaAsync`/`GetZoneAsync`) with:

```csharp
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
```

- Mists never touches `IZoneCatalog` (there's no static entry for it, and it's inherently
  gathering-eligible open world — always starts a session, mirroring how an unrecognized numeric
  zone id already defaults to "open world, start a session" today).
- A numeric-prefixed instance id reuses the exact existing catalog flow, unchanged — the only
  difference from today is *how* the integer got extracted.
- A shape `ZoneIdParser` can't interpret at all falls back to the raw string as the location name,
  and is treated as open world (a session starts) — consistent with the existing "unrecognized ⇒
  open world" default, now extended to non-numeric unrecognized values instead of only numeric ones
  not present in the catalog.

### Explicitly unchanged

`IZoneCatalog`/`ZoneCatalog`, `GatheringSessionService`, and every other zone-unrelated component
are untouched. This is confined to a new file (`ZoneIdParser.cs`) plus a rewrite of one method's
body in `ZoneTracker.cs`.

## Testing

New `AlbionCompanion.Gathering.Tests/ZoneIdParserTests.cs` — pure logic, no dependencies:

- A boxed `int` value → `NumericZoneId` equals that value, `IsMists` false.
- `"@MISTS@some-guid-looking-string"` → `IsMists` true, `NumericZoneId` null.
- `"1234-5"` → `NumericZoneId` equals `1234`, `IsMists` false.
- A completely unrecognized string (e.g. `"garbage"`, no `-`, not `@MISTS@`-prefixed) →
  `NumericZoneId` null, `IsMists` false, `RawValue` equals the input.
- A string shaped like `"abc-5"` (non-numeric before the `-`) → falls through to the "unrecognized"
  case (`NumericZoneId` null) rather than throwing.

Extend `AlbionCompanion.Gathering.Tests/ZoneTrackerTests.cs`:

- A zone-change response with parameter 8 = `"@MISTS@abc"` → `StartSessionAsync("Mists")` is
  called (verify via a fake `IGatheringSessionService`, matching this test file's existing style).
- A zone-change response with parameter 8 = `"1234-5"`, where a fake `IZoneCatalog` recognizes zone
  `1234` as open-world with name `"Cairn Camain"` → `StartSessionAsync("Cairn Camain")` is called
  (no instance-suffix artifact in the name).
- A zone-change response with parameter 8 = an entirely unrecognized string → no exception is
  thrown, and `StartSessionAsync` is called with that raw string (regression test for the original
  crash this design fixes).
