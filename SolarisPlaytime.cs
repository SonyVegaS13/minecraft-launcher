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

    private async Task SaveActivityAsync(string username, PlayerActivity activity)
    {
        string path = ActivityFile(username);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(activity));
        File.Move(tmp, path, overwrite: true);
    }

    private static string DisplayDuration(long seconds) =>
        $"{seconds / 3600} ч {(seconds % 3600) / 60} мин";

    private async Task RefreshActivityAndProfileAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        PlayerActivity activity = await ReadActivityAsync(username);
        if (!string.Equals(WelcomeText.Text, username, StringComparison.OrdinalIgnoreCase)) return;
        VanillaHours.Text = "В игре: " + DisplayDuration(activity.VanillaSeconds);
        ModdedHours.Text = "В игре: " + DisplayDuration(activity.ModdedSeconds);
        ActivityLastGame.Text = activity.LastMode switch
        {
            "vanilla" => "Solaris Vanilla",
            "modded" => "Solaris Modded",
            _ => "Вы ещё не играли"
        };
        ActivityLastDuration.Text = DisplayDuration(activity.LastSessionSeconds);
        ActivityLastLaunch.Text = activity.LastLaunchUtc is { } last
            ? last.ToLocalTime().ToString("dd.MM.yyyy, HH:mm") : "—";
        long vanilla = activity.VanillaSeconds;
        long modded = activity.ModdedSeconds;
        if (_cloudSession is not null && activity.HasCloudTotals)
        {
            vanilla = activity.SyncedVanillaSeconds +
                activity.PendingCloudSessions.Where(s => s.Mode == "vanilla").Sum(s => (long)s.Seconds);
            modded = activity.SyncedModdedSeconds +
                activity.PendingCloudSessions.Where(s => s.Mode == "modded").Sum(s => (long)s.Seconds);
        }
        FullProfileTotal.Text = "Всего в Minecraft: " + DisplayDuration(vanilla + modded);
        FullProfileVanilla.Text = "Vanilla: " + DisplayDuration(vanilla);
        FullProfileModded.Text = "Modded: " + DisplayDuration(modded);
        FullProfileName.Text = username;
        if (_cloudSession is { } cloud)
            FullProfileCreated.Text = "Дата регистрации: " +
                cloud.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy");
        else
        {
            try
            {
                LocalAccount? account = await ReadAccountAsync();
                FullProfileCreated.Text = account?.CreatedUtc is { } created
                    ? "Дата регистрации: " + created.ToLocalTime().ToString("dd.MM.yyyy")
                    : "Дата регистрации: неизвестна (старый локальный аккаунт)";
            }
            catch { FullProfileCreated.Text = "Дата регистрации: неизвестна"; }
        }
    }

    private async Task TrackGameProcessAsync(Process process, string username, string mode)
    {
        PlayerActivity state = await ReadActivityAsync(username);
        state.LastMode = mode;
        state.LastLaunchUtc = DateTimeOffset.UtcNow;
        DateTimeOffset startedUtc = state.LastLaunchUtc.Value;
        await SaveActivityAsync(username, state);

        // Keep the lightweight launcher hidden, not terminated, to track offline
        // single-player as well as multiplayer time. Persist every 20 seconds;
        // losing Windows power discards at most the last heartbeat.
        Hide();
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
                await SaveActivityAsync(username, state);
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
            if (_cloudSession is not null && state.LastSessionSeconds > 0)
                state.PendingCloudSessions.Add(new CloudGameUpload
                {
                    Id = Guid.NewGuid(), Mode = mode, StartedUtc = startedUtc,
                    Seconds = state.LastSessionSeconds
                });
            try
            {
                await SaveActivityAsync(username, state);
                if (_cloudSession is not null && !_cloudOffline)
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
