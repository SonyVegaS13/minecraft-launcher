using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Solaris.Id.Api;

public static class ProfileEndpoints
{
    public static async Task<object> Profile(SolarisUser user, UserManager<SolarisUser> users) => new {
        user.Id, user.Nickname, user.Email, user.CreatedUtc, user.LastActiveUtc, user.EmailConfirmed,
        isAdmin = await users.IsInRoleAsync(user, "SolarisAdmin"), type = "solaris-id"
    };
    public static void MapSolarisProfiles(this WebApplication app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization();
        me.MapGet("", async (ClaimsPrincipal principal, UserManager<SolarisUser> users) => {
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(await Profile(user, users));
        });
        me.MapPatch("", async (ProfilePatch request, ClaimsPrincipal principal, UserManager<SolarisUser> users, SolarisDbContext data) =>
        {
            if (!IdentityEndpoints.ValidNickname(request.Nickname)) return IdentityEndpoints.Failure("nickname_invalid");
            await using var transaction = await data.Database.BeginTransactionAsync();
            var user = await SessionTokens.LockAuthenticatedUserAsync(data, principal); if (user is null) return Results.Unauthorized();
            string normalized = request.Nickname!.ToUpperInvariant();
            var reservation = await data.NicknameReservations.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedNickname == normalized);
            if (reservation != null && reservation.UserId != user.Id) return IdentityEndpoints.Failure("nickname_reserved", 409);
            user.Nickname = request.Nickname!; user.UserName = request.Nickname; user.NormalizedNickname = normalized;
            IdentityResult updated;
            try { updated = await users.UpdateAsync(user); }
            catch (DbUpdateException ex) when (IdentityEndpoints.UniqueViolation(ex)) { return IdentityEndpoints.Failure("nickname_in_use", 409); }
            if (!updated.Succeeded) return IdentityEndpoints.Failure("profile_update_conflict", 409);
            if (reservation is null)
            {
                data.NicknameReservations.Add(new SolarisNicknameReservation { NormalizedNickname = normalized, UserId = user.Id, ReservedUtc = DateTimeOffset.UtcNow });
                try { await data.SaveChangesAsync(); }
                catch (DbUpdateException ex) when (IdentityEndpoints.UniqueViolation(ex)) { return IdentityEndpoints.Failure("nickname_reserved", 409); }
            }
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) return IdentityEndpoints.Failure("profile_update_conflict", 409);
            await SessionTokens.RevokeAllAsync(data, user.Id);
            await transaction.CommitAsync();
            return Results.Ok(new { user.Id, user.Nickname, user.Email, user.CreatedUtc, user.LastActiveUtc, user.EmailConfirmed,
                isAdmin = await users.IsInRoleAsync(user, "SolarisAdmin"), type = "solaris-id", status = "profile_updated", reauthenticationRequired = true });
        }).RequireRateLimiting("auth");
        me.MapGet("/playtime", async (ClaimsPrincipal principal, SolarisDbContext data) =>
            Results.Ok(await GameAccess.GetTrustedPlaytimeAsync(data, principal.FindFirstValue(ClaimTypes.NameIdentifier)!)));
        me.MapPost("/playtime", () => IdentityEndpoints.Failure("trusted_server_events_required", 403));
        me.MapPost("/legacy", async (LegacyImportRequest request, ClaimsPrincipal principal, SolarisDbContext data) =>
        {
            const long maximum = 5L * 365 * 24 * 3600;
            if (request.SourceId is not { Length: 64 } || !request.SourceId.All(Uri.IsHexDigit) ||
                request.VanillaSeconds is < 0 or > maximum || request.ModdedSeconds is < 0 or > maximum || request.VanillaSeconds + request.ModdedSeconds > maximum)
                return IdentityEndpoints.Failure("legacy_import_invalid");
            string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (await data.LegacyImports.AnyAsync(x => x.UserId == userId && x.SourceId == request.SourceId))
                return Results.Ok(new { status = "already_imported", source = "untrusted-legacy" });
            data.LegacyImports.Add(new SolarisLegacyImport { UserId = userId, SourceId = request.SourceId, VanillaSeconds = request.VanillaSeconds, ModdedSeconds = request.ModdedSeconds, ImportedUtc = DateTimeOffset.UtcNow });
            try { await data.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (IdentityEndpoints.UniqueViolation(ex)) { return Results.Ok(new { status = "already_imported", source = "untrusted-legacy" }); }
            return Results.Ok(new { status = "imported", source = "untrusted-legacy" });
        }).RequireRateLimiting("auth");
        me.MapGet("/sessions", async (ClaimsPrincipal principal, SolarisDbContext data) =>
        {
            string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!; var current = SessionTokens.SessionId(principal); var now = DateTimeOffset.UtcNow;
            return Results.Ok(await data.AuthSessions.AsNoTracking().Where(x => x.UserId == userId && x.RevokedUtc == null && x.RefreshExpiresUtc > now)
                .OrderByDescending(x => x.CreatedUtc).Select(x => new { x.Id, x.CreatedUtc, x.LastSeenUtc, expiresUtc = x.RefreshExpiresUtc, current = x.Id == current, isMfa = x.MfaVerified }).ToListAsync());
        });
        me.MapDelete("/sessions/{id:guid}", async (Guid id, ClaimsPrincipal principal, SolarisDbContext data) =>
        {
            string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (!await data.AuthSessions.AnyAsync(x => x.Id == id && x.UserId == userId)) return Results.NotFound();
            await data.AuthSessions.Where(x => x.Id == id && x.UserId == userId && x.RevokedUtc == null).ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedUtc, DateTimeOffset.UtcNow));
            return Results.Ok(new { status = "revoked" });
        }).RequireRateLimiting("auth");
        me.MapGet("/skin", async (ClaimsPrincipal principal, SolarisDbContext data) =>
        {
            var skin = await data.Skins.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == principal.FindFirstValue(ClaimTypes.NameIdentifier));
            return skin is null ? Results.NotFound() : Results.File(skin.Png, "image/png", enableRangeProcessing: false);
        });
        me.MapPut("/skin", async (HttpRequest request, ClaimsPrincipal principal, SolarisDbContext data) =>
        {
            const int limit = 524288;
            if (request.ContentLength > limit) return IdentityEndpoints.Failure("skin_too_large", 413);
            await using var stream = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                int size = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted); if (size == 0) break;
                if (stream.Length + size > limit) return IdentityEndpoints.Failure("skin_too_large", 413);
                await stream.WriteAsync(buffer.AsMemory(0, size), request.HttpContext.RequestAborted);
            }
            byte[] png = stream.ToArray();
            if (!SkinPng.IsValid(png)) return IdentityEndpoints.Failure("skin_must_be_valid_png_64x64_rgb_or_rgba");
            string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
            string hash = Convert.ToHexString(SHA256.HashData(png)); var now = DateTimeOffset.UtcNow;
            // PostgreSQL upsert serializes concurrent replacement without creating
            // duplicate skins. No caller-controlled SQL is concatenated.
            await data.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Skins\" (\"UserId\",\"Png\",\"Sha256\",\"UpdatedUtc\") VALUES ({userId},{png},{hash},{now}) ON CONFLICT (\"UserId\") DO UPDATE SET \"Png\"=EXCLUDED.\"Png\",\"Sha256\"=EXCLUDED.\"Sha256\",\"UpdatedUtc\"=EXCLUDED.\"UpdatedUtc\"");
            return Results.Ok(new { status = "saved", sha256 = hash });
        }).RequireRateLimiting("auth");
    }
}
public sealed record ProfilePatch(string? Nickname);
public sealed record LegacyImportRequest(string? SourceId, long VanillaSeconds, long ModdedSeconds);
