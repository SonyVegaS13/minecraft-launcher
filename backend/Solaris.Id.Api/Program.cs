using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Solaris.Id.Api;

var builder = WebApplication.CreateBuilder(args);
// Confirmation links carry credentials in query strings. Never log full URLs,
// headers, bodies, SQL parameters, passwords or bearer/refresh token payloads.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.HttpLogging", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1048576);
string db = builder.Configuration["SOLARIS_ID_CONNECTION_STRING"] ?? throw new InvalidOperationException("SOLARIS_ID_CONNECTION_STRING is required. Never commit credentials.");
builder.Services.AddDbContext<SolarisDbContext>(options => options.UseNpgsql(db));
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    // Default known loopback proxies only. Never clear KnownNetworks/Proxies.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
var protection = builder.Services.AddDataProtection().SetApplicationName("Solaris.Id.Api");
string? keys = builder.Configuration["SOLARIS_ID_DATA_PROTECTION_KEYS"];
if (!string.IsNullOrWhiteSpace(keys)) protection.PersistKeysToFileSystem(new DirectoryInfo(keys));
else if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Persist protected Data Protection keys using SOLARIS_ID_DATA_PROTECTION_KEYS before production startup.");
builder.Services.AddIdentityCore<SolarisUser>(identity => {
    identity.User.RequireUniqueEmail = true;
    identity.SignIn.RequireConfirmedEmail = true;
    identity.Lockout.MaxFailedAccessAttempts = 5;
    identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    identity.Password.RequiredLength = 12;
    identity.Password.RequireDigit = true;
    identity.Password.RequireNonAlphanumeric = true;
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<SolarisDbContext>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(1));
builder.Services.AddScoped<IEmailSender<SolarisUser>, SolarisSmtpSender>();
builder.Services.AddAuthentication(SessionTokens.Scheme).AddScheme<AuthenticationSchemeOptions, SolarisSessionHandler>(SessionTokens.Scheme, _ => { });
builder.Services.AddAuthorizationBuilder().AddPolicy("SolarisAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("SolarisAdmin").RequireClaim("amr", "mfa"));
builder.Services.AddRateLimiter(limiter => {
    limiter.RejectionStatusCode = 429;
    int permits = builder.Environment.IsDevelopment() && int.TryParse(builder.Configuration["SOLARIS_AUTH_RATE_LIMIT"], out int configured) ? Math.Clamp(configured, 1, 10000) : 10;
    limiter.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
        PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));
});
var app = builder.Build();
int bootstrap = Array.IndexOf(args, "--bootstrap-admin");
if (bootstrap >= 0)
{
    if (bootstrap + 1 >= args.Length) throw new InvalidOperationException("--bootstrap-admin requires the explicit user UUID.");
    await ControlEndpoints.BootstrapAdminAsync(app.Services, args[bootstrap + 1]);
    return;
}
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) => {
    bool allowLoopback = app.Environment.IsDevelopment() && app.Configuration["SOLARIS_ID_ALLOW_HTTP_LOOPBACK"] == "true" &&
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
    if (!context.Request.IsHttps && !allowLoopback) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "https_required" }); return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", async (SolarisDbContext data) => await data.Database.CanConnectAsync() ? Results.Ok(new { service = "solaris-id", status = "ok" }) : Results.Problem("Database unavailable", statusCode: 503));
app.MapSolarisIdentity();
app.MapSolarisProfiles();
app.MapSolarisControl();
app.MapSolarisGameAccess();
app.Run();
public partial class Program { }
