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
        if (db.Database.IsSqlite())
        {
            await BaselineExistingEnsureCreatedDatabaseAsync(db, cancellationToken);
        }

        await db.Database.MigrateAsync(cancellationToken);
        if (db.Database.IsSqlite())
        {
            await AbortOrphanedAttemptsAsync(db, cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        }

        await SeedStandardTextsAsync(db, cancellationToken);
        logger.LogInformation("KeyWars-Datenbank ist bereit ({Environment}).", environment.EnvironmentName);
    }

    private async Task AbortOrphanedAttemptsAsync(KeyWarsDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orphanedAttempts = await db.TypingAttempts
            .Where(attempt =>
                attempt.FinishedAt == null &&
                (attempt.Phase == AttemptPhase.Prepared || attempt.Phase == AttemptPhase.Started))
            .ToListAsync(cancellationToken);
        var recoverableBindings = await db.ChallengeAttemptBindings
            .Where(binding =>
                !binding.Consumed &&
                db.TypingAttempts.Any(attempt =>
                    attempt.Id == binding.TypingAttemptId &&
                    attempt.FinishedAt == null &&
                    (attempt.Phase == AttemptPhase.Prepared ||
                     attempt.Phase == AttemptPhase.Started ||
                     attempt.Phase == AttemptPhase.Aborted)))
            .ToListAsync(cancellationToken);

        foreach (var attempt in orphanedAttempts)
        {
            attempt.Phase = AttemptPhase.Aborted;
        }

        if (recoverableBindings.Count > 0)
        {
            var challengeIds = recoverableBindings.Select(binding => binding.ChallengeId).Distinct().ToArray();
            var profileIds = recoverableBindings.Select(binding => binding.UserProfileId).Distinct().ToArray();
            var bindingParticipants = recoverableBindings
                .Select(binding => (binding.ChallengeId, binding.UserProfileId))
                .ToHashSet();
            var participants = await db.ChallengeParticipants
                .Where(participant =>
                    challengeIds.Contains(participant.ChallengeId) &&
                    profileIds.Contains(participant.UserProfileId))
                .ToListAsync(cancellationToken);
            foreach (var participant in participants.Where(participant =>
                         participant.Status == ParticipantStatus.Running &&
                         bindingParticipants.Contains((participant.ChallengeId, participant.UserProfileId))))
            {
                participant.Status = ParticipantStatus.Joined;
            }

            db.ChallengeAttemptBindings.RemoveRange(recoverableBindings);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (orphanedAttempts.Count > 0)
        {
            logger.LogInformation("{AttemptCount} verwaiste Tippversuche wurden beim Start neutral abgebrochen.", orphanedAttempts.Count);
        }

        if (recoverableBindings.Count > 0)
        {
            logger.LogInformation("{BindingCount} abgebrochene Challenge-Versuchsbindungen wurden für einen Neustart freigegeben.", recoverableBindings.Count);
        }
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
