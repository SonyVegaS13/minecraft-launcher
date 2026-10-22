using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    // Remains unset until an actual deployment is approved. A site operator may
    // supply a non-secret per-install solaris-id.json, shown on the login screen.
    private const string OfficialApiOrigin = "";
    private readonly SolarisIdConfiguration _idConfiguration = SolarisIdConfiguration.Load(
#if SOLARIS_ID_TEST_BUILD
        SolarisTestEdition.ConfigFile);
#else
        Path.Combine(AppContext.BaseDirectory, "solaris-id.json"));
#endif
    private bool _cloudMode;
    private bool _cloudResetMode;
    private bool _cloudOffline;
    private SolarisCloudSession? _cloudSession;
    private sealed class SolarisCloudSession : SolarisIdSession
    {
        public SolarisCloudSession() { }
        internal bool ServerRevocationConfirmed { get; set; }
    }
    private string CloudSessionFile => Path.Combine(_stateDir, "cloud-session.dat");

    private static HttpClient NewCloudHttpClient(int timeoutSeconds = 20) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

    private Uri? GetCloudOrigin()
    {
#if SOLARIS_ID_TEST_BUILD
        // Test credentials must never follow a developer environment override
        // or the installed launcher's normal configuration/official origin.
        return SolarisIdConfiguration.ValidateOrigin(_idConfiguration.ApiOrigin);
#else
        string configured = App.IsDeveloperMode
            ? Environment.GetEnvironmentVariable("SOLARIS_ID_API_ORIGIN") ?? _idConfiguration.ApiOrigin
            : string.IsNullOrWhiteSpace(OfficialApiOrigin) ? _idConfiguration.ApiOrigin : OfficialApiOrigin;
        return SolarisIdConfiguration.ValidateOrigin(configured);
#endif
    }

    private void InitializeCloudMode() => RefreshCloudAuthLayout();
    private void LocalAuthMode_Click(object sender, RoutedEventArgs e)
    {
        _cloudMode = false; _registerMode = false; _cloudResetMode = false;
        ResetAuthScreen();
    }
    private void CloudAuthMode_Click(object sender, RoutedEventArgs e)
    {
        if (GetCloudOrigin() is null)
        {
            AuthStatus.Text = "Solaris ID не настроен: оператору требуется HTTPS API. Локальные аккаунты сохранены.";
            return;
        }
        _cloudMode = true; _registerMode = false; _cloudResetMode = false;
        ResetAuthScreen();
    }
    private void ResetAuthScreen()
    {
        LoginBox.Clear(); CloudRegisterNickname.Clear();
        _cloudResetMode = false; CloudResetCodeBox.Clear(); CloudTwoFactorCode.Clear();
        ClearPasswordFields(); CloudUseRecoveryCode.IsChecked = false; AuthStatus.Text = ""; AuthTitle.Text = "Вход в аккаунт";
        AuthActionButton.Content = "ВОЙТИ  ›"; SwitchAuthButton.Content = "Создать аккаунт";
        ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
        RefreshCloudAuthLayout();
    }
    private void RefreshCloudAuthLayout()
    {
        if (AuthLoginLabel is null) return;
        AuthLoginLabel.Text = _cloudMode ? "Email Solaris ID" : "Логин";
        CloudRegisterNicknamePanel.Visibility = _cloudMode && _registerMode ? Visibility.Visible : Visibility.Collapsed;
        CloudResetCodePanel.Visibility = _cloudResetMode ? Visibility.Visible : Visibility.Collapsed;
        CloudTwoFactorPanel.Visibility = _cloudMode && !_registerMode && !_cloudResetMode ? Visibility.Visible : Visibility.Collapsed;
        CloudForgotPasswordButton.Visibility = _cloudMode && !_registerMode ? Visibility.Visible : Visibility.Collapsed;
        CloudForgotPasswordButton.Content = _cloudResetMode ? "Вернуться ко входу" : "Забыли пароль Solaris ID?";
        AuthStorageExplanation.Text = _cloudMode
            ? "Solaris ID · " + GetCloudOrigin()?.GetLeftPart(UriPartial.Authority) + "\nПрофиль синхронизируется; секрет сессии защищён Windows DPAPI."
            : "Аккаунт хранится локально на этом ПК. Это не подтверждение доступа к защищённым серверам.";
        LocalAuthModeButton.Opacity = _cloudMode ? 0.55 : 1; CloudAuthModeButton.Opacity = _cloudMode ? 1 : 0.55;
    }

    private async void CloudForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!_cloudMode || !AuthView.IsEnabled) return;
        if (_cloudResetMode) { ResetAuthScreen(); return; }
        string email = LoginBox.Text.Trim(); Uri? origin = GetCloudOrigin();
        if (origin is null) { AuthStatus.Text = "Сервер Solaris ID не настроен."; return; }
        if (email.Length == 0 || !email.Contains('@')) { AuthStatus.Text = "Сначала введи email Solaris ID."; return; }
        AuthView.IsEnabled = false;
        try
        {
            using var client = NewCloudHttpClient();
            using var response = await client.PostAsJsonAsync(new Uri(origin, "api/v1/identity/forgotPassword"), new { email });
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Письмо сейчас не удалось отправить. Попробуй позже.");
            _cloudResetMode = true; _registerMode = false;
            AuthTitle.Text = "Новый пароль Solaris ID"; AuthActionButton.Content = "ИЗМЕНИТЬ ПАРОЛЬ";
            SwitchAuthButton.Content = "Назад ко входу"; ConfirmPasswordPanel.Visibility = Visibility.Visible;
            ClearPasswordFields(); RefreshCloudAuthLayout();
            AuthStatus.Text = "Если email существует и подтверждён, проверь почту и введи код.";
        }
        catch (HttpRequestException) { AuthStatus.Text = "Нет связи с Solaris ID. Проверьте сеть; локальные данные сохранены."; }
        catch (TaskCanceledException) { AuthStatus.Text = "Solaris ID не ответил вовремя. Повторите позже."; }
        catch (Exception ex) { AuthStatus.Text = ex.Message; }
        finally { AuthView.IsEnabled = true; }
    }

    private async Task AuthenticateCloudAsync(string email, string password)
    {
        Uri origin = GetCloudOrigin() ?? throw new InvalidOperationException("Сервер Solaris ID не настроен.");
        using var client = NewCloudHttpClient();
        if (_cloudResetMode)
        {
            string resetCode = CloudResetCodeBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(resetCode)) throw new InvalidOperationException("Введите код из письма Solaris ID.");
            using var reset = await client.PostAsJsonAsync(new Uri(origin, "api/v1/identity/resetPassword"), new { email, resetCode, newPassword = password });
            if (!reset.IsSuccessStatusCode) throw new InvalidOperationException("Код недействителен или пароль не соответствует требованиям.");
            ResetAuthScreen(); AuthStatus.Text = "Пароль изменён. Теперь войди с новым паролем."; return;
        }
        if (_registerMode)
        {
            string nickname = CloudRegisterNickname.Text.Trim();
            if (!IsValidSolarisNickname(nickname)) throw new InvalidOperationException("Ник: 3–16 латинских букв, цифр или _. ");
            using var response = await client.PostAsJsonAsync(new Uri(origin, "api/v1/accounts"),
                new { nickname, email, password, confirmPassword = CurrentConfirmationPassword() });
            if (response.StatusCode != HttpStatusCode.Accepted)
                throw new InvalidOperationException(response.StatusCode == HttpStatusCode.ServiceUnavailable
                    ? "Регистрация временно недоступна: серверу требуется настройка почты."
                    : "Регистрация отклонена. Проверьте уникальность ника/email и требования к паролю.");
            ClearPasswordFields(); AuthStatus.Text = "Аккаунт создан. Подтверди email из письма Solaris ID, затем войди."; return;
        }
        string twoFactorCode = CloudTwoFactorCode.Text.Replace(" ", "").Trim();
        CloudTwoFactorCode.Clear();
        string? twoFactorRecoveryCode = CloudUseRecoveryCode.IsChecked == true ? twoFactorCode : null;
        if (twoFactorRecoveryCode is not null) twoFactorCode = "";
        using var auth = await client.PostAsJsonAsync(new Uri(origin, "api/v1/identity/login"), new { email, password, twoFactorCode, twoFactorRecoveryCode });
        if (!auth.IsSuccessStatusCode) throw new InvalidOperationException(auth.StatusCode == HttpStatusCode.TooManyRequests
            ? "Слишком много попыток. Повторите вход позже."
            : "Вход отклонён. Проверьте email, пароль, подтверждение почты и код 2FA, если он включён.");
        using JsonDocument payload = JsonDocument.Parse(await auth.Content.ReadAsStringAsync());
        var candidate = new SolarisCloudSession { Origin = origin.ToString(), Email = email, RememberMe = RememberMeCheck.IsChecked == true };
        SolarisIdTransport.ApplyTokens(candidate, payload.RootElement);
        await FetchCloudProfileAsync(candidate, client, allowRefresh: false);
        _cloudSession = candidate; _cloudOffline = false;
        if (candidate.RememberMe) PersistCloudSession(candidate); else DeleteCloudSessionFiles();
        ClearPasswordFields(); ShowMainView(candidate.Nickname);
        _ = SyncCloudDataAsync(candidate.Nickname);
    }

    private async Task FetchCloudProfileAsync(SolarisCloudSession session, HttpClient client, bool allowRefresh)
    {
        using HttpResponseMessage result = await SendCloudAsync(client, session, HttpMethod.Get, "api/v1/me", null, allowRefresh);
        if (result.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Доступ к Solaris ID отозван.");
        result.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        JsonElement user = doc.RootElement;
        string id = user.GetProperty("id").GetString() ?? "";
        string nickname = user.GetProperty("nickname").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(id) || !IsValidSolarisNickname(nickname) ||
            (session.Id.Length != 0 && session.Id != id)) throw new InvalidDataException("Неполный или несовместимый профиль Solaris ID.");
        session.Id = id; session.Nickname = nickname;
        session.Email = user.GetProperty("email").GetString() ?? "";
        session.CreatedUtc = user.GetProperty("createdUtc").GetDateTimeOffset();
        session.LastActiveUtc = user.TryGetProperty("lastActiveUtc", out var last) && last.ValueKind == JsonValueKind.String ? last.GetDateTimeOffset() : null;
        session.IsAdmin = user.TryGetProperty("isAdmin", out var admin) && admin.GetBoolean();
    }

    private Task<HttpResponseMessage> SendCloudAsync(HttpClient client, SolarisCloudSession session,
        HttpMethod method, string endpoint, HttpContent? content, bool allowRefresh = true) =>
        SolarisIdTransport.SendAsync(client, session, method, endpoint, content,
            () => { if (ReferenceEquals(_cloudSession, session) && session.RememberMe) PersistCloudSession(session); }, allowRefresh);

    private async Task<bool> TryRestoreCloudAsync()
    {
        SolarisCloudSession? saved = LoadCloudSession(); Uri? origin = GetCloudOrigin();
        if (saved is null || origin is null || !string.Equals(origin.ToString(), saved.Origin, StringComparison.Ordinal)) return false;
        saved.RememberMe = true; _cloudMode = true; _cloudSession = saved;
        try
        {
            using var client = NewCloudHttpClient(12);
            await FetchCloudProfileAsync(saved, client, allowRefresh: true); _cloudOffline = false;
        }
        catch (UnauthorizedAccessException) { ClearCloudSession(); RefreshCloudAuthLayout(); return false; }
        catch (HttpRequestException) { _cloudOffline = true; }
        catch (TaskCanceledException) { _cloudOffline = true; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { ClearCloudSession(); return false; }
        if (!ReferenceEquals(_cloudSession, saved)) return false;
        RememberMeCheck.IsChecked = true; RefreshCloudAuthLayout(); ShowMainView(saved.Nickname);
        if (!_cloudOffline) _ = SyncCloudDataAsync(saved.Nickname);
        return true;
    }

#if SOLARIS_ID_TEST_BUILD
    private static byte[] Entropy => SHA256.HashData(Encoding.UTF8.GetBytes("Solaris.ID.TestBuild.DPAPI.v1"));
#else
    private static byte[] Entropy => SHA256.HashData(Encoding.UTF8.GetBytes("Solaris.ID.DPAPI.v1"));
#endif
    private void StoreCloudSession(SolarisCloudSession data)
    {
        if (!data.RememberMe) return;
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data);
        try
        {
            byte[] protectedData = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(_stateDir); File.WriteAllBytes(CloudSessionFile + ".tmp", protectedData);
            File.Move(CloudSessionFile + ".tmp", CloudSessionFile, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void PersistCloudSession(SolarisCloudSession session)
    {
        try { StoreCloudSession(session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Never leave a pre-rotation refresh token pretending it is usable.
            session.RememberMe = false; DeleteCloudSessionFiles();
            ProfileText.Text = FullProfileStatus.Text = "Solaris ID подключён; Windows не смог сохранить сессию. При следующем запуске потребуется вход.";
        }
    }
    private SolarisCloudSession? LoadCloudSession()
    {
        try
        {
            if (!File.Exists(CloudSessionFile)) return null;
            byte[] secret = ProtectedData.Unprotect(File.ReadAllBytes(CloudSessionFile), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var session = JsonSerializer.Deserialize<SolarisCloudSession>(secret);
                return session is not null && !string.IsNullOrWhiteSpace(session.Id) && IsValidSolarisNickname(session.Nickname)
                    && SolarisIdConfiguration.ValidateOrigin(session.Origin) is not null ? session : null;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        catch { return null; }
    }
    private void DeleteCloudSessionFiles()
    {
        TryDeleteFile(CloudSessionFile);
        TryDeleteFile(CloudSessionFile + ".tmp");
    }

    private void ClearCloudSession()
    {
        if (_cloudSession is { } previous) previous.Invalidated = true;
        _cloudSession = null; _cloudOffline = false; _gamePasses.Clear(); DeleteCloudSessionFiles();
        if (CloudMfaDetails is not null) CloudMfaDetails.Text = "";
        if (CloudMfaPassword is not null) CloudMfaPassword.Clear();
        if (CloudMfaCode is not null) CloudMfaCode.Clear();
        if (CloudSessionList is not null) CloudSessionList.ItemsSource = null;
        if (CloudGameCode is not null) CloudGameCode.Clear();
        if (CloudGameStatus is not null) CloudGameStatus.Text = "";
        if (CloudProfileMessage is not null) CloudProfileMessage.Text = "";
        if (ControlAccountsList is not null) ControlAccountsList.ItemsSource = null;
        if (ControlAuditText is not null) ControlAuditText.Text = "";
        if (ControlSearchBox is not null) ControlSearchBox.Clear();
        if (ControlReasonBox is not null) ControlReasonBox.Clear();
        if (ControlStatus is not null) ControlStatus.Text = "";
    }
    private async Task LogoutCloudAsync()
    {
        SolarisCloudSession? session = _cloudSession;
        try
        {
            if (session is not null)
            {
                if (session.ServerRevocationConfirmed) return;
                if (session.Invalidated) throw new UnauthorizedAccessException();
                using var client = NewCloudHttpClient(8);
                using var content = JsonContent.Create(new { refreshToken = session.RefreshToken });
                using HttpResponseMessage response = await SendCloudAsync(client, session, HttpMethod.Post, "api/v1/identity/logout", content);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Локальный выход выполнен. Сервер не подтвердил отзыв сессии; завершите её с другого устройства после восстановления связи.",
                "Solaris ID", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { ClearCloudSession(); }
    }
    private async Task<string> GetCurrentLaunchUsernameAsync(string? mode = null)
    {
        if (_cloudSession is { } session)
        {
            if (!_cloudOffline && mode is not null && _idConfiguration.ServerFor(mode) is not null)
            {
                try { await PrepareGamePassAsync(mode); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                { _cloudOffline = true; FullProfileStatus.Text = "API недоступен: локальная игра доступна; защищённый сервер потребует подтверждение."; }
            }
            return session.Nickname;
        }
        LocalAccount? account = await ReadAccountAsync();
        return account?.Username is { Length: > 0 } name ? name : throw new InvalidOperationException("Для запуска Minecraft нужно войти в аккаунт.");
    }
    private static bool IsValidSolarisNickname(string value) => value.Length is >= 3 and <= 16 &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
