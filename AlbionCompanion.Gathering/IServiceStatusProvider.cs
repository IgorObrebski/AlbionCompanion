namespace AlbionCompanion.Gathering;

// Pending is its own state (rather than collapsing StartPending/ContinuePending into Stopped) so
// Settings.razor can hide the "start" button while a start is already in flight - otherwise the
// button could reappear during a pending start and a second Start() call would throw.
public enum ServiceStatus { Running, Stopped, Pending, NotInstalled }

public interface IServiceStatusProvider
{
    Task<ServiceStatus> GetStatusAsync();
    Task StartAsync();
}
