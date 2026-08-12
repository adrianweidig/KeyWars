using System.Globalization;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Data;

public sealed class DataRetentionService(
    KeyWarsDbContext db,
    BackupService backups,
    IAttemptSessionStateStore attemptSessions,
    IOptions<RetentionOptions> configuredOptions,
    TimeProvider timeProvider,
    ILogger<DataRetentionService> logger)
{
    private const string AttemptRewardSource = "attempt";

    private static readonly string[] ProtectedDataSets =
    [
        nameof(KeyWarsDbContext.RewardLedgerEntries),
        "abgeschlossene TypingAttempts",
        nameof(KeyWarsDbContext.LiveRoomSummaries),
        nameof(KeyWarsDbContext.LiveRoomParticipantSummaries),
        nameof(KeyWarsDbContext.ChallengeRoundResults),
        nameof(KeyWarsDbContext.Missions),
        nameof(KeyWarsDbContext.Achievements)
    ];

    private readonly RetentionOptions options = configuredOptions.Value;

    public async Task<DataRetentionReport> RunAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var provider = ResolveProvider();
        var startedAt = timeProvider.GetUtcNow();
        var staleAttemptCutoff = startedAt.AddHours(-options.StaleAttemptHours);
        var abandonedAttemptCutoff = startedAt.AddDays(-options.AbandonedAttemptRetentionDays);
        var gamificationEventCutoff = startedAt.AddDays(-options.SeenGamificationEventRetentionDays);
        var backupCutoff = startedAt.AddDays(-options.BackupRetentionDays);

        var staleAttempts = await ExpireStaleAttemptsAsync(
            provider,
            startedAt,
            staleAttemptCutoff,
            dryRun,
            cancellationToken);
        var expiredChallenges = await ExpireChallengesAsync(provider, startedAt, dryRun, cancellationToken);
        var abandonedAttempts = await DeleteAbandonedAttemptsAsync(
            provider,
            abandonedAttemptCutoff,
            dryRun,
            cancellationToken);
        var seenEvents = await DeleteSeenGamificationEventsAsync(
            provider,
            gamificationEventCutoff,
            dryRun,
            cancellationToken);
        var backupPairs = provider == RetentionDatabaseProvider.Sqlite
            ? await backups.ApplyRetentionAsync(
                backupCutoff,
                options.MinimumBackupPairs,
                dryRun,
                cancellationToken)
            : BackupRetentionResult.NotApplicable(
                dryRun,
                backupCutoff,
                options.MinimumBackupPairs,
                "Lokale SQLite-Backup-Paare sind im PostgreSQL-Modus nicht anwendbar.");
        var completedAt = timeProvider.GetUtcNow();

        var report = new DataRetentionReport(
            dryRun,
            startedAt,
            completedAt,
            staleAttempts,
            expiredChallenges,
            abandonedAttempts,
            seenEvents,
            backupPairs,
            ProtectedDataSets);
        logger.LogInformation(
            "Retention {Mode} für {Provider} abgeschlossen: Attempts abgelaufen={ExpiredAttempts}, Challenges abgelaufen={ExpiredChallenges}, Attempts gelöscht={DeletedAttempts}, Events gelöscht={DeletedEvents}, Backup-Paare gelöscht={DeletedBackups}.",
            dryRun ? "Dry-run" : "Apply",
            provider,
            staleAttempts.Affected,
            expiredChallenges.Affected,
            abandonedAttempts.Affected,
            seenEvents.Affected,
            backupPairs.DeletedPairs);
        return report;
    }

    private async Task<RetentionStepResult> ExpireStaleAttemptsAsync(
        RetentionDatabaseProvider provider,
        DateTimeOffset now,
        DateTimeOffset cutoff,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var candidates = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteStaleAttemptsAsync(cutoff, cancellationToken)
            : await PostgreSqlStaleAttempts(cutoff).LongCountAsync(cancellationToken);
        if (dryRun || candidates == 0)
        {
            return Result("stale-attempts", cutoff, candidates, 0, candidates, false);
        }

        long affected = 0;
        var batches = 0;
        while (batches < options.MaxBatchesPerRun)
        {
            var ids = provider == RetentionDatabaseProvider.Sqlite
                ? await SelectSqliteStaleAttemptIdsAsync(cutoff, cancellationToken)
                : await PostgreSqlStaleAttempts(cutoff)
                    .OrderBy(attempt => attempt.PreparedAt)
                    .ThenBy(attempt => attempt.Id)
                    .Select(attempt => attempt.Id)
                    .Take(options.BatchSize)
                    .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            var lifecycleLocks = await AcquireLifecycleLocksAsync(ids, cancellationToken);
            try
            {
                var confirmedIds = (await db.TypingAttempts
                        .AsNoTracking()
                        .Where(attempt =>
                            ids.Contains(attempt.Id) &&
                            attempt.FinishedAt == null &&
                            (attempt.Phase == AttemptPhase.Prepared || attempt.Phase == AttemptPhase.Started))
                        .Select(attempt => new
                        {
                            attempt.Id,
                            attempt.Phase,
                            attempt.PreparedAt,
                            attempt.StartedAt
                        })
                        .ToListAsync(cancellationToken))
                    .Where(attempt =>
                        (attempt.Phase == AttemptPhase.Prepared ? attempt.PreparedAt : attempt.StartedAt) < cutoff)
                    .Select(attempt => attempt.Id)
                    .ToArray();

                if (confirmedIds.Length > 0)
                {
                    var update = db.TypingAttempts.Where(attempt =>
                        confirmedIds.Contains(attempt.Id) &&
                        attempt.FinishedAt == null &&
                        (attempt.Phase == AttemptPhase.Prepared || attempt.Phase == AttemptPhase.Started));
                    if (provider == RetentionDatabaseProvider.PostgreSql)
                    {
                        update = update.Where(attempt =>
                            (attempt.Phase == AttemptPhase.Prepared && attempt.PreparedAt < cutoff) ||
                            (attempt.Phase == AttemptPhase.Started && attempt.StartedAt < cutoff));
                    }

                    affected += await update.ExecuteUpdateAsync(
                        setters => setters.SetProperty(attempt => attempt.Phase, AttemptPhase.Expired),
                        cancellationToken);

                    var expiredIds = await db.TypingAttempts
                        .AsNoTracking()
                        .Where(attempt =>
                            confirmedIds.Contains(attempt.Id) &&
                            attempt.Phase == AttemptPhase.Expired)
                        .Select(attempt => attempt.Id)
                        .ToArrayAsync(cancellationToken);
                    foreach (var id in expiredIds)
                    {
                        await attemptSessions.TryRemoveExpiredAsync(
                            id,
                            now,
                            TimeSpan.FromHours(options.StaleAttemptHours),
                            cancellationToken);
                    }
                }
            }
            finally
            {
                await DisposeLifecycleLocksAsync(lifecycleLocks);
            }

            batches++;
        }

        var remaining = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteStaleAttemptsAsync(cutoff, cancellationToken)
            : await PostgreSqlStaleAttempts(cutoff).LongCountAsync(cancellationToken);
        return Result(
            "stale-attempts",
            cutoff,
            candidates,
            affected,
            remaining,
            batches >= options.MaxBatchesPerRun && remaining > 0);
    }

    private async Task<RetentionStepResult> ExpireChallengesAsync(
        RetentionDatabaseProvider provider,
        DateTimeOffset now,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var candidates = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteExpiredChallengesAsync(now, cancellationToken)
            : await PostgreSqlExpiredChallenges(now).LongCountAsync(cancellationToken);
        if (dryRun || candidates == 0)
        {
            return Result("expired-challenges", now, candidates, 0, candidates, false);
        }

        long affected = 0;
        var batches = 0;
        while (batches < options.MaxBatchesPerRun)
        {
            int changed;
            if (provider == RetentionDatabaseProvider.Sqlite)
            {
                var cutoffValue = FormatSqliteDateTimeOffset(now);
                changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE Challenges
                    SET Status = {ChallengeStatus.Expired.ToString()},
                        FinishedAt = COALESCE(FinishedAt, {now})
                    WHERE Id IN (
                        SELECT Id
                        FROM Challenges
                        WHERE Status IN ({ChallengeStatus.Open.ToString()}, {ChallengeStatus.Running.ToString()})
                          AND substr(ExpiresAt, 1, 19) < {cutoffValue}
                        ORDER BY substr(ExpiresAt, 1, 19), Id
                        LIMIT {options.BatchSize}
                    )
                      AND Status IN ({ChallengeStatus.Open.ToString()}, {ChallengeStatus.Running.ToString()})
                      AND substr(ExpiresAt, 1, 19) < {cutoffValue};
                    """, cancellationToken);
            }
            else
            {
                var ids = await PostgreSqlExpiredChallenges(now)
                    .OrderBy(challenge => challenge.ExpiresAt)
                    .ThenBy(challenge => challenge.Id)
                    .Select(challenge => challenge.Id)
                    .Take(options.BatchSize)
                    .ToListAsync(cancellationToken);
                if (ids.Count == 0)
                {
                    break;
                }

                changed = await db.Challenges
                    .Where(challenge =>
                        ids.Contains(challenge.Id) &&
                        (challenge.Status == ChallengeStatus.Open || challenge.Status == ChallengeStatus.Running) &&
                        challenge.ExpiresAt <= now)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(challenge => challenge.Status, ChallengeStatus.Expired)
                            .SetProperty(challenge => challenge.FinishedAt, challenge => challenge.FinishedAt ?? now),
                        cancellationToken);
            }

            affected += changed;
            batches++;
        }

        var remaining = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteExpiredChallengesAsync(now, cancellationToken)
            : await PostgreSqlExpiredChallenges(now).LongCountAsync(cancellationToken);
        return Result(
            "expired-challenges",
            now,
            candidates,
            affected,
            remaining,
            batches >= options.MaxBatchesPerRun && remaining > 0);
    }

    private async Task<RetentionStepResult> DeleteAbandonedAttemptsAsync(
        RetentionDatabaseProvider provider,
        DateTimeOffset cutoff,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var candidates = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteAbandonedAttemptsAsync(cutoff, cancellationToken)
            : await CountPostgreSqlAbandonedAttemptsAsync(cutoff, cancellationToken);
        if (dryRun || candidates == 0)
        {
            return Result("abandoned-attempts", cutoff, candidates, 0, candidates, false);
        }

        long affected = 0;
        var batches = 0;
        while (batches < options.MaxBatchesPerRun)
        {
            var ids = provider == RetentionDatabaseProvider.Sqlite
                ? await SelectSqliteAbandonedAttemptIdsAsync(cutoff, cancellationToken)
                : await SelectPostgreSqlAbandonedAttemptIdsAsync(cutoff, cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            var lifecycleLocks = await AcquireLifecycleLocksAsync(ids, cancellationToken);
            try
            {
                var candidatesUnderLock = (await db.TypingAttempts
                        .AsNoTracking()
                        .Where(attempt =>
                            ids.Contains(attempt.Id) &&
                            !attempt.Completed &&
                            (attempt.Phase == AttemptPhase.Expired || attempt.Phase == AttemptPhase.Aborted))
                        .Select(attempt => new
                        {
                            attempt.Id,
                            attempt.UserProfileId,
                            attempt.PreparedAt
                        })
                        .ToListAsync(cancellationToken))
                    .Where(attempt => attempt.PreparedAt < cutoff)
                    .ToArray();
                var candidateIds = candidatesUnderLock.Select(attempt => attempt.Id).ToArray();
                var boundAttemptIds = await db.ChallengeAttemptBindings
                    .AsNoTracking()
                    .Where(binding => candidateIds.Contains(binding.TypingAttemptId))
                    .Select(binding => binding.TypingAttemptId)
                    .ToArrayAsync(cancellationToken);
                var normalizedSourceIds = candidatesUnderLock
                    .Select(attempt => attempt.Id.ToString("N").ToLowerInvariant())
                    .ToArray();
                var protectedLedgerEntries = await db.RewardLedgerEntries
                    .AsNoTracking()
                    .Where(entry =>
                        entry.Source == AttemptRewardSource &&
                        normalizedSourceIds.Contains(entry.SourceId.ToLower()))
                    .Select(entry => new { entry.UserProfileId, entry.SourceId })
                    .ToArrayAsync(cancellationToken);
                var bound = boundAttemptIds.ToHashSet();
                var rewarded = protectedLedgerEntries
                    .Select(entry => (entry.UserProfileId, entry.SourceId.ToLowerInvariant()))
                    .ToHashSet();
                var confirmedIds = candidatesUnderLock
                    .Where(attempt =>
                        !bound.Contains(attempt.Id) &&
                        !rewarded.Contains((attempt.UserProfileId, attempt.Id.ToString("N").ToLowerInvariant())))
                    .Select(attempt => attempt.Id)
                    .ToArray();

                if (confirmedIds.Length > 0)
                {
                    var delete = db.TypingAttempts.Where(attempt =>
                        confirmedIds.Contains(attempt.Id) &&
                        !attempt.Completed &&
                        (attempt.Phase == AttemptPhase.Expired || attempt.Phase == AttemptPhase.Aborted) &&
                        !db.ChallengeAttemptBindings.Any(binding => binding.TypingAttemptId == attempt.Id));
                    if (provider == RetentionDatabaseProvider.PostgreSql)
                    {
                        delete = delete.Where(attempt => attempt.PreparedAt < cutoff);
                    }

                    affected += await delete.ExecuteDeleteAsync(cancellationToken);
                }
            }
            finally
            {
                await DisposeLifecycleLocksAsync(lifecycleLocks);
            }

            batches++;
        }

        var remaining = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteAbandonedAttemptsAsync(cutoff, cancellationToken)
            : await CountPostgreSqlAbandonedAttemptsAsync(cutoff, cancellationToken);
        return Result(
            "abandoned-attempts",
            cutoff,
            candidates,
            affected,
            remaining,
            batches >= options.MaxBatchesPerRun && remaining > 0);
    }

    private async Task<RetentionStepResult> DeleteSeenGamificationEventsAsync(
        RetentionDatabaseProvider provider,
        DateTimeOffset cutoff,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var candidates = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteSeenGamificationEventsAsync(cutoff, cancellationToken)
            : await PostgreSqlSeenGamificationEvents(cutoff).LongCountAsync(cancellationToken);
        if (dryRun || candidates == 0)
        {
            return Result("seen-gamification-events", cutoff, candidates, 0, candidates, false);
        }

        long affected = 0;
        var batches = 0;
        while (batches < options.MaxBatchesPerRun)
        {
            int changed;
            if (provider == RetentionDatabaseProvider.Sqlite)
            {
                var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
                changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM GamificationEvents
                    WHERE Id IN (
                        SELECT Id
                        FROM GamificationEvents
                        WHERE SeenAt IS NOT NULL
                          AND substr(CreatedAt, 1, 19) < {cutoffValue}
                        ORDER BY substr(CreatedAt, 1, 19), Id
                        LIMIT {options.BatchSize}
                    );
                    """, cancellationToken);
            }
            else
            {
                var ids = await PostgreSqlSeenGamificationEvents(cutoff)
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.Id)
                    .Select(item => item.Id)
                    .Take(options.BatchSize)
                    .ToListAsync(cancellationToken);
                if (ids.Count == 0)
                {
                    break;
                }

                changed = await db.GamificationEvents
                    .Where(item =>
                        ids.Contains(item.Id) &&
                        item.SeenAt != null &&
                        item.CreatedAt < cutoff)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            affected += changed;
            batches++;
        }

        var remaining = provider == RetentionDatabaseProvider.Sqlite
            ? await CountSqliteSeenGamificationEventsAsync(cutoff, cancellationToken)
            : await PostgreSqlSeenGamificationEvents(cutoff).LongCountAsync(cancellationToken);
        return Result(
            "seen-gamification-events",
            cutoff,
            candidates,
            affected,
            remaining,
            batches >= options.MaxBatchesPerRun && remaining > 0);
    }

    private IQueryable<TypingAttempt> PostgreSqlStaleAttempts(DateTimeOffset cutoff) =>
        db.TypingAttempts.Where(attempt =>
            attempt.FinishedAt == null &&
            ((attempt.Phase == AttemptPhase.Prepared && attempt.PreparedAt < cutoff) ||
             (attempt.Phase == AttemptPhase.Started && attempt.StartedAt < cutoff)));

    private IQueryable<Challenge> PostgreSqlExpiredChallenges(DateTimeOffset cutoff) =>
        db.Challenges.Where(challenge =>
            (challenge.Status == ChallengeStatus.Open || challenge.Status == ChallengeStatus.Running) &&
            challenge.ExpiresAt <= cutoff);

    private IQueryable<GamificationEvent> PostgreSqlSeenGamificationEvents(DateTimeOffset cutoff) =>
        db.GamificationEvents.Where(item => item.SeenAt != null && item.CreatedAt < cutoff);

    private Task<long> CountSqliteStaleAttemptsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
        return CountAsync($"""
            SELECT COUNT(*) AS "Value"
            FROM TypingAttempts
            WHERE FinishedAt IS NULL
              AND (
                  (Phase = {AttemptPhase.Prepared.ToString()} AND substr(PreparedAt, 1, 19) < {cutoffValue})
                  OR
                  (Phase = {AttemptPhase.Started.ToString()} AND substr(StartedAt, 1, 19) < {cutoffValue})
              )
            """, cancellationToken);
    }

    private async Task<List<Guid>> SelectSqliteStaleAttemptIdsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
        return await db.Database.SqlQuery<Guid>($"""
                SELECT Id AS "Value"
                FROM TypingAttempts
                WHERE FinishedAt IS NULL
                  AND (
                      (Phase = {AttemptPhase.Prepared.ToString()} AND substr(PreparedAt, 1, 19) < {cutoffValue})
                      OR
                      (Phase = {AttemptPhase.Started.ToString()} AND substr(StartedAt, 1, 19) < {cutoffValue})
                  )
                ORDER BY substr(PreparedAt, 1, 19), Id
                LIMIT {options.BatchSize}
                """)
            .ToListAsync(cancellationToken);
    }

    private Task<long> CountSqliteExpiredChallengesAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
        return CountAsync($"""
            SELECT COUNT(*) AS "Value"
            FROM Challenges
            WHERE Status IN ({ChallengeStatus.Open.ToString()}, {ChallengeStatus.Running.ToString()})
              AND substr(ExpiresAt, 1, 19) < {cutoffValue}
            """, cancellationToken);
    }

    private Task<long> CountSqliteAbandonedAttemptsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
        return CountAsync($"""
            SELECT COUNT(*) AS "Value"
            FROM TypingAttempts AS attempt
            WHERE attempt.Completed = 0
              AND attempt.Phase IN ({AttemptPhase.Expired.ToString()}, {AttemptPhase.Aborted.ToString()})
              AND substr(attempt.PreparedAt, 1, 19) < {cutoffValue}
              AND NOT EXISTS (
                  SELECT 1
                  FROM ChallengeAttemptBindings AS binding
                  WHERE binding.TypingAttemptId = attempt.Id
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM RewardLedgerEntries AS ledger
                  WHERE ledger.UserProfileId = attempt.UserProfileId
                    AND ledger.Source = {AttemptRewardSource}
                    AND lower(ledger.SourceId) = lower(replace(attempt.Id, '-', ''))
              )
            """, cancellationToken);
    }

    private async Task<List<Guid>> SelectSqliteAbandonedAttemptIdsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
        return await db.Database.SqlQuery<Guid>($"""
                SELECT attempt.Id AS "Value"
                FROM TypingAttempts AS attempt
                WHERE attempt.Completed = 0
                  AND attempt.Phase IN ({AttemptPhase.Expired.ToString()}, {AttemptPhase.Aborted.ToString()})
                  AND substr(attempt.PreparedAt, 1, 19) < {cutoffValue}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ChallengeAttemptBindings AS binding
                      WHERE binding.TypingAttemptId = attempt.Id
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM RewardLedgerEntries AS ledger
                      WHERE ledger.UserProfileId = attempt.UserProfileId
                        AND ledger.Source = {AttemptRewardSource}
                        AND lower(ledger.SourceId) = lower(replace(attempt.Id, '-', ''))
                  )
                ORDER BY substr(attempt.PreparedAt, 1, 19), attempt.Id
                LIMIT {options.BatchSize}
                """)
            .ToListAsync(cancellationToken);
    }

    private Task<long> CountPostgreSqlAbandonedAttemptsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        CountAsync($"""
            SELECT COUNT(*) AS "Value"
            FROM "TypingAttempts" AS attempt
            WHERE NOT attempt."Completed"
              AND attempt."Phase" IN ({AttemptPhase.Expired.ToString()}, {AttemptPhase.Aborted.ToString()})
              AND attempt."PreparedAt" < {cutoff}
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ChallengeAttemptBindings" AS binding
                  WHERE binding."TypingAttemptId" = attempt."Id"
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM "RewardLedgerEntries" AS ledger
                  WHERE ledger."UserProfileId" = attempt."UserProfileId"
                    AND ledger."Source" = {AttemptRewardSource}
                    AND lower(ledger."SourceId") = lower(replace(attempt."Id"::text, '-', ''))
              )
            """, cancellationToken);

    private async Task<List<Guid>> SelectPostgreSqlAbandonedAttemptIdsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<Guid>($"""
                SELECT attempt."Id" AS "Value"
                FROM "TypingAttempts" AS attempt
                WHERE NOT attempt."Completed"
                  AND attempt."Phase" IN ({AttemptPhase.Expired.ToString()}, {AttemptPhase.Aborted.ToString()})
                  AND attempt."PreparedAt" < {cutoff}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "ChallengeAttemptBindings" AS binding
                      WHERE binding."TypingAttemptId" = attempt."Id"
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "RewardLedgerEntries" AS ledger
                      WHERE ledger."UserProfileId" = attempt."UserProfileId"
                        AND ledger."Source" = {AttemptRewardSource}
                        AND lower(ledger."SourceId") = lower(replace(attempt."Id"::text, '-', ''))
                  )
                ORDER BY attempt."PreparedAt", attempt."Id"
                LIMIT {options.BatchSize}
                """)
            .ToListAsync(cancellationToken);

    private Task<long> CountSqliteSeenGamificationEventsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var cutoffValue = FormatSqliteDateTimeOffset(cutoff);
        return CountAsync($"""
            SELECT COUNT(*) AS "Value"
            FROM GamificationEvents
            WHERE SeenAt IS NOT NULL
              AND substr(CreatedAt, 1, 19) < {cutoffValue}
            """, cancellationToken);
    }

    private async Task<long> CountAsync(FormattableString query, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<long>(query).SingleAsync(cancellationToken);

    private async Task<List<IAsyncDisposable>> AcquireLifecycleLocksAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        var locks = new List<IAsyncDisposable>(ids.Count);
        try
        {
            foreach (var id in ids.Order())
            {
                locks.Add(await attemptSessions.AcquireLifecycleLockAsync(id, cancellationToken));
            }

            return locks;
        }
        catch
        {
            await DisposeLifecycleLocksAsync(locks);
            throw;
        }
    }

    private static async Task DisposeLifecycleLocksAsync(List<IAsyncDisposable> locks)
    {
        for (var index = locks.Count - 1; index >= 0; index--)
        {
            await locks[index].DisposeAsync();
        }
    }

    private RetentionDatabaseProvider ResolveProvider()
    {
        if (db.Database.IsSqlite())
        {
            return RetentionDatabaseProvider.Sqlite;
        }

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            return RetentionDatabaseProvider.PostgreSql;
        }

        throw new NotSupportedException(
            $"Retention unterstützt SQLite und PostgreSQL, nicht '{db.Database.ProviderName ?? "unbekannt"}'.");
    }

    private static RetentionStepResult Result(
        string name,
        DateTimeOffset cutoff,
        long candidates,
        long affected,
        long remaining,
        bool batchLimitReached) =>
        new(name, cutoff, candidates, affected, remaining, batchLimitReached);

    private static string FormatSqliteDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private enum RetentionDatabaseProvider
    {
        Sqlite,
        PostgreSql
    }
}
