using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace KeyWars.IntegrationTests;

public sealed class DatabaseReadModelContractTests
{
    [Fact]
    public async Task CurrentMigrationAppliesWithScaleIndexesAndRematchConstraint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        AssertIndex<TypingAttempt>(db, nameof(TypingAttempt.UserProfileId), nameof(TypingAttempt.Phase), nameof(TypingAttempt.Completed), nameof(TypingAttempt.CreatedAt), nameof(TypingAttempt.Id));
        AssertIndex<RewardLedgerEntry>(db, nameof(RewardLedgerEntry.UserProfileId), nameof(RewardLedgerEntry.AwardedAt));
        AssertIndex<Achievement>(db, nameof(Achievement.UserProfileId), nameof(Achievement.UnlockedAt));
        AssertIndex<GamificationEvent>(db, nameof(GamificationEvent.UserProfileId), nameof(GamificationEvent.CreatedAt), nameof(GamificationEvent.Id));

        var challengeType = db.Model.FindEntityType(typeof(Challenge))!;
        var rematchIndex = challengeType.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(Challenge.RematchOfChallengeId)]));
        Assert.True(rematchIndex.IsUnique);
        var rematchForeignKey = challengeType.GetForeignKeys().Single(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([nameof(Challenge.RematchOfChallengeId)]));
        Assert.Equal(DeleteBehavior.Restrict, rematchForeignKey.DeleteBehavior);
    }

    [Fact]
    public async Task ModerationAuditEntriesAreAppendOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var entry = new ContentModerationAuditEntry
        {
            ActorProfileId = Guid.CreateVersion7(),
            ActorDisplayName = "Admin Test",
            TargetType = ContentModerationTargetType.TrainingText,
            TargetId = Guid.CreateVersion7(),
            TargetOwnerProfileId = Guid.CreateVersion7(),
            TargetTitle = "Prüftext",
            Action = ContentModerationAction.Quarantine,
            Reason = "Nachvollziehbarer Testgrund",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ContentModerationAuditEntries.Add(entry);
        await db.SaveChangesAsync();

        entry.Reason = "Nachträgliche Manipulation";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("unveränderlich", exception.Message);
    }

    [Fact]
    public void PostgreSqlUsesAnIndependentProviderNativeInitialMigration()
    {
        var options = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
            .UseNpgsql("Host=localhost;Database=keywars_contract;Username=keywars")
            .Options;
        using var db = new PostgresKeyWarsDbContext(options);

        var migrations = db.Database.GetMigrations().ToArray();
        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Single(migrations);
        Assert.EndsWith("_InitialPostgresV05", migrations[0], StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"UserProfiles\"", script, StringComparison.Ordinal);
        Assert.Contains("timestamp with time zone", script, StringComparison.Ordinal);
        Assert.Contains("uuid", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlite_master", script, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertIndex<TEntity>(KeyWarsDbContext db, params string[] propertyNames)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity))!;
        Assert.Contains(entityType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }
}
