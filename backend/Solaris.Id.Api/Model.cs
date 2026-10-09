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

public sealed class SolarisDbContext(DbContextOptions<SolarisDbContext> options)
    : IdentityDbContext<SolarisUser>(options)
{
    public DbSet<SolarisPlayerSession> PlayerSessions => Set<SolarisPlayerSession>();

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
    }
}
