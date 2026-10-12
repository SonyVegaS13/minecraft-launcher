using System;
using System.IO;

namespace SolarisLauncher;

internal static class SolarisDirectories
{
    internal static string StateDir
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (App.IsDeveloperMode && !App.IsDevChannel)
                return Path.Combine(root, "Solaris-Neon-Dev");

            // Preserve the existing DEV profile when consolidating its launcher
            // into the one canonical installation. A new player (or one who
            // only used the stable launcher) keeps the standard Solaris profile.
            string legacyDev = Path.Combine(root, "Solaris-Neon-Dev");
            if (Directory.Exists(legacyDev) &&
                (File.Exists(Path.Combine(legacyDev, "account.json")) ||
                 File.Exists(Path.Combine(legacyDev, "settings.json")) ||
                 Directory.Exists(Path.Combine(legacyDev, "game"))))
                return legacyDev;

            return Path.Combine(root, "Solaris");
        }
    }
}
