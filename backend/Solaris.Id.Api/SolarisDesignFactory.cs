using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solaris.Id.Api;

/// <summary>
/// Only used by the EF CLI to generate typed migrations. No connection is made
/// until a migration is explicitly applied by the server administrator.
/// </summary>
public sealed class SolarisDesignFactory : IDesignTimeDbContextFactory<SolarisDbContext>
{
    public SolarisDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SolarisDbContext>();
        // Prefer the explicit test/production connection string when applying
        // migrations. The dummy connection is ONLY for offline model generation.
        string db = Environment.GetEnvironmentVariable("SOLARIS_ID_CONNECTION_STRING")
            ?? "Host=localhost;Database=solaris_schema_only;Username=unused";
        options.UseNpgsql(db);
        return new SolarisDbContext(options.Options);
    }
}
