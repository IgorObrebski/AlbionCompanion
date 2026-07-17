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
    public string ParametersJson { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
