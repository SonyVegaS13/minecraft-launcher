using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SolarisLauncher;

/// <summary>
/// Exactly one installed Windows executable and two shortcuts to that same executable.
/// An older DEV installation is migrated without touching Minecraft or account data.
/// </summary>
internal static class SolarisBootstrapper
{
    internal static readonly string PermanentExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Solaris Launcher", "SolarisLauncher.exe");
    // Kept only to identify and safely remove the obsolete duplicate installation.
    private static readonly string LegacyDevExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Solaris Launcher DEV", "SolarisLauncher.exe");

    internal static bool RedirectToPermanentInstallation(bool devChannel = false,
        string? updatedAttemptId = null, bool recovery = false)
    {
        string source = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь SolarisLauncher.exe."));
        string target = Path.GetFullPath(PermanentExePath);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            EnsureShortcuts();
            _ = CleanupLegacyDevLaterAsync();
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Opening a downloaded older EXE must never overwrite a newer installation.
        if (!File.Exists(target) || CompareExeVersions(source, target) > 0)
        {
            string staged = target + "." + Guid.NewGuid().ToString("N") + ".incoming";
            try
            {
                File.Copy(source, staged);
                if (File.Exists(target))
                    File.Replace(staged, target, target + ".previous",
                        ignoreMetadataErrors: true);
                else
                    File.Move(staged, target);
            }
            finally { try { if (File.Exists(staged)) File.Delete(staged); } catch { } }
        }

        EnsureShortcuts();

        var start = new ProcessStartInfo
        {
            FileName = target,
            WorkingDirectory = Path.GetDirectoryName(target)!,
            UseShellExecute = true
        };
        // Preserve the transaction acknowledgement when migrating from the
        // legacy DEV path. Otherwise the old updater would falsely roll back.
        if (updatedAttemptId is not null)
        {
            start.ArgumentList.Add("--updated");
            start.ArgumentList.Add(updatedAttemptId);
        }
        else if (recovery)
            start.ArgumentList.Add("--update-recovery");
        if (devChannel) start.ArgumentList.Add("--dev-channel");

        if (Process.Start(start) is null)
            throw new InvalidOperationException("Не удалось открыть установленный Solaris.");

        try { NotifyShellIconChanged(); } catch { }
        return true;
    }

    private static int CompareExeVersions(string candidate, string installed)
    {
        static Version VersionOf(string path)
        {
            var f = FileVersionInfo.GetVersionInfo(path);
            return new Version(Math.Max(0, f.FileMajorPart), Math.Max(0, f.FileMinorPart),
                Math.Max(0, f.FileBuildPart), Math.Max(0, f.FilePrivatePart));
        }
        return VersionOf(candidate).CompareTo(VersionOf(installed));
    }

    private static string[] Shortcuts(string name) => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name)
    };

    private static void EnsureShortcuts()
    {
        // Never create a second Solaris Launcher DEV icon again.
        foreach (string path in Shortcuts("Solaris Launcher.lnk"))
        {
            try { CreateShortcut(path, PermanentExePath); }
            catch (Exception ex) { LogShortcutError(path, ex); }
        }

        foreach (string legacy in Shortcuts("Solaris Launcher DEV.lnk"))
        {
            try
            {
                // Remove only shortcuts pointing at our known obsolete EXE.
                if (File.Exists(legacy) &&
                    string.Equals(ReadShortcutTarget(legacy), LegacyDevExePath,
                        StringComparison.OrdinalIgnoreCase))
                    File.Delete(legacy);
            }
            catch (Exception ex) { LogShortcutError(legacy, ex); }
        }
    }

    private static string? ReadShortcutTarget(string path)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host недоступен.");
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
            try { return (string)shortcut.TargetPath; }
            finally { if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }

    private static void CreateShortcut(string path, string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host недоступен.");
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = ((dynamic)shell).CreateShortcut(path);
            try
            {
                shortcut.TargetPath = exe;
                shortcut.Arguments = "";
                shortcut.WorkingDirectory = Path.GetDirectoryName(exe)!;
                shortcut.IconLocation = exe + ",0";
                shortcut.Description = "Solaris — Minecraft Launcher";
                shortcut.Save();
            }
            finally { if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }

    private static void LogShortcutError(string path, Exception ex)
    {
        try
        {
            string log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Solaris", "logs", "installation.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:O}] {path}: {ex}\n");
        }
        catch { }
    }

    private static async Task CleanupLegacyDevLaterAsync()
    {
        // Only clean after the old self-updater has confirmed the new window.
        // Never recursively delete unexpected user files or the game profiles.
        string oldFolder = Path.GetDirectoryName(LegacyDevExePath)!;
        string updateState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Solaris-Neon-Dev", "updates", "safe-update.json");

        for (int attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try
            {
                if (!File.Exists(LegacyDevExePath)) break;
                if (File.Exists(updateState)) continue;
                if (!File.Exists(PermanentExePath) ||
                    CompareExeVersions(PermanentExePath, LegacyDevExePath) < 0) break;

                File.Delete(LegacyDevExePath);
                string oldBackup = LegacyDevExePath + ".previous";
                if (File.Exists(oldBackup)) File.Delete(oldBackup);
                if (Directory.Exists(oldFolder) &&
                    Directory.GetFileSystemEntries(oldFolder).Length == 0)
                    Directory.Delete(oldFolder);
                break;
            }
            catch (IOException) { /* Old executable might still be running. */ }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShellIconChanged()
        => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
