using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace Solaris.Id.Api;

public static class ControlEndpoints
{
    private const string AdminRole = "SolarisAdmin";
    private static bool ValidReason(string? value) => value is not null && value.Trim().Length is >= 3 and <= 256;
    private static void Audit(SolarisDbContext data, string actor, string target, string operation, string reason) => data.AuditEvents.Add(new SolarisAuditEvent {
        ActorId = actor, TargetId = target, Operation = operation, Reason = reason.Trim(), Utc = DateTimeOffset.UtcNow
    });
    private static async Task<bool> IsCurrentAdminAsync(ClaimsPrincipal actor, SolarisDbContext data)
    {
        string? id = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await data.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (user is null || user.IsBanned || !user.EmailConfirmed || !user.TwoFactorEnabled) return false;
        Guid sessionId = SessionTokens.SessionId(actor); var now = DateTimeOffset.UtcNow;
        return await data.AuthSessions.AsNoTracking().AnyAsync(x => x.Id == sessionId && x.UserId == user.Id && x.MfaVerified &&
            x.RevokedUtc == null && x.AccessExpiresUtc > now && x.RefreshExpiresUtc > now && x.SecurityStamp == user.SecurityStamp) &&
            await data.UserRoles.Join(data.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.NormalizedName })
                .AnyAsync(x => x.UserId == user.Id && x.NormalizedName == "SOLARISADMIN");
    }
    public static void MapSolarisControl(this WebApplication app)
    {
        var control = app.MapGroup("/api/v1/control").RequireAuthorization("SolarisAdmin").RequireRateLimiting("auth");
        control.MapGet("/status", async (SolarisDbContext data) => await data.Database.CanConnectAsync()
            ? Results.Ok(new { service = "solaris-id", status = "ok", database = "connected" })
            : IdentityEndpoints.Failure("database_unavailable", 503));
        control.MapGet("/accounts", async (string? search, SolarisDbContext data) =>
        {
            string term = (search ?? "").Trim().ToUpperInvariant();
            if (term.Length is < 2 or > 64) return IdentityEndpoints.Failure("search_min_2");
            return Results.Ok(await data.Users.AsNoTracking().Where(u => u.NormalizedNickname.Contains(term) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(term)))
                .OrderBy(u => u.Nickname).Take(25).Select(u => new { u.Id, u.Nickname, u.Email, u.EmailConfirmed, u.IsBanned, u.CreatedUtc, u.LastActiveUtc, u.TwoFactorEnabled }).ToListAsync());
        });
        control.MapGet("/accounts/{id}/stats", async (string id, SolarisDbContext data) =>
        {
            var user = await data.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id); if (user is null) return Results.NotFound();
            var time = await GameAccess.GetTrustedPlaytimeAsync(data, id);
            return Results.Ok(new { user.Id, user.Nickname, user.IsBanned, user.CreatedUtc, user.LastActiveUtc,
                time.VanillaSeconds, time.ModdedSeconds, time.TotalSeconds, time.Source });
        });
        foreach (string operation in new[] { "ban", "unban", "revoke" })
        {
            string action = operation;
            control.MapPost("/accounts/{id}/" + action, async (string id, AdminAction request, ClaimsPrincipal actor, UserManager<SolarisUser> users, SolarisDbContext data) =>
            {
                if (!ValidReason(request.Reason)) return IdentityEndpoints.Failure("reason_required");
                await using var transaction = await data.Database.BeginTransactionAsync();
                // Serialize changes to administrator safety rules across replicas.
                await data.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(76342001)");
                if (!await IsCurrentAdminAsync(actor, data)) return Results.Forbid();
                var target = await SessionTokens.LockUserAsync(data, id); if (target is null) return Results.NotFound();
                if (action == "ban" && (target.Id == actor.FindFirstValue(ClaimTypes.NameIdentifier) || await users.IsInRoleAsync(target, AdminRole))) return IdentityEndpoints.Failure("protected_admin");
                if (action is "ban" or "unban")
                {
                    target.IsBanned = action == "ban";
                    if (!(await users.UpdateAsync(target)).Succeeded) return IdentityEndpoints.Failure("account_update_conflict", 409);
                }
                if (!(await users.UpdateSecurityStampAsync(target)).Succeeded) return IdentityEndpoints.Failure("account_update_conflict", 409);
                await SessionTokens.RevokeAllAsync(data, target.Id);
                Audit(data, actor.FindFirstValue(ClaimTypes.NameIdentifier)!, target.Id, action, request.Reason!);
                await data.SaveChangesAsync(); await transaction.CommitAsync();
                return Results.Ok(new { status = action == "ban" ? "banned" : action == "unban" ? "unbanned" : "revoked" });
            });
        }
        control.MapPost("/accounts/{id}/roles", async (string id, RoleAction request, ClaimsPrincipal actor, UserManager<SolarisUser> users, RoleManager<IdentityRole> roles, SolarisDbContext data) =>
        {
            if (request.Role != AdminRole || !ValidReason(request.Reason)) return IdentityEndpoints.Failure("role_or_reason_invalid");
            await using var transaction = await data.Database.BeginTransactionAsync();
            await data.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(76342001)");
            if (!await IsCurrentAdminAsync(actor, data)) return Results.Forbid();
            var target = await SessionTokens.LockUserAsync(data, id); if (target is null) return Results.NotFound();
            bool hasRole = await users.IsInRoleAsync(target, AdminRole);
            if (request.Enabled && (!target.EmailConfirmed || !target.TwoFactorEnabled || target.IsBanned)) return IdentityEndpoints.Failure("admin_requires_confirmed_email_and_mfa", 409);
            if (!request.Enabled && hasRole)
            {
                var roleId = await data.Roles.Where(x => x.NormalizedName == AdminRole.ToUpperInvariant()).Select(x => x.Id).SingleAsync();
                if (await data.UserRoles.CountAsync(x => x.RoleId == roleId) <= 1) return IdentityEndpoints.Failure("last_admin_protected", 409);
            }
            if (hasRole != request.Enabled)
            {
                if (!await roles.RoleExistsAsync(AdminRole) && !(await roles.CreateAsync(new IdentityRole(AdminRole))).Succeeded) return IdentityEndpoints.Failure("role_update_conflict", 409);
                var result = request.Enabled ? await users.AddToRoleAsync(target, AdminRole) : await users.RemoveFromRoleAsync(target, AdminRole);
                if (!result.Succeeded || !(await users.UpdateSecurityStampAsync(target)).Succeeded) return IdentityEndpoints.Failure("role_update_conflict", 409);
                await SessionTokens.RevokeAllAsync(data, target.Id);
            }
            Audit(data, actor.FindFirstValue(ClaimTypes.NameIdentifier)!, target.Id, request.Enabled ? "role-grant" : "role-revoke", request.Reason!);
            await data.SaveChangesAsync(); await transaction.CommitAsync();
            return Results.Ok(new { status = "role_updated", role = AdminRole, enabled = request.Enabled });
        });
        control.MapPost("/accounts/{id}/reset-password", async (string id, AdminAction request, ClaimsPrincipal actor,
            UserManager<SolarisUser> users, Microsoft.AspNetCore.Identity.IEmailSender<SolarisUser> mail, SolarisDbContext data) =>
        {
            if (!ValidReason(request.Reason)) return IdentityEndpoints.Failure("reason_required");
            await using var transaction = await data.Database.BeginTransactionAsync();
            await data.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(76342001)");
            if (!await IsCurrentAdminAsync(actor, data)) return Results.Forbid();
            var target = await SessionTokens.LockUserAsync(data, id); if (target is null) return Results.NotFound();
            if (await users.IsInRoleAsync(target, AdminRole)) return IdentityEndpoints.Failure("protected_admin");
            if (!target.EmailConfirmed || target.Email is null) return IdentityEndpoints.Failure("email_unconfirmed");
            try { await mail.SendPasswordResetCodeAsync(target, target.Email, await users.GeneratePasswordResetTokenAsync(target)); }
            catch { return IdentityEndpoints.Failure("email_delivery_unavailable", 503); }
            Audit(data, actor.FindFirstValue(ClaimTypes.NameIdentifier)!, target.Id, "reset-request", request.Reason!);
            await data.SaveChangesAsync(); await transaction.CommitAsync(); return Results.Ok(new { status = "password_reset_email_sent" });
        });
        control.MapGet("/audit", async (SolarisDbContext data) => Results.Ok(await data.AuditEvents.AsNoTracking().OrderByDescending(e => e.Utc).Take(100).ToListAsync()));
    }
    public static async Task BootstrapAdminAsync(IServiceProvider services, string userId)
    {
        if (!Guid.TryParse(userId, out _)) throw new InvalidOperationException("Bootstrap requires the explicit persistent user UUID, never a nickname.");
        await using var scope = services.CreateAsyncScope();
        var data = scope.ServiceProvider.GetRequiredService<SolarisDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SolarisUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await using var transaction = await data.Database.BeginTransactionAsync();
        await data.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(76342001)");
        var target = await SessionTokens.LockUserAsync(data, userId) ?? throw new InvalidOperationException("Bootstrap user does not exist.");
        if (!target.EmailConfirmed || !target.TwoFactorEnabled || target.IsBanned) throw new InvalidOperationException("Bootstrap requires a confirmed, unbanned user with authenticator MFA already enabled.");
        if (await users.IsInRoleAsync(target, AdminRole)) { await transaction.CommitAsync(); return; }
        if (await data.UserRoles.Join(data.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.NormalizedName).AnyAsync(x => x == AdminRole.ToUpperInvariant()))
            throw new InvalidOperationException("An administrator already exists. Use audited MFA-protected Control roles management.");
        if (!await roles.RoleExistsAsync(AdminRole) && !(await roles.CreateAsync(new IdentityRole(AdminRole))).Succeeded) throw new InvalidOperationException("Could not create administrator role.");
        if (!(await users.AddToRoleAsync(target, AdminRole)).Succeeded || !(await users.UpdateSecurityStampAsync(target)).Succeeded) throw new InvalidOperationException("Could not grant administrator role.");
        await SessionTokens.RevokeAllAsync(data, target.Id);
        Audit(data, "operator-cli", target.Id, "bootstrap-admin", "Explicit first-administrator UUID bootstrap by server operator");
        await data.SaveChangesAsync(); await transaction.CommitAsync();
    }
}
public sealed record AdminAction(string? Reason);
public sealed record RoleAction(string? Role, bool Enabled, string? Reason);
