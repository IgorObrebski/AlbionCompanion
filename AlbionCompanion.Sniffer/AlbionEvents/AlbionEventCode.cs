namespace AlbionCompanion.Sniffer.AlbionEvents;

// Values sourced from Nouuu/Albion-Online-OpenRadar's eventcodes.go (actively maintained,
// patched alongside game protocol changes - unlike the previously used rafalfigura/AO-Radar
// mapping, which was confirmed stale: a real gathering capture never produced any of its
// HarvestStart/HarvestFinished/UpdateFame codes). Extend/correct as more codes are confirmed
// against real debug_packets.log captures.
public enum AlbionEventCode : byte
{
    Leave = 1, // Unconfirmed: fires as often as Move in captures, which is suspicious for a "player left view" event - verify before relying on it.
    JoinFinished = 2,
    Move = 3,
    Teleport = 4,
    HealthUpdate = 6,
    NewSimpleHarvestableObject = 38,
    NewSimpleHarvestableObjectList = 39,
    NewHarvestableObject = 40,
    HarvestableChangeState = 46,
    InventoryPutItem = 26,
    InventoryDeleteItem = 27,
    HarvestStart = 59,
    HarvestCancel = 60,
    HarvestFinished = 61,
    ChatMessage = 73,
    UpdateMoney = 81,
    UpdateFame = 82,
}
