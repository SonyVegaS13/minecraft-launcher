using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Solaris.Id.Api;

public static class IdentityEndpoints
{
    public static bool ValidNickname(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant);
    public static bool ValidEmail(string? value) => value is { Length: > 3 and <= 254 } && value == value.Trim() && new EmailAddressAttribute().IsValid(value);
    public static bool UniqueViolation(DbUpdateException ex) => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    public static IResult Failure(string code, int status = 400) => Results.Json(new { error = code }, statusCode: status);
    public static Uri? PublicOrigin(IConfiguration configuration) => Uri.TryCreate(configuration["SOLARIS_ID_PUBLIC_ORIGIN"], UriKind.Absolute, out var origin)
        && origin.Scheme == "https" && string.IsNullOrEmpty(origin.UserInfo) && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment) ? origin : null;

    private static async Task SendConfirmation(SolarisUser user, UserManager<SolarisUser> users, IEmailSender<SolarisUser> mail, IConfiguration configuration)
    {
        var origin = PublicOrigin(configuration) ?? throw new InvalidOperationException("Public HTTPS origin required");
        string code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
        await mail.SendConfirmationLinkAsync(user, user.Email!, origin.ToString().TrimEnd('/') + "/api/v1/identity/confirmEmail?userId=" + Uri.EscapeDataString(user.Id) + "&code=" + Uri.EscapeDataString(code));
    }
    public static void MapSolarisIdentity(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/identity").RequireRateLimiting("auth");
        // No MapIdentityApi bypass: every credential issued here is a persisted,
        // revocable Solaris session. Existing Identity users/hashes are reused.
        auth.MapPost("/register", () => Results.NotFound());
        app.MapPost("/api/v1/accounts", async (NewAccount request, UserManager<SolarisUser> users,
            IEmailSender<SolarisUser> mail, IConfiguration configuration, SolarisDbContext data) =>
        {
            if (!ValidNickname(request.Nickname)) return Failure("nickname_invalid");
            if (!ValidEmail(request.Email)) return Failure("email_invalid");
            if (request.Password is null || request.Password.Length is < 12 or > 256 || request.Password != request.ConfirmPassword)
                return Failure("password_or_confirmation_invalid");
            if (PublicOrigin(configuration) is null || string.IsNullOrWhiteSpace(configuration["SOLARIS_SMTP_HOST"]) || string.IsNullOrWhiteSpace(configuration["SOLARIS_SMTP_FROM"]))
                return Failure("email_not_configured", 503);
            var user = new SolarisUser { UserName = request.Nickname, Nickname = request.Nickname!, NormalizedNickname = request.Nickname!.ToUpperInvariant(), Email = request.Email, CreatedUtc = DateTimeOffset.UtcNow };
            await using (var transaction = await data.Database.BeginTransactionAsync())
            {
                if (await data.NicknameReservations.AnyAsync(x => x.NormalizedNickname == user.NormalizedNickname)) return Failure("nickname_reserved", 409);
                IdentityResult created;
                try
                {
                    created = await users.CreateAsync(user, request.Password);
                    if (created.Succeeded)
                    {
                        data.NicknameReservations.Add(new SolarisNicknameReservation { NormalizedNickname = user.NormalizedNickname, UserId = user.Id, ReservedUtc = DateTimeOffset.UtcNow });
                        await data.SaveChangesAsync();
                    }
                }
                catch (DbUpdateException ex) when (UniqueViolation(ex)) { return Failure("nickname_or_email_in_use", 409); }
                if (!created.Succeeded)
                    return Results.Json(new { errors = created.Errors.Select(e => e.Code).ToArray() }, statusCode: created.Errors.Any(e => e.Code.StartsWith("Duplicate", StringComparison.Ordinal)) ? 409 : 400);
                await transaction.CommitAsync();
            }
            try { await SendConfirmation(user, users, mail, configuration); }
            catch { return Failure("confirmation_delivery_unavailable", 503); }
            return Results.Accepted(value: new { status = "email_confirmation_required" });
        }).RequireRateLimiting("auth");

        auth.MapPost("/login", async (LoginRequest request, UserManager<SolarisUser> users, SolarisDbContext data) =>
        {
            if (!ValidEmail(request.Email) || request.Password is null || request.Password.Length > 256) return Failure("invalid_credentials", 401);
            var user = await users.FindByEmailAsync(request.Email!);
            if (user is null) return Failure("invalid_credentials", 401);
            await using var transaction = await data.Database.BeginTransactionAsync();
            user = await SessionTokens.LockUserAsync(data, user.Id);
            if (user is null || user.IsBanned || !user.EmailConfirmed) return Failure("invalid_credentials", 401);
            if (await users.IsLockedOutAsync(user)) return Failure("invalid_credentials", 401);
            if (!await users.CheckPasswordAsync(user, request.Password))
            { await users.AccessFailedAsync(user); await transaction.CommitAsync(); return Failure("invalid_credentials", 401); }
            bool mfa = false;
            if (user.TwoFactorEnabled)
            {
                bool recovery = !string.IsNullOrWhiteSpace(request.TwoFactorRecoveryCode);
                bool secondFactorValid;
                if (recovery && !string.IsNullOrWhiteSpace(request.TwoFactorCode)) return Failure("choose_one_second_factor");
                if (recovery)
                {
                    string code = request.TwoFactorRecoveryCode!.Trim();
                    // Identity persists the consumed code and concurrency stamp
                    // atomically. Reusing it never creates another session.
                    secondFactorValid = code.Length <= 64 && (await users.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(request.TwoFactorCode)) return Failure("two_factor_required", 401);
                    string code = request.TwoFactorCode.Replace(" ", "").Replace("-", "");
                    mfa = code.Length <= 12 && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
                    secondFactorValid = mfa;
                }
                // Recovery restores owner access, while Solaris Control requires
                // a session that actually verified an authenticator OTP.
                if (!secondFactorValid) { await users.AccessFailedAsync(user); await transaction.CommitAsync(); return Failure("invalid_credentials", 401); }
            }
            await users.ResetAccessFailedCountAsync(user);
            var response = await SessionTokens.CreateSessionAsync(user, mfa, data, users);
            await transaction.CommitAsync();
            return Results.Ok(response);
        });
        auth.MapPost("/refresh", async (RefreshRequest request, SolarisDbContext data, UserManager<SolarisUser> users) =>
        {
            if (!SessionTokens.IsPlausible(request.RefreshToken)) return Failure("invalid_session", 401);
            string hash = SessionTokens.Hash(request.RefreshToken!);
            var candidateToken = await data.AuthRefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash);
            if (candidateToken is null) return Failure("invalid_session", 401);
            var candidateSession = await data.AuthSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidateToken.SessionId);
            if (candidateSession is null) return Failure("invalid_session", 401);
            await using var transaction = await data.Database.BeginTransactionAsync();
            // All account mutations lock user before device sessions. This avoids
            // refresh-vs-ban/rename deadlocks and rechecks identity under lock.
            var user = await data.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\"={candidateSession.UserId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync();
            var token = await data.AuthRefreshTokens.FromSqlInterpolated($"SELECT * FROM \"AuthRefreshTokens\" WHERE \"TokenHash\"={hash} FOR UPDATE").SingleOrDefaultAsync();
            if (token is null) return Failure("invalid_session", 401);
            var session = await data.AuthSessions.FromSqlInterpolated($"SELECT * FROM \"AuthSessions\" WHERE \"Id\"={token.SessionId} FOR UPDATE").SingleOrDefaultAsync();
            var now = DateTimeOffset.UtcNow;
            if (session is null) return Failure("invalid_session", 401);
            if (token.ConsumedUtc != null)
            {
                session.RevokedUtc ??= now;
                await data.SaveChangesAsync(); await transaction.CommitAsync();
                return Failure("refresh_replayed_session_revoked", 401);
            }
            if (session.RevokedUtc != null || session.RefreshExpiresUtc <= now || token.ExpiresUtc <= now || user is null || user.IsBanned || !user.EmailConfirmed || session.SecurityStamp != await users.GetSecurityStampAsync(user))
            {
                session.RevokedUtc ??= now; await data.SaveChangesAsync(); await transaction.CommitAsync();
                return Failure("invalid_session", 401);
            }
            string access = SessionTokens.Create(), refresh = SessionTokens.Create();
            token.ConsumedUtc = now;
            session.AccessTokenHash = SessionTokens.Hash(access); session.AccessExpiresUtc = now + SessionTokens.AccessLifetime; session.LastSeenUtc = now;
            data.AuthRefreshTokens.Add(new SolarisRefreshToken { Id = Guid.NewGuid(), SessionId = session.Id, TokenHash = SessionTokens.Hash(refresh), CreatedUtc = now, ExpiresUtc = session.RefreshExpiresUtc });
            await data.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.LastActiveUtc, now));
            await data.SaveChangesAsync(); await transaction.CommitAsync();
            return Results.Ok(SessionTokens.Response(access, refresh, session.Id));
        });
        auth.MapPost("/logout", async (ClaimsPrincipal principal, SolarisDbContext data) =>
        {
            var id = SessionTokens.SessionId(principal);
            await data.AuthSessions.Where(x => x.Id == id && x.UserId == principal.FindFirstValue(ClaimTypes.NameIdentifier) && x.RevokedUtc == null)
                .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedUtc, DateTimeOffset.UtcNow));
            return Results.Ok(new { status = "signed_out" });
        }).RequireAuthorization();
        auth.MapGet("/confirmEmail", async (string? userId, string? code, UserManager<SolarisUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(code) || code.Length > 8192) return Failure("confirmation_invalid");
            var user = await users.FindByIdAsync(userId); if (user is null) return Failure("confirmation_invalid");
            string decoded; try { decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); } catch (FormatException) { return Failure("confirmation_invalid"); }
            var result = await users.ConfirmEmailAsync(user, decoded);
            return result.Succeeded ? Results.Ok(new { status = "email_confirmed" }) : Failure("confirmation_invalid");
        });
        auth.MapPost("/resendConfirmationEmail", async (EmailRequest request, UserManager<SolarisUser> users, IEmailSender<SolarisUser> mail, IConfiguration configuration) =>
        {
            if (!ValidEmail(request.Email)) return Failure("email_invalid");
            var user = await users.FindByEmailAsync(request.Email!);
            if (user != null && !user.EmailConfirmed && !user.IsBanned)
                try { await SendConfirmation(user, users, mail, configuration); } catch { return Failure("email_delivery_unavailable", 503); }
            return Results.Accepted(value: new { status = "check_email" });
        });
        auth.MapPost("/forgotPassword", async (EmailRequest request, UserManager<SolarisUser> users, IEmailSender<SolarisUser> mail) =>
        {
            if (!ValidEmail(request.Email)) return Failure("email_invalid");
            var user = await users.FindByEmailAsync(request.Email!);
            if (user is { EmailConfirmed: true, IsBanned: false })
                try { await mail.SendPasswordResetCodeAsync(user, user.Email!, await users.GeneratePasswordResetTokenAsync(user)); } catch { return Failure("email_delivery_unavailable", 503); }
            return Results.Accepted(value: new { status = "check_email" });
        });
        auth.MapPost("/resetPassword", async (ResetRequest request, UserManager<SolarisUser> users, SolarisDbContext data) =>
        {
            if (!ValidEmail(request.Email) || request.NewPassword is null || request.NewPassword.Length is < 12 or > 256 || string.IsNullOrWhiteSpace(request.ResetCode) || request.ResetCode.Length > 8192) return Failure("reset_invalid");
            var user = await users.FindByEmailAsync(request.Email!);
            if (user is null || !user.EmailConfirmed || user.IsBanned) return Failure("reset_invalid");
            var result = await users.ResetPasswordAsync(user, request.ResetCode, request.NewPassword);
            if (!result.Succeeded) return Failure("reset_invalid");
            await SessionTokens.RevokeAllAsync(data, user.Id);
            return Results.Ok(new { status = "password_reset" });
        });

        var mfa = app.MapGroup("/api/v1/me/mfa").RequireAuthorization().RequireRateLimiting("auth");
        mfa.MapPost("/setup", async (PasswordRequest request, ClaimsPrincipal principal, UserManager<SolarisUser> users, SolarisDbContext data) =>
        {
            await using var transaction = await data.Database.BeginTransactionAsync();
            var user = await SessionTokens.LockAuthenticatedUserAsync(data, principal); if (user is null) return Results.Unauthorized();
            if (user.TwoFactorEnabled) return Failure("mfa_already_enabled", 409);
            if (!await PasswordProof(user, request.Password, users)) { await transaction.CommitAsync(); return Failure("invalid_credentials", 401); }
            string? key = await users.GetAuthenticatorKeyAsync(user);
            if (key is null)
            {
                var reset = await users.ResetAuthenticatorKeyAsync(user); if (!reset.Succeeded) return Failure("mfa_setup_failed", 409);
                key = await users.GetAuthenticatorKeyAsync(user);
                // Identity rotates the security stamp when generating this key.
                // Keep only the live session that just proved its password so
                // it can finish enrollment; every other device must log in.
                Guid current = SessionTokens.SessionId(principal);
                string stamp = await users.GetSecurityStampAsync(user);
                if (await data.AuthSessions.Where(x => x.Id == current && x.UserId == user.Id && x.RevokedUtc == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.SecurityStamp, stamp)) != 1) return Failure("invalid_session", 401);
                await data.AuthSessions.Where(x => x.UserId == user.Id && x.Id != current && x.RevokedUtc == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedUtc, DateTimeOffset.UtcNow));
            }
            await transaction.CommitAsync();
            return Results.Ok(new { sharedKey = key, authenticatorUri = $"otpauth://totp/{Uri.EscapeDataString("Solaris ID:" + user.Email)}?secret={key}&issuer=Solaris%20ID&digits=6" });
        });
        mfa.MapPost("/enable", async (MfaCodeRequest request, ClaimsPrincipal principal, UserManager<SolarisUser> users, SolarisDbContext data) =>
        {
            await using var transaction = await data.Database.BeginTransactionAsync();
            var user = await SessionTokens.LockAuthenticatedUserAsync(data, principal); if (user is null) return Results.Unauthorized();
            if (user.TwoFactorEnabled) return Failure("mfa_already_enabled", 409);
            if (await users.IsLockedOutAsync(user)) return Failure("invalid_credentials", 401);
            string code = (request.Code ?? "").Replace(" ", "");
            if (code.Length != 6 || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
            { await users.AccessFailedAsync(user); await transaction.CommitAsync(); return Failure("mfa_code_invalid"); }
            var enabled = await users.SetTwoFactorEnabledAsync(user, true); if (!enabled.Succeeded) return Failure("mfa_enable_failed", 409);
            var stamp = await users.UpdateSecurityStampAsync(user); if (!stamp.Succeeded) return Failure("mfa_enable_failed", 409);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            if (codes is null) return Failure("mfa_enable_failed", 409);
            await SessionTokens.RevokeAllAsync(data, user.Id);
            await transaction.CommitAsync();
            return Results.Ok(new { recoveryCodes = codes, reauthenticationRequired = true });
        });
        mfa.MapPost("/disable", async (MfaDisableRequest request, ClaimsPrincipal principal, UserManager<SolarisUser> users, SolarisDbContext data) =>
        {
            await using var transaction = await data.Database.BeginTransactionAsync();
            var user = await SessionTokens.LockAuthenticatedUserAsync(data, principal); if (user is null) return Results.Unauthorized();
            if (await users.IsInRoleAsync(user, "SolarisAdmin")) return Failure("remove_admin_role_before_disabling_mfa", 409);
            if (!await PasswordProof(user, request.Password, users, resetOnSuccess: false)) { await transaction.CommitAsync(); return Failure("invalid_credentials", 401); }
            if (request.Code is not { Length: 6 } || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, request.Code))
            { await users.AccessFailedAsync(user); await transaction.CommitAsync(); return Failure("invalid_credentials", 401); }
            var disabled = await users.SetTwoFactorEnabledAsync(user, false); if (!disabled.Succeeded) return Failure("mfa_disable_failed", 409);
            if (!(await users.ResetAuthenticatorKeyAsync(user)).Succeeded || !(await users.UpdateSecurityStampAsync(user)).Succeeded) return Failure("mfa_disable_failed", 409);
            await SessionTokens.RevokeAllAsync(data, user.Id);
            await transaction.CommitAsync();
            return Results.Ok(new { status = "mfa_disabled", reauthenticationRequired = true });
        });
    }
    public static async Task<bool> PasswordProof(SolarisUser user, string? password, UserManager<SolarisUser> users, bool resetOnSuccess = true)
    {
        if (user.IsBanned || await users.IsLockedOutAsync(user) || password is null || password.Length > 256) return false;
        if (!await users.CheckPasswordAsync(user, password)) { await users.AccessFailedAsync(user); return false; }
        if (resetOnSuccess) await users.ResetAccessFailedCountAsync(user); return true;
    }
}
public sealed record NewAccount(string? Nickname, string? Email, string? Password, string? ConfirmPassword);
public sealed record LoginRequest(string? Email, string? Password, string? TwoFactorCode, string? TwoFactorRecoveryCode);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record EmailRequest(string? Email);
public sealed record ResetRequest(string? Email, string? ResetCode, string? NewPassword);
public sealed record PasswordRequest(string? Password);
public sealed record MfaCodeRequest(string? Code);
public sealed record MfaDisableRequest(string? Password, string? Code);
