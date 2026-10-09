using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Solaris.Id.Api;

public sealed class SolarisUser : IdentityUser
{
    public string Nickname { get; set; } = "";
    public string NormalizedNickname { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsBanned { get; set; }
}

public sealed class SolarisPlayerSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public int Seconds { get; set; }
}

public sealed class SolarisSkin
{
    public string UserId { get; set; } = "";
    public byte[] Png { get; set; } = Array.Empty<byte>();
    public string Sha256 { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class SolarisAuditEvent
{
    public long Id { get; set; }
    public string ActorId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset Utc { get; set; }
}

public sealed class SolarisDbContext(DbContextOptions<SolarisDbContext> options)
    : IdentityDbContext<SolarisUser>(options)
{
    public DbSet<SolarisPlayerSession> PlayerSessions => Set<SolarisPlayerSession>();
    public DbSet<SolarisSkin> Skins => Set<SolarisSkin>();
    public DbSet<SolarisAuditEvent> AuditEvents => Set<SolarisAuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SolarisUser>(entity =>
        {
            entity.Property(u => u.Nickname).HasMaxLength(16).IsRequired();
            entity.Property(u => u.NormalizedNickname).HasMaxLength(16).IsRequired();
            entity.HasIndex(u => u.NormalizedNickname).IsUnique();
            entity.HasCheckConstraint("CK_SolarisUser_NicknameNotEmpty",
                "char_length(\"Nickname\") BETWEEN 3 AND 16");
        });
        builder.Entity<SolarisPlayerSession>(entity =>
        {
            entity.HasKey(s => new { s.UserId, s.Id });
            entity.Property(s => s.Mode).HasMaxLength(12).IsRequired();
            entity.HasIndex(s => new { s.UserId, s.StartedUtc });
        });
        builder.Entity<SolarisSkin>(entity =>
        {
            entity.HasKey(s => s.UserId);
            entity.Property(s => s.Sha256).HasMaxLength(64).IsRequired();
        });
        builder.Entity<SolarisAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActorId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.TargetId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Operation).HasMaxLength(48).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => e.Utc);
        });
    }
}
