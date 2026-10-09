using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class App : Application
{
    private bool _handlingCrash;
    private System.Threading.Mutex? _launcherMutex;
    internal static string? PendingUpdateAcknowledgement { get; private set; }
    internal static bool LaunchedAfterRecovery { get; private set; }
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

        if (e.Args.Length >= 5 &&
            string.Equals(e.Args[0], "--self-update", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunSelfUpdateAsync(e.Args[1], e.Args[2], e.Args[3], e.Args[4]);
            return;
        }

        if (e.Args.Length >= 5 &&
            string.Equals(e.Args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = SolarisSafeUpdate.ApplyUpdateAsync(e.Args[1], e.Args[2], e.Args[3], e.Args[4],
                () => Shutdown());
            return;
        }

        // One visible launcher window per Windows session. Updater helper modes
        // deliberately bypass this mutex and never display a launcher UI.
        _launcherMutex = new System.Threading.Mutex(true, @"Local\SolarisLauncher.Main", out bool firstInstance);
        if (!firstInstance)
        {
            _launcherMutex.Dispose();
            _launcherMutex = null;
            Shutdown(0);
            return;
        }

        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--updated", StringComparison.OrdinalIgnoreCase))
            PendingUpdateAcknowledgement = e.Args[1];
        if (e.Args.Length >= 1 && string.Equals(e.Args[0], "--update-recovery", StringComparison.OrdinalIgnoreCase))
            LaunchedAfterRecovery = true;

        // 2.2.6+ auto-installs on the first ordinary launch and reopens
        // the permanent EXE. The update-helper modes above skip this step.
        // If the installation cannot be completed, preserve portable mode.
        try
        {
            if (SolarisBootstrapper.RedirectToPermanentInstallation())
            {
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            LogCrash("Automatic installation", ex);
            try
            {
                MessageBox.Show(
                    "Не удалось автоматически установить Solaris.\n" +
                    "Лаунчер продолжит работать из текущей папки.\n" +
                    "Можно закрыть другие копии Solaris и запустить его снова.\n\n" +
                    ex.Message,
                    "Solaris — установка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_launcherMutex is not null)
        {
            try { _launcherMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _launcherMutex.Dispose();
            _launcherMutex = null;
        }
        base.OnExit(e);
    }

    private Task RunSelfUpdateAsync(string launcherPath, string newLauncherPath, string expectedVersion, string attemptId)
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
                ArgumentList = { "--apply-update", launcherPath, newLauncherPath, expectedVersion, attemptId },
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            SolarisSafeUpdate.MarkFailed(expectedVersion, attemptId, ex);
            MessageBox.Show("Не удалось запустить помощник обновления. Предыдущая версия сохранена.\n\n" +
                ex.Message, "Solaris — обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Shutdown();
        return Task.CompletedTask;
    }

    // Replace next to the installed executable so Windows can make an atomic
    // swap with a recoverable backup, even if the download was on another drive.
    private async Task ApplyUpdateAsync(string launcherPath, string newLauncherPath)
    {
        string? stagedPath = null;
        string? backupPath = null;
        bool replaced = false;

        try
        {
            launcherPath = Path.GetFullPath(launcherPath);
            newLauncherPath = Path.GetFullPath(newLauncherPath);
            if (!File.Exists(newLauncherPath))
                throw new FileNotFoundException("Загруженное обновление не найдено.", newLauncherPath);

            bool oldProcessClosed = false;
            for (int attempt = 0; attempt < 90; attempt++)
            {
                try
                {
                    using var handle = new FileStream(launcherPath, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                    oldProcessClosed = true;
                    break;
                }
                catch (IOException) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) { await Task.Delay(500); }
            }
            if (!oldProcessClosed)
                throw new IOException("Solaris не завершился. Закрой лаунчер и повтори обновление.");

            stagedPath = launcherPath + ".incoming";
            backupPath = launcherPath + ".previous";
            File.Copy(newLauncherPath, stagedPath, overwrite: true);
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Replace(stagedPath, launcherPath, backupPath, ignoreMetadataErrors: true);
            replaced = true;
            try { File.Delete(newLauncherPath); } catch { }

            try { NotifyShell(); } catch { }
            StartLauncher(launcherPath);
        }
        catch (Exception ex)
        {
            if (replaced && backupPath is not null && File.Exists(backupPath))
            {
                try { File.Replace(backupPath, launcherPath, null, ignoreMetadataErrors: true); }
                catch { /* Keep previous executable on disk for manual recovery. */ }
            }

            try
            {
                MessageBox.Show(
                    "Не удалось применить обновление Solaris.\n" +
                    "Предыдущая версия сохранена, если файл замены был создан.\n\n" +
                    ex.Message,
                    "Solaris — обновление",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            try { if (File.Exists(launcherPath)) StartLauncher(launcherPath); } catch { }
        }
        finally
        {
            try { if (stagedPath is not null) File.Delete(stagedPath); } catch { }
            Shutdown();
        }
    }

    private static void StartLauncher(string path)
    {
        if (Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = true
            }) is null)
            throw new InvalidOperationException("Не удалось перезапустить Solaris.");
    }

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static extern void NotifyWindowsShell(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
        => NotifyWindowsShell(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

}
