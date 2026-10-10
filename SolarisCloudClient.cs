using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SolarisLauncher;

public partial class MainWindow
{
    // The official HTTPS origin is assigned only after Millida deployment.
    // In isolated --dev-test, developers can supply SOLARIS_ID_API_ORIGIN.
    private const string OfficialApiOrigin = "";
    private bool _cloudMode;
    private bool _cloudResetMode;
    private bool _cloudOffline;
    private SolarisCloudSession? _cloudSession;

    private sealed class SolarisCloudSession
    {
        public string Origin { get; set; } = "";
        public string Id { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public bool IsAdmin { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }

    private string CloudSessionFile => Path.Combine(_stateDir, "cloud-session.dat");

    private Uri? GetCloudOrigin()
    {
        string configured = App.IsDeveloperMode
            ? Environment.GetEnvironmentVariable("SOLARIS_ID_API_ORIGIN") ?? ""
            : OfficialApiOrigin;
        if (!Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out Uri? value) ||
            value.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(value.UserInfo))
            return null;
        return value;
    }

    private void InitializeCloudMode() => RefreshCloudAuthLayout();

    private void LocalAuthMode_Click(object sender, RoutedEventArgs e)
    {
        _cloudMode = false;
        _registerMode = false;
        _cloudResetMode = false;
        ResetAuthScreen();
    }

    private void CloudAuthMode_Click(object sender, RoutedEventArgs e)
    {
        if (GetCloudOrigin() is null)
        {
            AuthStatus.Text = "SOLARIS ID ожидает подключения отдельного сервера Millida. Пока используй локальный аккаунт.";
            return;
        }
        _cloudMode = true;
        _registerMode = false;
        _cloudResetMode = false;
        ResetAuthScreen();
    }

    private void ResetAuthScreen()
    {
        LoginBox.Clear();
        CloudRegisterNickname.Clear();
        _cloudResetMode = false;
        CloudResetCodeBox.Clear();
        ClearPasswordFields();
        AuthStatus.Text = "";
        AuthTitle.Text = "Вход в аккаунт";
        AuthActionButton.Content = "ВОЙТИ  ›";
        SwitchAuthButton.Content = "Создать аккаунт";
        ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
        RefreshCloudAuthLayout();
    }

    private void RefreshCloudAuthLayout()
    {
        if (AuthLoginLabel is null) return;
        AuthLoginLabel.Text = _cloudMode ? "Email Solaris ID" : "Логин";
        CloudRegisterNicknamePanel.Visibility = _cloudMode && _registerMode
            ? Visibility.Visible : Visibility.Collapsed;
        CloudResetCodePanel.Visibility = _cloudResetMode
            ? Visibility.Visible : Visibility.Collapsed;
        CloudForgotPasswordButton.Visibility = _cloudMode && !_registerMode
            ? Visibility.Visible : Visibility.Collapsed;
        CloudForgotPasswordButton.Content = _cloudResetMode
            ? "Вернуться ко входу" : "Забыли пароль Solaris ID?";
        AuthStorageExplanation.Text = _cloudMode
            ? "SOLARIS ID — аккаунт на независимом защищённом сервере."
            : "Аккаунт хранится локально на этом ПК.";
        LocalAuthModeButton.Opacity = _cloudMode ? 0.55 : 1;
        CloudAuthModeButton.Opacity = _cloudMode ? 1 : 0.55;
    }

