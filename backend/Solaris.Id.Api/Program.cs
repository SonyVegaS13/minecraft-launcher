using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using System.Security.Claims;
using Npgsql;
using Solaris.Id.Api;

var builder = WebApplication.CreateBuilder(args);
string db = builder.Configuration["SOLARIS_ID_CONNECTION_STRING"]
    ?? throw new InvalidOperationException(
        "SOLARIS_ID_CONNECTION_STRING is required. Never commit database credentials.");

builder.Services.AddDbContext<SolarisDbContext>(options => options.UseNpgsql(db));
builder.Services.AddIdentityApiEndpoints<SolarisUser>(identity =>
{
    identity.User.RequireUniqueEmail = true;
    identity.SignIn.RequireConfirmedEmail = true;
    identity.Lockout.MaxFailedAccessAttempts = 5;
    identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    identity.Password.RequiredLength = 12;
    identity.Password.RequireDigit = true;
    identity.Password.RequireNonAlphanumeric = true;
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<SolarisDbContext>();
builder.Services.AddScoped<IEmailSender<SolarisUser>, SolarisSmtpSender>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("SolarisAdmin", policy => policy
        .RequireAuthenticatedUser().RequireRole("SolarisAdmin")
        .RequireClaim("amr", "mfa"));
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = 429;
    limiter.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, AutoReplenishment = true
            }));
});

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();

