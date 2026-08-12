using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace KeyWars.Services;

public sealed record ProfileInsights(
    string Initials,
    string Division,
    ProfileTotals Totals,
    IReadOnlyList<ProfileTrendWindow> Trends,
    IReadOnlyList<ProfileActivityDay> ActivityDays,
    IReadOnlyList<ProfileModeBest> BestModes,
    IReadOnlyList<ProfileAttemptHistoryRow> History,
    int HistoryPage,
    int HistoryPageSize,
    int HistoryTotalItems,
    int HistoryTotalPages,
    IReadOnlyList<Achievement> FeaturedAchievements,
    IReadOnlyList<Mission> CurrentGoals,
    IReadOnlyList<GamificationEvent> RecentEvents);

public sealed record ProfileTotals(
    int CompletedAttempts,
    int CorrectCharacters,
    int IncorrectCharacters,
    int TypedCharacters,
    int EstimatedWords,
    TimeSpan TypingTime);

public sealed record ProfileTrendWindow(
    int Days,
    int SampleCount,
    double AverageWpm,
    double AverageAccuracy,
    double AverageConsistency,
    double WpmDelta,
    double AccuracyDelta,
    double ConsistencyDelta);

public sealed record ProfileActivityDay(
    DateOnly Date,
    int TrainingAttempts,
    int ArenaRuns,
    int CompletedGoals)
{
    public int Intensity => TrainingAttempts + ArenaRuns + CompletedGoals;
}

public sealed record ProfileModeBest(
    TrainingMode Mode,
    int SampleCount,
    double BestWpm,
    double BestAccuracy,
    double AverageWpm);

public sealed record ProfileAttemptHistoryRow(
    Guid Id,
    DateTimeOffset CreatedAt,
    TrainingMode Mode,
    double Wpm,
    double Accuracy,
    double Consistency,
    int ConsistencySampleCount,
    int DurationMilliseconds,
    int CorrectCharacters,
    int IncorrectCharacters);

public sealed class ProfileInsightsService(KeyWarsDbContext db, TimeProvider timeProvider)
{
    private static readonly int[] TrendWindows = [7, 30, 90];

