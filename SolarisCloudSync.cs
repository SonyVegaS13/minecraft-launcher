using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SolarisLauncher;

public partial class MainWindow
{
    private readonly SemaphoreSlim _cloudSyncGate = new(1, 1);

    // One stable GUID per finished game session. Completed uploads are removed
    // only after a server 200 response. The API rejects duplicates, allowing
    // retries on later launches without double-counting game time.
    private async Task SyncCloudDataAsync(string username)
    {
        if (_cloudSession is null || _cloudOffline ||
            !username.Equals(_cloudSession.Nickname, StringComparison.OrdinalIgnoreCase))
            return;
        if (!await _cloudSyncGate.WaitAsync(0)) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            SolarisCloudSession session = _cloudSession;
            PlayerActivity state = await ReadActivityAsync(username);
            foreach (CloudGameUpload upload in state.PendingCloudSessions.ToArray())
            {
                using var content = JsonContent.Create(upload);
                using HttpResponseMessage response = await SendCloudAsync(client,
                    session, HttpMethod.Post, "api/v1/me/playtime", content);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("Сервер не принял игровой сеанс.");
                state.PendingCloudSessions.Remove(upload);
                await SaveActivityAsync(username, state);
            }

            using HttpResponseMessage totals = await SendCloudAsync(client, session,
                HttpMethod.Get, "api/v1/me/playtime", null);
            totals.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(
                await totals.Content.ReadAsStringAsync());
            state.SyncedVanillaSeconds = document.RootElement
                .GetProperty("vanillaSeconds").GetInt64();
            state.SyncedModdedSeconds = document.RootElement
                .GetProperty("moddedSeconds").GetInt64();
            state.HasCloudTotals = true;
            await SaveActivityAsync(username, state);
            _cloudOffline = false;
            await RefreshActivityAndProfileAsync(username);
            await SyncCloudSkinAsync(username);
        }
        catch (HttpRequestException)
        {
            // Preserve all pending uploads in AppData for the next online login.
            _cloudOffline = true;
            ProfileText.Text = "SOLARIS ID — без соединения (игра доступна)";
            FullProfileStatus.Text = ProfileText.Text;
        }
        catch (TaskCanceledException) { _cloudOffline = true; }
        finally { _cloudSyncGate.Release(); }
    }

    private async Task<byte[]?> DownloadCloudSkinAsync()
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            using HttpResponseMessage response = await SendCloudAsync(client, session,
                HttpMethod.Get, "api/v1/me/skin", null);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            byte[] data = await response.Content.ReadAsByteArrayAsync();
            return data.Length is > 0 and < 524288 ? data : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private async Task UploadCloudSkinAsync(byte[] png)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var body = new ByteArrayContent(png);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            using HttpResponseMessage response = await SendCloudAsync(client, session,
                HttpMethod.Put, "api/v1/me/skin", body);
            response.EnsureSuccessStatusCode();
            TryDeleteFile(Path.Combine(Path.GetDirectoryName(CurrentSkinPath())!, "skin-pending.flag"));
        }
        catch (Exception)
        {
            // Pending flag ensures a later authenticated session can retry.
            string marker = Path.Combine(Path.GetDirectoryName(CurrentSkinPath())!,
                "skin-pending.flag");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "pending");
        }
    }

    private async Task SyncCloudSkinAsync(string username)
    {
        if (_cloudSession is null || _cloudOffline) return;
        string path = CurrentSkinPath();
        string pending = Path.Combine(Path.GetDirectoryName(path)!, "skin-pending.flag");
        try
        {
            if (File.Exists(path) && File.Exists(pending))
            {
                await UploadCloudSkinAsync(await File.ReadAllBytesAsync(path));
                return;
            }
            if (!File.Exists(path))
            {
                byte[]? remote = await DownloadCloudSkinAsync();
                if (remote is null) return;
                // Decode before persisting to avoid poisoning the local preview.
                DecodeSkin(remote);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, remote);
                await PrepareSkinPreviewAsync(username);
            }
        }
        catch { /* Best-effort cloud sync never blocks the game. */ }
    }
}
