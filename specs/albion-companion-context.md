# ALBION COMPANION APP – PEŁNY KONTEKST PROJEKTU
> Ten plik zawiera pełną specyfikację i decyzje architektoniczne do przekazania Claude Code w terminalu Ridera.

---

## CEL

Tworzymy desktopową aplikację **AlbionCompanion** dla graczy Albion Online (Windows).  
Open-source, publiczne repozytorium GitHub.  
MVP skupia się wyłącznie na **Snifferze sieciowym** – UI i wizualizacja danych są poza zakresem MVP.

---

## STACK TECHNOLOGICZNY

| Warstwa | Technologia |
|---|---|
| UI (na później) | Blazor Hybrid (.NET MAUI) |
| Logika + Sniffer | .NET 8/9 (C#) |
| Baza danych | SQLite (plik w `%appdata%`) |
| ORM | Entity Framework Core + Microsoft.EntityFrameworkCore.Sqlite |
| Przechwyt pakietów | SharpPcap + PacketDotNet |
| Parsowanie Photon | PhotonPackageParser (NuGet – `0blu/PhotonPackageParser`) |
| Słownik przedmiotów | JSON z `ao-data/ao-bin-dumps` (GitHub, importowany przy pierwszym uruchomieniu) |

**Uprawnienia:** Aplikacja wymaga praw **administratora** (app.manifest z `requireAdministrator`).  
**Npcap:** Przy starcie sprawdzamy czy Npcap jest zainstalowany. Jeśli nie – pobieramy i uruchamiamy installer automatycznie.

---

## STRUKTURA SOLUCJI

```
AlbionCompanion.sln
├── AlbionCompanion.Core/           # Modele EF, DbContext, interfejsy
│   ├── Models/
│   ├── Data/
│   └── Interfaces/
├── AlbionCompanion.Sniffer/        # Logika sniffera - PRIORYTET MVP
│   ├── PacketCapture/              # SharpPcap wrapper
│   ├── Protocol16/                 # Warstwa Photon (PhotonPackageParser)
│   └── AlbionEvents/               # Nasze kody eventów Albion (od zera)
├── AlbionCompanion.Gathering/      # Logika sesji zbierania
│   └── GatheringSessionService.cs
└── AlbionCompanion.App/            # MAUI/Blazor UI (poza zakresem MVP)
```

---

## MODELE BAZY DANYCH (EF Core)

```csharp
// Plik już istnieje w projekcie jako punkt wyjścia:

public class GatheringSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }   // null = sesja aktywna lub DC
    public string StartLocation { get; set; }
    public int TotalFameEarned { get; set; }
    public ICollection<GatheredItem> GatheredItems { get; set; } = new List<GatheredItem>();
    public ICollection<FameLog> FameLogs { get; set; } = new List<FameLog>();
}

public class GatheredItem
{
    [Key] public int Id { get; set; }
    public Guid SessionId { get; set; }
    public GatheringSession Session { get; set; }
    public string ItemId { get; set; }
    public int Amount { get; set; }
    public DateTime Timestamp { get; set; }
}

public class FameLog
{
    [Key] public int Id { get; set; }
    public Guid SessionId { get; set; }
    public GatheringSession Session { get; set; }
    public string FameType { get; set; } // "Gathering", "MobKill"
    public int Amount { get; set; }
    public DateTime Timestamp { get; set; }
}

public class FlipLog
{
    [Key] public int Id { get; set; }
    public string ItemId { get; set; }
    public string OrderType { get; set; } // "Buy" lub "Sell"
    public int PricePerItem { get; set; }
    public int Amount { get; set; }
    public int TaxPaid { get; set; }
    public string Location { get; set; }
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } // "LiveSniffer" lub "MailboxSync"
}

public class ItemDictionary
{
    [Key] public string UniqueName { get; set; } // np. "T4_ORE"
    public string DisplayNamePL { get; set; }
    public string DisplayNameEN { get; set; }
    public int Tier { get; set; }
    public string ItemGroup { get; set; }
}

public class PriceCache
{
    // Composite key: (ItemId, Location) konfigurowany w DbContext
    public string ItemId { get; set; }
    public string Location { get; set; }
    public int SellPriceMin { get; set; }
    public int BuyPriceMax { get; set; }
    public DateTime LastUpdated { get; set; }
}
```

---

## INTERFEJSY (Clean Architecture)

```csharp
// Sniffer
public interface IPacketSniffer
{
    void Start();
    void Stop();
    event EventHandler<byte[]> OnPhotonPayloadReceived;
}

// Photon Parser
public interface IPhotonParser
{
    void HandlePayload(byte[] payload);
    event EventHandler<PhotonEvent> OnEventReceived;
    event EventHandler<PhotonResponse> OnResponseReceived;
}

// Gathering
public interface IGatheringSessionService
{
    Task StartSessionAsync(string location);
    Task EndSessionAsync();
    Task AddItemAsync(string itemId, int amount);
    Task AddFameAsync(string fameType, int amount);
    Task<GatheringSession?> GetActiveSessionAsync();
}

// Item Dictionary
public interface IItemDictionaryService
{
    Task<IEnumerable<ItemDictionary>> SearchItemsAsync(string query);
    Task<ItemDictionary?> GetItemByIdAsync(string id);
    Task SeedFromJsonAsync(); // importuje z ao-bin-dumps przy pierwszym uruchomieniu
}

// Price Provider
public interface IPriceProvider
{
    Task<IEnumerable<PriceCache>> GetPricesAsync(IEnumerable<string> itemIds, string location);
}
```

---

## LOGIKA BIZNESOWA SNIFFERA

### Przepływ danych:
```
Gra (UDP 5055/5056)
  → SharpPcap (przechwyt surowych pakietów)
  → PacketDotNet (ekstrakcja payload UDP)
  → PhotonPackageParser (deserializacja Protocol16)
  → AlbionEventHandler (nasze kody eventów – od zera)
  → GatheringSessionService (zapis do SQLite przez EF Core)
  → C# Event (do przyszłego UI)
```

### Porty:
- UDP **5055** i **5056** – oba nasłuchujemy

### Parser Photon – dwie warstwy:
1. **Protocol16 (NIE piszemy od zera):** Używamy NuGet `PhotonPackageParser` by `0blu`. To czyste parsowanie protokołu Photon, analogicznie do używania Newtonsoft.Json – nie reimplementujemy formatu binarnego.
2. **Logika Albion (piszemy od zera):** Mapowanie EventCode/OperationCode na zdarzenia gry (loot, fame, enter/exit zone). Będziemy odkrywać kody iteracyjnie przez logowanie wszystkich pakietów.

### Strategia odkrywania kodów eventów:
**KROK 1 (logging mode):** Zanim cokolwiek interpretujemy, logujemy WSZYSTKIE przychodzące eventy do pliku `debug_packets.log` w formacie:
```
[timestamp] EVENT code=XX params={klucz:wartość, ...}
[timestamp] RESPONSE opCode=XX returnCode=YY params={...}
```
Gracz uruchamia aplikację, idzie zbierać surowce, wraca – analizujemy log i identyfikujemy które kody odpowiadają zbieraniu, fame, wejściu/wyjściu ze strefy.

---

## LOGIKA SESJI GATHERING

- **Start sesji:** Gracz wychodzi z bramy miasta (zone change event: miasto → dzicz)
- **Koniec sesji:** Gracz wchodzi do strefy miasta (zone change: dzicz → miasto)
- **DC Handling:** Sesja z `EndTime = null` = otwarta. Przy ponownym uruchomieniu:
  - Jeśli gracz loguje się w dziczy → kontynuuj istniejącą sesję (zostaw `EndTime = null`)
  - Jeśli gracz loguje się w mieście → zamknij sesję z `EndTime = DateTime.UtcNow`
- **Puste przebiegi:** Jeśli sesja kończy się z zerową liczbą zebranych przedmiotów → usuń ją z bazy
- **Gwarancja:** Zawsze max jedna sesja z `EndTime = null` w bazie

### Dekodowanie lokacji (nazw stref) - ROZWIĄZANE 2026-07-16

Zone id (`RESPONSE` z `params[253]==2`, identyfikator w `params[8]`) mapujemy teraz na nazwę i typ
strefy przez `ZoneCatalog` (`AlbionCompanion.Gathering/ZoneCatalog.cs`), które pobiera
`zones.json` z `ao-data/ao-bin-dumps` (mirror: Nouuu/Albion-Online-OpenRadar). Ten sam plik,
którego użyjemy do słownika przedmiotów, zawiera też pełną mapę zoneId→{name, type}.

To rozwiązało też realny bug: bank i market w mieście mają **własne, odrębne zoneId** (np.
Fort Sterling=4000, Bank=4001, Market=4002 - wszystkie `PLAYERCITY_SAFEAREA_*`), więc naiwne
"wróciłem do strefy startowej" fałszywie traktowało wizytę w banku jako wyprawę na zbieractwo.
`ZoneTracker` teraz klasyfikuje **każdą** strefę (miasto/safe-area vs otwarty świat) przez
`IZoneCatalog.IsCityOrSafeAreaAsync`, zamiast pamiętać jedną "strefę domową" - wejście do
dowolnej safe-area (miasto, bank, market) kończy aktywną sesję (no-op jeśli jej nie było),
wejście do prawdziwej dziczy/dungeonu ją zaczyna, z `StartLocation` = prawdziwa nazwa strefy
(np. "Cairn Camain") zamiast gołego ID.

Nadal do zrobienia: dynamiczne instancje (dungeony, hideouty, Mists) używają w praktyce
suchowanego ID (`"1234-5"`) albo syntetycznych kluczy nienumerycznych (`@MISTS@<guid>`) - obecna
implementacja traktuje nierozpoznane zoneId jako "otwarty świat" (bezpieczny fallback), ale nie
wyciąga z nich prawdziwej nazwy.

---

## SŁOWNIK PRZEDMIOTÓW – źródło danych

- **Repo:** `https://github.com/ao-data/ao-bin-dumps`
- **Plik:** `items.json` (raw URL: `https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/items.json`)
- **Kiedy:** Pobieramy i importujemy do SQLite przy **pierwszym uruchomieniu** (lub gdy tabela `ItemDictionary` jest pusta)
- **Format JSON:** Tablica obiektów z polami `UniqueName`, `LocalizedNames` (słownik języków), `Tier`, `ShopCategory`

---

## NPCAP – automatyczna instalacja

```csharp
// Pseudokod logiki startowej
bool isNpcapInstalled = CheckNpcapRegistry(); // HKLM\SOFTWARE\Npcap
if (!isNpcapInstalled)
{
    DownloadNpcapInstaller(); // z nmap.org/npcap
    RunInstallerWithAdminRights(); // Process.Start z /S dla silent lub normalny dialog
}
```

---

## CO BUDUJEMY TERAZ (zakres MVP Sniffer)

1. ✅ Utwórz solucję z projektami według powyższej struktury
2. ✅ Skonfiguruj `app.manifest` z `requireAdministrator` w projekcie startowym
3. ✅ Zainstaluj NuGety: `SharpPcap`, `PacketDotNet`, `PhotonPackageParser`, `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Tools`
4. ✅ Utwórz `AppDbContext` z DbSetami dla wszystkich modeli + composite key dla `PriceCache`
5. ✅ Dodaj pierwszą migrację EF Core
6. ✅ Zaimplementuj `PacketSniffer` (SharpPcap, nasłuch UDP 5055/5056)
7. ✅ Zaimplementuj `PhotonParser` (wrapper na PhotonPackageParser)
8. ✅ Zaimplementuj `AlbionEventLogger` – loguje WSZYSTKIE eventy do pliku (debug mode)
9. ✅ Sprawdź Npcap przy starcie, zainstaluj jeśli brak
10. ✅ Prosty `Program.cs` (Console App na start, nie MAUI) który odpala sniffer i wyświetla logi

**UI (Blazor MAUI) jest poza zakresem MVP – dodamy po tym jak sniffer działa.**

---

## WZORCE PROJEKTOWE

- **SOLID** – każdy moduł ma jeden interfejs, jedną odpowiedzialność
- **Vertical Slices** – moduły niezależne, dzielą tylko DbContext
- **Clean Architecture** – interfejsy w Core, implementacje w osobnych projektach
- **Dependency Injection** – Microsoft.Extensions.DependencyInjection

---

## UWAGI DODATKOWE

- Projekt jest **open-source** – kod ma być czytelny i dobrze skomentowany
- Nazwy klas/metod po **angielsku**, komentarze mogą być po polsku
- Wszystkie operacje I/O (baza, sieć) są **async/await**
- Logika parsowania eventów Albion będzie ewoluować iteracyjnie – projektuj z myślą o łatwym dodawaniu nowych `EventCode` handlerów
- Aplikacja docelowo Windows-only (x64), .NET 8 minimum
