# Sesja 2026-07-16 — od "sniffer nic nie łapie" do działającego pipeline'u gatheringu

## Punkt startowy

Sniffer parsował pakiety Photon, ale prawie wszystkie eventy kończyły się jako
"unsupported type" albo pokazywały tylko `Move`/`Leave`. Npcap fałszywie zgłaszał brak
instalacji mimo że był zainstalowany.

## Co zrobiliśmy, w kolejności przyczynowej

1. **Fix Npcap** (`NpcapRegistryChecker.cs`) — Npcap rejestruje się w 32-bitowym widoku
   rejestru (`WOW6432Node`) nawet na 64-bit Windows; sprawdzamy teraz oba widoki.

2. **Root cause parsowania: Albion przeszedł na Photon Protocol18** (patch 2026-04-13), a nasz
   vendorowany deserializer był napisany pod stary Protocol16. Przepisaliśmy
   `Protocol16Type.cs`/`Protocol16Deserializer.cs` pod nową tabelę typów (LEB128 varints,
   CompressedInt/Long z zigzag encoding, zero-sentinele, "slim custom types") na podstawie
   `Nouuu/Albion-Online-OpenRadar` (aktywnie utrzymywany projekt).

3. **Odkrycie: prawdziwy kod eventu/operacji siedzi w parametrze, nie w zewnętrznym polu.**
   Zewnętrzny `EventData.Code` i `PhotonResponse.OperationCode` są prawie zawsze `1` — to tylko
   generyczny wrapper. Prawdziwy semantyczny kod eventu jest w `params[252]`, a dla
   RESPONSE/REQUEST w `params[253]`. Potwierdzone korelacją częstotliwości z
   HarvestStart/HarvestFinished/ChatMessage/UpdateFame z aktualnej listy kodów
   (`AlbionEventCode.cs`, zresynchronizowana z OpenRadar zamiast martwego forka AO-Radar).

4. **`AlbionCompanion.Gathering`** (nowy projekt) — `GatheringSessionService` wg spec: start/end
   sesji, usuwanie pustych przebiegów, gwarancja jednej aktywnej sesji.

5. **`ZoneTracker`** — start/koniec sesji na podstawie zmiany strefy (`RESPONSE` z
   `params[253]==2`, strefa w `params[8]`). Dwie iteracje naprawy:
   - Bank i market w mieście mają **własne, odrębne zoneId** (Fort Sterling=4000,
     Bank=4001, Market=4002) — naiwne "wróciłem do strefy startowej" fałszywie traktowało
     wizytę w banku jako wyprawę na zbieractwo. Naprawione przez `ZoneCatalog`, które pobiera
     `zones.json` z `ao-data/ao-bin-dumps` i klasyfikuje **każdą** strefę (miasto/safe-area vs
     otwarty świat), zamiast pamiętać jedną "strefę domową". To samo źródło dało nam też
     czytelne nazwy lokacji za darmo (np. "Cairn Camain" zamiast gołego ID).

6. **`GatheringEventRouter`** — zasila sesję prawdziwymi eventami zbieractwa.
   - Używa `HarvestStart` (59), nie `HarvestFinished` (61): `HarvestFinished` odpala tylko przy
     **pełnym wyczerpaniu złoża do zera**, więc pomija najczęstszy przypadek (zaczynanie
     kopania złoża, które ma np. 2/5 ładunków, albo niedokończenie złoża do końca).
     `HarvestStart` odpala na każdym swingu.
   - **Bug znaleziony przez usera**: `HarvestStart` jest broadcastowany do wszystkich w
     zasięgu, nie tylko do gracza — sesja łapała cudze akcje. Naprawione przez
     `LocalPlayerTracker`, który śledzi własny `entityId` gracza (zmienia się przy każdej
     zmianie strefy!) z tej samej odpowiedzi `253:2`, i filtruje eventy po aktorze.
   - **Drugi bug znaleziony przez usera**: sesja pokazywała jeden typ surowca przez całą
     sesję, mimo że user kopał 3 różne rudy (żelazo/cyna/tytan). Okazało się, że kod
     kategorii surowca (`params[4]` w HarvestStart) koduje **kategorię niezależnie od tieru**
     (Żelazo T4/Cyna T3/Tytan T5 to wszystko kategoria "Ore"=27). Potwierdzone w
     `Nouuu/Albion-Online-OpenRadar`'s `HarvestablesDatabase.js`:
     ```
     0-5→WOOD, 6-10→ROCK, 11-15→FIBER, 16-22→HIDE, 23-27→ORE
     ```
     Tier trzeba osobno wyciągnąć z broadcastu `NewHarvestableObject` (kod 40, tier w
     `params[7]`), korelując po ID węzła (`HarvestableNodeTracker.cs`). Wynik:
     `itemId = "T{tier}_{CATEGORY}"` (np. "T4_ORE") gdy znamy tier, inaczej fallback na goły
     kod kategorii.

