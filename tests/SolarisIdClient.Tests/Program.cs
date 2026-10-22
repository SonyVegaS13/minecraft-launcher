using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SolarisLauncher;

// Isolated HTTP transport/configuration UNIT tests. Scripted responses exercise
// the actual client code; they are never evidence that backend authentication,
// SMTP, PostgreSQL, Minecraft or Windows DPAPI has been tested.
internal static class Program
{
    private const string OldAccess = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OldRefresh = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string NewAccess = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string NewRefresh = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
    private const string DeviceId = "f5db8a7d-2684-49b9-b7ad-812f01605cb4";
    private static int _checks;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine("Solaris ID client UNIT tests only; scripted HTTP is not backend/game authentication.");
        (string Name, Func<Task> Run)[] tests = [
            ("strict HTTPS operator origin", OriginMatrix),
            ("server audience identifier validation", ServerIdentifiers),
            ("configuration loading is bounded and fail-safe", ConfigurationFiles),
            ("twenty simultaneous HTTP401 responses rotate once", ConcurrentUnauthorized),
            ("twenty proactively expired requests rotate once", ConcurrentProactiveExpiry),
            ("HTTP403 is preserved without refresh", ForbiddenPreserved),
            ("JSON POST is replayed byte-for-byte", () => ReplayContent(HttpMethod.Post, "application/json; charset=utf-8")),
            ("binary PNG PUT is replayed byte-for-byte", () => ReplayContent(HttpMethod.Put, "image/png")),
            ("refused refresh cannot replay a protected request", RejectedRefresh),
            ("concurrent refused refresh invalidates once", ConcurrentRejectedRefresh),
            ("logout during proactive refresh cannot restore a session", () => InvalidatedDuringRefresh(proactive: true)),
            ("logout during HTTP401 refresh cannot replay a request", () => InvalidatedDuringRefresh(proactive: false)),
            ("logout discards a late successful protected response", () => InvalidatedDuringResponse(HttpStatusCode.OK)),
            ("logout discards a late HTTP401 without rotating", () => InvalidatedDuringResponse(HttpStatusCode.Unauthorized)),
            ("repeated HTTP401 after rotation cannot loop", RepeatedUnauthorized),
            ("explicit no-refresh mode does not rotate", NoRefreshMode),
            ("malformed token responses never partially mutate state", MalformedTokens),
            ("malformed refresh does not persist or replay", MalformedRefresh),
            ("invalid endpoint paths never reach HTTP", InvalidEndpoints),
            ("oversized content never reaches HTTP", OversizedPayload),
        ];
        foreach (var test in tests)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine("PASS " + test.Name); }
            catch (Exception e) { _failed++; Console.WriteLine("FAIL " + test.Name + " (" + e.GetType().Name + "): " + e.Message); }
        }
        Console.WriteLine($"SOLARIS_ID_CLIENT_UNIT: {tests.Length} scenarios, {_checks} checks, {_failed} failures");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool value, string label)
    {
        Interlocked.Increment(ref _checks);
        if (!value) throw new InvalidOperationException(label);
    }

    private static async Task Throws<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, label); return; }
        throw new InvalidOperationException(label);
    }

    private static SolarisIdSession Session(bool expired = false) => new()
    {
        Origin = "https://solaris.example.invalid/", Id = "owner", Nickname = "Owner",
        AccessToken = OldAccess, RefreshToken = OldRefresh, SessionId = DeviceId,
        ExpiresUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1)
    };

    private static HttpResponseMessage Response(HttpStatusCode status, string text = "{}") => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static string TokenResponse() => JsonSerializer.Serialize(new
    {
        tokenType = "Bearer", accessToken = NewAccess, refreshToken = NewRefresh,
        sessionId = DeviceId, expiresIn = 900
    });

    private static bool IsRefresh(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/api/v1/identity/refresh";

    private static async Task OriginMatrix()
    {
        foreach (string? value in new string?[] { null, "", "http://auth.example.test/", "ftp://auth.example.test/",
            "file:///tmp/auth", "https://u:password@auth.example.test/", "https://auth.example.test/api/",
            "https://auth.example.test/?q=x", "https://auth.example.test/#fragment", "https://auth.example.test//",
            "https://auth.example.test/%2fescape", "not an origin" })
            Check(SolarisIdConfiguration.ValidateOrigin(value) is null, "reject non-root HTTPS origin");
        Uri? origin = SolarisIdConfiguration.ValidateOrigin(" https://AUTH.example.test:8443/ ");
        Check(origin?.AbsoluteUri == "https://auth.example.test:8443/", "valid origin normalized and port preserved");
        Check(SolarisIdConfiguration.ValidateOrigin("https://auth.example.test")?.AbsolutePath == "/", "root slash normalized");
        await Task.CompletedTask;
    }

    private static async Task ServerIdentifiers()
    {
        foreach (string good in new[] { "vanilla-local", "modded-local", "s1", new string('a', 64) })
            Check(SolarisIdConfiguration.IsServerId(good), "valid server audience accepted");
        foreach (string? bad in new string?[] { null, "", "a", "Vanilla", "_vanilla", "-vanilla", "with space", "a/b", "a?b", "a#b", "a\nb", new string('a', 65), "ванилла" })
            Check(!SolarisIdConfiguration.IsServerId(bad), "invalid server audience rejected");
        var settings = new SolarisIdConfiguration { VanillaServerId = "vanilla-local", ModdedServerId = "modded-local" };
        Check(settings.ServerFor("vanilla") == "vanilla-local" && settings.ServerFor("modded") == "modded-local", "mode maps only configured audience");
        Check(settings.ServerFor("other") is null, "unknown mode has no server audience");
        await Task.CompletedTask;
    }

    private static async Task ConfigurationFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "solaris-client-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "solaris-id.json");
            Check(SolarisIdConfiguration.Load(file).ApiOrigin == "", "missing configuration leaves service unconfigured");
            await File.WriteAllTextAsync(file, "not json");
            Check(SolarisIdConfiguration.Load(file).ApiOrigin == "", "invalid JSON cannot partially configure service");
            await File.WriteAllTextAsync(file, new string(' ', 16385));
            Check(SolarisIdConfiguration.Load(file).ApiOrigin == "", "configuration size cap enforced");
            await File.WriteAllTextAsync(file, "{\"apiOrigin\":\"https://auth.example.test\",\"vanillaServerId\":\"vanilla-local\"}");
            var result = SolarisIdConfiguration.Load(file);
            Check(result.ApiOrigin == "https://auth.example.test" && result.ServerFor("vanilla") == "vanilla-local", "valid operator configuration loaded");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task ConcurrentUnauthorized()
    {
        const int count = 20;
        int initial = 0, replay = 0, refresh = 0, persisted = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Session();
        using var client = new HttpClient(new ScriptedHandler(async request =>
        {
            if (IsRefresh(request))
            {
                Interlocked.Increment(ref refresh);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("refreshToken").GetString() == OldRefresh, "refresh sends the actual old token once");
                await Task.Delay(20);
                return Response(HttpStatusCode.OK, TokenResponse());
            }
            if (request.Headers.Authorization?.Parameter == OldAccess)
            {
                if (Interlocked.Increment(ref initial) == count) barrier.SetResult();
                await barrier.Task;
                return Response(HttpStatusCode.Unauthorized);
            }
            Check(request.Headers.Authorization?.Parameter == NewAccess, "replayed request uses rotated access");
            Interlocked.Increment(ref replay);
            return Response(HttpStatusCode.OK);
        }));
        await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
        {
            using var result = await SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/me", null, () => Interlocked.Increment(ref persisted));
            Check(result.StatusCode == HttpStatusCode.OK, "parallel request succeeds after rotation");
        }));
        Check(initial == count && replay == count, "all twenty requests actually received401 then retried");
        Check(refresh == 1 && persisted == 1, "exactly one refresh and one persistence callback");
        Check(session.AccessToken == NewAccess && session.RefreshToken == NewRefresh, "rotated pair preserved in current session");
    }

    private static async Task ConcurrentProactiveExpiry()
    {
        int refresh = 0, api = 0, persisted = 0;
        var session = Session(expired: true);
        using var client = new HttpClient(new ScriptedHandler(async request =>
        {
            if (IsRefresh(request))
            {
                Interlocked.Increment(ref refresh); await Task.Delay(20);
                return Response(HttpStatusCode.OK, TokenResponse());
            }
            Interlocked.Increment(ref api);
            Check(request.Headers.Authorization?.Parameter == NewAccess, "expired access never sent to resource");
            return Response(HttpStatusCode.OK);
        }));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var result = await SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/me", null, () => Interlocked.Increment(ref persisted));
            Check(result.IsSuccessStatusCode, "proactive parallel resource request succeeds");
        }));
        Check(refresh == 1 && persisted == 1 && api == 20, "proactive concurrency rotates once");
    }

    private static async Task ForbiddenPreserved()
    {
        int calls = 0;
        using var client = new HttpClient(new ScriptedHandler(request =>
        {
            calls++; Check(!IsRefresh(request), "403 must not invoke refresh");
            return Task.FromResult(Response(HttpStatusCode.Forbidden));
        }));
        using var result = await SolarisIdTransport.SendAsync(client, Session(), HttpMethod.Get, "api/v1/control/audit", null);
        Check(result.StatusCode == HttpStatusCode.Forbidden && calls == 1, "caller receives original403 for role/MFA error");
    }

    private static async Task ReplayContent(HttpMethod method, string contentType)
    {
        byte[] original = method == HttpMethod.Put ? new byte[] {137,80,78,71,13,10,26,10,0,255,128,1} : Encoding.UTF8.GetBytes("{\"nickname\":\"Вега\",\"bytes\":\"\\u0000\"}\n");
        int protectedCalls = 0, refresh = 0, persisted = 0;
        using var body = new ByteArrayContent(original); body.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        body.Headers.TryAddWithoutValidation("X-Content-Test", "retained");
        using var client = new HttpClient(new ScriptedHandler(async request =>
        {
            if (IsRefresh(request)) { refresh++; return Response(HttpStatusCode.OK, TokenResponse()); }
            protectedCalls++;
            Check(request.Method == method, "replay preserves HTTP method");
            Check(request.Content is not null && (await request.Content.ReadAsByteArrayAsync()).SequenceEqual(original), "replay preserves exact UTF8/binary bytes");
            Check(request.Content!.Headers.ContentType!.ToString() == contentType, "replay preserves content type");
            Check(request.Content.Headers.GetValues("X-Content-Test").Single() == "retained", "replay preserves content headers");
            return Response(protectedCalls == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        }));
        using var result = await SolarisIdTransport.SendAsync(client, Session(), method, "api/v1/me/skin", body, () => persisted++);
        Check(result.IsSuccessStatusCode && protectedCalls == 2 && refresh == 1 && persisted == 1, "one exact replay follows one rotation");
    }

    private static async Task RejectedRefresh()
    {
        var session = Session(); int protectedCalls = 0, refresh = 0, persisted = 0;
        using var client = new HttpClient(new ScriptedHandler(request =>
        {
            if (IsRefresh(request)) { refresh++; return Task.FromResult(Response(HttpStatusCode.Unauthorized)); }
            protectedCalls++; return Task.FromResult(Response(HttpStatusCode.Unauthorized));
        }));
        await Throws<UnauthorizedAccessException>(async () => { using var _ = await SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/me", null, () => persisted++); }, "refused refresh reaches caller as expired session");
        Check(protectedCalls == 1 && refresh == 1 && persisted == 0, "invalid refresh cannot replay or persist");
        Check(session.AccessToken == OldAccess && session.RefreshToken == OldRefresh, "refusal never invents replacement tokens");
    }

    private static async Task ConcurrentRejectedRefresh()
    {
        int resources = 0, refresh = 0, persisted = 0;
        var session = Session();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new ScriptedHandler(async request =>
        {
            if (IsRefresh(request))
            {
                Interlocked.Increment(ref refresh); await Task.Delay(20);
                return Response(HttpStatusCode.Unauthorized);
            }
            if (Interlocked.Increment(ref resources) == 20) barrier.SetResult();
            await barrier.Task;
            return Response(HttpStatusCode.Unauthorized);
        }));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await Throws<UnauthorizedAccessException>(async () => { using var result = await SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/me", null, () => Interlocked.Increment(ref persisted)); }, "each queued request sees the same refused session");
        }));
        Check(refresh == 1 && resources == 20 && persisted == 0, "a refused refresh is not retried by queued callers");
        await Throws<UnauthorizedAccessException>(async () => { using var result = await SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/me", null); }, "invalidated session cannot make a later request");
        Check(refresh == 1 && resources == 20, "invalidated session never sends old bearer again");
    }

    private static async Task RepeatedUnauthorized()
    {
        int refresh = 0, protectedCalls = 0;
        using var client = new HttpClient(new ScriptedHandler(request =>
        {
            if (IsRefresh(request)) { refresh++; return Task.FromResult(Response(HttpStatusCode.OK, TokenResponse())); }
            protectedCalls++; return Task.FromResult(Response(HttpStatusCode.Unauthorized));
        }));
        await Throws<UnauthorizedAccessException>(async () => { using var _ = await SolarisIdTransport.SendAsync(client, Session(), HttpMethod.Get, "api/v1/me", null); }, "second401 terminates request");
        Check(refresh == 1 && protectedCalls == 2, "no recursive refresh/replay loop");
    }

    private static async Task InvalidatedDuringRefresh(bool proactive)
    {
        int refresh = 0, resources = 0, persisted = 0;
        var session = Session(expired: proactive);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpResponseMessage rotated = Response(HttpStatusCode.OK, TokenResponse());
        using var client = new HttpClient(new ScriptedHandler(async request =>
        {
            if (!IsRefresh(request)) { resources++; return Response(HttpStatusCode.Unauthorized); }
            refresh++; entered.SetResult(); await release.Task;
            return rotated;
        }));
        Task<HttpResponseMessage> pending = SolarisIdTransport.SendAsync(client, session, HttpMethod.Get,
            "api/v1/me", null, () => persisted++);
        await entered.Task;
        session.Invalidated = true;
        release.SetResult();
        await Throws<UnauthorizedAccessException>(async () => { using var _ = await pending; }, "in-flight refresh respects local logout");
        Check(refresh == 1 && resources == (proactive ? 0 : 1) && persisted == 0, "abandoned refresh never persists or replays");
        Check(session.AccessToken == OldAccess && session.RefreshToken == OldRefresh && session.Invalidated, "late rotation cannot repopulate invalidated session");
        await Throws<ObjectDisposedException>(async () => { await rotated.Content.ReadAsStringAsync(); }, "abandoned refresh response is disposed");
    }

    private static async Task InvalidatedDuringResponse(HttpStatusCode status)
    {
        int calls = 0;
        var session = Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpResponseMessage result = Response(status);
        using var client = new HttpClient(new ScriptedHandler(async request =>
        {
            calls++; Check(!IsRefresh(request), "late response cannot trigger refresh");
            entered.SetResult(); await release.Task;
            return result;
        }));
        Task<HttpResponseMessage> pending = SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/control/audit", null);
        await entered.Task;
        session.Invalidated = true;
        release.SetResult();
        await Throws<UnauthorizedAccessException>(async () => { using var _ = await pending; }, "late response cannot reach a replacement account UI");
        Check(calls == 1 && session.Invalidated, "late response never reactivates the abandoned account");
        await Throws<ObjectDisposedException>(async () => { await result.Content.ReadAsStringAsync(); }, "abandoned protected response is disposed");
    }

    private static async Task NoRefreshMode()
    {
        int calls = 0;
        using var client = new HttpClient(new ScriptedHandler(request =>
        {
            calls++; Check(!IsRefresh(request), "explicit no-refresh cannot rotate");
            return Task.FromResult(Response(HttpStatusCode.Unauthorized));
        }));
        await Throws<UnauthorizedAccessException>(async () => { using var _ = await SolarisIdTransport.SendAsync(client, Session(expired: true), HttpMethod.Get, "api/v1/me", null, allowRefresh: false); }, "no-refresh401 terminates authentication");
        Check(calls == 1, "no protected retry in no-refresh mode");
    }

    private static async Task MalformedTokens()
    {
        string good = TokenResponse();
        string[] malformed = ["{}", good.Replace(NewAccess, "short"), good.Replace(NewRefresh, ""), good.Replace(DeviceId, "not-a-guid"),
            good.Replace("900", "0"), good.Replace("900", "86401"), good.Replace("900", "2147483648"), good.Replace(NewAccess, new string('!', 43)),
            good.Replace("\"expiresIn\":900", "\"expiresIn\":\"900\""), good.Replace("\"sessionId\":\"" + DeviceId + "\"", "\"sessionId\":null")];
        foreach (string json in malformed)
        {
            var session = Session(); DateTimeOffset expiry = session.ExpiresUtc; bool rejected = false;
            using var token = JsonDocument.Parse(json);
            try { SolarisIdTransport.ApplyTokens(session, token.RootElement); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { rejected = true; }
            Check(rejected, "malformed response rejected");
            Check(session.AccessToken == OldAccess && session.RefreshToken == OldRefresh && session.SessionId == DeviceId && session.ExpiresUtc == expiry, "malformed token response leaves every field unchanged");
        }
        var valid = Session(); using var doc = JsonDocument.Parse(good); SolarisIdTransport.ApplyTokens(valid, doc.RootElement);
        Check(valid.AccessToken == NewAccess && valid.RefreshToken == NewRefresh && valid.SessionId == DeviceId && valid.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(14), "complete valid token response applied");
        await Task.CompletedTask;
    }

    private static async Task MalformedRefresh()
    {
        int resources = 0, persisted = 0;
        var session = Session();
        using var client = new HttpClient(new ScriptedHandler(request =>
        {
            if (IsRefresh(request)) return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
            resources++; return Task.FromResult(Response(HttpStatusCode.Unauthorized));
        }));
        bool failed = false;
        try { using var _ = await SolarisIdTransport.SendAsync(client, session, HttpMethod.Get, "api/v1/me", null, () => persisted++); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { failed = true; }
        Check(failed && resources == 1 && persisted == 0, "malformed refresh cannot trigger protected replay or persistence");
        Check(session.AccessToken == OldAccess && session.RefreshToken == OldRefresh, "malformed refresh preserves old pair for explicit error handling");
    }

    private static async Task InvalidEndpoints()
    {
        int calls = 0;
        using var client = new HttpClient(new ScriptedHandler(request => { calls++; return Task.FromResult(Response(HttpStatusCode.OK)); }));
        foreach (string endpoint in new[] { "https://other.example.test/", "//other.example.test/", "api/v1/../control", "api/v1/me#fragment", "api/v1/\\other" })
            await Throws<InvalidOperationException>(async () => { using var _ = await SolarisIdTransport.SendAsync(client, Session(), HttpMethod.Get, endpoint, null); }, "invalid route rejected before network");
        var invalidOrigin = Session(); invalidOrigin.Origin = "http://solaris.example.test/";
        await Throws<InvalidOperationException>(async () => { using var _ = await SolarisIdTransport.SendAsync(client, invalidOrigin, HttpMethod.Get, "api/v1/me", null); }, "non-HTTPS origin cannot receive bearer");
        Check(calls == 0, "no request or bearer sent to invalid origin/route");
    }

    private static async Task OversizedPayload()
    {
        int calls = 0;
        using var client = new HttpClient(new ScriptedHandler(request => { calls++; return Task.FromResult(Response(HttpStatusCode.OK)); }));
        using var body = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]);
        await Throws<InvalidOperationException>(async () => { using var _ = await SolarisIdTransport.SendAsync(client, Session(), HttpMethod.Put, "api/v1/me/skin", body); }, "client body limit enforced before network");
        Check(calls == 0, "oversized request never sent");
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => script(request);
    }
}
