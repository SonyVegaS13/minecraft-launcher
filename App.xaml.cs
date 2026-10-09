using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class App : Application
{
    private bool _handlingCrash;
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Solaris", "logs", "launcher-crash.log");

    private static void LogCrash(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* A crash logger must never crash the launcher. */ }
    }

    private void HandleDispatcherCrash(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        LogCrash("WPF dispatcher", args.Exception);
        args.Handled = true;
        if (_handlingCrash) return;
        _handlingCrash = true;
        try
        {
            MessageBox.Show(
                "Ошибка запуска Solaris Launcher. Подробности записаны в журнал:" +
                Environment.NewLine + CrashLogPath + Environment.NewLine + Environment.NewLine +
                args.Exception.GetBaseException().Message,
                "Solaris Launcher — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        Shutdown(-1);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += HandleDispatcherCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                LogCrash("Unhandled AppDomain", exception);
        };

        // Let WPF choose the best available render tier (GPU when supported).
        // The previous forced software renderer caused visible typing/input lag
        // on Windows 10 LTSC with a full-screen neon bitmap backdrop.
        // An explicit opt-in is retained for computers with broken GPU drivers.
        if (string.Equals(Environment.GetEnvironmentVariable("SOLARIS_SOFTWARE_RENDER"),
            "1", StringComparison.Ordinal))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
        }

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

        try { base.OnStartup(e); }
        catch (Exception ex)
        {
            LogCrash("App startup", ex);
            try { MessageBox.Show(ex.GetBaseException().Message + Environment.NewLine + CrashLogPath,
                "Solaris Launcher — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            Shutdown(-1);
        }
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
                ArgumentList = { "--apply-update", launcherPath, newLauncherPath },
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