7. **Architektoniczny bug**: `Convert.ToByte()` na wartości spoza zakresu bajtu rzucał
   `OverflowException` **wewnątrz synchronicznego wywołania eventu Photon** (w
   `AlbionEventNameLogger`/`GatheringEventRouter`), co propagowało się z powrotem do
   `AlbionPhotonParser.ReceivePacket` i **przerywało parsowanie reszty pakietu UDP** (pakiet
   może mieć wiele komend naraz). Naprawione dwutorowo: (a) bezpieczna konwersja zamiast
   rzucającej, (b) `AlbionPhotonParser.RaiseIsolated` — subskrybenci eventów są teraz
   izolowani, więc błąd w jednym handlerze nie niszczy reszty pakietu.

8. **`ItemDictionaryService`** — import `items.json` z `ao-data/ao-bin-dumps`
   (`formatted/items.json`, nie gołe `items.json` jak zakładał pierwotny spec) przy pierwszym
   uruchomieniu. Realny format nie ma pól `Tier`/`ShopCategory` wprost — wyprowadzamy je z
   konwencji `UniqueName` (`T4_ORE` → tier=4, group="ORE").

## Stan na koniec dnia

Pełny pipeline działa end-to-end: sniffer → deserializer P18 → rozpoznanie eventów →
`ZoneTracker` (poprawnie ignoruje bank/market) → `GatheringEventRouter` (filtr po graczu,
tier+kategoria) → SQLite. Potwierdzone na żywych danych wielokrotnie. 67/67 testów
jednostkowych zielonych.

**Znane ograniczenie (do ogarnięcia jutro):** tier surowca rozwiązuje się poprawnie tylko dla
węzłów, których broadcast `NewHarvestableObject` złapaliśmy (węzeł musiał "spawnąć"/stać się
widoczny PO starcie nasłuchu). W dzisiejszym teście złapaliśmy to dla 1 z 12 kopanych węzłów —
reszta wraca jako goły kod kategorii bez tieru.

## TODO na jutro

1. **Zdekodować `NewSimpleHarvestableObjectList` (kod 39).** To broadcast zawierający listę
   ID węzłów + kilka tablic bajtów (widać w logu jako `System.Byte[]` odpowiednio
   sformatowane przez `AlbionEventLogger`). Wygląda na okresowy re-sync stanu WSZYSTKICH
   pobliskich węzłów naraz (nie tylko nowo zaspawnionych) — potencjalnie druga, bardziej
   niezawodna szansa na zdobycie tieru dla węzłów, których pierwotny spawn przegapiliśmy.
   Trzeba ustalić dokładnie który bajt w której tablicy to tier, a który to co innego
   (widzieliśmy min. 4 równoległe tablice per broadcast: `params[0]`=lista nodeId,
   `params[1]`, `params[2]`, `params[3]`=lista koordynatów [x,y], `params[4]`).
2. Po rozszyfrowaniu — podłączyć jako dodatkowe źródło do `HarvestableNodeTracker`, obok
   `NewHarvestableObject`.
3. Przetestować na żywo, że pokrycie tier/kategoria wzrosło (docelowo bliskie 100% zamiast
   dzisiejszego 1/12).

## Pliki zmienione dziś (do referencji)

- `AlbionCompanion.Sniffer/Vendor/Photon16/{Protocol16Type.cs, Protocol16Deserializer.cs}` — port na P18
- `AlbionCompanion.Sniffer/AlbionEvents/{AlbionEventCode.cs, AlbionEventCodeMapper.cs, AlbionEventNameLogger.cs, AlbionEventLogger.cs}`
- `AlbionCompanion.Sniffer/Protocol16/{AlbionPhotonParser.cs, IPhotonParser.cs, PhotonRequest.cs}`
- `AlbionCompanion.Sniffer/Npcap/NpcapRegistryChecker.cs`
- `AlbionCompanion.Sniffer/PacketCapture/{PacketSniffer.cs, UdpPayloadFilter.cs}`
- `AlbionCompanion.Gathering/*` (cały nowy projekt)
- `AlbionCompanion.ConsoleHost/Program.cs` (DI wiring, tryb `ALBION_DEBUG_PORTS`)
- `specs/albion-companion-context.md` (zaktualizowane sekcje: lokacje, słownik przedmiotów)
