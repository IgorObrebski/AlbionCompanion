using System.Diagnostics;

const string ServiceName = "AlbionCompanionService";

var programDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");
var serviceInstallPath = Path.Combine(programDataPath, "service");
Directory.CreateDirectory(serviceInstallPath);

Console.WriteLine("Installing AlbionCompanion sniffer service...");

// Step 1: migrate the old per-user database/logs to the shared location, if not already done.
var oldAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
var oldDbPath = Path.Combine(oldAppDataPath, "albion.db");
var newDbPath = Path.Combine(programDataPath, "albion.db");
if (File.Exists(oldDbPath) && !File.Exists(newDbPath))
{
    Console.WriteLine($"Migrating existing database from {oldDbPath} to {newDbPath}...");
    try
    {
        File.Copy(oldDbPath, newDbPath);
    }
    catch (IOException ex)
    {
        Console.WriteLine($"ERROR: failed to migrate database from {oldDbPath} to {newDbPath}: {ex.Message}");
        return 1;
    }
}

// Step 2: copy the published Service binaries (this installer expects to be run from the same
// folder as a `dotnet publish` output of AlbionCompanion.Service, or that output copied alongside
// this exe under a "service-publish" subfolder).
var sourcePublishPath = Path.Combine(AppContext.BaseDirectory, "service-publish");
if (!Directory.Exists(sourcePublishPath))
{
    Console.WriteLine($"ERROR: expected published service output at {sourcePublishPath} - run `dotnet publish AlbionCompanion.Service -o <this exe's folder>/service-publish` first.");
    return 1;
}

try
{
    foreach (var file in Directory.GetFiles(sourcePublishPath, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourcePublishPath, file);
        var destination = Path.Combine(serviceInstallPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }
}
catch (IOException ex)
{
    Console.WriteLine($"ERROR: failed to copy service binaries from {sourcePublishPath} to {serviceInstallPath}: {ex.Message}");
    Console.WriteLine("If the service is currently running, stop it first (its binaries may be locked).");
    return 1;
}

var serviceExePath = Path.Combine(serviceInstallPath, "AlbionCompanion.Service.exe");

// Step 3: register the service (idempotent - stop and delete first if it already exists, e.g. a
// reinstall). Deleting a running service just marks it for deletion once stopped, so stop it first
// and then poll until it actually reaches STOPPED - `sc stop` only initiates the stop and returns
// immediately, it doesn't wait for the transition, so proceeding straight to `delete` can race a
// service that's still shutting down and hit "marked for deletion" again.
RunScAndWait($"stop {ServiceName}");
if (!WaitForServiceStopped(ServiceName, TimeSpan.FromSeconds(10)))
{
    Console.WriteLine($"WARNING: {ServiceName} did not reach STOPPED state within 10s of `sc stop`; proceeding to delete anyway, but this may fail if the service is still shutting down.");
}
RunScAndWait($"delete {ServiceName}");
var createResult = RunScAndWait($"create {ServiceName} binPath= \"{serviceExePath}\" start= auto");
if (createResult != 0)
{
    Console.WriteLine($"ERROR: `sc create` failed with exit code {createResult}. Make sure this installer is run as Administrator.");
    return 1;
}

// Step 4: grant the current interactive user START/STOP/QUERY rights, so the App's Settings page
// never needs a UAC prompt to start/stop the service. Grants:
//   SY (LocalSystem)            - full control, matches the OS default.
//   BA (Administrators)         - full control, matches the OS default.
//   IU (Interactive Users)      - query/enumerate/interrogate/read-control, matches the OS default.
//   the current user's SID      - query-config, enumerate-dependents, enumerate-service,
//                                  start, stop, interrogate, read-control. Deliberately does NOT
//                                  include DC (SERVICE_CHANGE_CONFIG) - granting that to an
//                                  unelevated interactive user is a local privilege escalation: it
//                                  lets that user rewrite the service's binPath to any executable,
//                                  which then runs as LocalSystem on the next start. The design only
//                                  calls for start/stop/query rights, not config-change rights.
//   SU (Service logon accounts) - query/enumerate/interrogate/read-control, matches the OS default.
//
// Two-letter SDDL codes used for the current user's ACE: CC=query-config, LC=enum-dependents,
// SW=enum-service, RP=start, WP=stop, LO=list-object-names, CR=read-control, RC=read-control
// (kept for parity with the other ACEs' trailing RC). DC (change-config), DT (pause/continue), and
// WD (interrogate-as-write, i.e. broader than needed) are intentionally omitted.
//
// Verified: this SDDL string was round-tripped through .NET's
// System.Security.AccessControl.RawSecurityDescriptor parser (the same SDDL grammar Win32's
// ConvertStringSecurityDescriptorToSecurityDescriptor/`sc sdset` use) and parsed without error,
// producing the expected five ACEs. `sc sdset` itself could not be exercised against a live
// service in this sandbox (no elevation available) - see task-15-report.md for details.
var currentUserSid = GetCurrentUserSid();
var sddl = $"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWRPWPLOCRRC;;;{currentUserSid})(A;;CCLCSWLOCRRC;;;SU)";
var sdsetResult = RunScAndWait($"sdset {ServiceName} \"{sddl}\"");
if (sdsetResult != 0)
{
    Console.WriteLine($"WARNING: `sc sdset` failed with exit code {sdsetResult}. The service was created but the current user may need admin rights (or a UAC prompt) to start/stop it from the App.");
}

var startResult = RunScAndWait($"start {ServiceName}");
if (startResult != 0)
{
    Console.WriteLine($"ERROR: `sc start` failed with exit code {startResult}. The service was created and registered, but did not start - check for a missing runtime dependency or a bad binPath before assuming this installer succeeded.");
    return 1;
}

Console.WriteLine("Done.");
return 0;

static string GetCurrentUserSid()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return identity.User!.Value;
}

static bool WaitForServiceStopped(string serviceName, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var process = Process.Start(new ProcessStartInfo("sc.exe", $"query {serviceName}") { UseShellExecute = false, RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // If the service doesn't exist (first install) or is already stopped, there's nothing to
        // wait for.
        if (process.ExitCode != 0 || output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Thread.Sleep(500);
    }

    return false;
}

static int RunScAndWait(string arguments)
{
    try
    {
        var process = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true })!;
        Console.WriteLine(process.StandardOutput.ReadToEnd());
        process.WaitForExit();
        return process.ExitCode;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: failed to run `sc.exe {arguments}`: {ex.Message}");
        return -1;
    }
}
