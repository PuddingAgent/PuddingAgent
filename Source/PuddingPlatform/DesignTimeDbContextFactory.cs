using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PuddingPlatform.Data;

namespace PuddingPlatform;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PlatformDbContext>();
        optionsBuilder.UseSqlite("Data Source=pudding_platform.db");
        return new PlatformDbContext(optionsBuilder.Options);
    }
}
