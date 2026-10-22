namespace Solaris.Id.Api;

/// <summary>A short-lived one-use launcher proof. Only its SHA-256 is stored.</summary>
public sealed class SolarisGameTicket
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";
    public string UserId { get; set; } = "";
    public Guid AuthSessionId { get; set; }
    public string ServerId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? ConsumedUtc { get; set; }
}

/// <summary>The code locates a quarantined connection; it is not an authentication credential.</summary>
public sealed class SolarisGameConnection
{
    public string ServerId { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public string Code { get; set; } = "";
    public string Nickname { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? DeniedUtc { get; set; }
    public Guid? GameSessionId { get; set; }
}

/// <summary>Time is derived solely from API-clock intervals between authenticated server events.</summary>
public sealed class SolarisTrustedGameSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public Guid AuthSessionId { get; set; }
    public string ServerId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Nickname { get; set; } = "";
    public Guid ConnectionId { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset LastHeartbeatUtc { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }
    public long LastSequence { get; set; }
    public long PlayedTicks { get; set; }
}

public sealed record SolarisTrustedPlaytime(long VanillaSeconds, long ModdedSeconds)
{
    public long TotalSeconds => VanillaSeconds + ModdedSeconds;
    public string Source => "trusted-servers";
}
