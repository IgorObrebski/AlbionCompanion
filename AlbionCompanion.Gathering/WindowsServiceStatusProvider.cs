using System.ComponentModel;
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
                ServiceControllerStatus.StartPending => ServiceStatus.Pending,
                ServiceControllerStatus.ContinuePending => ServiceStatus.Pending,
                ServiceControllerStatus.StopPending => ServiceStatus.Pending,
                ServiceControllerStatus.PausePending => ServiceStatus.Pending,
                _ => ServiceStatus.Stopped,
            };
            return Task.FromResult(status);
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws this when the named service isn't registered at all.
            return Task.FromResult(ServiceStatus.NotInstalled);
        }
        catch (Win32Exception)
        {
            // Access denied or another OS-level failure reading the service's status - the spec
            // requires this surfaced as a status rather than propagating and crashing the caller
            // (Settings.razor's poll timer, previously unguarded against exactly this).
            return Task.FromResult(ServiceStatus.NotInstalled);
        }
    }

    public Task StartAsync()
    {
        // ServiceController has no async WaitForStatus overload (confirmed against
        // System.ServiceProcess.ServiceController 10.0.0 on net10.0-windows), so the
        // blocking wait is pushed onto the thread pool instead. Win32Exception (e.g. access
        // denied - the SDDL grant wasn't applied, or the caller isn't the right user) and
        // TimeoutException (WaitForStatus never reached Running within the timeout) are both
        // swallowed here rather than propagating - GetStatusAsync's next poll reports whatever the
        // real state turned out to be, which is the actionable signal for the UI either way.
        return Task.Run(() =>
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
            catch (Win32Exception)
            {
            }
            catch (System.TimeoutException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        });
    }
}
