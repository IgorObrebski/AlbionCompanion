using System.Diagnostics;

namespace AlbionCompanion.Gathering;

// Confirmed on this machine 2026-08-04: Albion Online runs as two processes, "Albion-Online.exe"
// (the game client) and "Albion-Online_BE.exe" (a helper/backend process) - watch for either so a
// launch sequence that starts them in either order still counts as "the game is running."
public class GameProcessWatcher : IGameProcessWatcher
{
    private static readonly string[] ProcessNames = { "Albion-Online", "Albion-Online_BE" };

    public bool IsGameRunning() =>
        ProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0);
}
