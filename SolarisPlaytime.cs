using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace SolarisLauncher;

public partial class MainWindow
{
    private sealed class PlayerActivity
    {
        public long VanillaSeconds { get; set; }
        public long ModdedSeconds { get; set; }
        public string LastMode { get; set; } = "";
        public int LastSessionSeconds { get; set; }
        public DateTimeOffset? LastLaunchUtc { get; set; }
        public List<CloudGameUpload> PendingCloudSessions { get; set; } = new();
        public long SyncedVanillaSeconds { get; set; }
        public long SyncedModdedSeconds { get; set; }
        public bool HasCloudTotals { get; set; }
        public int TrustedTotalsVersion { get; set; }
    }

    private sealed class CloudGameUpload
    {
        public Guid Id { get; set; }
        public string Mode { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public int Seconds { get; set; }
    }

    private string ActivityFile(string username)
    {
        string safe = new string(username.ToLowerInvariant().Where(c =>
            char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (_cloudSession is { } cloud &&
            username.Equals(cloud.Nickname, StringComparison.OrdinalIgnoreCase))
        {
            // Remote ID, not nickname: prevent mixing unrelated local/cloud accounts.
            safe = "cloud-" + new string(cloud.Id.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        }
        return Path.Combine(_stateDir, "profiles", safe, "activity.json");
    }

    private async Task<PlayerActivity> ReadActivityAsync(string username)
    {
        string path = ActivityFile(username);
        if (!File.Exists(path)) return new PlayerActivity();
        try { return JsonSerializer.Deserialize<PlayerActivity>(await File.ReadAllTextAsync(path))
            ?? new PlayerActivity(); }
        catch { return new PlayerActivity(); }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> ActivityGates = new(StringComparer.OrdinalIgnoreCase);

    private static async Task SaveActivityFileAsync(string path, PlayerActivity activity, bool trustedTotalsOnly = false)
    {
        var gate = ActivityGates.GetOrAdd(path, _ => new System.Threading.SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PlayerActivity? current = null;
            if (File.Exists(path))
            {
                try { current = JsonSerializer.Deserialize<PlayerActivity>(await File.ReadAllTextAsync(path)); }
                catch (JsonException)
                { File.Copy(path, path + ".before-recovery-" + Guid.NewGuid().ToString("N")); }
            }
            if (trustedTotalsOnly && current is not null)
            {
                current.SyncedVanillaSeconds = activity.SyncedVanillaSeconds;
                current.SyncedModdedSeconds = activity.SyncedModdedSeconds;
                current.HasCloudTotals = activity.HasCloudTotals;
                current.TrustedTotalsVersion = activity.TrustedTotalsVersion;
                activity = current;
            }
            else if (current is not null)
            {
                // Local process heartbeat must never overwrite a concurrent
                // refresh of server-verified totals.
                activity.SyncedVanillaSeconds = current.SyncedVanillaSeconds;
                activity.SyncedModdedSeconds = current.SyncedModdedSeconds;
                activity.HasCloudTotals = current.HasCloudTotals;
                activity.TrustedTotalsVersion = current.TrustedTotalsVersion;
            }
            string tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(activity));
            File.Move(tmp, path, overwrite: true);
        }
        finally { gate.Release(); }
    }

    private static string DisplayDuration(long seconds) =>
        $"{seconds / 3600} ч {(seconds % 3600) / 60} мин";

    private async Task RefreshActivityAndProfileAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        SolarisCloudSession? owner = _cloudSession;
        string activityPath = ActivityFile(username);
        PlayerActivity activity = await ReadActivityAsync(username);
        if (!string.Equals(WelcomeText.Text, username, StringComparison.OrdinalIgnoreCase) ||
            !ReferenceEquals(owner, _cloudSession) || activityPath != ActivityFile(username)) return;
        bool cloudProfile = _cloudSession is not null;
        bool trustedTotals = activity.HasCloudTotals && activity.TrustedTotalsVersion == 1;
        VanillaHours.Text = cloudProfile ? (trustedTotals ? "На сервере: " + DisplayDuration(activity.SyncedVanillaSeconds) : "Серверные часы: —") : "Локально: " + DisplayDuration(activity.VanillaSeconds);
        ModdedHours.Text = cloudProfile ? (trustedTotals ? "На сервере: " + DisplayDuration(activity.SyncedModdedSeconds) : "Серверные часы: —") : "Локально: " + DisplayDuration(activity.ModdedSeconds);
        ActivityLastGame.Text = activity.LastMode switch
        {
            "vanilla" => "Solaris Vanilla",
            "modded" => "Solaris Modded",
            _ => "Вы ещё не играли"
        };
        ActivityLastDuration.Text = "Локальный сеанс: " + DisplayDuration(activity.LastSessionSeconds);
        ActivityLastLaunch.Text = activity.LastLaunchUtc is { } last
            ? last.ToLocalTime().ToString("dd.MM.yyyy, HH:mm") : "—";
        long vanilla = activity.VanillaSeconds;
        long modded = activity.ModdedSeconds;
        if (_cloudSession is not null && trustedTotals)
        {
            vanilla = activity.SyncedVanillaSeconds;
            modded = activity.SyncedModdedSeconds;
        }
        FullProfileTotal.Text = cloudProfile ? (trustedTotals ? "Подтверждено серверами: " + DisplayDuration(vanilla + modded) : "Серверная статистика пока недоступна") : "Локальное время Minecraft: " + DisplayDuration(vanilla + modded);
        FullProfileVanilla.Text = cloudProfile && !trustedTotals ? "Vanilla: —" : "Vanilla: " + DisplayDuration(vanilla);
        FullProfileModded.Text = cloudProfile && !trustedTotals ? "Modded: —" : "Modded: " + DisplayDuration(modded);
        FullProfileName.Text = username;
        if (_cloudSession is { } cloud)
        {
            FullProfileCreated.Text = "Дата регистрации: " + cloud.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy");
            FullProfileSolarisId.Text = "Solaris ID: " + cloud.Id;
            FullProfileEmail.Text = "Email: " + cloud.Email;
            FullProfileLastActive.Text = cloud.LastActiveUtc is { } active ? "Активность: " + active.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "";
        }
        else
        {
            try
            {
                LocalAccount? account = await ReadAccountAsync();
                if (!ReferenceEquals(owner, _cloudSession) || activityPath != ActivityFile(username)) return;
                FullProfileCreated.Text = account?.CreatedUtc is { } created
                    ? "Дата регистрации: " + created.ToLocalTime().ToString("dd.MM.yyyy")
                    : "Дата регистрации: неизвестна (старый локальный аккаунт)";
            }
            catch { FullProfileCreated.Text = "Дата регистрации: неизвестна"; }
        }
    }

    private async Task TrackGameProcessAsync(Process process, string username, string mode)
    {
        string activityPath = ActivityFile(username);
        SolarisCloudSession? owner = _cloudSession;
        PlayerActivity state = await ReadActivityAsync(username);
        state.LastMode = mode;
        state.LastLaunchUtc = DateTimeOffset.UtcNow;
        DateTimeOffset startedUtc = state.LastLaunchUtc.Value;
        await SaveActivityFileAsync(activityPath, state);

        // Keep the lightweight launcher hidden, not terminated, to track offline
        // single-player as well as multiplayer time. Persist every 20 seconds;
        // losing Windows power discards at most the last heartbeat.
        // A protected server sends a connection code that must be approved in
        // the launcher. Keep this window available for Alt+Tab in opt-in mode.
        if (owner is null || _idConfiguration.ServerFor(mode) is null) Hide();
        var clock = Stopwatch.StartNew();
        long persistedSeconds = 0;
        Task exited = process.WaitForExitAsync();
        try
        {
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(20)));
                long elapsed = (long)clock.Elapsed.TotalSeconds;
                long delta = Math.Max(0, elapsed - persistedSeconds);
                if (delta <= 0) continue;
                if (mode == "vanilla") state.VanillaSeconds += delta;
                else state.ModdedSeconds += delta;
                persistedSeconds = elapsed;
                await SaveActivityFileAsync(activityPath, state);
            }
            await exited;
        }
        finally
        {
            long elapsed = (long)clock.Elapsed.TotalSeconds;
            long delta = Math.Max(0, elapsed - persistedSeconds);
            if (mode == "vanilla") state.VanillaSeconds += delta;
            else state.ModdedSeconds += delta;
            state.LastSessionSeconds = (int)Math.Min(int.MaxValue, elapsed);
            // Preserve local history only; trusted server bridges own online hours.
            try
            {
                await SaveActivityFileAsync(activityPath, state);
                if (owner is not null && ReferenceEquals(owner, _cloudSession) && !_cloudOffline)
                    await SyncCloudDataAsync(username);
            }
            catch { /* Local playtime must survive a temporary cloud outage. */ }
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            StatusText.Text = "Игровой сеанс завершён. Solaris готов к запуску.";
            Progress.Value = 0;
            await RefreshActivityAndProfileAsync(username);
        }
    }
}