    private async void CloudForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!_cloudMode) return;
        if (_cloudResetMode)
        {
            ResetAuthScreen();
            return;
        }
        string email = LoginBox.Text.Trim();
        Uri? origin = GetCloudOrigin();
        if (origin is null)
        {
            AuthStatus.Text = "Сервер Solaris ID ещё не подключён.";
            return;
        }
        if (email.Length == 0 || !email.Contains('@'))
        {
            AuthStatus.Text = "Сначала введи email Solaris ID.";
            return;
        }
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.PostAsJsonAsync(
                new Uri(origin, "api/v1/identity/forgotPassword"), new { email });
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Письмо сейчас не удалось отправить. Попробуй позже.");
            _cloudResetMode = true;
            _registerMode = false;
            AuthTitle.Text = "Новый пароль Solaris ID";
            AuthActionButton.Content = "ИЗМЕНИТЬ ПАРОЛЬ";
            SwitchAuthButton.Content = "Назад ко входу";
            CloudForgotPasswordButton.Content = "Вернуться ко входу";
            ConfirmPasswordPanel.Visibility = Visibility.Visible;
            ClearPasswordFields();
            RefreshCloudAuthLayout();
            AuthStatus.Text = "Если email существует и подтверждён, проверь почту и введи код.";
        }
        catch (Exception ex) { AuthStatus.Text = ex.Message; }
    }

    private async Task AuthenticateCloudAsync(string email, string password)
    {
        Uri origin = GetCloudOrigin() ?? throw new InvalidOperationException(
            "Сервер Solaris ID ещё не подключён.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SolarisLauncher/2.2.10");
        if (_cloudResetMode)
        {
            string resetCode = CloudResetCodeBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(resetCode))
                throw new InvalidOperationException("Введите код из письма Solaris ID.");
            using var reset = await client.PostAsJsonAsync(
                new Uri(origin, "api/v1/identity/resetPassword"),
                new { email, resetCode, newPassword = password });
            if (!reset.IsSuccessStatusCode)
                throw new InvalidOperationException("Код недействителен или пароль не соответствует требованиям.");
            ResetAuthScreen();
            AuthStatus.Text = "Пароль изменён. Теперь войди с новым паролем.";
            return;
        }
        if (_registerMode)
        {
            string nickname = CloudRegisterNickname.Text.Trim();
            if (!IsValidLogin(nickname))
                throw new InvalidOperationException("Ник: 3–16 латинских букв, цифр или _. ");
            using var response = await client.PostAsJsonAsync(
                new Uri(origin, "api/v1/accounts"), new { nickname, email, password });
            if (response.StatusCode != HttpStatusCode.Accepted)
                throw new InvalidOperationException(response.StatusCode ==
                    HttpStatusCode.ServiceUnavailable
                    ? "Регистрация Solaris ID ещё не настроена на сервере."
                    : "Не удалось зарегистрироваться. Проверьте email, ник и требования к паролю.");
            ClearPasswordFields();
            AuthStatus.Text = "Аккаунт создан. Подтверди email из письма Solaris ID, затем войди.";
            return;
        }

        using var auth = await client.PostAsJsonAsync(
            new Uri(origin, "api/v1/identity/login?useCookies=false"),
            new { email, password });
        if (!auth.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "Не удалось войти в Solaris ID. Проверь email, пароль и подтверждение почты.");
        using JsonDocument payload = JsonDocument.Parse(await auth.Content.ReadAsStringAsync());
        JsonElement root = payload.RootElement;
        var candidate = new SolarisCloudSession
        {
            Origin = origin.ToString(),
            Email = email,
            AccessToken = root.GetProperty("accessToken").GetString() ?? "",
            RefreshToken = root.GetProperty("refreshToken").GetString() ?? "",
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(
                root.GetProperty("expiresIn").GetInt32())
        };
        await FetchCloudProfileAsync(candidate, client, allowRefresh: false);
        _cloudSession = candidate;
        _cloudOffline = false;
        if (RememberMeCheck.IsChecked == true) StoreCloudSession(candidate);
        else TryDeleteFile(CloudSessionFile);
        ShowMainView(candidate.Nickname);
        _ = SyncCloudDataAsync(candidate.Nickname);
        CloudLegacyImportPanel.Visibility = Visibility.Visible;
    }

    private async Task FetchCloudProfileAsync(SolarisCloudSession session, HttpClient client,
        bool allowRefresh)
    {
        using HttpResponseMessage result = await SendCloudAsync(
            client, session, HttpMethod.Get, "api/v1/me", null);
        if (result.StatusCode == HttpStatusCode.Unauthorized && allowRefresh)
        {
            using var refreshResponse = await client.PostAsJsonAsync(
                new Uri(session.Origin + "api/v1/identity/refresh"),
                new { refreshToken = session.RefreshToken });
            if (!refreshResponse.IsSuccessStatusCode)
                throw new UnauthorizedAccessException("Сессия Solaris ID больше недействительна.");
            using JsonDocument refreshed = JsonDocument.Parse(
                await refreshResponse.Content.ReadAsStringAsync());
            JsonElement token = refreshed.RootElement;
            session.AccessToken = token.GetProperty("accessToken").GetString() ?? "";
            session.RefreshToken = token.GetProperty("refreshToken").GetString() ?? "";
            session.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(
                token.GetProperty("expiresIn").GetInt32());
            using HttpResponseMessage retry = await SendCloudAsync(
                client, session, HttpMethod.Get, "api/v1/me", null);
            retry.EnsureSuccessStatusCode();
            await ApplyCloudProfileAsync(session, retry);
            StoreCloudSession(session);
            return;
        }
        if (result.StatusCode == HttpStatusCode.Unauthorized ||
            result.StatusCode == HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Сессия Solaris ID отозвана.");
        result.EnsureSuccessStatusCode();
        await ApplyCloudProfileAsync(session, result);
    }

    private static async Task ApplyCloudProfileAsync(SolarisCloudSession session,
        HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement user = doc.RootElement;
        session.Id = user.GetProperty("id").GetString() ?? "";
        session.Nickname = user.GetProperty("nickname").GetString() ?? "";
        session.Email = user.GetProperty("email").GetString() ?? "";
        session.CreatedUtc = user.GetProperty("createdUtc").GetDateTimeOffset();
        session.IsAdmin = user.TryGetProperty("isAdmin", out var admin) && admin.GetBoolean();
        if (string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(session.Nickname))
            throw new InvalidDataException("Неполный профиль Solaris ID.");
    }

    private static Task<HttpResponseMessage> SendCloudAsync(HttpClient client,
        SolarisCloudSession session, HttpMethod method, string endpoint, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, new Uri(session.Origin + endpoint))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client.SendAsync(request);
    }

    private async Task<bool> TryRestoreCloudAsync()
    {
        SolarisCloudSession? saved = LoadCloudSession();
        Uri? origin = GetCloudOrigin();
        if (saved is null || origin is null ||
            !string.Equals(origin.ToString(), saved.Origin, StringComparison.Ordinal))
            return false;
        _cloudMode = true;
        _cloudSession = saved;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            await FetchCloudProfileAsync(saved, client, allowRefresh: true);
            _cloudOffline = false;
        }
        catch (UnauthorizedAccessException)
        {
            ClearCloudSession();
            RefreshCloudAuthLayout();
            return false;
        }
        catch (HttpRequestException) { _cloudOffline = true; }
        catch (TaskCanceledException) { _cloudOffline = true; }

        if (_cloudSession is null) return false;
        RememberMeCheck.IsChecked = true;
        RefreshCloudAuthLayout();
        ShowMainView(saved.Nickname);
        CloudLegacyImportPanel.Visibility = Visibility.Visible;
        if (!_cloudOffline)
            _ = SyncCloudDataAsync(saved.Nickname);
        return true;
    }

    private static byte[] Entropy => SHA256.HashData(Encoding.UTF8.GetBytes("Solaris.ID.DPAPI.v1"));
    private void StoreCloudSession(SolarisCloudSession data)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] protectedData = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_stateDir);
        File.WriteAllBytes(CloudSessionFile + ".tmp", protectedData);
        File.Move(CloudSessionFile + ".tmp", CloudSessionFile, overwrite: true);
        CryptographicOperations.ZeroMemory(plain);
    }

    private SolarisCloudSession? LoadCloudSession()
    {
        try
        {
            if (!File.Exists(CloudSessionFile)) return null;
            byte[] secret = ProtectedData.Unprotect(
                File.ReadAllBytes(CloudSessionFile), Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SolarisCloudSession>(secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        catch { return null; }
    }

    private void ClearCloudSession()
    {
        _cloudSession = null;
        _cloudOffline = false;
        TryDeleteFile(CloudSessionFile);
    }

    private async Task<string> GetCurrentLaunchUsernameAsync()
    {
        if (_cloudSession is not null) return _cloudSession.Nickname;
        LocalAccount? account = await ReadAccountAsync();
        return account?.Username is { Length: > 0 } name ? name
            : throw new InvalidOperationException("Для запуска Minecraft нужно войти в аккаунт.");
    }
}
