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

        base.OnStartup(e);
    }

    private async Task RunSelfUpdateAsync(string launcherPath, string newLauncherPath)
    {
        try
        {
            launcherPath = Path.GetFullPath(launcherPath);
            newLauncherPath = Path.GetFullPath(newLauncherPath);

            await Task.Delay(1200);

            bool released = false;
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    using FileStream stream = new(
                        launcherPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);

                    released = true;
                    break;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            if (!released)
                throw new IOException("Не удалось дождаться освобождения SolarisLauncher.exe.");

            File.Copy(newLauncherPath, launcherPath, true);

            try
            {
                File.Delete(newLauncherPath);
            }
            catch
            {
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
                UseShellExecute = true
            });

            Shutdown();
        }
        catch
        {
            Shutdown();
        }
    }
}
