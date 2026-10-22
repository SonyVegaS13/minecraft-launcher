using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Solaris.Id.Api;

/// <summary>
/// HTTPS out-of-band connection approval. A nickname, IP or offline Minecraft UUID
/// never authorizes a player. The trusted bridge must quarantine its actual connection
/// until the signed-in owner consumes a server/session-bound ticket in this API.
/// </summary>
public static class GameAccess
{
    public const int TicketLifetimeSeconds = 120;
    public const int ConnectionLifetimeSeconds = 90;
    public const int MaximumHeartbeatGapSeconds = 45;
    private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static readonly Regex NicknamePattern = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant);
    private static readonly Regex ServerIdPattern = new("^[a-z0-9][a-z0-9_-]{1,63}$", RegexOptions.CultureInvariant);

    public static void ConfigureModel(ModelBuilder builder)
    {
        builder.Entity<SolarisGameTicket>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ServerId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Nickname).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.ExpiresUtc);
            e.HasOne<SolarisUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SolarisAuthSession>().WithMany().HasForeignKey(x => x.AuthSessionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SolarisGameConnection>(e =>
        {
            e.HasKey(x => new { x.ServerId, x.ConnectionId });
            e.Property(x => x.ServerId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Code).HasMaxLength(10).IsRequired();
            e.Property(x => x.Nickname).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.ExpiresUtc);
            e.HasIndex(x => x.GameSessionId).IsUnique();
            e.HasOne<SolarisTrustedGameSession>().WithMany().HasForeignKey(x => x.GameSessionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SolarisTrustedGameSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ServerId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Mode).HasMaxLength(12).IsRequired();
            e.Property(x => x.Nickname).HasMaxLength(16).IsRequired();
            e.HasIndex(x => new { x.ServerId, x.ConnectionId }).IsUnique();
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("\"ClosedUtc\" IS NULL");
            e.HasIndex(x => new { x.UserId, x.StartedUtc });
            e.HasOne<SolarisUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SolarisAuthSession>().WithMany().HasForeignKey(x => x.AuthSessionId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static async Task<SolarisTrustedPlaytime> GetTrustedPlaytimeAsync(SolarisDbContext data, string userId)
    {
        var sessions = data.Set<SolarisTrustedGameSession>().AsNoTracking().Where(s => s.UserId == userId);
        long vanillaTicks = await sessions.Where(s => s.Mode == "vanilla").SumAsync(s => (long?)s.PlayedTicks) ?? 0;
        long moddedTicks = await sessions.Where(s => s.Mode == "modded").SumAsync(s => (long?)s.PlayedTicks) ?? 0;
        return new(vanillaTicks / TimeSpan.TicksPerSecond, moddedTicks / TimeSpan.TicksPerSecond);
    }

    public static IEndpointRouteBuilder MapSolarisGameAccess(this IEndpointRouteBuilder routes)
    {
        var servers = new ServerRegistry(routes.ServiceProvider.GetRequiredService<IConfiguration>());
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            bool user = context.Request.Path.StartsWithSegments("/api/v1/me");
            string partition = user
                ? "user:" + (context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous")
                : "server:" + context.Request.Headers["X-Solaris-Server"].ToString();
            return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = user ? 60 : 12000,
                Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            });
        });
        routes.ServiceProvider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(limiter.Dispose);
        async ValueTask<object?> RateLimit(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            using var lease = await limiter.AcquireAsync(context.HttpContext, cancellationToken: context.HttpContext.RequestAborted);
            if (!lease.IsAcquired) return Results.StatusCode(429);
            return await next(context);
        }

        routes.MapPost("/api/v1/me/game-tickets", async (HttpContext context, SolarisDbContext data, IssueTicket request) =>
            await IssueTicketAsync(context, data, request, servers)).RequireAuthorization().AddEndpointFilter(RateLimit);
        routes.MapPost("/api/v1/me/game-tickets/approve", async (HttpContext context, SolarisDbContext data, ApproveTicket request) =>
            await ApproveAsync(context, data, request, servers)).RequireAuthorization().AddEndpointFilter(RateLimit);
        var bridge = routes.MapGroup("/api/v1/server");
        bridge.AddEndpointFilter(async (context, next) =>
        {
            ServerSetting? server = servers.Authenticate(context.HttpContext);
            if (server is null) return Results.Unauthorized();
            context.HttpContext.Items[typeof(ServerSetting)] = server;
            return await RateLimit(context, next);
        });
        bridge.MapPost("/connections", CreateConnectionAsync);
        bridge.MapGet("/connections/{connectionId:guid}", PollConnectionAsync);
        bridge.MapDelete("/connections/{connectionId:guid}", CancelConnectionAsync);
        bridge.MapPost("/game-sessions/{gameSessionId:guid}/heartbeat", async (HttpContext context, SolarisDbContext data, Guid gameSessionId, GameEvent request) =>
            await RecordEventAsync(context, data, gameSessionId, request, false));
        bridge.MapPost("/game-sessions/{gameSessionId:guid}/close", async (HttpContext context, SolarisDbContext data, Guid gameSessionId, GameEvent request) =>
            await RecordEventAsync(context, data, gameSessionId, request, true));
        return routes;
    }

    private static async Task<IResult> IssueTicketAsync(HttpContext context, SolarisDbContext data, IssueTicket request, ServerRegistry servers)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ServerId)) return Bad("server_required");
        if (!servers.Contains(request.ServerId)) return Results.Problem("Selected Solaris server authentication is not configured.", statusCode: 503);
        var identity = await GetIdentityAsync(context, data);
        if (identity is null) return Results.Unauthorized();
        var (user, session) = identity.Value;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        data.Set<SolarisGameTicket>().Add(new SolarisGameTicket
        {
            Id = Guid.NewGuid(), TokenHash = Hash(token), UserId = user.Id, AuthSessionId = session.Id,
            ServerId = request.ServerId, Nickname = user.Nickname, CreatedUtc = now,
            ExpiresUtc = now.AddSeconds(TicketLifetimeSeconds)
        });
        await data.SaveChangesAsync();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { ticket = token, serverId = request.ServerId, expiresUtc = now.AddSeconds(TicketLifetimeSeconds) });
    }

    private static async Task<IResult> ApproveAsync(HttpContext context, SolarisDbContext data, ApproveTicket request, ServerRegistry servers)
    {
        if (request is null || request.Ticket is null || request.Ticket.Length != 43 || request.Code is null) return Bad("approval_invalid");
        string code = request.Code.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (code.Length != 10 || code.Any(c => !CodeAlphabet.Contains(c))) return Bad("approval_invalid");
        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();
        await using var transaction = await data.Database.BeginTransactionAsync();
        // Serialize decisions for this account, including competing tickets and both server audiences.
        var user = await data.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\"={userId} FOR UPDATE").SingleOrDefaultAsync();
        if (user is null) return Results.Unauthorized();
        await data.Entry(user).ReloadAsync();
        var identity = await GetIdentityAsync(context, data, user);
        if (identity is null) return Results.Unauthorized();
        var session = identity.Value.Session;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string hash = Hash(request.Ticket);
        var ticket = await data.Set<SolarisGameTicket>().SingleOrDefaultAsync(t => t.TokenHash == hash);
        var connection = await data.Set<SolarisGameConnection>().SingleOrDefaultAsync(c => c.Code == code);
        if (ticket is null || ticket.UserId != user.Id || ticket.AuthSessionId != session.Id ||
            ticket.ConsumedUtc is not null || ticket.ExpiresUtc <= now ||
            !string.Equals(ticket.Nickname, user.Nickname, StringComparison.Ordinal)) return Results.Conflict(new { error = "ticket_invalid_or_used" });
        if (connection is null || connection.ServerId != ticket.ServerId || connection.DeniedUtc is not null ||
            connection.GameSessionId is not null || connection.ExpiresUtc <= now ||
            !string.Equals(connection.Nickname, user.Nickname, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new { error = "connection_mismatch_or_expired" });
        // Lock that exact connection too: cancellation cannot race a successful approval.
        connection = await data.Set<SolarisGameConnection>().FromSqlInterpolated(
            $"SELECT * FROM \"SolarisGameConnection\" WHERE \"ServerId\"={connection.ServerId} AND \"ConnectionId\"={connection.ConnectionId} FOR UPDATE").SingleAsync();
        await data.Entry(connection).ReloadAsync();
        if (connection.DeniedUtc is not null || connection.GameSessionId is not null || connection.ExpiresUtc <= now)
            return Results.Conflict(new { error = "connection_mismatch_or_expired" });
        var previous = await data.Set<SolarisTrustedGameSession>().Where(s => s.UserId == user.Id && s.ClosedUtc == null).ToListAsync();
        foreach (var active in previous)
        {
            // A new approval never silently replaces a still-live authenticated connection.
            if (await SessionIsValidAsync(data, active.AuthSessionId, user, now) &&
                (now - active.LastHeartbeatUtc).TotalSeconds <= MaximumHeartbeatGapSeconds)
                return Results.Conflict(new { error = "account_already_in_game" });
            active.ClosedUtc = now;
        }
        // Flush stale closures before inserting the new row protected by the partial unique index.
        if (previous.Count > 0) await data.SaveChangesAsync();
        var game = new SolarisTrustedGameSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, AuthSessionId = session.Id, ServerId = ticket.ServerId,
            Mode = servers.ModeFor(ticket.ServerId), Nickname = user.Nickname, ConnectionId = connection.ConnectionId,
            StartedUtc = now, LastHeartbeatUtc = now
        };
        ticket.ConsumedUtc = now;
        connection.GameSessionId = game.Id;
        data.Set<SolarisTrustedGameSession>().Add(game);
        try
        {
            await data.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException e) when (IsUniqueConflict(e))
        {
            return Results.Conflict(new { error = "ticket_or_account_already_in_use" });
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { status = "approved" });
    }

    private static async Task<IResult> CreateConnectionAsync(HttpContext context, SolarisDbContext data, NewConnection request)
    {
        var server = CurrentServer(context);
        if (request is null || request.ConnectionId == Guid.Empty || !NicknamePattern.IsMatch(request.Nickname ?? "")) return Bad("connection_invalid");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var existing = await data.Set<SolarisGameConnection>().FindAsync(server.Id, request.ConnectionId);
        if (existing is not null)
        {
            if (!string.Equals(existing.Nickname, request.Nickname, StringComparison.Ordinal) || existing.GameSessionId is not null ||
                existing.DeniedUtc is not null || existing.ExpiresUtc <= now) return Results.Conflict(new { error = "connection_already_used" });
            return Results.Ok(new { code = existing.Code, expiresUtc = existing.ExpiresUtc });
        }
        // The server may not accumulate unbounded pending records during a malfunction.
        if (await data.Set<SolarisGameConnection>().CountAsync(c => c.ServerId == server.Id && c.DeniedUtc == null && c.GameSessionId == null && c.ExpiresUtc > now) >= 512)
            return Results.StatusCode(429);
        var connection = new SolarisGameConnection
        {
            ServerId = server.Id, ConnectionId = request.ConnectionId, Code = RandomCode(), Nickname = request.Nickname!,
            CreatedUtc = now, ExpiresUtc = now.AddSeconds(ConnectionLifetimeSeconds)
        };
        data.Set<SolarisGameConnection>().Add(connection);
        try { await data.SaveChangesAsync(); }
        catch (DbUpdateException e) when (IsUniqueConflict(e))
        {
            return Results.Conflict(new { error = "connection_conflict_retry" });
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { code = connection.Code, expiresUtc = connection.ExpiresUtc });
    }

    private static async Task<IResult> PollConnectionAsync(HttpContext context, SolarisDbContext data, Guid connectionId)
    {
        var server = CurrentServer(context);
        var connection = await data.Set<SolarisGameConnection>().SingleOrDefaultAsync(c => c.ServerId == server.Id && c.ConnectionId == connectionId);
        if (connection is null) return Results.NotFound();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (connection.DeniedUtc is not null) return Results.Ok(new { state = "denied" });
        if (connection.GameSessionId is null)
        {
            if (connection.ExpiresUtc <= now)
            {
                connection.DeniedUtc = now; await data.SaveChangesAsync();
                return Results.Ok(new { state = "denied" });
            }
            return Results.Ok(new { state = "pending" });
        }
        var game = await data.Set<SolarisTrustedGameSession>().FindAsync(connection.GameSessionId.Value);
        if (game is null || game.ClosedUtc is not null || game.ServerId != server.Id ||
            !await GameIsValidAsync(data, game, now) || (now - game.LastHeartbeatUtc).TotalSeconds > MaximumHeartbeatGapSeconds)
        {
            connection.DeniedUtc = now;
            if (game is not null && game.ClosedUtc is null) game.ClosedUtc = now;
            await data.SaveChangesAsync();
            return Results.Ok(new { state = "denied" });
        }
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { state = "approved", userId = game.UserId, nickname = game.Nickname, gameSessionId = game.Id });
    }

    private static async Task<IResult> CancelConnectionAsync(HttpContext context, SolarisDbContext data, Guid connectionId)
    {
        var server = CurrentServer(context);
        await using var transaction = await data.Database.BeginTransactionAsync();
        var connection = await data.Set<SolarisGameConnection>().FromSqlInterpolated(
            $"SELECT * FROM \"SolarisGameConnection\" WHERE \"ServerId\"={server.Id} AND \"ConnectionId\"={connectionId} FOR UPDATE").SingleOrDefaultAsync();
        if (connection is null) return Results.Ok(new { state = "closed" });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        connection.DeniedUtc ??= now;
        if (connection.GameSessionId is Guid id)
        {
            var game = await data.Set<SolarisTrustedGameSession>().FromSqlInterpolated(
                $"SELECT * FROM \"SolarisTrustedGameSession\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync();
            if (game is not null && game.ClosedUtc is null)
            {
                if (await GameIsValidAsync(data, game, now)) AccountElapsed(game, now);
                game.ClosedUtc = now;
            }
        }
        await data.SaveChangesAsync(); await transaction.CommitAsync();
        return Results.Ok(new { state = "closed" });
    }

    private static async Task<IResult> RecordEventAsync(HttpContext context, SolarisDbContext data, Guid gameSessionId, GameEvent request, bool close)
    {
        var server = CurrentServer(context);
        if (request is null || request.Sequence <= 0) return Bad("sequence_invalid");
        await using var transaction = await data.Database.BeginTransactionAsync();
        var game = await data.Set<SolarisTrustedGameSession>().FromSqlInterpolated(
            $"SELECT * FROM \"SolarisTrustedGameSession\" WHERE \"Id\"={gameSessionId} FOR UPDATE").SingleOrDefaultAsync();
        if (game is null || game.ServerId != server.Id) return Results.NotFound();
        if (game.ClosedUtc is not null)
            return close ? Results.Ok(new { state = "closed", status = "already_recorded" }) : Results.StatusCode(410);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!await GameIsValidAsync(data, game, now) || (now - game.LastHeartbeatUtc).TotalSeconds > MaximumHeartbeatGapSeconds)
        {
            game.ClosedUtc = now; await data.SaveChangesAsync(); await transaction.CommitAsync();
            return Results.StatusCode(410);
        }
        if (request.Sequence <= game.LastSequence)
            return Results.Ok(new { state = "approved", status = "already_recorded", gameSessionId = game.Id });
        AccountElapsed(game, now);
        game.LastSequence = request.Sequence;
        if (close) game.ClosedUtc = now;
        await data.SaveChangesAsync(); await transaction.CommitAsync();
        return Results.Ok(new { state = close ? "closed" : "approved", status = "recorded", gameSessionId = game.Id });
    }

    private static void AccountElapsed(SolarisTrustedGameSession game, DateTimeOffset now)
    {
        TimeSpan elapsed = now - game.LastHeartbeatUtc;
        if (elapsed > TimeSpan.Zero && elapsed.TotalSeconds <= MaximumHeartbeatGapSeconds)
            game.PlayedTicks = checked(game.PlayedTicks + elapsed.Ticks);
        game.LastHeartbeatUtc = now;
    }

    private static async Task<(SolarisUser User, SolarisAuthSession Session)?> GetIdentityAsync(HttpContext context, SolarisDbContext data, SolarisUser? existing = null)
    {
        string? userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || !Guid.TryParse(context.User.FindFirstValue("sid"), out Guid sessionId)) return null;
        var user = existing ?? await data.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.Id != userId || user.IsBanned || !user.EmailConfirmed) return null;
        var session = await data.Set<SolarisAuthSession>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId);
        if (session is null || session.UserId != user.Id || !SessionIsValid(session, user, DateTimeOffset.UtcNow)) return null;
        return (user, session);
    }

    private static async Task<bool> GameIsValidAsync(SolarisDbContext data, SolarisTrustedGameSession game, DateTimeOffset now)
    {
        var user = await data.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == game.UserId);
        return user is not null && !user.IsBanned && user.EmailConfirmed &&
            string.Equals(user.Nickname, game.Nickname, StringComparison.Ordinal) &&
            await SessionIsValidAsync(data, game.AuthSessionId, user, now);
    }

    private static async Task<bool> SessionIsValidAsync(SolarisDbContext data, Guid id, SolarisUser user, DateTimeOffset now)
    {
        var session = await data.Set<SolarisAuthSession>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
        return session is not null && session.UserId == user.Id && SessionIsValid(session, user, now);
    }

    private static bool SessionIsValid(SolarisAuthSession session, SolarisUser user, DateTimeOffset now) =>
        session.RevokedUtc is null && session.RefreshExpiresUtc > now &&
        string.Equals(session.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal);

    private static ServerSetting CurrentServer(HttpContext context) => (ServerSetting)context.Items[typeof(ServerSetting)]!;
    private static IResult Bad(string code) => Results.BadRequest(new { error = code });
    private static bool IsUniqueConflict(DbUpdateException e) => e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RandomCode() => string.Concat(Enumerable.Range(0, 10).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]));

    public sealed record IssueTicket(string? ServerId);
    public sealed record ApproveTicket(string? Ticket, string? Code);
    public sealed record NewConnection(Guid ConnectionId, string? Nickname);
    public sealed record GameEvent(long Sequence);

    private sealed record ServerSetting(string Id, string Mode, string Key);
    private sealed class ServerRegistry
    {
        private readonly Dictionary<string, ServerSetting> _servers = new(StringComparer.Ordinal);
        public ServerRegistry(IConfiguration configuration)
        {
            string json = configuration["SOLARIS_GAME_SERVERS"] ?? "[]";
            ServerSetting[] settings;
            try { settings = JsonSerializer.Deserialize<ServerSetting[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
            catch (JsonException) { throw new InvalidOperationException("SOLARIS_GAME_SERVERS must be a valid server credential array; values are not logged."); }
            if (settings.Length > 32) throw new InvalidOperationException("Too many Solaris game server configurations.");
            var keyHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in settings)
            {
                if (setting is null || string.IsNullOrEmpty(setting.Id) || !ServerIdPattern.IsMatch(setting.Id) || setting.Mode is not ("vanilla" or "modded") ||
                    string.IsNullOrWhiteSpace(setting.Key) || setting.Key.Length is < 32 or > 256 || !keyHashes.Add(Hash(setting.Key)) || !_servers.TryAdd(setting.Id, setting))
                    throw new InvalidOperationException("Invalid or duplicate Solaris server id, mode or credential; use unique random keys of at least32 characters.");
            }
        }
        public bool Contains(string id) => _servers.ContainsKey(id);
        public string ModeFor(string id) => _servers.TryGetValue(id, out var value) ? value.Mode : throw new InvalidOperationException("Game server configuration changed.");
        public ServerSetting? Authenticate(HttpContext context)
        {
            string id = context.Request.Headers["X-Solaris-Server"].ToString();
            string authorization = context.Request.Headers.Authorization.ToString();
            const string prefix = "SolarisServer ";
            if (id.Length > 64 || authorization.Length > 300 || !_servers.TryGetValue(id, out var setting) ||
                !authorization.StartsWith(prefix, StringComparison.Ordinal)) return null;
            byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[prefix.Length..]));
            byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(setting.Key));
            return CryptographicOperations.FixedTimeEquals(actual, expected) ? setting : null;
        }
    }
}