// Reject revoked sessions and banned accounts on *every* authorized API
// request, not only the /me endpoint. Token refresh is handled separately
// by Identity; administrators must also rotate security stamps after a ban.
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";
    if (path.Equals("/api/v1/identity/login", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsPost(context.Request.Method))
    {
        if (context.Request.ContentLength > 16384)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        context.Request.EnableBuffering();
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.TryGetProperty("email", out var field))
            {
                string? email = field.GetString();
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var manager = context.RequestServices.GetRequiredService<UserManager<SolarisUser>>();
                    var candidate = await manager.FindByEmailAsync(email);
                    if (candidate?.IsBanned == true)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException) { /* Identity returns 400. */ }
        finally { context.Request.Body.Position = 0; }
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        var manager = context.RequestServices.GetRequiredService<UserManager<SolarisUser>>();
        var user = await manager.GetUserAsync(context.User);
        if (user is null || user.IsBanned)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        string? stamp = context.User.FindFirstValue(
            context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>()
                .Value.ClaimsIdentity.SecurityStampClaimType);
        if (!string.IsNullOrWhiteSpace(stamp) &&
            !string.Equals(stamp, await manager.GetSecurityStampAsync(user), StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});
app.UseAuthorization();

// Identity's built-in /register only accepts email/password and cannot validate
// Solaris nicknames. Block it, allowing account creation ONLY through /accounts.
// This is a defense in depth alongside the DB's nickname nonempty constraint.
app.Use(async (context, next) =>
{
    if (string.Equals(context.Request.Path.Value?.TrimEnd('/'),
        "/api/v1/identity/register", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { service = "solaris-id", status = "ok" }));
app.MapGroup("/api/v1/identity")
    .MapIdentityApi<SolarisUser>()
    .RequireRateLimiting("auth");

app.MapPost("/api/v1/accounts", async (
    NewAccount request,
    UserManager<SolarisUser> users,
    IEmailSender<SolarisUser> email,
    IConfiguration configuration) =>
{
    if (!Regex.IsMatch(request.Nickname ?? "", @"^[A-Za-z0-9_]{3,16}$"))
        return Results.BadRequest(new { error = "nickname_invalid" });
    if (!new EmailAddressAttribute().IsValid(request.Email))
        return Results.BadRequest(new { error = "email_invalid" });
    string? publicOrigin = configuration["SOLARIS_ID_PUBLIC_ORIGIN"];
    if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
        || origin.Scheme != Uri.UriSchemeHttps)
        return Results.Problem("Solaris ID email verification is not yet configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    if (string.IsNullOrWhiteSpace(configuration["SOLARIS_SMTP_HOST"]) ||
        string.IsNullOrWhiteSpace(configuration["SOLARIS_SMTP_FROM"]))
        return Results.Problem("Solaris ID mail is not yet configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var user = new SolarisUser
    {
        UserName = request.Nickname,
        Nickname = request.Nickname,
        NormalizedNickname = request.Nickname.ToUpperInvariant(),
        Email = request.Email.Trim(),
        CreatedUtc = DateTimeOffset.UtcNow
    };
    IdentityResult result = await users.CreateAsync(user, request.Password);
    if (!result.Succeeded)
        return Results.BadRequest(new { errors = result.Errors.Select(e => e.Code).ToArray() });

    string token = await users.GenerateEmailConfirmationTokenAsync(user);
    string encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
    string link = $"{origin.ToString().TrimEnd('/')}/api/v1/identity/confirmEmail" +
        $"?userId={Uri.EscapeDataString(user.Id)}&code={Uri.EscapeDataString(encoded)}";
    try
    {
        await email.SendConfirmationLinkAsync(user, user.Email!, link);
    }
    catch
    {
        // Not successful: user may request a new confirmation mail after SMTP repair.
        return Results.Problem("Confirmation email could not be delivered.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Accepted(value: new { status = "email_confirmation_required" });
}).RequireRateLimiting("auth");

app.MapGet("/api/v1/me", async (System.Security.Claims.ClaimsPrincipal principal,
    UserManager<SolarisUser> users) =>
{
    SolarisUser? user = await users.GetUserAsync(principal);
    if (user is null || user.IsBanned) return Results.Forbid();
    return Results.Ok(new
    {
        user.Id, user.Nickname, user.Email, user.CreatedUtc,
        user.EmailConfirmed, isAdmin = await users.IsInRoleAsync(user, "SolarisAdmin"),
        type = "solaris-id"
    });
}).RequireAuthorization();

app.MapGet("/api/v1/me/playtime", async (System.Security.Claims.ClaimsPrincipal principal,
    UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    SolarisUser? user = await users.GetUserAsync(principal);
    if (user is null || user.IsBanned) return Results.Forbid();
    var sessions = data.PlayerSessions.AsNoTracking().Where(s => s.UserId == user.Id);
    var vanilla = await sessions.Where(s => s.Mode == "vanilla").SumAsync(s => (long?)s.Seconds) ?? 0;
    var modded = await sessions.Where(s => s.Mode == "modded").SumAsync(s => (long?)s.Seconds) ?? 0;
    var imports = data.LegacyImports.AsNoTracking().Where(x => x.UserId == user.Id);
    vanilla += await imports.SumAsync(x => (long?)x.VanillaSeconds) ?? 0;
    modded += await imports.SumAsync(x => (long?)x.ModdedSeconds) ?? 0;
    return Results.Ok(new { vanillaSeconds = vanilla, moddedSeconds = modded,
        totalSeconds = vanilla + modded });
}).RequireAuthorization();

app.MapPost("/api/v1/me/playtime", async (GameSession request,
    System.Security.Claims.ClaimsPrincipal principal,
    UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    SolarisUser? user = await users.GetUserAsync(principal);
    if (user is null || user.IsBanned) return Results.Forbid();
    if (request.Id == Guid.Empty || request.Mode is not ("vanilla" or "modded") ||
        request.Seconds is < 1 or > 86400 ||
        request.StartedUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
        request.StartedUtc < DateTimeOffset.UtcNow.AddDays(-365) ||
        request.Seconds > (DateTimeOffset.UtcNow - request.StartedUtc).TotalSeconds + 300)
        return Results.BadRequest(new { error = "invalid_session" });
    // Session IDs make uploads idempotent when clients retry after disconnection.
    if (await data.PlayerSessions.AnyAsync(s => s.UserId == user.Id && s.Id == request.Id))
        return Results.Ok(new { status = "already_recorded" });
    data.PlayerSessions.Add(new SolarisPlayerSession {
        Id = request.Id, UserId = user.Id, Mode = request.Mode,
        Seconds = request.Seconds, StartedUtc = request.StartedUtc
    });
    try
    {
        await data.SaveChangesAsync();
        return Results.Ok(new { status = "recorded" });
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        // Two concurrent retries uploaded the same session id.
        return Results.Ok(new { status = "already_recorded" });
    }
}).RequireAuthorization();



app.MapPost("/api/v1/me/legacy", async (LegacyImportRequest request,
    ClaimsPrincipal principal, UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || user.IsBanned) return Results.Forbid();
    if (request.SourceId.Length != 64 || !request.SourceId.All(Uri.IsHexDigit) ||
        request.VanillaSeconds < 0 || request.ModdedSeconds < 0 ||
        request.VanillaSeconds + request.ModdedSeconds > 5L * 365 * 24 * 3600)
        return Results.BadRequest(new { error = "legacy_import_invalid" });
    if (await data.LegacyImports.AnyAsync(x =>
        x.UserId == user.Id && x.SourceId == request.SourceId))
        return Results.Ok(new { status = "already_imported" });
    data.LegacyImports.Add(new SolarisLegacyImport
    {
        UserId = user.Id,
        SourceId = request.SourceId,
        VanillaSeconds = request.VanillaSeconds,
        ModdedSeconds = request.ModdedSeconds,
        ImportedUtc = DateTimeOffset.UtcNow
    });
    try
    {
        await data.SaveChangesAsync();
        return Results.Ok(new { status = "imported" });
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        return Results.Ok(new { status = "already_imported" });
    }
}).RequireAuthorization().RequireRateLimiting("auth");

app.MapGet("/api/v1/me/skin", async (ClaimsPrincipal principal,
    UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || user.IsBanned) return Results.Forbid();
    var skin = await data.Skins.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == user.Id);
    return skin is null ? Results.NotFound() : Results.File(skin.Png, "image/png");
}).RequireAuthorization();

app.MapPut("/api/v1/me/skin", async (HttpRequest request, ClaimsPrincipal principal,
    UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || user.IsBanned) return Results.Forbid();
    if (request.ContentLength is > 524288)
        return Results.BadRequest(new { error = "skin_too_large" });
    await using var stream = new MemoryStream();
    await request.Body.CopyToAsync(stream);
    if (stream.Length is < 128 or > 524288)
        return Results.BadRequest(new { error = "skin_size_invalid" });
    byte[] png = stream.ToArray();
    byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
    if (!png.AsSpan(0, 8).SequenceEqual(signature) ||
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != 64 ||
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != 64)
        return Results.BadRequest(new { error = "skin_must_be_png_64x64" });
    SolarisSkin? existing = await data.Skins.FindAsync(user.Id);
    if (existing is null)
    {
        existing = new SolarisSkin { UserId = user.Id };
        data.Skins.Add(existing);
    }
    existing.Png = png;
    existing.Sha256 = Convert.ToHexString(SHA256.HashData(png));
    existing.UpdatedUtc = DateTimeOffset.UtcNow;
    await data.SaveChangesAsync();
    return Results.Ok(new { status = "saved", sha256 = existing.Sha256 });
}).RequireAuthorization().RequireRateLimiting("auth");

// These routes do NOT grant the administrator role. It must be provisioned
// out of band, and the token must carry a verified MFA claim.
var control = app.MapGroup("/api/v1/control")
    .RequireAuthorization("SolarisAdmin").RequireRateLimiting("auth");

control.MapGet("/accounts", async (string? search, SolarisDbContext data) =>
{
    string term = (search ?? "").Trim().ToUpperInvariant();
    if (term.Length is < 2 or > 64)
        return Results.BadRequest(new { error = "search_min_2" });
    var accounts = await data.Users.AsNoTracking()
        .Where(u => u.NormalizedNickname.Contains(term) ||
            (u.NormalizedEmail != null && u.NormalizedEmail.Contains(term)))
        .OrderBy(u => u.Nickname).Take(25)
        .Select(u => new { u.Id, u.Nickname, u.Email, u.EmailConfirmed,
            u.IsBanned, u.CreatedUtc, u.TwoFactorEnabled })
        .ToListAsync();
    return Results.Ok(accounts);
});

control.MapPost("/accounts/{id}/ban", async (string id, AdminAction request,
    ClaimsPrincipal actor, UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    var admin = await users.GetUserAsync(actor);
    var target = await users.FindByIdAsync(id);
    if (admin is null || target is null) return Results.NotFound();
    if (admin.Id == target.Id || await users.IsInRoleAsync(target, "SolarisAdmin"))
        return Results.BadRequest(new { error = "protected_admin" });
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        return Results.BadRequest(new { error = "reason_required" });
    target.IsBanned = true;
    await users.UpdateAsync(target);
    await users.UpdateSecurityStampAsync(target);
    data.AuditEvents.Add(new SolarisAuditEvent
    {
        ActorId = admin.Id, TargetId = target.Id, Operation = "ban",
        Reason = request.Reason.Trim(), Utc = DateTimeOffset.UtcNow
    });
    await data.SaveChangesAsync();
    return Results.Ok(new { status = "banned" });
});

control.MapPost("/accounts/{id}/unban", async (string id, AdminAction request,
    ClaimsPrincipal actor, UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    var admin = await users.GetUserAsync(actor);
    var target = await users.FindByIdAsync(id);
    if (admin is null || target is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        return Results.BadRequest(new { error = "reason_required" });
    target.IsBanned = false;
    await users.UpdateAsync(target);
    await users.UpdateSecurityStampAsync(target);
    data.AuditEvents.Add(new SolarisAuditEvent
    {
        ActorId = admin.Id, TargetId = target.Id, Operation = "unban",
        Reason = request.Reason.Trim(), Utc = DateTimeOffset.UtcNow
    });
    await data.SaveChangesAsync();
    return Results.Ok(new { status = "unbanned" });
});

control.MapPost("/accounts/{id}/revoke", async (string id, AdminAction request,
    ClaimsPrincipal actor, UserManager<SolarisUser> users, SolarisDbContext data) =>
{
    var admin = await users.GetUserAsync(actor);
    var target = await users.FindByIdAsync(id);
    if (admin is null || target is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        return Results.BadRequest(new { error = "reason_required" });
    await users.UpdateSecurityStampAsync(target);
    data.AuditEvents.Add(new SolarisAuditEvent
    {
        ActorId = admin.Id, TargetId = target.Id, Operation = "revoke",
        Reason = request.Reason.Trim(), Utc = DateTimeOffset.UtcNow
    });
    await data.SaveChangesAsync();
    return Results.Ok(new { status = "revoked" });
});

control.MapPost("/accounts/{id}/reset-password", async (string id, AdminAction request,
    ClaimsPrincipal actor, UserManager<SolarisUser> users,
    IEmailSender<SolarisUser> mail, SolarisDbContext data) =>
{
    var admin = await users.GetUserAsync(actor);
    var target = await users.FindByIdAsync(id);
    if (admin is null || target is null) return Results.NotFound();
    if (await users.IsInRoleAsync(target, "SolarisAdmin"))
        return Results.BadRequest(new { error = "protected_admin" });
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        return Results.BadRequest(new { error = "reason_required" });
    if (!target.EmailConfirmed || string.IsNullOrWhiteSpace(target.Email))
        return Results.BadRequest(new { error = "email_unconfirmed" });
    string code = await users.GeneratePasswordResetTokenAsync(target);
    await mail.SendPasswordResetCodeAsync(target, target.Email, code);
    // Administrators do not receive or see the token or the player's password.
    data.AuditEvents.Add(new SolarisAuditEvent
    {
        ActorId = admin.Id, TargetId = target.Id, Operation = "reset-request",
        Reason = request.Reason.Trim(), Utc = DateTimeOffset.UtcNow
    });
    await data.SaveChangesAsync();
    return Results.Ok(new { status = "password_reset_email_sent" });
});

control.MapGet("/audit", async (SolarisDbContext data) =>
    Results.Ok(await data.AuditEvents.AsNoTracking()
        .OrderByDescending(e => e.Utc).Take(100).ToListAsync()));

app.Run();

public sealed record NewAccount(string Nickname, string Email, string Password);
public sealed record GameSession(Guid Id, string Mode, DateTimeOffset StartedUtc, int Seconds);
public sealed record AdminAction(string Reason);
public sealed record LegacyImportRequest(string SourceId, long VanillaSeconds, long ModdedSeconds);
