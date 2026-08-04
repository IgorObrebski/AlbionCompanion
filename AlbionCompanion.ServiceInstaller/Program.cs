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
    File.Copy(oldDbPath, newDbPath);
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

foreach (var file in Directory.GetFiles(sourcePublishPath, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(sourcePublishPath, file);
    var destination = Path.Combine(serviceInstallPath, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(file, destination, overwrite: true);
}

var serviceExePath = Path.Combine(serviceInstallPath, "AlbionCompanion.Service.exe");

// Step 3: register the service (idempotent - stop and delete first if it already exists, e.g. a
// reinstall). Deleting a running service just marks it for deletion once stopped, so stop it first.
RunScAndWait($"stop {ServiceName}");
RunScAndWait($"delete {ServiceName}");
var createResult = RunScAndWait($"create {ServiceName} binPath= \"{serviceExePath}\" start= auto");
if (createResult != 0)
{
    Console.WriteLine($"ERROR: `sc create` failed with exit code {createResult}. Make sure this installer is run as Administrator.");
    return 1;
}

// Step 4: grant the current interactive user START/STOP/QUERY/CHANGE-CONFIG rights, so the App's
// Settings page never needs a UAC prompt to start/stop the service. Grants:
//   SY (LocalSystem)            - full control, matches the OS default.
//   BA (Administrators)         - full control, matches the OS default.
//   IU (Interactive Users)      - query/enumerate/interrogate/read-control, matches the OS default.
//   the current user's SID      - query, enumerate, start, stop, change-config, interrogate,
//                                  user-defined-control, read-control.
//   SU (Service logon accounts) - query/enumerate/interrogate/read-control, matches the OS default.
//
// Verified: this SDDL string was round-tripped through .NET's
// System.Security.AccessControl.RawSecurityDescriptor parser (the same SDDL grammar Win32's
// ConvertStringSecurityDescriptorToSecurityDescriptor/`sc sdset` use) and parsed without error,
// producing the expected five ACEs. `sc sdset` itself could not be exercised against a live
// service in this sandbox (no elevation available) - see task-15-report.md for details.
var currentUserSid = GetCurrentUserSid();
var sddl = $"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWRPWPDCLOCRRC;;;{currentUserSid})(A;;CCLCSWLOCRRC;;;SU)";
var sdsetResult = RunScAndWait($"sdset {ServiceName} \"{sddl}\"");
if (sdsetResult != 0)
{
    Console.WriteLine($"WARNING: `sc sdset` failed with exit code {sdsetResult}. The service was created but the current user may need admin rights (or a UAC prompt) to start/stop it from the App.");
}

RunScAndWait($"start {ServiceName}");

Console.WriteLine("Done.");
return 0;

static string GetCurrentUserSid()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return identity.User!.Value;
}

static int RunScAndWait(string arguments)
{
    var process = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true })!;
    Console.WriteLine(process.StandardOutput.ReadToEnd());
    process.WaitForExit();
    return process.ExitCode;
}
