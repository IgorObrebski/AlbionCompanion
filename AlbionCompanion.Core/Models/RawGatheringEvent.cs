using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class RawGatheringEvent
{
    [Key]
    public long Id { get; set; }
    public Guid? SessionId { get; set; }
    public GatheringSession? Session { get; set; }
    public byte PhotonCode { get; set; }
    public byte? SemanticEventCode { get; set; }
    // Human-readable name for SemanticEventCode (e.g. "HarvestFinished" for 61), resolved from
    // AlbionCompanion.Sniffer.AlbionEvents.AlbionEventCode at write time - added so anyone
    // browsing this table doesn't have to memorize/cross-reference magic numbers. Null when the
    // code is absent (SemanticEventCode itself null) or isn't a name we've confirmed yet.
    public string? EventName { get; set; }
    public string ParametersJson { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
