using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record AchievementProgressState(
    string Key,
    DateTimeOffset? UnlockedAt,
    double Current,
    double Target,
    string Unit);

public sealed record AchievementProgressOverview(
    IReadOnlyDictionary<string, AchievementProgressState> Items,
    int UnlockedCount);

public sealed class AchievementProgressService(KeyWarsDbContext db)
{
    public async Task<AchievementProgressOverview> ReadAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = await db.UserProfiles
            .AsNoTracking()
            .Where(item => item.Id == profileId)
            .Select(item => new ProfileProgressAggregate(
                item.CurrentStreakDays,
                item.RatedMatchCount,
                item.ArenaRating,
                db.TrainingTexts.Count(text => text.OwnerProfileId == profileId),
                db.TextCollections.Count(collection => collection.OwnerProfileId == profileId)))
            .SingleAsync(cancellationToken);

        var attempts = await db.TypingAttempts
            .AsNoTracking()
            .Where(item =>
                item.UserProfileId == profileId &&
                item.Completed &&
                item.Official &&
                item.CompetitionIntegrityEligible)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new AttemptProgressAggregate(
                group.Count(),
                group.Count(item => item.Mode == TrainingMode.Text),
                group.Count(item =>
                    item.Mode == TrainingMode.Words10 ||
                    item.Mode == TrainingMode.Words25 ||
                    item.Mode == TrainingMode.Words50 ||
                    item.Mode == TrainingMode.Words100),
                group.Count(item =>
                    item.Mode == TrainingMode.Sprint15 ||
                    item.Mode == TrainingMode.Sprint30 ||
                    item.Mode == TrainingMode.Sprint60 ||
                    item.Mode == TrainingMode.Sprint120),
                group.Count(item => item.Mode == TrainingMode.WeaknessFocus),
                group.Count(item => item.Accuracy >= 98),
                group.Count(item => item.Accuracy >= 95),
                group.Max(item => item.Accuracy),
                group.Max(item => item.Wpm)))
            .SingleOrDefaultAsync(cancellationToken) ?? AttemptProgressAggregate.Empty;

        var arena = await db.LiveRoomParticipantSummaries
            .AsNoTracking()
            .Where(item =>
                item.UserProfileId == profileId &&
                item.Status == ParticipantStatus.Finished &&
                item.CompetitionEligible)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new PerformanceProgressAggregate(
                group.Count(),
                group.Max(item => item.Accuracy),
                group.Max(item => item.Wpm)))
            .SingleOrDefaultAsync(cancellationToken) ?? PerformanceProgressAggregate.Empty;

        var challenges = await db.ChallengeRoundResults
            .AsNoTracking()
            .Where(item =>
                item.UserProfileId == profileId &&
                item.Status == ParticipantStatus.Finished &&
                item.CompetitionEligible)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new ChallengeProgressAggregate(
                group.Count(),
                group.Max(item => item.Accuracy)))
            .SingleOrDefaultAsync(cancellationToken) ?? ChallengeProgressAggregate.Empty;

        var missions = await db.Missions
            .AsNoTracking()
            .Where(item => item.UserProfileId == profileId && item.Completed)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new MissionProgressAggregate(
                group.Count(),
                group.Count(item => item.Key.StartsWith("weekly-"))))
            .SingleOrDefaultAsync(cancellationToken) ?? MissionProgressAggregate.Empty;

        var unlockedRows = await db.Achievements
            .AsNoTracking()
            .Where(item => item.UserProfileId == profileId)
            .Select(item => new { item.Key, item.UnlockedAt })
            .ToListAsync(cancellationToken);
        var unlocked = unlockedRows.ToDictionary(item => item.Key, item => item.UnlockedAt, StringComparer.Ordinal);

        return new AchievementProgressOverview(
            BuildProgress(profile, attempts, arena, challenges, missions, unlocked),
            unlocked.Count);
    }

    private static IReadOnlyDictionary<string, AchievementProgressState> BuildProgress(
        ProfileProgressAggregate profile,
        AttemptProgressAggregate attempts,
        PerformanceProgressAggregate arena,
        ChallengeProgressAggregate challenges,
        MissionProgressAggregate missions,
        IReadOnlyDictionary<string, DateTimeOffset> unlocked)
    {
        var progress = new Dictionary<string, AchievementProgressState>(StringComparer.Ordinal);
        var bestAccuracy = Math.Max(attempts.BestAccuracy, arena.BestAccuracy);
        var bestWpm = Math.Max(attempts.BestWpm, arena.BestWpm);
        var arenaResults = Math.Max(profile.RatedMatchCount, arena.Completed);

        Add("erster-versuch", attempts.Completed, 1, "Runde");
        Add("training-5-attempts", attempts.Completed, 5, "Runden");
        Add("training-10-attempts", attempts.Completed, 10, "Runden");
        Add("training-25-attempts", attempts.Completed, 25, "Runden");
        Add("training-50-attempts", attempts.Completed, 50, "Runden");
        Add("training-100-attempts", attempts.Completed, 100, "Runden");
        Add("training-text-round", attempts.TextRounds + arena.Completed, 1, "Runde");
        Add("training-words-round", attempts.WordRounds, 1, "Runde");
        Add("training-sprint-round", attempts.SprintRounds, 1, "Runde");
        Add("training-weakness-focus", attempts.WeaknessRounds, 1, "Runde");
        Add("precision-95", bestAccuracy, 95, "%");
        Add("praezise", bestAccuracy, 98, "%");
        Add("precision-100", bestAccuracy, 99.9, "%");
        Add("precision-3x-98", attempts.Precise98, 3, "Runden");
        Add("precision-10x-95", attempts.Precise95, 10, "Runden");
        Add("speed-40", bestWpm, 40, "WPM");
        Add("speed-60", bestWpm, 60, "WPM");
        Add("speed-80", bestWpm, 80, "WPM");
        Add("speed-100", bestWpm, 100, "WPM");
        Add("speed-personal-best", attempts.BestWpm, attempts.BestWpm > 0 ? attempts.BestWpm + 2 : 2, "WPM");
        Add("streak-3", profile.CurrentStreakDays, 3, "Tage");
        Add("streak-7", profile.CurrentStreakDays, 7, "Tage");
        Add("streak-14", profile.CurrentStreakDays, 14, "Tage");
        Add("streak-30", profile.CurrentStreakDays, 30, "Tage");
        Add("arena-first", arenaResults, 1, "Rennen");
        Add("arena-5", arenaResults, 5, "Rennen");
        Add("arena-10", arenaResults, 10, "Rennen");
        Add("arena-rating-1050", profile.ArenaRating, 1050, "Rating-Punkte");
        Add("arena-rating-1100", profile.ArenaRating, 1100, "Rating-Punkte");
        Add("arena-perfect-accuracy", arena.BestAccuracy, 99.9, "%");
        Add("text-author-first", profile.AuthoredTexts, 1, "Text");
        Add("text-author-3", profile.AuthoredTexts, 3, "Texte");
        Add("text-collection-first", profile.Collections, 1, "Sammlung");
        Add("team-first-challenge", challenges.Completed, 1, "Herausforderung");
        Add("team-3-challenges", challenges.Completed, 3, "Herausforderungen");
        Add("team-precise", challenges.BestAccuracy, 98, "%");
        Add("mission-first", missions.Completed, 1, "Mission");
        Add("mission-5", missions.Completed, 5, "Missionen");
        Add("mission-weekly", missions.WeeklyCompleted, 1, "Wochenmission");

        var missing = MotivationCatalog.AchievementDefinitions
            .Where(definition => !progress.ContainsKey(definition.Key))
            .Select(definition => definition.Key)
            .ToArray();
        if (missing.Length > 0 || progress.Count != MotivationCatalog.AchievementDefinitions.Count)
        {
            throw new InvalidOperationException(
                $"Für den Erfolge-Katalog fehlt eine Fortschrittsdefinition: {string.Join(", ", missing)}");
        }

        return progress;

        void Add(string key, double current, double target, string unit)
        {
            if (!double.IsFinite(target) || target <= 0)
            {
                throw new InvalidOperationException($"Das Fortschrittsziel für '{key}' ist ungültig.");
            }

            var isUnlocked = unlocked.TryGetValue(key, out var unlockedAt);
            progress.Add(
                key,
                new AchievementProgressState(
                    key,
                    isUnlocked ? unlockedAt : null,
                    Math.Clamp(double.IsFinite(current) ? current : 0, 0, target),
                    target,
                    unit));
        }
    }

    private sealed record ProfileProgressAggregate(
        int CurrentStreakDays,
        int RatedMatchCount,
        int ArenaRating,
        int AuthoredTexts,
        int Collections);

    private sealed record AttemptProgressAggregate(
        int Completed,
        int TextRounds,
        int WordRounds,
        int SprintRounds,
        int WeaknessRounds,
        int Precise98,
        int Precise95,
        double BestAccuracy,
        double BestWpm)
    {
        public static AttemptProgressAggregate Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record PerformanceProgressAggregate(int Completed, double BestAccuracy, double BestWpm)
    {
        public static PerformanceProgressAggregate Empty { get; } = new(0, 0, 0);
    }

    private sealed record ChallengeProgressAggregate(int Completed, double BestAccuracy)
    {
        public static ChallengeProgressAggregate Empty { get; } = new(0, 0);
    }

    private sealed record MissionProgressAggregate(int Completed, int WeeklyCompleted)
    {
        public static MissionProgressAggregate Empty { get; } = new(0, 0);
    }
}
