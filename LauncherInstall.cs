using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    // One stable path. The shortcut never changes across versions.
    private static readonly string PermanentExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Solaris Launcher", "SolarisLauncher.exe");

    private bool IsRunningInstalled()
    {
        string? running = Environment.ProcessPath;
        return running is not null &&
            Path.GetFullPath(running).Equals(
                Path.GetFullPath(PermanentExePath), StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateInstallButtons()
    {
        Visibility state = IsRunningInstalled() ? Visibility.Collapsed : Visibility.Visible;
        InstallSolarisButton.Visibility = state;
        InstallSolarisButtonMain.Visibility = state;
    }

    private void InstallSolaris_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string source = Environment.ProcessPath
                ?? throw new InvalidOperationException("Не удалось определить путь к SolarisLauncher.exe.");

            string parent = Path.GetDirectoryName(PermanentExePath)!;
            Directory.CreateDirectory(parent);

            if (!IsRunningInstalled())
            {
                string staged = Path.Combine(parent, "SolarisLauncher.installing.exe");
                try
                {
                    File.Copy(source, staged, overwrite: true);
                    if (File.Exists(PermanentExePath))
                    {
                        // If an older installation exists, preserve a backup.
                        string previous = Path.Combine(parent, "SolarisLauncher.previous.exe");
                        File.Replace(staged, PermanentExePath, previous, ignoreMetadataErrors: true);
                    }
                    else
                        File.Move(staged, PermanentExePath);
                }
                finally
                {
                    try { File.Delete(staged); } catch { }
                }
            }

            string desktopLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Solaris Launcher.lnk");
            string programsLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "Solaris Launcher.lnk");

            CreateWindowsShortcut(desktopLink, PermanentExePath);
            CreateWindowsShortcut(programsLink, PermanentExePath);
            NotifyShellIconChanged();

            var start = new ProcessStartInfo
            {
                FileName = PermanentExePath,
                WorkingDirectory = parent,
                UseShellExecute = true
            };
            if (Process.Start(start) is null)
                throw new InvalidOperationException("Не удалось запустить установленную копию Solaris.");

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Установка не завершилась. Текущий Solaris продолжит работать.\n\n" +
                ex.Message + "\n\n" +
                "Если установленный Solaris уже запущен, закрой его и повтори попытку.",
                "Solaris — установка",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // Windows Shell shortcut: shortcut target AND icon point at the permanent EXE.
    // When the EXE updates, every shortcut remains valid and uses the new icon.
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
    {
        try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* A shell refresh failure is not an install failure. */ }
    }
}
