using Microsoft.EntityFrameworkCore;

namespace KeyWars.Data;

public sealed class PostgresKeyWarsDbContext(DbContextOptions<PostgresKeyWarsDbContext> options)
    : KeyWarsDbContext(options);
