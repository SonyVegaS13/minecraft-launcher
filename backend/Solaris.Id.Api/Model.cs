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
    public DateTimeOffset? LastActiveUtc { get; set; }
}

public sealed class SolarisNicknameReservation
{
    public string NormalizedNickname { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset ReservedUtc { get; set; }
}

public sealed class SolarisAuthSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string AccessTokenHash { get; set; } = "";
    public DateTimeOffset AccessExpiresUtc { get; set; }
    public DateTimeOffset RefreshExpiresUtc { get; set; }
    public string SecurityStamp { get; set; } = "";
    public bool MfaVerified { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class SolarisRefreshToken
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? ConsumedUtc { get; set; }
}

public sealed class SolarisPlayerSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public int Seconds { get; set; }
}

public sealed class SolarisLegacyImport
{
    public string UserId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public long VanillaSeconds { get; set; }
    public long ModdedSeconds { get; set; }
    public DateTimeOffset ImportedUtc { get; set; }
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
    public DbSet<SolarisNicknameReservation> NicknameReservations => Set<SolarisNicknameReservation>();
    public DbSet<SolarisAuthSession> AuthSessions => Set<SolarisAuthSession>();
    public DbSet<SolarisRefreshToken> AuthRefreshTokens => Set<SolarisRefreshToken>();
    public DbSet<SolarisPlayerSession> PlayerSessions => Set<SolarisPlayerSession>();
    public DbSet<SolarisSkin> Skins => Set<SolarisSkin>();
    public DbSet<SolarisLegacyImport> LegacyImports => Set<SolarisLegacyImport>();
    public DbSet<SolarisAuditEvent> AuditEvents => Set<SolarisAuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        GameAccess.ConfigureModel(builder);
        builder.Entity<SolarisUser>(entity =>
        {
            entity.Property(u => u.Nickname).HasMaxLength(16).IsRequired();
            entity.Property(u => u.NormalizedNickname).HasMaxLength(16).IsRequired();
            entity.HasIndex(u => u.NormalizedNickname).IsUnique();
            entity.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("EmailIndex");
            entity.HasCheckConstraint("CK_SolarisUser_NicknameNotEmpty",
                "char_length(\"Nickname\") BETWEEN 3 AND 16");
        });
        builder.Entity<SolarisNicknameReservation>(entity =>
        {
            entity.ToTable("NicknameReservations");
            entity.HasKey(x => x.NormalizedNickname);
            entity.Property(x => x.NormalizedNickname).HasMaxLength(16);
            entity.HasOne<SolarisUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SolarisAuthSession>(entity =>
        {
            entity.ToTable("AuthSessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AccessTokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SecurityStamp).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.AccessTokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.CreatedUtc });
            entity.HasOne<SolarisUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<SolarisRefreshToken>(entity =>
        {
            entity.ToTable("AuthRefreshTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne<SolarisAuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<SolarisPlayerSession>(entity =>
        {
            entity.HasKey(s => new { s.UserId, s.Id });
            entity.Property(s => s.Mode).HasMaxLength(12).IsRequired();
            entity.HasIndex(s => new { s.UserId, s.StartedUtc });
        });
        builder.Entity<SolarisLegacyImport>(entity =>
        {
            entity.HasKey(i => new { i.UserId, i.SourceId });
            entity.Property(i => i.SourceId).HasMaxLength(64).IsRequired();
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
