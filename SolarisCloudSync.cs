using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SolarisLauncher;

public partial class MainWindow
{
    private readonly SemaphoreSlim _cloudSyncGate = new(1, 1);

    // Cloud hours come only from trusted bridge events. Old process-time
    // records/queues remain on disk as local history, never uploaded or added.
    private async Task SyncCloudDataAsync(string username)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || !username.Equals(session.Nickname, StringComparison.OrdinalIgnoreCase)) return;
        if (!await _cloudSyncGate.WaitAsync(0)) return;
        try
        {
            using var client = NewCloudHttpClient();
            await FetchCloudProfileAsync(session, client, allowRefresh: true);
            if (!ReferenceEquals(session, _cloudSession)) return;
            string activityPath = ActivityFile(username);
            PlayerActivity state = await ReadActivityAsync(username);
            using HttpResponseMessage totals = await SendCloudAsync(client, session, HttpMethod.Get, "api/v1/me/playtime", null);
            totals.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await totals.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("source", out var source) || source.GetString() != "trusted-servers")
                throw new InvalidDataException("Сервер не подтвердил источник игровой статистики.");
            long vanilla = document.RootElement.GetProperty("vanillaSeconds").GetInt64();
            long modded = document.RootElement.GetProperty("moddedSeconds").GetInt64();
            if (vanilla < 0 || modded < 0 || vanilla > long.MaxValue - modded) throw new InvalidDataException("Некорректная статистика Solaris ID.");
            if (!ReferenceEquals(session, _cloudSession)) return;
            state.SyncedVanillaSeconds = vanilla; state.SyncedModdedSeconds = modded; state.HasCloudTotals = true; state.TrustedTotalsVersion = 1;
            await SaveActivityFileAsync(activityPath, state, trustedTotalsOnly: true);
            if (!ReferenceEquals(session, _cloudSession)) return;
            _cloudOffline = false;
            ProfileText.Text = FullProfileStatus.Text = "SOLARIS ID — подключён; часы подтверждены игровыми серверами";
            await RefreshActivityAndProfileAsync(username);
            await SyncCloudSkinAsync(username);
        }
        catch (UnauthorizedAccessException)
        {
            if (ReferenceEquals(session, _cloudSession))
            {
                _cloudOffline = true; DeleteCloudSessionFiles(); _gamePasses.Clear();
                ProfileText.Text = FullProfileStatus.Text = "Сессия отозвана — выйдите и войдите снова. Защищённый сервер недоступен.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (ReferenceEquals(session, _cloudSession))
            {
                _cloudOffline = true;
                ProfileText.Text = FullProfileStatus.Text = "Solaris ID без связи. Локальная игра доступна; вход на защищённый сервер требует API.";
            }
        }
        catch (IOException)
        {
            if (ReferenceEquals(session, _cloudSession))
                ProfileText.Text = FullProfileStatus.Text = "Не удалось обновить локальный кеш Solaris ID. Проверьте свободное место и доступ к файлам.";
        }
        finally { _cloudSyncGate.Release(); }
    }

    private async Task<byte[]?> DownloadCloudSkinAsync()
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return null;
        using var client = NewCloudHttpClient(12);
        using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Get, "api/v1/me/skin", null);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 524288) throw new InvalidDataException("Скин слишком большой.");
        using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > 524288) throw new InvalidDataException("Скин слишком большой.");
            await buffer.WriteAsync(chunk.AsMemory(0,read));
        }
        byte[] data = buffer.ToArray();
        if (data.Length is <= 0 or > 524288) throw new InvalidDataException("Скин слишком большой или пуст.");
        DecodeSkin(data);
        return ReferenceEquals(session, _cloudSession) ? data : null;
    }

    private async Task UploadCloudSkinAsync(byte[] png, SolarisCloudSession session)
    {
        if (!ReferenceEquals(session, _cloudSession)) return;
        string path = CurrentSkinPath();
        string marker = Path.Combine(Path.GetDirectoryName(path)!, "skin-pending.flag");
        try
        {
            if (png.Length > 524288) throw new InvalidDataException("Скин превышает лимит 512 КиБ.");
            DecodeSkin(png);
            if (_cloudOffline) throw new HttpRequestException();
            using var client = NewCloudHttpClient();
            using var body = new ByteArrayContent(png);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Put, "api/v1/me/skin", body);
            response.EnsureSuccessStatusCode();
            TryDeleteFile(marker);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "pending");
        }
    }

    private async Task SyncCloudSkinAsync(string username)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) return;
        string path = CurrentSkinPath();
        string pending = Path.Combine(Path.GetDirectoryName(path)!, "skin-pending.flag");
        try
        {
            if (File.Exists(path) && File.Exists(pending))
            {
                await UploadCloudSkinAsync(await File.ReadAllBytesAsync(path), session);
                return;
            }
            // Always refresh cached skins, so changes made on another PC appear.
            byte[]? remote = await DownloadCloudSkinAsync();
            if (!ReferenceEquals(session, _cloudSession)) return;
            if (remote is null) TryDeleteFile(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, remote);
                File.Move(temp, path, overwrite: true);
            }
            await PrepareSkinPreviewAsync(username);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidDataException)
        { /* Keep last valid local skin; never replace it with public nickname data. */ }
    }
}