    public async Task<ProfileInsights> GetAsync(UserProfile profile, int historyPage, int historyPageSize, CancellationToken cancellationToken)
    {
        historyPage = Math.Max(1, historyPage);
        historyPageSize = Math.Clamp(historyPageSize, 5, 50);

        var attempts = CompletedAttempts(profile.Id);
        var totals = await BuildTotalsAsync(attempts, cancellationToken);
        var trends = await BuildTrendsAsync(profile.Id, cancellationToken);
        var bestModes = await BuildBestModesAsync(attempts, cancellationToken);
        var totalItems = totals.CompletedAttempts;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)historyPageSize));
        historyPage = Math.Min(historyPage, totalPages);
        var history = await ReadHistoryPageAsync(profile.Id, historyPage, historyPageSize, cancellationToken);

        var activity = await BuildActivityAsync(profile.Id, 90, cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var weekStart = MotivationService.GetWeekStart(today);
        var goals = await db.Missions
            .AsNoTracking()
            .Where(mission => mission.UserProfileId == profile.Id && (mission.MissionDate == today || mission.MissionDate == weekStart))
            .OrderBy(mission => mission.MissionDate == today ? 0 : 1)
            .ThenBy(mission => mission.Title)
            .ToListAsync(cancellationToken);
        var achievements = await ReadFeaturedAchievementsAsync(profile.Id, cancellationToken);
        var profileKey = FormatSqliteGuid(profile.Id);
        var recentEvents = db.Database.IsSqlite()
            ? await db.GamificationEvents
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM GamificationEvents
                    WHERE UserProfileId = {profileKey}
                    ORDER BY CreatedAt DESC, Id DESC
                    LIMIT 8
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
            : await db.GamificationEvents
                .AsNoTracking()
                .Where(item => item.UserProfileId == profile.Id)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(8)
                .ToListAsync(cancellationToken);

        return new ProfileInsights(
            BuildInitials(profile),
            ArenaDivision.NameFor(profile.ArenaRating),
            totals,
            trends,
            activity,
            bestModes,
            history,
            historyPage,
            historyPageSize,
            totalItems,
            totalPages,
            achievements,
            goals,
            recentEvents);
    }

    private IQueryable<TypingAttempt> CompletedAttempts(Guid profileId) =>
        db.TypingAttempts
            .AsNoTracking()
            .Where(attempt => attempt.UserProfileId == profileId && attempt.Phase == AttemptPhase.Finished && attempt.Completed);

    private static async Task<ProfileTotals> BuildTotalsAsync(IQueryable<TypingAttempt> attempts, CancellationToken cancellationToken)
    {
        var aggregate = await attempts
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Correct = group.Sum(attempt => attempt.CorrectCharacters),
                Incorrect = group.Sum(attempt => attempt.IncorrectCharacters),
                Duration = group.Sum(attempt => attempt.DurationMilliseconds)
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (aggregate is null)
        {
            return new ProfileTotals(0, 0, 0, 0, 0, TimeSpan.Zero);
        }

        var typedCharacters = aggregate.Correct + aggregate.Incorrect;
        return new ProfileTotals(
            aggregate.Count,
            aggregate.Correct,
            aggregate.Incorrect,
            typedCharacters,
            typedCharacters / 5,
            TimeSpan.FromMilliseconds(aggregate.Duration));
    }

    private async Task<IReadOnlyList<ProfileTrendWindow>> BuildTrendsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!db.Database.IsSqlite())
        {
            return await BuildTrendsWithEfAsync(profileId, now, cancellationToken);
        }

        var profileKey = FormatSqliteGuid(profileId);
        var phase = AttemptPhase.Finished.ToString();
        var nowValue = FormatSqliteDateTimeOffset(now);
        var rows = await db.Database.SqlQuery<TrendAggregateRow>($"""
                WITH windows(Days, CurrentStart, PreviousStart) AS (
                    SELECT 7, {FormatSqliteDateTimeOffset(now.AddDays(-7))}, {FormatSqliteDateTimeOffset(now.AddDays(-14))}
                    UNION ALL
                    SELECT 30, {FormatSqliteDateTimeOffset(now.AddDays(-30))}, {FormatSqliteDateTimeOffset(now.AddDays(-60))}
                    UNION ALL
                    SELECT 90, {FormatSqliteDateTimeOffset(now.AddDays(-90))}, {FormatSqliteDateTimeOffset(now.AddDays(-180))}
                ), eligible AS (
                    SELECT Wpm, Accuracy, Consistency, substr(CreatedAt, 1, 19) AS ActivityAt
                    FROM TypingAttempts
                    WHERE UserProfileId = {profileKey}
                      AND Phase = {phase}
                      AND Completed = 1
                      AND substr(CreatedAt, 1, 19) >= {FormatSqliteDateTimeOffset(now.AddDays(-180))}
                      AND substr(CreatedAt, 1, 19) < {nowValue}
                )
                SELECT windows.Days,
                       COUNT(CASE WHEN eligible.ActivityAt >= windows.CurrentStart THEN 1 END) AS CurrentSampleCount,
                       COALESCE(AVG(CASE WHEN eligible.ActivityAt >= windows.CurrentStart THEN eligible.Wpm END), 0) AS CurrentAverageWpm,
                       COALESCE(AVG(CASE WHEN eligible.ActivityAt >= windows.CurrentStart THEN eligible.Accuracy END), 0) AS CurrentAverageAccuracy,
                       COALESCE(AVG(CASE WHEN eligible.ActivityAt >= windows.CurrentStart THEN eligible.Consistency END), 0) AS CurrentAverageConsistency,
                       COUNT(CASE WHEN eligible.ActivityAt < windows.CurrentStart THEN 1 END) AS PreviousSampleCount,
                       COALESCE(AVG(CASE WHEN eligible.ActivityAt < windows.CurrentStart THEN eligible.Wpm END), 0) AS PreviousAverageWpm,
                       COALESCE(AVG(CASE WHEN eligible.ActivityAt < windows.CurrentStart THEN eligible.Accuracy END), 0) AS PreviousAverageAccuracy,
                       COALESCE(AVG(CASE WHEN eligible.ActivityAt < windows.CurrentStart THEN eligible.Consistency END), 0) AS PreviousAverageConsistency
                FROM windows
                LEFT JOIN eligible
                  ON eligible.ActivityAt >= windows.PreviousStart
                 AND eligible.ActivityAt < {nowValue}
                GROUP BY windows.Days
                ORDER BY windows.Days
                """).ToListAsync(cancellationToken);
        var byDays = rows.ToDictionary(row => row.Days);

        return TrendWindows.Select(days =>
        {
            var row = byDays[days];
            var currentWpm = Math.Round(row.CurrentAverageWpm, 2);
            var currentAccuracy = Math.Round(row.CurrentAverageAccuracy, 2);
            var currentConsistency = Math.Round(row.CurrentAverageConsistency, 2);
            return new ProfileTrendWindow(
                days,
                row.CurrentSampleCount,
                currentWpm,
                currentAccuracy,
                currentConsistency,
                row.CurrentSampleCount == 0 || row.PreviousSampleCount == 0 ? 0 : Math.Round(currentWpm - row.PreviousAverageWpm, 2),
                row.CurrentSampleCount == 0 || row.PreviousSampleCount == 0 ? 0 : Math.Round(currentAccuracy - row.PreviousAverageAccuracy, 2),
                row.CurrentSampleCount == 0 || row.PreviousSampleCount == 0 ? 0 : Math.Round(currentConsistency - row.PreviousAverageConsistency, 2));
        }).ToList();
    }

    private async Task<IReadOnlyList<ProfileTrendWindow>> BuildTrendsWithEfAsync(
        Guid profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var start7 = now.AddDays(-7);
        var previous7 = now.AddDays(-14);
        var start30 = now.AddDays(-30);
        var previous30 = now.AddDays(-60);
        var start90 = now.AddDays(-90);
        var previous90 = now.AddDays(-180);
        var aggregate = await CompletedAttempts(profileId)
            .Where(attempt => attempt.CreatedAt >= previous90 && attempt.CreatedAt < now)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Current7Count = group.Count(attempt => attempt.CreatedAt >= start7),
                Current7Wpm = group.Where(attempt => attempt.CreatedAt >= start7).Sum(attempt => attempt.Wpm),
                Current7Accuracy = group.Where(attempt => attempt.CreatedAt >= start7).Sum(attempt => attempt.Accuracy),
                Current7Consistency = group.Where(attempt => attempt.CreatedAt >= start7).Sum(attempt => attempt.Consistency),
                Previous7Count = group.Count(attempt => attempt.CreatedAt >= previous7 && attempt.CreatedAt < start7),
                Previous7Wpm = group.Where(attempt => attempt.CreatedAt >= previous7 && attempt.CreatedAt < start7).Sum(attempt => attempt.Wpm),
                Previous7Accuracy = group.Where(attempt => attempt.CreatedAt >= previous7 && attempt.CreatedAt < start7).Sum(attempt => attempt.Accuracy),
                Previous7Consistency = group.Where(attempt => attempt.CreatedAt >= previous7 && attempt.CreatedAt < start7).Sum(attempt => attempt.Consistency),
                Current30Count = group.Count(attempt => attempt.CreatedAt >= start30),
                Current30Wpm = group.Where(attempt => attempt.CreatedAt >= start30).Sum(attempt => attempt.Wpm),
                Current30Accuracy = group.Where(attempt => attempt.CreatedAt >= start30).Sum(attempt => attempt.Accuracy),
                Current30Consistency = group.Where(attempt => attempt.CreatedAt >= start30).Sum(attempt => attempt.Consistency),
                Previous30Count = group.Count(attempt => attempt.CreatedAt >= previous30 && attempt.CreatedAt < start30),
                Previous30Wpm = group.Where(attempt => attempt.CreatedAt >= previous30 && attempt.CreatedAt < start30).Sum(attempt => attempt.Wpm),
                Previous30Accuracy = group.Where(attempt => attempt.CreatedAt >= previous30 && attempt.CreatedAt < start30).Sum(attempt => attempt.Accuracy),
                Previous30Consistency = group.Where(attempt => attempt.CreatedAt >= previous30 && attempt.CreatedAt < start30).Sum(attempt => attempt.Consistency),
                Current90Count = group.Count(attempt => attempt.CreatedAt >= start90),
                Current90Wpm = group.Where(attempt => attempt.CreatedAt >= start90).Sum(attempt => attempt.Wpm),
                Current90Accuracy = group.Where(attempt => attempt.CreatedAt >= start90).Sum(attempt => attempt.Accuracy),
                Current90Consistency = group.Where(attempt => attempt.CreatedAt >= start90).Sum(attempt => attempt.Consistency),
                Previous90Count = group.Count(attempt => attempt.CreatedAt >= previous90 && attempt.CreatedAt < start90),
                Previous90Wpm = group.Where(attempt => attempt.CreatedAt >= previous90 && attempt.CreatedAt < start90).Sum(attempt => attempt.Wpm),
                Previous90Accuracy = group.Where(attempt => attempt.CreatedAt >= previous90 && attempt.CreatedAt < start90).Sum(attempt => attempt.Accuracy),
                Previous90Consistency = group.Where(attempt => attempt.CreatedAt >= previous90 && attempt.CreatedAt < start90).Sum(attempt => attempt.Consistency)
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (aggregate is null)
        {
            return TrendWindows.Select(days => new ProfileTrendWindow(days, 0, 0, 0, 0, 0, 0, 0)).ToList();
        }

        return
        [
            BuildTrend(7, aggregate.Current7Count, aggregate.Current7Wpm, aggregate.Current7Accuracy, aggregate.Current7Consistency,
                aggregate.Previous7Count, aggregate.Previous7Wpm, aggregate.Previous7Accuracy, aggregate.Previous7Consistency),
            BuildTrend(30, aggregate.Current30Count, aggregate.Current30Wpm, aggregate.Current30Accuracy, aggregate.Current30Consistency,
                aggregate.Previous30Count, aggregate.Previous30Wpm, aggregate.Previous30Accuracy, aggregate.Previous30Consistency),
            BuildTrend(90, aggregate.Current90Count, aggregate.Current90Wpm, aggregate.Current90Accuracy, aggregate.Current90Consistency,
                aggregate.Previous90Count, aggregate.Previous90Wpm, aggregate.Previous90Accuracy, aggregate.Previous90Consistency)
        ];
    }

    private static ProfileTrendWindow BuildTrend(
        int days,
        int currentCount,
        double currentWpmSum,
        double currentAccuracySum,
        double currentConsistencySum,
        int previousCount,
        double previousWpmSum,
        double previousAccuracySum,
        double previousConsistencySum)
    {
        var currentWpm = currentCount == 0 ? 0 : Math.Round(currentWpmSum / currentCount, 2);
        var currentAccuracy = currentCount == 0 ? 0 : Math.Round(currentAccuracySum / currentCount, 2);
        var currentConsistency = currentCount == 0 ? 0 : Math.Round(currentConsistencySum / currentCount, 2);
        var previousWpm = previousCount == 0 ? 0 : Math.Round(previousWpmSum / previousCount, 2);
        var previousAccuracy = previousCount == 0 ? 0 : Math.Round(previousAccuracySum / previousCount, 2);
        var previousConsistency = previousCount == 0 ? 0 : Math.Round(previousConsistencySum / previousCount, 2);
        return new ProfileTrendWindow(
            days,
            currentCount,
            currentWpm,
            currentAccuracy,
            currentConsistency,
            currentCount == 0 || previousCount == 0 ? 0 : Math.Round(currentWpm - previousWpm, 2),
            currentCount == 0 || previousCount == 0 ? 0 : Math.Round(currentAccuracy - previousAccuracy, 2),
            currentCount == 0 || previousCount == 0 ? 0 : Math.Round(currentConsistency - previousConsistency, 2));
    }

    private static async Task<IReadOnlyList<ProfileModeBest>> BuildBestModesAsync(IQueryable<TypingAttempt> attempts, CancellationToken cancellationToken)
    {
        var aggregates = await attempts
            .GroupBy(attempt => attempt.Mode)
            .Select(group => new
            {
                Mode = group.Key,
                SampleCount = group.Count(),
                BestWpm = group.Max(attempt => attempt.Wpm),
                BestAccuracy = group.Max(attempt => attempt.Accuracy),
                AverageWpm = group.Average(attempt => attempt.Wpm)
            })
            .OrderByDescending(best => best.BestWpm)
            .ThenByDescending(best => best.SampleCount)
            .ThenBy(best => best.Mode)
            .Take(8)
            .ToListAsync(cancellationToken);

        return aggregates
            .Select(best => new ProfileModeBest(best.Mode, best.SampleCount, best.BestWpm, best.BestAccuracy, best.AverageWpm))
            .ToList();
    }

    private async Task<IReadOnlyList<ProfileActivityDay>> BuildActivityAsync(Guid profileId, int days, CancellationToken cancellationToken)
    {
        var endDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var startDate = endDate.AddDays(-(days - 1));
        var periodStart = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var goalCounts = await db.Missions
            .AsNoTracking()
            .Where(mission => mission.UserProfileId == profileId &&
                mission.Completed &&
                mission.MissionDate >= startDate &&
                mission.MissionDate <= endDate)
            .GroupBy(mission => mission.MissionDate)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Date, item => item.Count, cancellationToken);
        var trainingCounts = await ReadTrainingActivityCountsAsync(profileId, periodStart, periodEnd, cancellationToken);
        var arenaCounts = await ReadArenaActivityCountsAsync(profileId, periodStart, periodEnd, cancellationToken);

        var activity = new List<ProfileActivityDay>(days);
        for (var offset = 0; offset < days; offset++)
        {
            var date = startDate.AddDays(offset);
            activity.Add(new ProfileActivityDay(
                date,
                trainingCounts.GetValueOrDefault(date),
                arenaCounts.GetValueOrDefault(date),
                goalCounts.GetValueOrDefault(date)));
        }

        return activity;
    }

    private async Task<IReadOnlyDictionary<DateOnly, int>> ReadTrainingActivityCountsAsync(
        Guid profileId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite())
        {
            var providerCounts = await CompletedAttempts(profileId)
                .Where(attempt => attempt.CreatedAt >= start && attempt.CreatedAt < end)
                .GroupBy(attempt => attempt.CreatedAt.Date)
                .Select(group => new { Date = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);
            return providerCounts.ToDictionary(item => DateOnly.FromDateTime(item.Date), item => item.Count);
        }

        var profileKey = FormatSqliteGuid(profileId);
        var phase = AttemptPhase.Finished.ToString();
        var startValue = FormatSqliteDateTimeOffset(start);
        var endValue = FormatSqliteDateTimeOffset(end);
        var counts = await db.Database
            .SqlQuery<ActivityCountRow>($"""
                SELECT substr(CreatedAt, 1, 10) AS ActivityDate, COUNT(*) AS "Count"
                FROM TypingAttempts
                WHERE UserProfileId = {profileKey}
                  AND Phase = {phase}
                  AND Completed = 1
                  AND substr(CreatedAt, 1, 19) >= {startValue}
                  AND substr(CreatedAt, 1, 19) < {endValue}
                GROUP BY substr(CreatedAt, 1, 10)
                """)
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(
            item => DateOnly.ParseExact(item.ActivityDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            item => item.Count);
    }

    private async Task<IReadOnlyDictionary<DateOnly, int>> ReadArenaActivityCountsAsync(
        Guid profileId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite())
        {
            var providerCounts = await (
                    from participant in db.LiveRoomParticipantSummaries.AsNoTracking()
                    join room in db.LiveRoomSummaries.AsNoTracking() on participant.LiveRoomSummaryId equals room.Id
                    where participant.UserProfileId == profileId &&
                        room.FinishedAt != null &&
                        room.FinishedAt >= start &&
                        room.FinishedAt < end
                    group room by room.FinishedAt!.Value.Date into dayGroup
                    select new { Date = dayGroup.Key, Count = dayGroup.Count() })
                .ToListAsync(cancellationToken);
            return providerCounts.ToDictionary(item => DateOnly.FromDateTime(item.Date), item => item.Count);
        }

        var profileKey = FormatSqliteGuid(profileId);
        var startValue = FormatSqliteDateTimeOffset(start);
        var endValue = FormatSqliteDateTimeOffset(end);
        var counts = await db.Database
            .SqlQuery<ActivityCountRow>($"""
                SELECT substr(r.FinishedAt, 1, 10) AS ActivityDate, COUNT(*) AS "Count"
                FROM LiveRoomParticipantSummaries p
                INNER JOIN LiveRoomSummaries r ON p.LiveRoomSummaryId = r.Id
                WHERE p.UserProfileId = {profileKey}
                  AND r.FinishedAt IS NOT NULL
                  AND substr(r.FinishedAt, 1, 19) >= {startValue}
                  AND substr(r.FinishedAt, 1, 19) < {endValue}
                GROUP BY substr(r.FinishedAt, 1, 10)
                """)
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(
            item => DateOnly.ParseExact(item.ActivityDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            item => item.Count);
    }

    private sealed class ActivityCountRow
    {
        public string ActivityDate { get; set; } = "";
        public int Count { get; set; }
    }

    private async Task<IReadOnlyList<ProfileAttemptHistoryRow>> ReadHistoryPageAsync(
        Guid profileId,
        int historyPage,
        int historyPageSize,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite())
        {
            return await CompletedAttempts(profileId)
                .OrderByDescending(attempt => attempt.CreatedAt)
                .ThenByDescending(attempt => attempt.Id)
                .Skip((historyPage - 1) * historyPageSize)
                .Take(historyPageSize)
                .Select(attempt => new ProfileAttemptHistoryRow(
                    attempt.Id,
                    attempt.CreatedAt,
                    attempt.Mode,
                    attempt.Wpm,
                    attempt.Accuracy,
                    attempt.Consistency,
                    attempt.ConsistencySampleCount,
                    attempt.DurationMilliseconds,
                    attempt.CorrectCharacters,
                    attempt.IncorrectCharacters))
                .ToListAsync(cancellationToken);
        }

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, CreatedAt, Mode, Wpm, Accuracy, Consistency, ConsistencySampleCount,
                       DurationMilliseconds, CorrectCharacters, IncorrectCharacters
                FROM TypingAttempts
                WHERE UserProfileId = $profileId
                  AND Phase = $phase
                  AND Completed = 1
                ORDER BY CreatedAt DESC, Id DESC
                LIMIT $limit OFFSET $offset
                """;
            AddParameter(command, "$profileId", FormatSqliteGuid(profileId));
            AddParameter(command, "$phase", AttemptPhase.Finished.ToString());
            AddParameter(command, "$limit", historyPageSize);
            AddParameter(command, "$offset", (historyPage - 1) * historyPageSize);

            var rows = new List<ProfileAttemptHistoryRow>(historyPageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ProfileAttemptHistoryRow(
                    Guid.Parse(reader.GetString(0)),
                    DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    Enum.Parse<TrainingMode>(reader.GetString(2)),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9)));
            }

            return rows;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<IReadOnlyList<Achievement>> ReadFeaturedAchievementsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlite())
        {
            return await db.Achievements
                .AsNoTracking()
                .Where(item => item.UserProfileId == profileId)
                .OrderByDescending(item => item.UnlockedAt)
                .Take(5)
                .ToListAsync(cancellationToken);
        }

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, UserProfileId, Key, Title, Description, UnlockedAt
                FROM Achievements
                WHERE UserProfileId = $profileId
                ORDER BY UnlockedAt DESC
                LIMIT 5
                """;
            AddParameter(command, "$profileId", FormatSqliteGuid(profileId));

            var achievements = new List<Achievement>(5);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                achievements.Add(new Achievement
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    UserProfileId = Guid.Parse(reader.GetString(1)),
                    Key = reader.GetString(2),
                    Title = reader.GetString(3),
                    Description = reader.GetString(4),
                    UnlockedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)
                });
            }

            return achievements;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string FormatSqliteDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatSqliteGuid(Guid value) => value.ToString().ToUpperInvariant();

    private static string BuildInitials(UserProfile profile)
    {
        var parts = profile.DisplayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(part => part[0].ToString().ToUpperInvariant())
            .ToArray();
        if (parts.Length > 0)
        {
            return string.Concat(parts);
        }

        var fallback = string.IsNullOrWhiteSpace(profile.SamAccountName) ? "KW" : profile.SamAccountName.Trim();
        return fallback.Length == 1
            ? fallback.ToUpperInvariant()
            : fallback[..2].ToUpperInvariant();
    }

    private sealed class TrendAggregateRow
    {
        public int Days { get; set; }
        public int CurrentSampleCount { get; set; }
        public double CurrentAverageWpm { get; set; }
        public double CurrentAverageAccuracy { get; set; }
        public double CurrentAverageConsistency { get; set; }
        public int PreviousSampleCount { get; set; }
        public double PreviousAverageWpm { get; set; }
        public double PreviousAverageAccuracy { get; set; }
        public double PreviousAverageConsistency { get; set; }
    }
}
