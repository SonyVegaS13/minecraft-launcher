using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 3 &&
            string.Equals(e.Args[0], "--self-update", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSelfUpdateAsync(e.Args[1], e.Args[2]);
            return;
        }

        if (e.Args.Length >= 3 && string.Equals(e.Args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ApplyUpdateAsync(e.Args[1], e.Args[2]);
            return;
        }

        base.OnStartup(e);
    }

    private Task RunSelfUpdateAsync(string launcherPath, string newLauncherPath)
    {
        try
        {
            launcherPath = Path.GetFullPath(launcherPath);
            newLauncherPath = Path.GetFullPath(newLauncherPath);
            string helperPath = Path.Combine(Path.GetTempPath(), $"SolarisUpdater-{Guid.NewGuid():N}.exe");
            File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path"), helperPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"--apply-update \"{launcherPath}\\" \\"{newLauncherPath}\\"",
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Solaris update error");
        }
        Shutdown();
        return Task.CompletedTask;
    }

    private async Task ApplyUpdateAsync(string launcherPath, string newLauncherPath)
    {
        try
        {
            launcherPath = Path.GetFullPath(launcherPath);
            newLauncherPath = Path.GetFullPath(newLauncherPath);
            bool released = false;
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    using FileStream stream = new(launcherPath, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                    released = true;
                    break;
                }
                catch (IOException) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) { await Task.Delay(500); }
            }
            if (!released) throw new IOException("Old launcher did not exit.");
            File.Copy(newLauncherPath, launcherPath, true);
            try { File.Delete(newLauncherPath); } catch { }
            Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Solaris update error");
        }
        finally { Shutdown(); }
    }
}
