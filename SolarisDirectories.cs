using System;
using System.IO;

namespace SolarisLauncher;

internal static class SolarisDirectories
{
    internal static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        App.IsDeveloperMode ? "Solaris-Neon-Dev" : "Solaris");
}
