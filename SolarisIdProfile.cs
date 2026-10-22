using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace SolarisLauncher;

public partial class MainWindow
{
    private sealed class DeviceSessionRow
    {
        public string Id { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public bool Current { get; set; }
        public bool IsMfa { get; set; }
        public override string ToString() => $"{(Current ? "ЭТО УСТРОЙСТВО" : "Другое устройство")} · {LastSeenUtc.ToLocalTime():dd.MM.yyyy HH:mm} · {(IsMfa ? "2FA" : "обычный вход")}";
    }
    private sealed record GamePass(string ServerId, string Ticket, DateTimeOffset ExpiresUtc, SolarisCloudSession Owner);
    private readonly Dictionary<string, GamePass> _gamePasses = new(StringComparer.Ordinal);
    private bool _gameApprovalBusy;
    private bool _cloudLogoutRunning;

    private void ResetToAuthView(string status = "")
    {
        MainView.Visibility = Visibility.Collapsed; AuthView.Visibility = Visibility.Visible;
        NewsView.Visibility = SettingsView.Visibility = ProfileView.Visibility = ControlView.Visibility = Visibility.Collapsed;
        LoginBox.Clear(); ClearPasswordFields(); CloudTwoFactorCode.Clear();
        RememberMeCheck.IsChecked = false; CloudUseRecoveryCode.IsChecked = false; _registerMode = false; _cloudResetMode = false;
        ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
        AuthTitle.Text = "Вход в аккаунт"; AuthActionButton.Content = "ВОЙТИ  ›"; SwitchAuthButton.Content = "Создать аккаунт";
        AuthStatus.Text = status; RefreshCloudAuthLayout();
    }

