using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KeyWars.Data;

public sealed class KeyWarsDbContextFactory : IDesignTimeDbContextFactory<KeyWarsDbContext>
{
    public KeyWarsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new KeyWarsDbContext(options);
    }
}

public sealed class PostgresKeyWarsDbContextFactory : IDesignTimeDbContextFactory<PostgresKeyWarsDbContext>
{
    public PostgresKeyWarsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
            .UseNpgsql("Host=localhost;Database=keywars_design;Username=keywars")
            .Options;
        return new PostgresKeyWarsDbContext(options);
    }
}
