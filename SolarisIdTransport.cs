using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SolarisLauncher;

internal class SolarisIdSession
{
    public string Origin { get; set; } = "";
    public string Id { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastActiveUtc { get; set; }
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string SessionId { get; set; } = "";
    public bool RememberMe { get; set; }
    public bool IsAdmin { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    [JsonIgnore] internal SemaphoreSlim RefreshGate { get; } = new(1, 1);
    [JsonIgnore] internal volatile bool Invalidated;
}

// Shared by the WPF client and cross-platform protocol tests. Refresh rotation
// is serialized per device session, including concurrent HTTP 401 responses.
internal static class SolarisIdTransport
{
    private static void RequireActive(SolarisIdSession session)
    {
        if (session.Invalidated)
            throw new UnauthorizedAccessException("Сессия Solaris ID отозвана. Войдите снова.");
    }

    internal static void ApplyTokens(SolarisIdSession session, JsonElement token)
    {
        string access = token.GetProperty("accessToken").GetString() ?? "";
        string refresh = token.GetProperty("refreshToken").GetString() ?? "";
        string id = token.GetProperty("sessionId").GetString() ?? "";
        int seconds = token.GetProperty("expiresIn").GetInt32();
        if (token.GetProperty("tokenType").GetString() != "Bearer" ||
            !Regex.IsMatch(access, "\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(refresh, "\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant) ||
            !Guid.TryParseExact(id, "D", out Guid parsedId) || parsedId == Guid.Empty ||
            (session.SessionId.Length != 0 && !string.Equals(session.SessionId, parsedId.ToString(), StringComparison.OrdinalIgnoreCase)) ||
            seconds is < 1 or > 86400)
            throw new JsonException("Неполный ответ сессии Solaris ID.");
        session.AccessToken = access;
        session.RefreshToken = refresh;
        session.SessionId = parsedId.ToString();
        session.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    private static Uri Endpoint(SolarisIdSession session, string endpoint)
    {
        Uri origin = SolarisIdConfiguration.ValidateOrigin(session.Origin) ??
            throw new InvalidOperationException("Solaris ID требует настроенный HTTPS origin.");
        if (!endpoint.StartsWith("api/v1/", StringComparison.Ordinal) || endpoint.Contains("..") ||
            endpoint.Contains('#') || endpoint.Contains('\\'))
            throw new InvalidOperationException("Недопустимый маршрут Solaris ID.");
        return new Uri(origin, endpoint);
    }

    private static async Task RotateAsync(HttpClient client, SolarisIdSession session,
        string observedAccess, Action? onRotated, bool proactive = false)
    {
        await session.RefreshGate.WaitAsync();
        try
        {
            RequireActive(session);
            // Recheck expiry under the gate: another thread can rotate after
            // the caller observed expiry but before it captures a token.
            if (proactive && session.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(15)) return;
            // A competing request already rotated the same refresh token.
            if (session.AccessToken != observedAccess) return;
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                Endpoint(session, "api/v1/identity/refresh"), new { refreshToken = session.RefreshToken });
            RequireActive(session);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                session.Invalidated = true;
                throw new UnauthorizedAccessException("Сессия Solaris ID отозвана. Войдите снова.");
            }
            response.EnsureSuccessStatusCode();
            try
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                RequireActive(session);
                ApplyTokens(session, document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                session.Invalidated = true;
                throw new JsonException("Некорректный ответ обновления сессии Solaris ID.");
            }
            RequireActive(session);
            onRotated?.Invoke();
        }
        finally { session.RefreshGate.Release(); }
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        SolarisIdSession session, HttpMethod method, string endpoint, HttpContent? content,
        Action? onRotated = null, bool allowRefresh = true)
    {
        RequireActive(session);
        Uri address = Endpoint(session, endpoint);
        byte[]? payload = content is null ? null : await content.ReadAsByteArrayAsync();
        if (payload?.Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("Запрос Solaris ID слишком большой.");
        async Task<HttpResponseMessage> Send()
        {
            RequireActive(session);
            using var request = new HttpRequestMessage(method, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            if (payload is not null)
            {
                request.Content = new ByteArrayContent(payload);
                foreach (var header in content!.Headers)
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            HttpResponseMessage response = await client.SendAsync(request);
            if (session.Invalidated)
            {
                response.Dispose();
                RequireActive(session);
            }
            return response;
        }
        string observed = session.AccessToken;
        if (allowRefresh && session.ExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(15))
            await RotateAsync(client, session, observed, onRotated, proactive: true);
        observed = session.AccessToken;
        HttpResponseMessage result = await Send();
        if (allowRefresh && result.StatusCode == HttpStatusCode.Unauthorized)
        {
            result.Dispose();
            await RotateAsync(client, session, observed, onRotated);
            result = await Send();
        }
        if (result.StatusCode == HttpStatusCode.Unauthorized)
        {
            result.Dispose();
            session.Invalidated = true;
            throw new UnauthorizedAccessException("Solaris ID отказал в доступе. Войдите снова или проверьте права.");
        }
        return result;
    }
}
