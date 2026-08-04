namespace AlbionCompanion.Gathering;

public enum ServiceStatus { Running, Stopped, NotInstalled }

public interface IServiceStatusProvider
{
    Task<ServiceStatus> GetStatusAsync();
    Task StartAsync();
}
