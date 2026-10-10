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
        options.UseNpgsql("Host=localhost;Database=solaris_schema_only;Username=unused");
        return new SolarisDbContext(options.Options);
    }
}
