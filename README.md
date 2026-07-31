[README.md](https://github.com/user-attachments/files/30583860/README.md)
<div align="center">

# AlbionCompanion

**A reverse-engineered network analysis companion for Albion Online.**

Captures and decodes the game's live network protocol to reconstruct gathering sessions, fame, and loot in real time — no game files modified, no memory reading, no client injection.

[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#requirements)
[![Tests](https://img.shields.io/badge/tests-94%20passing-2ea44f)](#testing)
[![License](https://img.shields.io/badge/license-MIT-blue)](#license)

</div>

---

## What this actually does

Albion Online communicates with its servers over UDP using the Photon Engine protocol. AlbionCompanion sits passively on the network layer, captures that traffic, decodes it, and turns raw packets into meaningful, structured events — when you started gathering, what resource you picked up, when you re-entered a city, how much fame you earned.

There is no public API for any of this. Every layer of the pipeline below the Photon transport itself had to be reverse-engineered, verified against a live client, and corrected when reality didn't match the initial assumptions — which it frequently didn't.

```
Albion Online client
      │  UDP :5055 / :5056
      ▼
┌─────────────────────┐
│  Packet capture      │  SharpPcap + Npcap (raw NIC-level capture)
└─────────┬─────────────┘
      ▼
┌─────────────────────┐
│  Protocol16/18       │  Photon deserialization — LEB128 varints, zigzag
│  deserialization     │  CompressedInt/Long, custom slim types
└─────────┬─────────────┘
      ▼
┌─────────────────────┐
│  Albion event        │  Maps opaque numeric event codes to real semantics
│  mapping             │  (HarvestStart, zone change, fame update, ...)
└─────────┬─────────────┘
      ▼
┌─────────────────────┐
│  Domain logic         │  Session lifecycle, actor filtering, zone
│  (Gathering module)   │  classification, tier/category resolution
└─────────┬─────────────┘
      ▼
┌─────────────────────┐
│  SQLite (EF Core)     │  Persisted sessions, gathered items, fame log
└─────────────────────┘
```

## The interesting part: this protocol doesn't want to be read

A handful of problems this project had to actually solve, in the order they were found:

- **The game silently migrated Photon versions mid-development.** Albion moved from Protocol16 to Protocol18 in a patch, which broke the vendored deserializer with silent "unsupported type" failures instead of a clean error. The type table, varint encoding, and compressed-int zigzag logic all had to be rebuilt against the new spec.
- **The "event code" isn't where you'd expect it.** The outer `EventData.Code` / `OperationCode` fields are almost always a generic wrapper value of `1`. The actual semantic event code is nested inside the parameter dictionary (`params[252]`/`params[253]`) — found by correlating packet frequency against known player actions, not from any documentation.
- **Broadcast events aren't scoped to you.** `HarvestStart` fires for every player in visual range, not just the local one — an early build was silently recording other players' gathering into the user's own session. Fixed by tracking the local player's `entityId` (which changes on every zone transition) from the same handshake response and filtering every domain event through it.
- **Resource tier and resource type are encoded separately, and inconsistently.** A gathering swing's category code identifies the resource family (ore, wood, fiber…) but *not* its tier — Tier 4 Iron, Tier 3 Tin, and Tier 5 Titanium all report the same "Ore" category. The tier has to be correlated separately from a different broadcast (`NewHarvestableObject`) by node ID before an item can be identified as `T4_ORE` rather than just "some ore."
- **One bad event handler could kill an entire packet's processing.** An unguarded numeric conversion inside a synchronous Photon event handler could throw and unwind back into the packet parser itself, silently dropping the rest of a multi-command UDP packet. Fixed with safe conversions and isolated event dispatch, so one failing subscriber can't take the others down with it.
- **City sub-areas aren't the city.** The bank and the marketplace each have their own distinct zone ID, separate from the city's main zone. A naive "did I return to my starting zone" check misclassified a bank visit as ending a gathering trip. Solved by classifying *every* zone through a proper city/safe-area catalog instead of remembering a single "home zone."

Full write-up of the debugging session that uncovered these: [`docs/sessions/2026-07-16-gathering-mvp.md`](docs/sessions/2026-07-16-gathering-mvp.md).

## Architecture

Modular solution, one responsibility per project, Clean Architecture boundaries (interfaces live in `Core`, implementations sit in their own modules):

| Project | Responsibility |
|---|---|
| `AlbionCompanion.Sniffer` | Raw packet capture (SharpPcap) + Photon protocol deserialization |
| `AlbionCompanion.Gathering` | Domain logic: session lifecycle, zone tracking, actor filtering, item/tier resolution |
| `AlbionCompanion.Core` | EF Core models, `DbContext`, shared interfaces |
| `AlbionCompanion.ConsoleHost` | Headless runner for the sniffer + gathering pipeline, used during protocol/domain development |
| `AlbionCompanion.App` | Desktop UI — Blazor Hybrid on .NET MAUI *(actively being built, see [Roadmap](#roadmap))* |
| `*.Tests` | One test project per module, xUnit |

Design principles followed throughout: **SOLID**, **vertical slices** per module (sharing only the `DbContext`), **dependency injection** via `Microsoft.Extensions.DependencyInjection`, fully **async/await** I/O.

## Tech stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10 (C#) |
| UI *(in progress)* | Blazor Hybrid on .NET MAUI |
| Packet capture | SharpPcap + PacketDotNet, backed by Npcap |
| Protocol transport | Photon Protocol16/18 (custom-rebuilt deserializer) |
| Persistence | SQLite via Entity Framework Core |
| Reference data | Live-imported item & zone dictionaries from `ao-data/ao-bin-dumps` |
| Testing | xUnit |

## MVP scope vs. roadmap

**MVP (done):** network sniffer + gathering pipeline, fully headless. Capture → decode → domain events → SQLite, with zero UI. The console host exists specifically so the protocol and domain logic could be built and verified before any UI work started.

**Roadmap (in progress / future):**
- 🚧 Desktop UI in Blazor Hybrid (.NET MAUI) — live session view, currently being layered on top of the working backend
- ⏭ Market flip tracking (`FlipLog` model already in place) — buy/sell order capture and tax-aware profit calculation
- ⏭ Live price lookups per location
- ⏭ Dynamic zone instances (dungeons, hideouts, Mists) — currently fall back to a safe "open world" classification rather than resolving a real name
- ⏭ Packaged, signed installer with automatic Npcap provisioning

## Testing

94 automated tests across the sniffer, gathering, and core modules, covering domain logic (session start/end/no-op/disconnect handling, actor filtering, tier+category resolution, retention sweeps) against synthetic protocol data.

Automated tests intentionally do **not** claim to verify real game behavior — a live client is unpredictable in ways synthetic fixtures aren't. That gap is covered explicitly by [`docs/testing/manual-test-plan.md`](docs/testing/manual-test-plan.md), a structured manual pass run against a real running client, with a table of exactly what the automated suite already covers versus what still needs a live check.

```bash
dotnet test
```

## Requirements

- Windows (x64), .NET 10 SDK
- [Npcap](https://npcap.com/) — auto-detected on startup; the app downloads and runs the installer if it's missing
- Administrator privileges (required for raw packet capture — enforced via `app.manifest`)

## Getting started

```bash
git clone https://github.com/IgorObrebski/AlbionCompanion.git
cd AlbionCompanion
dotnet restore
dotnet ef database update --project AlbionCompanion.Core

# Headless pipeline (sniffer + gathering, no UI) — run as Administrator
dotnet run --project AlbionCompanion.ConsoleHost
```

The SQLite database and debug logs are written to `%APPDATA%\AlbionCompanion\`.

## License

MIT — see [LICENSE](LICENSE).
