namespace AlbionCompanion.Gathering;

public sealed record ZoneInfo(string Name, string Type);

public interface IZoneCatalog
{
    Task<ZoneInfo?> GetZoneAsync(int zoneId);
    Task<bool> IsCityOrSafeAreaAsync(int zoneId);
}
