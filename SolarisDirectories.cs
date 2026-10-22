using System;
using System.IO;
using System.Text.Json;

namespace SolarisLauncher;

internal static class SolarisDirectories
{
    // A single launcher can upgrade either of the two old install channels.
    // Record the profile that actually initiated the upgrade, rather than
    // switching the player's account and saves just because another folder exists.
    private static string SelectionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Solaris", "launcher-profile.txt");

    internal static string StateDir
    {
        get
        {
#if SOLARIS_ID_TEST_BUILD
            // The test binary must never inspect/migrate an installed profile.
            return SolarisTestEdition.StateDirectory;
#else
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string standard = Path.Combine(appData, "Solaris");
            string dev = Path.Combine(appData, "Solaris-Neon-Dev");
            if (App.IsDeveloperMode && !App.IsDevChannel) return dev;

            string? attempt = App.PendingUpdateAcknowledgement;
            if (!string.IsNullOrWhiteSpace(attempt))
            {
                if (HasMatchingUpdaterTransaction("Solaris", attempt))
                {
                    SaveProfile("stable");
                    return standard;
                }
                if (HasMatchingUpdaterTransaction("Solaris-Neon-Dev", attempt))
                {
                    SaveProfile("dev");
                    return dev;
                }
            }

            if (SolarisBootstrapper.IsRunningFromLegacyDevPath())
            {
                SaveProfile("dev");
                return dev;
            }

            string saved = ReadProfile();
            if (saved == "dev") return dev;
            if (saved == "stable") return standard;

            // Existing DEV-only users retain their installed mods and account.
            // Everyone else starts in the standard Solaris data directory.
            if (HasPlayerData(dev) && !HasPlayerData(standard))
            {
                SaveProfile("dev");
                return dev;
            }

            SaveProfile("stable");
            return standard;
#endif
        }
    }

    private static bool HasPlayerData(string directory) =>
        File.Exists(Path.Combine(directory, "account.json")) ||
        File.Exists(Path.Combine(directory, "settings.json")) ||
        Directory.Exists(Path.Combine(directory, "game"));

    private static bool HasMatchingUpdaterTransaction(string channel, string attemptId)
    {
        try
        {
            string marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                channel, "updates", "safe-update.json");
            if (!File.Exists(marker)) return false;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(marker));
            return doc.RootElement.TryGetProperty("AttemptId", out JsonElement stored) &&
                string.Equals(stored.GetString(), attemptId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string ReadProfile()
    {
        try { return File.Exists(SelectionPath)
            ? File.ReadAllText(SelectionPath).Trim().ToLowerInvariant() : ""; }
        catch { return ""; }
    }

    private static void SaveProfile(string profile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SelectionPath)!);
            File.WriteAllText(SelectionPath, profile);
        }
        catch { /* Missing profile preference must never prevent launching. */ }
    }
}
