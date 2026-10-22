using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Solaris.Id.Api;

public static class SessionTokens
{
    public const string Scheme = "SolarisSession";
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool IsPlausible(string? value) => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static Guid SessionId(ClaimsPrincipal principal) => Guid.TryParse(principal.FindFirstValue("sid"), out var value) ? value : Guid.Empty;

    // Call inside a transaction, before locking sessions or checking mutable
    // account invariants. Authentication may already have tracked an older user.
    public static async Task<SolarisUser?> LockUserAsync(SolarisDbContext data, string userId)
    {
        var user = await data.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\"={userId} FOR UPDATE").SingleOrDefaultAsync();
        if (user is not null) await data.Entry(user).ReloadAsync();
        return user;
    }

    public static async Task<SolarisUser?> LockAuthenticatedUserAsync(SolarisDbContext data, ClaimsPrincipal principal)
    {
        string? userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return null;
        var user = await LockUserAsync(data, userId);
        if (user is null || user.IsBanned || !user.EmailConfirmed) return null;
        Guid sessionId = SessionId(principal); var now = DateTimeOffset.UtcNow;
        // A mutation may have waited for another transaction after the initial
        // bearer check. Recheck revocation/stamp before changing MFA or profile.
        return await data.AuthSessions.AsNoTracking().AnyAsync(x => x.Id == sessionId && x.UserId == user.Id &&
            x.RevokedUtc == null && x.AccessExpiresUtc > now && x.RefreshExpiresUtc > now && x.SecurityStamp == user.SecurityStamp)
            ? user : null;
    }

    public static async Task<object> CreateSessionAsync(SolarisUser user, bool mfa, SolarisDbContext data, UserManager<SolarisUser> users)
    {
        var now = DateTimeOffset.UtcNow;
        string access = Create(), refresh = Create();
        var session = new SolarisAuthSession {
            Id = Guid.NewGuid(), UserId = user.Id, AccessTokenHash = Hash(access),
            AccessExpiresUtc = now + AccessLifetime, RefreshExpiresUtc = now + RefreshLifetime,
            SecurityStamp = await users.GetSecurityStampAsync(user), MfaVerified = mfa,
            CreatedUtc = now, LastSeenUtc = now
        };
        data.AuthSessions.Add(session);
        data.AuthRefreshTokens.Add(new SolarisRefreshToken {
            Id = Guid.NewGuid(), SessionId = session.Id, TokenHash = Hash(refresh),
            CreatedUtc = now, ExpiresUtc = session.RefreshExpiresUtc
        });
        user.LastActiveUtc = now;
        await data.SaveChangesAsync();
        return Response(access, refresh, session.Id);
    }
    public static object Response(string access, string refresh, Guid sessionId) => new {
        tokenType = "Bearer", accessToken = access, refreshToken = refresh,
        expiresIn = (int)AccessLifetime.TotalSeconds, sessionId
    };
    public static async Task RevokeAllAsync(SolarisDbContext data, string userId)
    {
        await data.AuthSessions.Where(s => s.UserId == userId && s.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedUtc, DateTimeOffset.UtcNow));
    }
}

public sealed class SolarisSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, SolarisDbContext data, UserManager<SolarisUser> users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        string value = header[7..];
        if (!SessionTokens.IsPlausible(value)) return AuthenticateResult.Fail("Invalid session");
        string hash = SessionTokens.Hash(value);
        var session = await data.AuthSessions.AsNoTracking().SingleOrDefaultAsync(s => s.AccessTokenHash == hash);
        var now = DateTimeOffset.UtcNow;
        if (session is null || session.RevokedUtc != null || session.AccessExpiresUtc <= now || session.RefreshExpiresUtc <= now)
            return AuthenticateResult.Fail("Expired session");
        var user = await users.FindByIdAsync(session.UserId);
        if (user is null || user.IsBanned || !user.EmailConfirmed || session.SecurityStamp != await users.GetSecurityStampAsync(user))
            return AuthenticateResult.Fail("Revoked session");
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Nickname), new("sid", session.Id.ToString()) };
        foreach (string role in await users.GetRolesAsync(user)) claims.Add(new Claim(ClaimTypes.Role, role));
        if (session.MfaVerified && user.TwoFactorEnabled) claims.Add(new Claim("amr", "mfa"));
        if (session.LastSeenUtc < now.AddMinutes(-1))
        {
            await data.AuthSessions.Where(s => s.Id == session.Id && s.RevokedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenUtc, now));
            await data.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastActiveUtc, now));
        }
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SessionTokens.Scheme)), SessionTokens.Scheme));
    }
}
