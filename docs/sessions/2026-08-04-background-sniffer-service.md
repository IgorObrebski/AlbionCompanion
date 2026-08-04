# Sesja 2026-08-04 — od "appka nie łapie gry" do osobnego Windows Service

## Punkt startowy

Kilka drobnych fixów z rana (rehydracja `LocalPlayerTracker` przy cold-starcie, auto-zamykanie
"wiecznych" sesji, light mode UI), a potem user zgłosił prawdziwy problem: jeśli Albion już
działał i user dopiero potem odpalał appkę, sesja gatheringu nigdy nie startowała — cała
logika startu sesji siedziała w tym samym procesie co UI i reagowała tylko na zmianę zony.

## Co zrobiliśmy, w kolejności przyczynowej

1. **Diagnoza przez czytanie logów na żywo, bez proszenia usera o cokolwiek** —
   `debug_packets.log`/`albion.db` bezpośrednio z `%APPDATA%\AlbionCompanion`. Napisany
   jednorazowy skrypt na `Microsoft.Data.Sqlite` (brak `sqlite3`/Python na maszynie) do
   odpytywania bazy. Znaleziono dwa realne, osobne bugi po drodze:
   - **Sesja-sierota**: `GetActiveSessionAsync` uznawało za aktywną każdą sesję z
     `EndTime == NULL` bez względu na wiek — appka "wznawiała" sesję z dnia wcześniej
     zamiast zaczynać nową. Fix: auto-zamykanie sesji bez aktywności >1h.
   - **Prawdziwy root cause**: `ZoneTracker` startuje sesję tylko na evencie zmiany zony
     (`253:2`) — jeśli user nie zmienia zony *po* starcie appki, żaden taki event nie
     nadejdzie, niezależnie od tego jak długo appka działa.

2. **Brainstorming architektury** (`superpowers:brainstorming`) → spec →
   `docs/superpowers/specs/2026-08-04-background-sniffer-service-design.md`. Kluczowa decyzja:
   przenieść sniffer + logikę sesji do **osobnego Windows Service**, działającego od boota,
   niezależnie od cyklu życia appki UI. Appka staje się cienkim klientem czytającym wspólną
   bazę + odbierającym live-eventy przez named pipe. Serwis wewnętrznie usypia się, gdy
   `Albion-Online.exe` nie działa (żeby nie żreć zasobów cały czas).

3. **Plan implementacji** (`superpowers:writing-plans`) → 16 tasków, wykonanych przez
   `superpowers:subagent-driven-development` (świeży subagent per task, review + fix loop po
   każdym):
   - Task 1-3: refaktor `ICharacterService`/`IGatheringSessionService` (wspólny interfejs
     eventów `IGatheringLiveEventSource`).
   - Task 4-6: `LiveEventPipeServer`/`LiveEventPipeClient` (named pipe, newline-delimited
     JSON) + testy integracyjne na realnych `System.IO.Pipes`.
   - Task 7-8: `IGameProcessWatcher` (gate na obecność procesu gry), WAL mode dla
     współdzielonego SQLite.
   - Task 9-10: `GatheringLiveState.Attach` z dwoma źródłami, `IServiceStatusProvider`
     (wrapper na `ServiceController`).
   - Task 11: `AlbionCompanion.Service` (Worker Service) — gating pipeline'u wg obecności
     gry, `LiveEventPipeServer` żyje przez cały czas życia serwisu.
   - Task 12: `AlbionCompanion.App` staje się cienkim klientem (`AppClientHostBuilder`,
     bez sniffera).
   - Task 13-14: strona Ustawień (status serwisu, ręczny start), baner rozłączenia.
   - Task 15: `AlbionCompanion.ServiceInstaller` (kopiowanie plików, migracja bazy,
     `sc create`/`sc sdset`).
   - Task 16: pełny build/test regresyjny.

