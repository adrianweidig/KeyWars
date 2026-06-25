using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Data;

public sealed class DatabaseInitializer(
    IServiceProvider services,
    ILogger<DatabaseInitializer> logger,
    IHostEnvironment environment)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        await BaselineExistingEnsureCreatedDatabaseAsync(db, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await SeedStandardTextsAsync(db, cancellationToken);
        logger.LogInformation("KeyWars-Datenbank ist bereit ({Environment}).", environment.EnvironmentName);
    }

    private async Task BaselineExistingEnsureCreatedDatabaseAsync(KeyWarsDbContext db, CancellationToken cancellationToken)
    {
        var userTableCount = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'")
            .SingleAsync(cancellationToken);
        if (userTableCount == 0)
        {
            return;
        }

        var historyTableCount = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'")
            .SingleAsync(cancellationToken);
        if (historyTableCount > 0)
        {
            return;
        }

        var initialMigration = db.Database.GetMigrations().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(initialMigration))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);",
            cancellationToken);
        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({initialMigration}, {productVersion});",
            cancellationToken);
        logger.LogWarning("Bestehende SQLite-Datenbank ohne EF-Migrationshistorie wurde als {MigrationId} baseline-markiert.", initialMigration);
    }

    private static async Task SeedStandardTextsAsync(KeyWarsDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var standardText in GermanWordBank.StandardTexts)
        {
            var normalized = TypingEngine.NormalizeText(standardText.Body);
            var characterCount = TypingEngine.SplitGraphemes(normalized).Count;
            var existing = await db.TrainingTexts.SingleOrDefaultAsync(text => text.SourceKey == standardText.Key, cancellationToken);
            if (existing is null)
            {
                db.TrainingTexts.Add(new TrainingText
                {
                    Title = standardText.Title,
                    SourceKey = standardText.Key,
                    Body = normalized,
                    CharacterCount = characterCount,
                    IsStandard = true,
                    RatingEligible = true,
                    Visibility = TrainingTextVisibility.Organization,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                continue;
            }

            if (existing.Title == standardText.Title &&
                existing.Body == normalized &&
                existing.CharacterCount == characterCount &&
                existing.OwnerProfileId is null &&
                existing.IsStandard &&
                existing.RatingEligible &&
                existing.Visibility == TrainingTextVisibility.Organization)
            {
                continue;
            }

            existing.OwnerProfileId = null;
            existing.Title = standardText.Title;
            existing.Body = normalized;
            existing.CharacterCount = characterCount;
            existing.IsStandard = true;
            existing.RatingEligible = true;
            existing.Visibility = TrainingTextVisibility.Organization;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
