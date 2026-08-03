using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class Character
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    // Exact in-game character name - matched against the nickname carried by the zone-join
    // response and the periodic PlayerAnnounce broadcast (see LocalPlayerTracker) to identify
    // which character is currently playing, without any manual "who am I" picker.
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
