# Multi-character support — design

## Context

AlbionCompanion currently tracks one continuous stream of gathering activity with no notion
of *which character* earned it. `ILocalPlayerTracker.CurrentEntityId` identifies "the local
player" purely by a transient Photon entity id, re-derived from the zone-join response
(`253:2`) — an id the server reassigns on every zone/instance transition (confirmed live
2026-08-03: the same character's entity id changed multiple times within a single unbroken
play session, not just across app restarts). This causes two related problems:

1. **The known rehydration bug**: if the app is closed and reopened while the player stays in
   the same zone, no `253:2` fires, so `CurrentEntityId` stays `null` and every gathering event
   is silently dropped until the player changes zones (see `[[project_feature_backlog]]`).
2. **No character identity at all**: a player with several characters (the user's own
   situation — 5 characters) gets one merged pile of sessions/stats, with no way to see "what
   has *this* character earned" without manually cross-referencing `StartLocation`/timestamps.

This design fixes both by introducing a `Character` entity that sessions attach to, identified
automatically from two live-capture-confirmed Photon signals (no manual "who am I playing"
picker needed).

## How character identity is detected

Two Photon semantic event codes carry `(entityId, nickname)` pairs, confirmed via live capture
2026-08-03:

- **`253:2` (zone-join `RESPONSE`)** — already parsed by `LocalPlayerTracker` for
  `CurrentEntityId`. This is a `RESPONSE` to *our own* request, so it is inherently self-only —
  no other player's client ever receives it. It also carries the character's nickname
  (parameter 2). This is the **high-confidence signal**: whenever it fires, both
  `CurrentEntityId` and the current character's name are known with certainty.
- **`252:279` (a periodic `EVENT`)** — confirmed to broadcast for *any* nearby player, not just
  the local one (two different nicknames observed for two different nearby entities in the same
  capture window). It fires independent of zone transitions, which is what makes it useful: it
  can refresh `CurrentEntityId` without requiring a zone change. Because it is not self-only, a
  reading is only trusted as "us" when its nickname matches either:
  - the name last confirmed via `253:2` this run (the common case — keeps `CurrentEntityId`
    current as it churns), or
  - any name in the user's registered character list (the cold-start case — the app was just
    restarted in the same zone, so no `253:2` has fired yet, and this is the only way to
    recover identity without a zone transition).

This lives in `LocalPlayerTracker`, which grows a second piece of state,
`CurrentCharacterName` (string?), alongside the existing `CurrentEntityId`. It takes a
dependency on `ICharacterService` (read-only: the registered name list) purely for the
cold-start match — no other coupling.

## Data model

```
Character
  Id: Guid
  Name: string        -- exact in-game character name, unique
  CreatedAt: DateTime

GatheringSession
  ...(unchanged)...
  CharacterId: Guid?   -- nullable FK to Character
```

One session belongs to at most one character for its whole lifetime — a session never switches
character mid-flight (that would require logging out, which already ends the session).

**Existing sessions** get `CharacterId = null` on migration. They surface in the UI as
belonging to an "Unknown character" bucket rather than being retroactively guessed at — there
is no reliable way to backfill this.

`GatheringSessionService.StartSessionAsync` resolves `CharacterId` at session-creation time from
`LocalPlayerTracker.CurrentCharacterName` (looked up against `ICharacterService`, `null` if the
name isn't registered or isn't known yet).

## Services

- **`Character` CRUD**: `ICharacterService` / `CharacterService` — `GetAllAsync()`,
  `AddAsync(name)`, `DeleteAsync(id)`. Deleting a character does **not** cascade-delete its
  sessions — `GatheringSession.CharacterId` goes `null` (same `SetNull` pattern already used for
  `RawGatheringEvent.SessionId`), so history is never silently destroyed by a rename/typo-fix
  cleanup.
- **Character overview**: a method (on `ICharacterService` or a new
  `ICharacterOverviewService` — decide during planning based on how large `CharacterService`
  gets) returning, per character: `TotalFameEarned`, `TotalSilverEarned`,
  `TotalItemsCollected`, `LastActive` (latest session `StartTime`), `HasActiveSession` (bool).
  Same aggregation shape as `SessionHistoryService.GetCompletedSessionsAsync`, scoped to one
  `CharacterId` and summed rather than paged.

## Navigation & UI

- **`/` — Character hub** (new landing page, replaces today's Home as the app's entry point).
  One card per registered character: name, last active, total fame/silver/items, and a
  highlighted/badged state when that character currently has an active session. An
  "+ Add character" control (name-only form) lives on this page.
- **`/characters/{id}` — character dashboard**: the same aggregate cards as the hub card (just
  this one character, larger), plus that character's session list (reuses `Sessions.razor`'s
  table, filtered to `CharacterId`) — fully browsable with Albion closed ("na sucho").
- **`/characters/{id}/broadcast`** — today's `Home.razor` live-session view, scoped to one
  character, labeled **"Broadcast"** in the UI (nav label, page heading) instead of "Home" or
  "Live" — this is what it actually is, a live broadcast of the current session's stats as
  events arrive.
- **Toast notification**: when `GatheringSessionService.OnSessionStarted` fires for a
  successfully character-resolved session, show a self-dismissing toast — "Session started for
  {character}" with a button to that character's `/characters/{id}/broadcast`. Ignoring it
  changes nothing; capture keeps running in the background regardless. If `CharacterId` is
  `null` (unregistered character), skip the toast — nothing useful to link to yet.
- **`/sessions`** (existing global list) is unchanged in behavior, gains a **Character** column
  for context (shows "Unknown" for `CharacterId == null`).

## Edge cases

- **Unregistered character**: sessions still record normally (unaffected — gathering doesn't
  require a resolved character), just land with `CharacterId = null` in the "Unknown"
  bucket until the player adds that character by name.
- **Two characters with colliding effective identity**: not possible — `Character.Name` is
  unique, and only one game client (one `CurrentEntityId`) is ever tracked at a time.
- **`279` nickname briefly matches a *different* registered character than the one currently
  active** (e.g. two of the user's own characters both registered, standing near each other —
  not realistic since one person can't run two clients against this single-capture setup, but
  worth naming): the "must match the already-confirmed name" rule for the warm-path prevents
  this from reassigning `CurrentCharacterName` away from the zone-join-confirmed identity mid-
  session; only the cold-start path (no confirmed name yet) trusts a registered-list match, and
  that only happens once per app run before the first `253:2`.

## Testing

- `LocalPlayerTracker` tests: `253:2` still sets `CurrentEntityId` + now also
  `CurrentCharacterName`; a `279` event matching the confirmed name updates `CurrentEntityId`
  without touching `CurrentCharacterName`; a `279` event with a non-matching name is ignored; a
  `279` event matching a registered character name is accepted only when no name is confirmed
  yet (cold start).
- `CharacterService` tests: CRUD, delete-detaches-sessions-without-deleting-them.
- `GatheringSessionService` tests: `StartSessionAsync` resolves `CharacterId` correctly (known
  name / unknown name / no name yet).
- No new charting work needed — the character dashboard reuses `SessionHistoryService`'s
  existing aggregation shape.
