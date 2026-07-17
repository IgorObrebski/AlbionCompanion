# AlbionCompanion — Manual Test Plan (Pre-Alpha)

This document is a checklist to run through with a real Albion Online client, to verify the
software actually reflects reality — not just that its unit tests pass in isolation. Work through
each scenario, record the result in the table at the end, and capture anything unexpected as a new
GitHub issue with the relevant log excerpt / DB row attached.

## Prerequisites

- Npcap installed (or let the app auto-install it — that's scenario A1 below).
- Run `AlbionCompanion.App` (or `AlbionCompanion.ConsoleHost` for scenarios that specifically call
  it out) as Administrator — both require it per their `app.manifest`.
- Know where to look:
  - Database: `%APPDATA%\AlbionCompanion\albion.db` (SQLite — open with `sqlite3` CLI or a GUI tool
    like DB Browser for SQLite).
  - Debug logs, all in `%APPDATA%\AlbionCompanion\`:
    - `debug_packets.log` — every recognized Photon event, raw.
    - `debug_event_names.log` — recognized event *names* specifically.
    - `debug_parse_failures.log` — Photon parse errors.
    - `debug_raw_event_record_failures.log` — `RawEventRecorder` write failures.
    - `debug_maui_startup_failures.log` — MAUI host startup exceptions (only in `AlbionCompanion.App`).
    - `debug_ports.log` — only created if you set `ALBION_DEBUG_PORTS=1` before running
      `ConsoleHost` (scenario A2).
- SQL snippets below assume `sqlite3 "%APPDATA%\AlbionCompanion\albion.db"`. Adjust for whatever
  tool you use.

## What automated tests already cover (110 tests, don't re-litigate these manually)

| Area | Covered automatically | What's left for manual testing |
|---|---|---|
| `GatheringSessionService` | Start/end/no-op/DC logic, domain events, empty-session discard | Whether real zone-change traffic actually triggers these calls correctly |
| `ZoneTracker` / `ZoneIdParser` | Branch logic for numeric/Mists/instance/unrecognized zone ids (synthetic inputs) | Whether *real* zone ids from a live game match the assumed shapes (`"1234-5"`, `"@MISTS@..."`) at all — this was never confirmed against live capture |
| `GatheringEventRouter` | Actor filtering, category+tier→ItemId mapping, HarvestStart semantics (synthetic events) | Whether real gathering swings produce the expected `GatheredItem` rows in practice |
| `RawEventRecorder` / retention | Write correctness, concurrency safety, 7-day sweep logic | Whether the raw log actually captures what a real session produces, and whether the sweep fires correctly across real app restarts spanning days |
| `GatheringLiveState` / `SessionHistoryService` | Aggregation math, thread-safety, DB queries (synthetic data) | Whether the UI actually reflects real, live gameplay end-to-end |
| MAUI lifecycle fix | Await-before-cleanup logic (code-level) | Real `Window.Destroying` behavior on an actual window close — never verified against a real GUI (see prior session notes) |

Nothing above has ever been exercised against a real running Albion Online client. That's the whole
point of this manual pass.

---

## A. Sniffer and packet capture

### A1. Npcap auto-install
1. If Npcap is not installed, run `AlbionCompanion.App` or `AlbionCompanion.ConsoleHost`.
2. **Expected:** the app detects it's missing (`HKLM\SOFTWARE\Npcap` absent) and downloads/runs the
   installer automatically, without you doing anything manual.
3. **Verify:** `HKLM\SOFTWARE\Npcap` now exists in the registry; app proceeds past the Npcap check
   without hanging.

### A2. Device detection
1. Run `AlbionCompanion.ConsoleHost` (it prints the device list; `AlbionCompanion.App` does not).
2. **Expected:** console prints "Network devices Npcap can see:" followed by at least one real
   network adapter (the one your game traffic actually goes through).
3. **Verify:** the adapter you're actually using to play is in the list.

### A3. Capture actually starts
1. Launch the game and start playing (any activity).
2. **Verify:** `debug_packets.log` and `debug_event_names.log` are growing (check file
   modified-time / size) while you play, confirming raw UDP capture → Photon parsing is live.

### A4. Port-diagnostic mode (only if 5055/5056 ever stop looking right)
1. Set `ALBION_DEBUG_PORTS=1`, run `AlbionCompanion.ConsoleHost`.
2. Play for a minute doing a gathering action.
3. **Verify:** `debug_ports.log` shows UDP traffic on 5055/5056 with a growing count during the
   gathering action specifically — confirms those ports still carry gathering traffic (this mode
   exists because that assumption was never fully nailed down).

---

## B. Zone recognition

For each of the following, confirm both the visible in-game transition AND the DB effect.

### B1. City → open world starts a session
1. Note your current city. Walk out through a city gate into open world (not a portal to a
   dungeon/HG for this test — plain open-world zone).
2. **Verify in DB:**
   ```sql
   SELECT Id, StartTime, EndTime, StartLocation FROM GatheringSessions
   ORDER BY StartTime DESC LIMIT 1;
   ```
   `EndTime` should be `NULL`, `StartLocation` should be the **real zone name** (e.g. "Cairn
   Camain"), not a bare numeric id.

### B2. Open world → city ends the session
1. From B1, walk back into any city (doesn't have to be the same one).
2. **Verify:** the session row from B1 now has `EndTime` set (non-null), *if* you gathered at least
   one item in between — otherwise the row should be gone entirely (see B6).

### B3. Bank/market visits don't spuriously start/end sessions
1. While in open world (an active session running), this doesn't apply — skip to: while sitting in
   a city with no active session, visit your bank, then the market, then back to the main city
   street.
2. **Verify:** no new session row was created at any point during this sequence —
   `SELECT COUNT(*) FROM GatheringSessions WHERE StartTime > '<timestamp before this test>'`
   should be `0`. This is the specific bug (bank/market as separate zoneIds) the zone-catalog
   redesign fixed — confirm it's still fixed against real bank/market zone ids, not just the
   synthetic ones in `ZoneTrackerTests`.

### B4. Dungeon entry
1. Enter a solo or group dungeon from open world.
2. **Verify:** a session starts (check DB as in B1). **This is the untested case** — the whole
   `ZoneIdParser` dynamic-instance branch was built without a real capture sample. Note in your
   result whether `StartLocation` is a sensible zone name or something garbled/raw — if garbled,
   capture the raw value from `debug_packets.log`'s corresponding zone-change response (parameter 8)
   and file it as a bug with that raw value attached; it'll tell us the real wire format we were
   guessing at.

### B5. Hideout entry
1. Enter your (or an ally's) hideout.
2. **Verify:** same as B4 — does a session start, and is the location name sensible? Hideouts also
   exercise the untested numeric-prefixed-instance branch.

### B6. Mists entry
1. Enter the Mists (Roads of Avalon).
2. **Verify:** session starts with `StartLocation = 'Mists'` exactly (this one has a hardcoded
   expected name, unlike B4/B5 — confirm it actually reads `"Mists"` and not a raw guid string,
   which would mean the assumed `"@MISTS@..."` wire format was wrong).

### B7. Empty session is discarded
1. Walk from a city into open world, then immediately back into the city without gathering
   anything.
2. **Verify:** no session row remains — `SELECT COUNT(*) FROM GatheringSessions` should be
   unchanged from before this test (the empty-session-discard logic should have deleted it).

---

## C. Gathering item detection

### C1. Basic resource gathering
1. Gather a resource node (any tier/type) for several swings, note roughly how many swings.
2. **Verify:**
   ```sql
   SELECT ItemId, Amount, Timestamp FROM GatheredItems
   WHERE SessionId = (SELECT Id FROM GatheringSessions WHERE EndTime IS NULL)
   ORDER BY Timestamp;
   ```
   - `ItemId` should look like `T{tier}_{CATEGORY}` (e.g. `T4_ORE`).
   - **Important:** each row's `Amount` should be `1`, not your actual per-swing yield — this is
     deliberate (see `GatheringEventRouter`'s header comment: `HarvestStart` doesn't carry a real
     yield amount, so every swing is recorded as `1`). If you see anything other than `1`, that's a
     regression, not a feature.
   - Row count should roughly match your swing count (one row per swing, allowing for network drop
     variance).

### C2. Different tiers of the same resource category
1. Gather two different tiers of the same resource (e.g. Iron Ore then Titanium Ore) in the same
   session.
2. **Verify:** the `ItemId` values are genuinely different (`T4_ORE` vs `T5_ORE`, not both the same
   category) — this is the specific tier-collision bug the `HarvestableCategory`/
   `HarvestableNodeTracker` redesign fixed.

### C3. Only your own actions are recorded
1. Gather in a zone with at least one other visible player also gathering (different node).
2. **Verify:** `GatheredItems` for your session only contains items *you* actually picked up —
   no rows attributable to the other player's harvesting (the actor-id filter in
   `GatheringEventRouter` is supposed to guarantee this).

---

## D. Fame (expected gap — confirm it, don't be surprised by it)

### D1. Fame is NOT recorded today
1. Gather resources and earn visible fame (watch for the in-game fame popup).
2. **Verify:**
   ```sql
   SELECT TotalFameEarned FROM GatheringSessions WHERE EndTime IS NULL;
   SELECT COUNT(*) FROM FameLogs WHERE SessionId = (SELECT Id FROM GatheringSessions WHERE EndTime IS NULL);
   ```
   Both should show **zero/no rows** — this is expected, not a bug. Per `GatheringEventRouter`'s
   own comment, `UpdateFame` was never confirmed to fire in any capture session, so it was
   deliberately left unwired rather than risk recording wrong numbers. Confirm this is still true
   (fame really doesn't show up anywhere) so it stays a known, intentional gap rather than a
   silent regression of something that used to work.

---

## E. Session continuity across restarts / disconnects

### E1. Close the app mid-session in open world (simulated DC)
1. Start gathering (session active, some items gathered). Force-close the app (not a clean
   shutdown — kill the process or unplug network briefly) while still in open world.
2. Relaunch the app while still standing in open world.
3. **Verify:** the same session row continues (`SELECT * FROM GatheringSessions WHERE EndTime IS
   NULL` shows the *original* `StartTime`, not a new row) — the "resume in wilderness" DC-handling
   logic.

### E2. Close the app while in a city, relaunch
1. With no active session, close the app while standing in a city. Relaunch.
2. **Verify:** no new session appears, and if a stale open session existed from a previous crash
   while you were in a city, it gets closed on the next zone-classification check (walk anywhere to
   trigger a zone-change response) rather than lingering forever as `EndTime IS NULL`.

---

## F. Raw event log and retention

### F1. Raw events are actually captured
1. Play for a few minutes doing varied activity (movement, gathering, zone changes).
2. **Verify:**
   ```sql
   SELECT COUNT(*), MIN(Timestamp), MAX(Timestamp) FROM RawGatheringEvents;
   ```
   Row count should be substantial (every Photon event, not just gathering-related ones) and
   timestamps should span your play session.

### F2. Retention sweep
1. This one needs the app to have run at least once more than 7 days ago with data still in
   `RawGatheringEvents` — likely not practically testable right after a fresh install. If/when you
   have older data:
   ```sql
   SELECT COUNT(*) FROM RawGatheringEvents WHERE Timestamp < datetime('now', '-7 days');
   ```
   Should be `0` after any app startup (the sweep runs once per launch). If this ever returns
   non-zero right after a fresh launch, the sweep isn't running — check startup console output
   ("Cleaning up old raw gathering events...") actually appeared.

---

## G. Live session view (UI)

### G1. Live updates while playing
1. Launch `AlbionCompanion.App`, leave the Home page open, go gather in open world.
2. **Verify (while still playing, don't just check after):** the page updates in real time —
   status flips to "Active — {location}", the item table gains rows and sums as you gather, fame
   stays 0 (see D1) — without you needing to restart the app or manually refresh anything.

### G2. UI matches DB exactly
1. After a gathering session, compare what the Home page shows against:
   ```sql
   SELECT ItemId, SUM(Amount) FROM GatheredItems
   WHERE SessionId = (SELECT Id FROM GatheringSessions ORDER BY StartTime DESC LIMIT 1)
   GROUP BY ItemId;
   ```
2. **Verify:** the numbers match exactly, item for item.

### G3. Session-end tally persists until next session
1. Return to a city (session ends). Without starting a new session, check the Home page.
2. **Verify:** it still shows the just-ended session's final tally (not blank), with status now
   "Ended" instead of "Active" — per the deliberate design decision to keep last results visible.
3. Start a new gathering session.
4. **Verify:** the page resets to zero/empty for the new session, not carrying over the old tally.

---

## H. Session history (UI)

### H1. List matches the database
1. After several completed sessions exist, open the Sessions tab.
2. **Verify against:**
   ```sql
   SELECT Id, StartTime, EndTime, StartLocation, TotalFameEarned FROM GatheringSessions
   WHERE EndTime IS NOT NULL ORDER BY StartTime DESC;
   ```
   Every row should appear in the UI list, most-recent-first, with matching location/fame/duration.
   The **currently active** session (if any) should NOT appear in this list (it's DB-filtered to
   `EndTime IS NOT NULL`).

### H2. Item counts on the list
1. Compare the "Items" column for a session against:
   ```sql
   SELECT SUM(Amount) FROM GatheredItems WHERE SessionId = '<session-guid>';
   ```
2. **Verify:** matches exactly.

### H3. Detail page
1. Click a session row.
2. **Verify:** the detail page's per-item table matches the same `GROUP BY ItemId` query as G2 but
   scoped to that specific historical session, and the "Back to sessions" link returns to the list.

### H4. Duration formatting for a long session
1. If you have (or can construct via test data) a session lasting over 24 hours, check the duration
   column doesn't silently drop the day count.
2. **Verify:** format includes a day component (e.g. `1.02:15:30`) rather than wrapping to `02:15:30`.

---

## I. Application lifecycle

### I1. Normal close
1. Use the app normally for a bit, close the window via the OS close button.
2. **Verify:** the process actually exits (check Task Manager) — no lingering `AlbionCompanion.App`
   process after the window disappears.

### I2. Fast close right after launch
1. Launch the app and close the window as fast as you can, immediately.
2. **Verify:** the process still exits cleanly (possibly with a brief delay) — no orphaned process
   left running in the background, no zombie packet-capture handle left open (check Task Manager /
   Resource Monitor for a lingering handle on the network adapter).

### I3. Startup failure is visible somewhere
1. Not easy to trigger with a healthy install — skip unless something is actually broken. If the
   app ever fails to fully initialize (e.g. DB locked by another process), check
   `debug_maui_startup_failures.log` for a logged exception rather than the app just silently doing
   nothing forever.

---

## Results Log

| # | Scenario | Result (PASS/FAIL) | Date | Notes |
|---|---|---|---|---|
| A1 | Npcap auto-install | | | |
| A2 | Device detection | | | |
| A3 | Capture starts | | | |
| A4 | Port diagnostic | | | |
| B1 | City→open world starts session | | | |
| B2 | Open world→city ends session | | | |
| B3 | Bank/market no spurious start/end | | | |
| B4 | Dungeon entry | | | |
| B5 | Hideout entry | | | |
| B6 | Mists entry | | | |
| B7 | Empty session discarded | | | |
| C1 | Basic gathering detection | | | |
| C2 | Tier distinction | | | |
| C3 | Actor filtering | | | |
| D1 | Fame not recorded (expected) | | | |
| E1 | DC in wilderness, resume | | | |
| E2 | DC in city, no resume | | | |
| F1 | Raw events captured | | | |
| F2 | Retention sweep | | | |
| G1 | Live UI updates | | | |
| G2 | Live UI matches DB | | | |
| G3 | Session-end tally persists/resets | | | |
| H1 | History list matches DB | | | |
| H2 | History item counts | | | |
| H3 | History detail page | | | |
| H4 | Long-session duration format | | | |
| I1 | Normal close | | | |
| I2 | Fast close | | | |
| I3 | Startup failure logging | | | |
