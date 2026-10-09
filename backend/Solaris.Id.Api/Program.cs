using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
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
        user.EmailConfirmed, type = "solaris-id"
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
        request.Seconds is < 1 or > 86400 || request.StartedUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        return Results.BadRequest(new { error = "invalid_session" });
    // Session IDs make uploads idempotent when clients retry after disconnection.
    if (await data.PlayerSessions.AnyAsync(s => s.UserId == user.Id && s.Id == request.Id))
        return Results.Ok(new { status = "already_recorded" });
    data.PlayerSessions.Add(new SolarisPlayerSession {
        Id = request.Id, UserId = user.Id, Mode = request.Mode,
        Seconds = request.Seconds, StartedUtc = request.StartedUtc
    });
    await data.SaveChangesAsync();
    return Results.Ok(new { status = "recorded" });
}).RequireAuthorization();

app.Run();

public sealed record NewAccount(string Nickname, string Email, string Password);
public sealed record GameSession(Guid Id, string Mode, DateTimeOffset StartedUtc, int Seconds);
