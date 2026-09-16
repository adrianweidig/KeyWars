using System.Globalization;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record SeasonWindow(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
internal sealed record SeasonScorePreparation(UserProfile Profile, DateTimeOffset AwardedAt);

public sealed class SeasonService(
    KeyWarsDbContext db,
    TimeProvider timeProvider,
    IOptions<SeasonOptions> configuredOptions)
{
    private readonly SeasonOptions options = Validate(configuredOptions.Value);

    public SeasonService(KeyWarsDbContext db, TimeProvider timeProvider)
        : this(db, timeProvider, Options.Create(new SeasonOptions()))
    {
    }

    public bool Enabled => options.Enabled;

    public SeasonWindow CurrentWindow => ResolveWindow(timeProvider.GetUtcNow(), options.MonthsPerSeason);

    public async Task PrepareScoresAsync(
        IReadOnlyCollection<UserProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        await PrepareScoresAsync(
            profiles.Select(profile => new SeasonScorePreparation(profile, timeProvider.GetUtcNow())).ToArray(),
            cancellationToken);
    }

    internal async Task PrepareScoresAsync(
        IReadOnlyCollection<SeasonScorePreparation> preparations,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || preparations.Count == 0)
        {
            return;
        }

        foreach (var group in preparations.GroupBy(item =>
                     ResolveWindow(item.AwardedAt, options.MonthsPerSeason).Key))
        {
            var season = await PrepareAtAsync(group.First().AwardedAt, cancellationToken);
            var currentSeasonKey = CurrentWindow.Key;
            var profiles = group
                .Select(item => item.Profile)
                .DistinctBy(profile => profile.Id)
                .ToArray();
            var profileIds = profiles.Select(profile => profile.Id).ToArray();
            var stored = await db.SeasonScores
                .Where(score => score.SeasonId == season.Id && profileIds.Contains(score.UserProfileId))
                .ToListAsync(cancellationToken);
            var scoresByProfile = stored.ToDictionary(score => score.UserProfileId);
            foreach (var profile in profiles)
            {
                if (!scoresByProfile.TryGetValue(profile.Id, out var score))
                {
                    score = new SeasonScore
                    {
                        SeasonId = season.Id,
                        UserProfileId = profile.Id
                    };
                    db.SeasonScores.Add(score);
                    scoresByProfile[profile.Id] = score;
                }

                if (season.Key == currentSeasonKey)
                {
                    profile.SeasonPoints = score.Points;
                }
            }
        }
    }

    public async Task<Season?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var window = CurrentWindow;
        return await db.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(season => season.Key == window.Key, cancellationToken);
    }

    public async Task<Season> EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Saisonwertung ist deaktiviert.");
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return await PrepareCurrentAsync(cancellationToken);
        }

        await using var sqliteWriteFence = db.Database.IsSqlite()
            ? await SeasonWriteFence.AcquireAsync(db, cancellationToken)
            : null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var postgresWriteFence = db.Database.IsNpgsql()
            ? await SeasonWriteFence.AcquireAsync(db, cancellationToken)
            : null;
        var season = await PrepareCurrentAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return season;
    }

    public async Task<int> AwardForRewardAsync(
        UserProfile profile,
        RewardLedgerEntry reward,
        int points,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || points <= 0 || reward.SeasonPoints > 0)
        {
            return 0;
        }

        if (reward.UserProfileId != profile.Id)
        {
            throw new InvalidOperationException("Saisonpunkte und XP-Eintrag gehören nicht zum selben Profil.");
        }

        var season = await PrepareAtAsync(reward.AwardedAt, cancellationToken);
        var score = db.SeasonScores.Local.SingleOrDefault(item =>
            item.SeasonId == season.Id && item.UserProfileId == profile.Id);
        score ??= await db.SeasonScores.SingleOrDefaultAsync(item =>
            item.SeasonId == season.Id && item.UserProfileId == profile.Id,
            cancellationToken);
        if (score is null)
        {
            score = new SeasonScore
            {
                SeasonId = season.Id,
                UserProfileId = profile.Id
            };
            db.SeasonScores.Add(score);
        }

        reward.SeasonId = season.Id;
        reward.SeasonPoints = points;
        score.Points = checked(score.Points + points);
        score.UpdatedAt = score.UpdatedAt > reward.AwardedAt ? score.UpdatedAt : reward.AwardedAt;
        if (season.Key == CurrentWindow.Key)
        {
            profile.SeasonPoints = score.Points;
        }
        return points;
    }

    public async Task<int> AwardAsync(
        UserProfile profile,
        string source,
        string sourceId,
        int points,
        DateTimeOffset awardedAt,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || points <= 0)
        {
            return 0;
        }

        var localReward = db.RewardLedgerEntries.Local.SingleOrDefault(item =>
            item.UserProfileId == profile.Id && item.Source == source && item.SourceId == sourceId);
        if (localReward is not null)
        {
            return await AwardForRewardAsync(profile, localReward, points, cancellationToken);
        }

        if (await db.RewardLedgerEntries.AnyAsync(item =>
                item.UserProfileId == profile.Id && item.Source == source && item.SourceId == sourceId,
                cancellationToken))
        {
            return 0;
        }

        var reward = new RewardLedgerEntry
        {
            UserProfileId = profile.Id,
            Source = source,
            SourceId = sourceId,
            Xp = 0,
            AwardedAt = awardedAt
        };
        db.RewardLedgerEntries.Add(reward);
        return await AwardForRewardAsync(profile, reward, points, cancellationToken);
    }

    internal async Task<Season> PrepareCurrentAsync(CancellationToken cancellationToken)
    {
        return await PrepareAtAsync(timeProvider.GetUtcNow(), cancellationToken);
    }

    internal async Task<Season> PrepareAtAsync(
        DateTimeOffset instant,
        CancellationToken cancellationToken)
    {
        var window = ResolveWindow(instant, options.MonthsPerSeason);
        if (db.Database.IsNpgsql())
        {
            await SeasonWriteFence.AcquireAsync(db, cancellationToken);
        }

        var tracked = db.Seasons.Local.SingleOrDefault(season => season.Key == window.Key);
        if (tracked is not null)
        {
            return tracked;
        }

        if (db.Database.IsSqlite())
        {
            await using var localFence = db.Database.CurrentTransaction is null
                ? await SeasonWriteFence.AcquireAsync(db, cancellationToken)
                : null;
            var sqliteStored = await db.Seasons.SingleOrDefaultAsync(
                season => season.Key == window.Key,
                cancellationToken);
            if (sqliteStored is not null)
            {
                return sqliteStored;
            }

            var sqliteSeason = new Season
            {
                Key = window.Key,
                Name = window.Name,
                StartsAt = window.StartsAt,
                EndsAt = window.EndsAt,
                CreatedAt = timeProvider.GetUtcNow()
            };
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO Seasons (Id, Key, Name, StartsAt, EndsAt, CreatedAt)
                VALUES ({sqliteSeason.Id}, {sqliteSeason.Key}, {sqliteSeason.Name}, {sqliteSeason.StartsAt},
                        {sqliteSeason.EndsAt}, {sqliteSeason.CreatedAt})
                """, cancellationToken);
            if (inserted > 0)
            {
                db.Seasons.Attach(sqliteSeason);
                if (window.Key == CurrentWindow.Key)
                {
                    await SynchronizeCurrentProfileCacheAsync(sqliteSeason.Id, cancellationToken);
                }

                return sqliteSeason;
            }

            return await db.Seasons.SingleAsync(
                season => season.Key == window.Key,
                cancellationToken);
        }

        var stored = await db.Seasons.SingleOrDefaultAsync(season => season.Key == window.Key, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        stored = await db.Seasons.SingleOrDefaultAsync(season => season.Key == window.Key, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        var season = new Season
        {
            Key = window.Key,
            Name = window.Name,
            StartsAt = window.StartsAt,
            EndsAt = window.EndsAt,
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.Seasons.Add(season);
        if (window.Key == CurrentWindow.Key)
        {
            await SynchronizeCurrentProfileCacheAsync(season.Id, cancellationToken);
        }

        return season;
    }

    private async Task SynchronizeCurrentProfileCacheAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE UserProfiles
                SET SeasonPoints = COALESCE((
                    SELECT score.Points
                    FROM SeasonScores AS score
                    WHERE score.SeasonId = {seasonId}
                      AND score.UserProfileId = UserProfiles.Id
                ), 0)
                WHERE SeasonPoints <> COALESCE((
                    SELECT score.Points
                    FROM SeasonScores AS score
                    WHERE score.SeasonId = {seasonId}
                      AND score.UserProfileId = UserProfiles.Id
                ), 0)
                """, cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "UserProfiles" AS profile
                SET "SeasonPoints" = current_score."Points"
                FROM (
                    SELECT candidate."Id", COALESCE(score."Points", 0) AS "Points"
                    FROM "UserProfiles" AS candidate
                    LEFT JOIN "SeasonScores" AS score
                      ON score."UserProfileId" = candidate."Id"
                     AND score."SeasonId" = {seasonId}
                ) AS current_score
                WHERE profile."Id" = current_score."Id"
                  AND profile."SeasonPoints" <> current_score."Points"
                """, cancellationToken);
        }

        var trackedProfiles = db.UserProfiles.Local.ToArray();
        if (trackedProfiles.Length == 0)
        {
            return;
        }

        var trackedIds = trackedProfiles.Select(profile => profile.Id).ToArray();
        var scores = await db.SeasonScores
            .AsNoTracking()
            .Where(score => score.SeasonId == seasonId && trackedIds.Contains(score.UserProfileId))
            .ToDictionaryAsync(score => score.UserProfileId, score => score.Points, cancellationToken);
        foreach (var profile in trackedProfiles)
        {
            var points = scores.GetValueOrDefault(profile.Id);
            var trackedPoints = db.Entry(profile).Property(item => item.SeasonPoints);
            trackedPoints.CurrentValue = points;
            trackedPoints.OriginalValue = points;
            trackedPoints.IsModified = false;
        }
    }

    public static SeasonWindow ResolveWindow(DateTimeOffset instant, int monthsPerSeason)
    {
        ValidateMonths(monthsPerSeason);
        var utc = instant.UtcDateTime;
        var zeroBasedMonth = utc.Month - 1;
        var startMonth = (zeroBasedMonth / monthsPerSeason) * monthsPerSeason + 1;
        var start = new DateTimeOffset(utc.Year, startMonth, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(monthsPerSeason);
        var key = monthsPerSeason == 1
            ? start.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : $"{start:yyyy-MM}_{end.AddTicks(-1):yyyy-MM}";
        var name = monthsPerSeason == 1
            ? start.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("de-DE"))
            : $"{start:MMMM yyyy} bis {end.AddMonths(-1):MMMM yyyy}";
        return new SeasonWindow(key, name, start, end);
    }

    private static SeasonOptions Validate(SeasonOptions value)
    {
        value.Validate();
        return value;
    }

    private static void ValidateMonths(int monthsPerSeason)
    {
        if (monthsPerSeason is < 1 or > 12 || 12 % monthsPerSeason != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monthsPerSeason));
        }
    }
}