4. **Finalny przegląd całej gałęzi** (model opus) złapał to, czego pojedyncze review'y
   tasków nie mogły — bugi na **szwach między taskami**:
   - `CharacterRegistryChanged` trafiał do innej instancji `ICharacterService` niż ta, którą
     realnie używał `LocalPlayerTracker` (dwa różne kontenery DI w `Worker`).
   - Migracja bazy/WAL działała tylko gdy gra była uruchomiona (zagate'owana razem z
     pipeline'em) — na czystej maszynie z zamkniętą grą appka nigdy nie widziała
     zmigrowanej bazy.
   - Named pipe nie miał explicit ACL — LocalSystem-owy serwis tworzy pipe z domyślnym
     security descriptorem, appka jako zwykły user mogłaby nie móc się połączyć.
   - Plus 6 Important: SDDL w instalatorze nadawał `SERVICE_CHANGE_CONFIG` (lokalna
     eskalacja uprawnień!), brak reconnect po zerwaniu połączenia, race w
     `RetryNowAsync`, zero error handlingu w `Worker` (jeden throw = cały serwis padał),
     Settings mogło crashować proces, `IGameProcessWatcher` niewstrzykiwany (testy
     tautologiczne).
   - Jedna dozwolona fala fixów naprawiła 12/14 znalezisk czysto. Dwa Important (race w
     reconnect-guardzie, brak `Synchronize` w ACL pipe'a) wymagały decyzji usera — wybrał
     naprawić teraz. Guard okazał się wymagać **trzech rund** (unconditional Exchange →
     CompareExchange → w końcu strukturalna zmiana: release tylko z jednego z dwóch
     wzajemnie wykluczających się miejsc, zamiast generycznego `finally`).

5. **Merge do `master`, push.** 197/197 testów zielonych.

6. **Pierwsza realna instalacja na żywej maszynie — i seria bugów, których żaden sandbox nie
   mógł złapać:**
   - `sc start` → **błąd 1053**. Root cause z Event Viewera: `dotnet` na tej maszynie jest
     zainstalowany per-user (`C:\Users\<user>\.dotnet`), niewidoczny dla `LocalSystem`. Fix:
     publikacja serwisu jako **self-contained** (`RuntimeIdentifier`/`SelfContained` w
     `.csproj`), żeby nie zależał od żadnego globalnie wykrywalnego runtime'u.
   - Sesja się nie łapała mimo działającego serwisu → **literówka wielkości liter**:
     postać zarejestrowana jako `"ejnsztain"`, nick w grze `"Ejnsztain"` — dopasowanie
     case-sensitive. Rename w appce potwierdził cały cross-process flow
     (rename → `CharacterRegistryChanged` przez pipe → invalidacja cache w serwisie →
     poprawne przypisanie następnej sesji).
   - Appka crashowała przy wejściu na Broadcast, "An unhandled error has occurred", zero
     śladu w Event Logu. Dodano tymczasowe (ale trwałe) logowanie
     (`FileLoggerProvider.cs` + `AppDomain.UnhandledException`) →
     **`AppClientHostBuilder` nie rejestrował `HttpClient`**, którego potrzebuje
     `ItemDictionaryService`. Nigdy nie złapane wcześniej, bo bug z case-sensitivity
     nazwy postaci oznaczał, że realna ścieżka renderowania danych na Broadcast nigdy
     wcześniej się nie wykonała. Fix + regression test
     (`AppClientHostBuilderTests` — resolwuje **każdy** zarejestrowany serwis, nie tylko
     konstruuje provider).

## Stan na koniec dnia

Cały pipeline działa end-to-end na żywej maszynie: serwis wstaje od boota, usypia się gdy
gra nie działa, appka łączy się przez pipe (ACL działa), rename postaci propaguje się
cross-process, sesja startuje z poprawnym `CharacterId` niezależnie od kolejności
odpalenia gry/appki — czyli oryginalny bug jest faktycznie naprawiony i zweryfikowany, nie
tylko "powinien działać".

Dodano `scripts/redeploy-service.ps1` (stop → publish self-contained → copy → start) do
iteracji na kodzie serwisu bez pełnej reinstalacji przez `sc create`/`sdset`.

## Znane ograniczenia / do pamiętania

- Windows na tej maszynie ma domyślną politykę execution policy blokującą `.ps1` — używać
  `powershell -ExecutionPolicy Bypass -File <script>` dla jednorazowych odpaleń, nie
  zmieniać globalnej polityki.
- `CharacterId` sesji nigdy nie jest przypisywany ponownie po starcie — literówka w nazwie
  postaci naprawiona *po* starcie sesji nie naprawia już tej sesji, trzeba poczekać na
  nową (wejście do miasta i z powrotem).
- Kategoria 29 (nierozpoznany harvestable), `@TYPE@guid` w `ZoneIdParser`, loot z
  mobków/skrzynek — wciąż otwarte, nieporuszone w tej sesji.

## Pliki zmienione dziś (do referencji)

- `AlbionCompanion.Service/*`, `AlbionCompanion.ServiceInstaller/*` (nowe projekty)
- `AlbionCompanion.Gathering/LiveEvents/*` (`LiveEventMessage`, `LiveEventPipeServer`,
  `LiveEventPipeClient`, `IGatheringLiveEventSource`)
- `AlbionCompanion.Gathering/{IGameProcessWatcher,GameProcessWatcher,IServiceStatusProvider,
  WindowsServiceStatusProvider,AppClientHostBuilder}.cs`
- `AlbionCompanion.Gathering/{ICharacterService,CharacterService,GatheringSessionService,
  GatheringLiveState,IGatheringLiveState,LocalPlayerTracker,AppHostBuilder}.cs`
- `AlbionCompanion.App/{MauiProgram.cs,App.xaml.cs,FileLoggerProvider.cs,
  Components/ConnectionBanner.razor,Components/Pages/Settings.razor}`
- `AlbionCompanion.App/wwwroot/{app.css,index.html}` (light mode, tema toggle, bannery)
- `scripts/redeploy-service.ps1` (nowy)
- `docs/superpowers/specs/2026-08-04-background-sniffer-service-design.md`,
  `docs/superpowers/plans/2026-08-04-background-sniffer-service.md`
