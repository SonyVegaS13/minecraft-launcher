using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

/// <summary>
/// Single-attempt, crash-aware update transaction. One visible consent, one
/// detached helper, one relaunch and an explicit success acknowledgement.
/// The installed EXE is never deleted without a recoverable .previous copy.
/// </summary>
internal static class SolarisSafeUpdate
{
    // DEV transactions and rollback files must never mix with stable 2.2.9.
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        App.IsDevChannel ? "Solaris-Neon-Dev" : "Solaris", "updates");
    private static string StatePath => Path.Combine(Root, "safe-update.json");
    private static string AckPath(string id) => Path.Combine(Root, "ready-" + id + ".ack");
    private sealed class UpdateState
    {
        public string Version { get; set; } = "";
        public string AttemptId { get; set; } = "";
        public string Phase { get; set; } = "";
        public DateTimeOffset Utc { get; set; }
    }

    private static UpdateState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath));
        }
        catch { return null; }
    }

    private static void Save(UpdateState state)
    {
        Directory.CreateDirectory(Root);
        string temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, StatePath, overwrite: true);
    }

    internal static bool CanOffer(string version)
    {
        UpdateState? state = ReadState();
        if (state is null || !string.Equals(state.Version, version, StringComparison.Ordinal))
            return true;

        TimeSpan elapsed = DateTimeOffset.UtcNow - state.Utc;
        if (state.Phase == "failed" && elapsed < TimeSpan.FromHours(12))
            return false; // Never auto-loop a broken update.
        if (state.Phase == "installing" && elapsed < TimeSpan.FromMinutes(30))
            return false; // A running helper owns the update operation.
        if (state.Phase == "downloading" && elapsed < TimeSpan.FromMinutes(30))
            return false;
        return true; // Stale marker: the player can retry in a later session.
    }

    internal static void Begin(string version, string attemptId)
    {
        if (!CanOffer(version))
            throw new InvalidOperationException("Эта версия Solaris уже обновляется либо попытка недавно завершилась ошибкой.");
        Save(new UpdateState { Version = version, AttemptId = attemptId,
            Phase = "downloading", Utc = DateTimeOffset.UtcNow });
    }

    internal static void MarkFailed(string version, string attemptId, Exception exception)
    {
        try
        {
            UpdateState? state = ReadState();
            if (state is not null && state.AttemptId != attemptId) return;
            Save(new UpdateState { Version = version, AttemptId = attemptId,
                Phase = "failed", Utc = DateTimeOffset.UtcNow });
            File.AppendAllText(Path.Combine(Root, "safe-update.log"),
                $"[{DateTimeOffset.Now:O}] {version} {attemptId}: {exception}\n");
        }
        catch { }
    }

    internal static void PublishReady(string attemptId)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(AckPath(attemptId), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { /* The detached helper will time out and preserve backup. */ }
    }

    private static Process StartInstalled(string path, string mode, string? value = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = true
        };
        info.ArgumentList.Add(mode);
        if (value is not null) info.ArgumentList.Add(value);
        if (App.IsDevChannel) info.ArgumentList.Add("--dev-channel");
        return Process.Start(info) ?? throw new IOException("Не удалось перезапустить Solaris.");
    }

    internal static async Task ApplyUpdateAsync(string installedPath, string downloadedPath,
        string expectedVersion, string attemptId, Action finish)
    {
        string? staged = null;
        string? backup = null;
        Process? next = null;
        bool replaced = false;
        // Only one detached helper may mutate the installed executable.
        using var mutex = new Mutex(false, App.IsDevChannel
            ? @"Local\SolarisLauncher.DevSafeUpdate"
            : @"Local\SolarisLauncher.SafeUpdate");
        bool ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new IOException("Другое обновление Solaris ещё выполняется.");

            installedPath = Path.GetFullPath(installedPath);
            downloadedPath = Path.GetFullPath(downloadedPath);
            if (!File.Exists(downloadedPath) || new FileInfo(downloadedPath).Length < 1_000_000)
                throw new InvalidDataException("Файл обновления отсутствует или повреждён.");
            var exeVersion = FileVersionInfo.GetVersionInfo(downloadedPath);
            Version expected = Version.Parse(expectedVersion);
            if (expected.Revision < 0)
                expected = new Version(expected.Major, expected.Minor, expected.Build, 0);
            if (new Version(exeVersion.FileMajorPart, exeVersion.FileMinorPart,
                exeVersion.FileBuildPart, exeVersion.FilePrivatePart) != expected)
                throw new InvalidDataException("Версия скачанного EXE не совпадает с релизом.");

            Save(new UpdateState { Version = expectedVersion, AttemptId = attemptId,
                Phase = "installing", Utc = DateTimeOffset.UtcNow });

            bool unlocked = false;
            for (int i = 0; i < 90; i++)
            {
                try
                {
                    using var handle = new FileStream(installedPath, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                    unlocked = true;
                    break;
                }
                catch (IOException) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) { await Task.Delay(500); }
            }
            if (!unlocked)
                throw new IOException("Основной лаунчер не освободил файл EXE.");

            staged = installedPath + "." + attemptId + ".incoming";
            backup = installedPath + ".previous";
            File.Copy(downloadedPath, staged, overwrite: true);
            File.Replace(staged, installedPath, backup, ignoreMetadataErrors: true);
            replaced = true;

            next = StartInstalled(installedPath, "--updated", attemptId);
            bool acknowledged = false;
            for (int i = 0; i < 60; i++)
            {
                if (File.Exists(AckPath(attemptId)))
                {
                    acknowledged = true;
                    break;
                }
                if (next.HasExited) break;
                await Task.Delay(500);
            }
            if (!acknowledged)
                throw new IOException("Новая версия не подтвердила успешный запуск.");

            try { File.Delete(downloadedPath); } catch { }
            try { File.Delete(AckPath(attemptId)); } catch { }
            try { File.Delete(StatePath); } catch { }
        }
        catch (Exception ex)
        {
            MarkFailed(expectedVersion, attemptId, ex);
            if (replaced && backup is not null && File.Exists(backup))
            {
                try
                {
                    if (next is not null && !next.HasExited)
                    {
                        next.Kill();
                        await next.WaitForExitAsync();
                    }
                    for (int retry = 0; retry < 15; retry++)
                    {
                        try { File.Replace(backup, installedPath, null, ignoreMetadataErrors: true); break; }
                        catch (IOException) when (retry < 14) { await Task.Delay(500); }
                    }
                }
                catch (Exception rollbackError)
                {
                    MarkFailed(expectedVersion, attemptId, rollbackError);
                }
            }
            try
            {
                MessageBox.Show(
                    "Не удалось завершить обновление Solaris.\n" +
                    "Повторное предложение временно отключено.\n\n" + ex.Message,
                    "Solaris SAFE UPDATE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            // Exactly one recovery launch, with update checks disabled.
            try { if (File.Exists(installedPath)) StartInstalled(installedPath, "--update-recovery"); }
            catch { }
        }
        finally
        {
            try { if (staged is not null && File.Exists(staged)) File.Delete(staged); } catch { }
            if (ownsMutex) { try { mutex.ReleaseMutex(); } catch { } }
            finish();
        }
    }
}