    private async void CloudProfileRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudSession is not { } session) { CloudProfileMessage.Text = "Сначала войдите в Solaris ID."; return; }
        await SyncCloudDataAsync(session.Nickname);
        if (!ReferenceEquals(session, _cloudSession) || _cloudLogoutRunning) return;
        CloudProfileMessage.Text = _cloudOffline ? "API недоступен или сессия отозвана. Для защищённого сервера требуется соединение." : "Профиль, скин и подтверждённые часы обновлены.";
    }
    private async void CloudRename_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || _cloudOffline) { CloudProfileMessage.Text = "Для изменения ника подключитесь к Solaris ID."; return; }
        string nickname = CloudNicknameBox.Text.Trim();
        if (!IsValidSolarisNickname(nickname)) { CloudProfileMessage.Text = "Ник: 3–16 латинских букв, цифр или _."; return; }
        if (MessageBox.Show(this, "Смена ника завершит все сессии Solaris ID. Локальные файлы останутся привязаны к постоянному ID. Продолжить?",
            "Solaris ID", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            using var client = NewCloudHttpClient(); using var body = JsonContent.Create(new { nickname });
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Patch, "api/v1/me", body);
            if (response.StatusCode == HttpStatusCode.Conflict) { CloudProfileMessage.Text = "Этот ник уже занят."; return; }
            response.EnsureSuccessStatusCode();
            if (!ReferenceEquals(session, _cloudSession)) return;
            ClearCloudSession(); ResetToAuthView("Ник изменён. Войдите снова; прежние сессии отозваны сервером.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) CloudProfileMessage.Text = "Не удалось изменить ник. Проверьте подключение и действительность сессии."; }
    }
    private async void CloudMfaSetup_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        string password = CloudMfaPassword.Password; CloudMfaPassword.Clear();
        if (session is null || _cloudOffline) { CloudProfileMessage.Text = "Для 2FA нужен онлайн-вход."; return; }
        if (password.Length == 0) { CloudProfileMessage.Text = "Для настройки 2FA введите текущий пароль."; return; }
        try
        {
            using var client = NewCloudHttpClient(); using var body = JsonContent.Create(new { password });
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Post, "api/v1/me/mfa/setup", body);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!ReferenceEquals(session, _cloudSession) || session.Invalidated || _cloudLogoutRunning) return;
            CloudMfaDetails.Text = "Ключ для приложения-аутентификатора: " + doc.RootElement.GetProperty("sharedKey").GetString() +
                "\n" + doc.RootElement.GetProperty("authenticatorUri").GetString();
            CloudProfileMessage.Text = "Сохраните ключ в приложении-аутентификаторе и подтвердите одноразовым кодом. Не публикуйте ключ.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) CloudProfileMessage.Text = "Настройка 2FA отклонена. Проверьте пароль и соединение."; }
    }
    private async void CloudMfaEnable_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession; string code = CloudMfaCode.Text.Replace(" ", "").Trim(); CloudMfaCode.Clear();
        if (session is null || _cloudOffline || code.Length == 0) { CloudProfileMessage.Text = "Для подтверждения 2FA нужны вход и одноразовый код."; return; }
        try
        {
            using var client = NewCloudHttpClient(); using var body = JsonContent.Create(new { code });
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Post, "api/v1/me/mfa/enable", body);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!ReferenceEquals(session, _cloudSession)) return;
            var codes = new List<string>();
            if (doc.RootElement.TryGetProperty("recoveryCodes", out var recovery))
                foreach (JsonElement item in recovery.EnumerateArray()) codes.Add(item.GetString() ?? "");
            session.ServerRevocationConfirmed = true;
            session.Invalidated = true; _cloudOffline = true; DeleteCloudSessionFiles(); _gamePasses.Clear();
            CloudMfaDetails.Text = "2FA включена. Сохраните резервные коды в безопасном месте:\n" + string.Join("\n", codes);
            CloudProfileMessage.Text = "Для Solaris Control выйдите и войдите снова с кодом 2FA. Коды не записываются в файлы лаунчера.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) CloudProfileMessage.Text = "Код 2FA не принят или сервер недоступен."; }
    }
    private async void CloudSessionsRefresh_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession; if (session is null) return;
        try
        {
            using var client = NewCloudHttpClient();
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Get, "api/v1/me/sessions", null);
            response.EnsureSuccessStatusCode();
            var list = await response.Content.ReadFromJsonAsync<List<DeviceSessionRow>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (ReferenceEquals(session, _cloudSession) && !session.Invalidated && !_cloudLogoutRunning) CloudSessionList.ItemsSource = list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or JsonException)
        { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) CloudProfileMessage.Text = "Не удалось получить список устройств."; }
    }
    private async void CloudSessionRevoke_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (session is null || CloudSessionList.SelectedItem is not DeviceSessionRow selected) return;
        if (MessageBox.Show(this, "Завершить выбранную сессию и связанные с ней игровые подключения?", "Solaris ID",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            using var client = NewCloudHttpClient();
            using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Delete,
                "api/v1/me/sessions/" + Uri.EscapeDataString(selected.Id), null);
            response.EnsureSuccessStatusCode();
            if (!ReferenceEquals(session, _cloudSession)) return;
            if (selected.Current) { ClearCloudSession(); ResetToAuthView("Сессия этого устройства завершена сервером."); }
            else { CloudProfileMessage.Text = "Сессия отозвана."; CloudSessionsRefresh_Click(sender, e); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        { if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning) CloudProfileMessage.Text = "Сервер не подтвердил отзыв сессии."; }
    }

    private async Task<GamePass> PrepareGamePassAsync(string mode)
    {
        SolarisCloudSession session = _cloudSession ?? throw new UnauthorizedAccessException("Войдите в Solaris ID.");
        string serverId = _idConfiguration.ServerFor(mode) ?? throw new InvalidOperationException("Для этого режима оператор не настроил защищённый сервер Solaris ID.");
        if (_gamePasses.TryGetValue(mode, out GamePass? existing) && ReferenceEquals(existing.Owner, session) &&
            existing.ServerId == serverId && existing.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(10)) return existing;
        using var client = NewCloudHttpClient(); using var body = JsonContent.Create(new { serverId });
        using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Post, "api/v1/me/game-tickets", body);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement value = document.RootElement;
        string ticket = value.GetProperty("ticket").GetString() ?? "";
        DateTimeOffset expires = value.GetProperty("expiresUtc").GetDateTimeOffset();
        if (value.GetProperty("serverId").GetString() != serverId || ticket.Length is < 16 or > 4096 ||
            expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("Некорректный игровой пропуск Solaris ID.");
        if (!ReferenceEquals(session, _cloudSession) || session.Invalidated || _cloudLogoutRunning) throw new UnauthorizedAccessException("Аккаунт изменён.");
        var pass = new GamePass(serverId, ticket, expires, session); _gamePasses[mode] = pass;
        CloudGameStatus.Text = "Пропуск подготовлен. Подключитесь к серверу, введите показанный им код здесь. Не подтверждайте чужие подключения.";
        return pass;
    }
    private async void CloudGameApprove_Click(object sender, RoutedEventArgs e)
    {
        SolarisCloudSession? session = _cloudSession;
        if (_gameApprovalBusy || _cloudLogoutRunning || session is null) return;
        string mode = CloudGameMode.SelectedIndex == 0 ? "vanilla" : "modded";
        string code = CloudGameCode.Text.Trim();
        if (code.Length is < 4 or > 32 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        { CloudGameStatus.Text = "Введите код, показанный вашим подключением Minecraft."; return; }
        string? server = _idConfiguration.ServerFor(mode);
        if (server is null) { CloudGameStatus.Text = "Оператор ещё не настроил audience этого сервера в solaris-id.json."; return; }
        if (MessageBox.Show(this, $"Подтвердить ВАШЕ текущее подключение?\nАккаунт: {_cloudSession?.Nickname}\nСервер: {server}\nКод: {code}\n\nНе подтверждайте код, присланный другим человеком.",
            "Solaris ID — разрешить подключение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(session, _cloudSession) || session.Invalidated) return;
        _gameApprovalBusy = true;
        try
        {
            GamePass pass = await PrepareGamePassAsync(mode);
            using var client = NewCloudHttpClient(); using var body = JsonContent.Create(new { ticket = pass.Ticket, code });
            using HttpResponseMessage response = await SendCloudAsync(client, pass.Owner, HttpMethod.Post, "api/v1/me/game-tickets/approve", body);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Код/пропуск истёк, ник или сервер не совпадает, либо аккаунт заблокирован. Переподключитесь и попробуйте снова.");
            using JsonDocument approved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (approved.RootElement.GetProperty("status").GetString() != "approved")
                throw new InvalidDataException("API не подтвердил подключение.");
            if (!ReferenceEquals(session, _cloudSession) || session.Invalidated || _cloudLogoutRunning) return;
            _gamePasses.Remove(mode); CloudGameCode.Clear(); _cloudOffline = false;
            CloudGameStatus.Text = "Подключение подтверждено Solaris ID. Игровой сервер проверит разрешение и откроет доступ.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException or JsonException or InvalidDataException or KeyNotFoundException)
        {
            if (_gamePasses.TryGetValue(mode, out GamePass? pending) && ReferenceEquals(pending.Owner, session)) _gamePasses.Remove(mode);
            if (ReferenceEquals(session, _cloudSession) && !_cloudLogoutRunning)
                CloudGameStatus.Text = ex is InvalidOperationException ? ex.Message : "API недоступен или сессия отозвана. Защищённый сервер остаётся закрыт; локальная игра доступна.";
        }
        finally { _gameApprovalBusy = false; }
    }

    // Optional isolated 1.20.1 client. Existing Vanilla 26.2 installation,
    // verification, update state, target version and game directory are untouched.
    private async void CloudVanilla1201_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _idConfiguration.ServerFor("vanilla") is null || _cloudSession is null)
        { CloudGameStatus.Text = "Для этого профиля нужны Solaris ID и настроенный Vanilla audience."; return; }
        if (!TryBeginLaunch(button, "VANILLA 1.20.1", out var cancellation, out var token)) return;
        try
        {
            string directory = Path.Combine(_stateDir, "solaris-id-clients", "vanilla-1.20.1");
            string java = await EnsureJavaForModeAsync(directory, 17, 2, 20, token);
            var launcher = new MinecraftLauncher(new MinecraftPath(directory));
            WireMinecraftProgress(launcher, "Solaris ID Vanilla 1.20.1", 20, 90);
            await launcher.InstallAsync("1.20.1", token);
            string nickname = await GetCurrentLaunchUsernameAsync("vanilla");
            // Offline session is transport metadata only. The protected server
            // independently quarantines and requires explicit HTTPS approval.
            var options = new MLaunchOption
            {
                Session = MSession.CreateOfflineSession(nickname), JavaPath = java,
                MaximumRamMb = GetRamForMode("vanilla"), MinimumRamMb = Math.Min(2048, GetRamForMode("vanilla")),
                GameLauncherName = "SolarisLauncher", GameLauncherVersion = "Solaris-ID"
            };
            var process = await BuildInstalledMinecraftProcessAsync(directory, "1.20.1", options, token); token.ThrowIfCancellationRequested();
            process.StartInfo.UseShellExecute = false; process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden; process.Start();
            CloudGameStatus.Text = "Запущен отдельный клиент 1.20.1. Добавьте адрес защищённого сервера вручную; для кода вернитесь в профиль Solaris ID.";
            await TrackGameProcessAsync(process, nickname, "vanilla");
        }
        catch (OperationCanceledException) { CloudGameStatus.Text = "Подготовка клиента 1.20.1 отменена."; }
        catch (Exception) { CloudGameStatus.Text = "Не удалось подготовить отдельный клиент 1.20.1. Проверьте сеть, Java и локальные файлы."; }
        finally { if (cancellation is not null) EndLaunch(button, "VANILLA 1.20.1", cancellation); }
    }
}
