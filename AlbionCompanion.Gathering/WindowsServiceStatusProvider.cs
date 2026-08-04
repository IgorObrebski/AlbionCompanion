using System.ServiceProcess;

namespace AlbionCompanion.Gathering;

// Wraps ServiceController for the one Windows Service this app cares about. The installer (a
// separate console app) grants the interactive user START/STOP rights on this specific service
// via `sc sdset`, so StartAsync below never triggers a UAC prompt when run from the App.
public class WindowsServiceStatusProvider : IServiceStatusProvider
{
    private const string ServiceName = "AlbionCompanionService";

    public Task<ServiceStatus> GetStatusAsync()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            var status = controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceStatus.Running,
                _ => ServiceStatus.Stopped,
            };
            return Task.FromResult(status);
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws this when the named service isn't registered at all.
            return Task.FromResult(ServiceStatus.NotInstalled);
        }
    }

    public Task StartAsync()
    {
        // ServiceController has no async WaitForStatus overload (confirmed against
        // System.ServiceProcess.ServiceController 10.0.0 on net10.0-windows), so the
        // blocking wait is pushed onto the thread pool instead.
        return Task.Run(() =>
        {
            using var controller = new ServiceController(ServiceName);
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
        });
    }
}
