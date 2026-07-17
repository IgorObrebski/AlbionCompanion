namespace AlbionCompanion.Gathering;

// Marker interface: construction alone subscribes to IPhotonParser.OnEventReceived, mirroring
// IHarvestableNodeTracker/ILocalPlayerTracker - DI just needs a type to resolve and hold alive
// for the process lifetime.
public interface IRawEventRecorder
{
}
