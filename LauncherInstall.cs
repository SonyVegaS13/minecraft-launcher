using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SolarisLauncher;

/// <summary>
/// Creates one per-user installation before the WPF window is constructed.
/// All future releases replace the EXE at this stable path. Game files and
/// local account data remain in %APPDATA%\Solaris and are not touched.
/// </summary>
internal static class SolarisBootstrapper
{
    internal static readonly string PermanentExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Solaris Launcher", "SolarisLauncher.exe");

    /// <summary>
    /// Returns true after handing off to the permanent EXE. Returns false only
    /// when already running the installed EXE; never creates a launch loop.
    /// </summary>
    internal static bool RedirectToPermanentInstallation()
    {
        string sourcePath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к SolarisLauncher.exe."));
        string destinationPath = Path.GetFullPath(PermanentExePath);

        if (sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            // Upgrade from 2.2.5 or recover deleted shortcuts without copying.
            EnsureShortcuts(onlyMissing: true);
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        // Avoid accidentally downgrading an existing Solaris install if a user
        // opens an old EXE from Downloads. Installed releases take precedence.
        bool needsCopy = !File.Exists(destinationPath)
            || CompareExeVersions(sourcePath, destinationPath) > 0;

        if (needsCopy)
        {
            string stagedPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".incoming";
            try
            {
                File.Copy(sourcePath, stagedPath);
                if (File.Exists(destinationPath))
                {
                    // Atomic same-volume replacement; backup survives updates.
                    File.Replace(stagedPath, destinationPath,
                        destinationPath + ".previous", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(stagedPath, destinationPath);
                }
            }
            finally
            {
                try { File.Delete(stagedPath); } catch { }
            }
        }

        // Failure to create one shortcut (e.g. a redirected OneDrive desktop)
        // is not allowed to break the newly installed launcher.
        EnsureShortcuts(onlyMissing: false);

        var start = new ProcessStartInfo
        {
            FileName = destinationPath,
            WorkingDirectory = Path.GetDirectoryName(destinationPath)!,
            UseShellExecute = true
        };

        if (Process.Start(start) is null)
            throw new InvalidOperationException("Не удалось запустить установленный Solaris.");

        // Refresh Explorer's icon cache after installing a new branded EXE.
        try { NotifyShellIconChanged(); } catch { }
        return true;
    }

    private static int CompareExeVersions(string candidatePath, string installedPath)
    {
        static Version FileVersion(string path)
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart < 0 || info.FileMinorPart < 0 ||
                info.FileBuildPart < 0 || info.FilePrivatePart < 0)
                return new Version(0, 0, 0, 0);
            return new Version(info.FileMajorPart, info.FileMinorPart,
                info.FileBuildPart, info.FilePrivatePart);
        }

        // If the version is equal, the installed EXE stays put.
        return FileVersion(candidatePath).CompareTo(FileVersion(installedPath));
    }

    private static void EnsureShortcuts(bool onlyMissing)
    {
        string desktopLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Solaris Launcher.lnk");
        string programsLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Solaris Launcher.lnk");

        foreach (string linkPath in new[] { desktopLink, programsLink })
        {
            if (onlyMissing && File.Exists(linkPath)) continue;
            try { CreateWindowsShortcut(linkPath, PermanentExePath); }
            catch (Exception ex) { LogShortcutError(linkPath, ex); }
        }
    }

    private static void LogShortcutError(string path, Exception exception)
    {
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Solaris", "logs", "installation.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {path}: {exception}\n");
        }
        catch { }
    }

    // A .lnk points at the stable EXE (not at Downloads). The Windows icon is
    // embedded in that EXE and can evolve with each future release.
    private static void CreateWindowsShortcut(string linkPath, string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host недоступен.");
        object shellObject = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Не удалось создать ярлык Windows.");
        try
        {
            dynamic shell = shellObject;
            object shortcutObject = shell.CreateShortcut(linkPath);
            try
            {
                dynamic shortcut = shortcutObject;
                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = Path.GetDirectoryName(executable)!;
                shortcut.IconLocation = executable + ",0";
                shortcut.Description = "Solaris Neon — Minecraft Launcher";
                shortcut.Save();
            }
            finally
            {
                if (Marshal.IsComObject(shortcutObject))
                    Marshal.FinalReleaseComObject(shortcutObject);
            }
        }
        finally
        {
            if (Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShellIconChanged()
        => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
