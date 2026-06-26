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
    int DurationMilliseconds,
    int CorrectCharacters);

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
        var totalItems = await attempts.CountAsync(cancellationToken);
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
        var recentEvents = await db.GamificationEvents
            .FromSqlInterpolated($"""
                SELECT *
                FROM GamificationEvents
                WHERE UserProfileId = {profileKey}
                ORDER BY substr(CreatedAt, 1, 19) DESC, Id DESC
                LIMIT 8
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new ProfileInsights(
            BuildInitials(profile),
            BuildDivision(profile.ArenaRating),
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
        var result = new List<ProfileTrendWindow>(TrendWindows.Length);
        foreach (var days in TrendWindows)
        {
            var currentStart = now.AddDays(-days);
            var previousStart = currentStart.AddDays(-days);
            var current = await AggregateWindowAsync(profileId, currentStart, now, cancellationToken);
            var previous = await AggregateWindowAsync(profileId, previousStart, currentStart, cancellationToken);
            result.Add(new ProfileTrendWindow(
                days,
                current.SampleCount,
                current.AverageWpm,
                current.AverageAccuracy,
                current.AverageConsistency,
                current.SampleCount == 0 || previous.SampleCount == 0 ? 0 : Math.Round(current.AverageWpm - previous.AverageWpm, 2),
                current.SampleCount == 0 || previous.SampleCount == 0 ? 0 : Math.Round(current.AverageAccuracy - previous.AverageAccuracy, 2),
                current.SampleCount == 0 || previous.SampleCount == 0 ? 0 : Math.Round(current.AverageConsistency - previous.AverageConsistency, 2)));
        }

        return result;
    }

    private async Task<WindowAggregate> AggregateWindowAsync(
        Guid profileId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var aggregate = await CompletedAttemptsBetween(profileId, start, end)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Wpm = group.Average(attempt => attempt.Wpm),
                Accuracy = group.Average(attempt => attempt.Accuracy),
                Consistency = group.Average(attempt => attempt.Consistency)
            })
            .SingleOrDefaultAsync(cancellationToken);

        return aggregate is null
            ? new WindowAggregate(0, 0, 0, 0)
            : new WindowAggregate(
                aggregate.Count,
                Math.Round(aggregate.Wpm, 2),
                Math.Round(aggregate.Accuracy, 2),
                Math.Round(aggregate.Consistency, 2));
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
        var goalCounts = await db.Missions
            .AsNoTracking()
            .Where(mission => mission.UserProfileId == profileId &&
                mission.Completed &&
                mission.MissionDate >= startDate &&
                mission.MissionDate <= endDate)
            .GroupBy(mission => mission.MissionDate)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Date, item => item.Count, cancellationToken);

        var activity = new List<ProfileActivityDay>(days);
        for (var offset = 0; offset < days; offset++)
        {
            var date = startDate.AddDays(offset);
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var end = start.AddDays(1);
            var trainingAttempts = await CompletedAttemptsBetween(profileId, start, end)
                .CountAsync(cancellationToken);
            var arenaRuns = await CountArenaRunsBetweenAsync(profileId, start, end, cancellationToken);
            activity.Add(new ProfileActivityDay(date, trainingAttempts, arenaRuns, goalCounts.GetValueOrDefault(date)));
        }

        return activity;
    }

    private async Task<IReadOnlyList<ProfileAttemptHistoryRow>> ReadHistoryPageAsync(
        Guid profileId,
        int historyPage,
        int historyPageSize,
        CancellationToken cancellationToken)
    {
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
                SELECT Id, CreatedAt, Mode, Wpm, Accuracy, Consistency, DurationMilliseconds, CorrectCharacters
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
                    reader.GetInt32(7)));
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

    private IQueryable<TypingAttempt> CompletedAttemptsBetween(Guid profileId, DateTimeOffset start, DateTimeOffset end)
    {
        var profileKey = FormatSqliteGuid(profileId);
        var phase = AttemptPhase.Finished.ToString();
        var startValue = FormatSqliteDateTimeOffset(start);
        var endValue = FormatSqliteDateTimeOffset(end);
        return db.TypingAttempts
            .FromSqlInterpolated($"""
                SELECT *
                FROM TypingAttempts
                WHERE UserProfileId = {profileKey}
                  AND Phase = {phase}
                  AND Completed = 1
                  AND substr(CreatedAt, 1, 19) >= {startValue}
                  AND substr(CreatedAt, 1, 19) < {endValue}
                """)
            .AsNoTracking();
    }

    private async Task<int> CountArenaRunsBetweenAsync(Guid profileId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
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
                SELECT COUNT(*)
                FROM LiveRoomParticipantSummaries p
                INNER JOIN LiveRoomSummaries r ON p.LiveRoomSummaryId = r.Id
                WHERE p.UserProfileId = $profileId
                  AND r.FinishedAt IS NOT NULL
                  AND substr(r.FinishedAt, 1, 19) >= $start
                  AND substr(r.FinishedAt, 1, 19) < $end
                """;
            AddParameter(command, "$profileId", FormatSqliteGuid(profileId));
            AddParameter(command, "$start", FormatSqliteDateTimeOffset(start));
            AddParameter(command, "$end", FormatSqliteDateTimeOffset(end));
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
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

    private static string BuildDivision(int rating) => rating switch
    {
        >= 1600 => "Meister",
        >= 1400 => "Gold",
        >= 1200 => "Silber",
        >= 1000 => "Bronze",
        _ => "Aufbau"
    };

    private sealed record WindowAggregate(int SampleCount, double AverageWpm, double AverageAccuracy, double AverageConsistency);
}
