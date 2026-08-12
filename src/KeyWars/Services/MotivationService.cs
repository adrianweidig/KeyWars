using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record CoachRecommendation(string Text, TrainingMode Mode, int Minutes);
public enum MissionCadence
{
    Daily,
    Weekly
}

public sealed record MissionDefinition(string Key, MissionCadence Cadence, string Title, string Description, int TargetValue, int XpReward);
public sealed record AchievementDefinition(string Key, string Category, string Title, string Description);
public sealed record LevelProgress(int Level, int ExperiencePoints, int LevelStartXp, int NextLevelXp, int ProgressXp, int RemainingXp, double ProgressPercent);
public sealed record MotivationOutcome(
    int XpDelta,
    int LevelBefore,
    int LevelAfter,
    double ProgressPercent,
    IReadOnlyList<GamificationEvent> Events)
{
    public static MotivationOutcome Empty(UserProfile profile)
    {
        var progress = MotivationService.GetLevelProgress(profile.ExperiencePoints);
        return new MotivationOutcome(0, progress.Level, progress.Level, progress.ProgressPercent, []);
    }
}

internal sealed record ArenaMotivationInput(
    UserProfile Profile,
    string SourceId,
    double Wpm,
    double Accuracy,
    int DurationMilliseconds);

public sealed class MotivationService(KeyWarsDbContext db, TimeProvider timeProvider, GamificationEventWriter gamificationEvents)
{
    private const string SourceAttempt = "attempt";
    private const string SourceArena = "arena";
    private const string SourceMission = "mission";
    private const string SourceAchievement = "achievement";

    private const int MinimumXpCharacters = 20;
    private const int MinimumXpDurationMilliseconds = 5_000;

    public MotivationService(KeyWarsDbContext db, TimeProvider timeProvider)
        : this(db, timeProvider, new GamificationEventWriter(db))
    {
    }

    public static IReadOnlyList<AchievementDefinition> AchievementDefinitions => MotivationCatalog.AchievementDefinitions;

    public async Task<MotivationOutcome> ApplyAttemptAsync(Guid profileId, TypingAttempt attempt, string targetText, CancellationToken cancellationToken = default)
    {
        return await ApplyAttemptAsync(profileId, attempt, [], cancellationToken);
    }

    public async Task<MotivationOutcome> ApplyAttemptAsync(Guid profileId, TypingAttempt attempt, IReadOnlyList<TypingError> errors, CancellationToken cancellationToken = default)
    {
        if (!attempt.Completed || !attempt.Official)
        {
            return await BuildCurrentOutcomeAsync(profileId, cancellationToken);
        }

        var previousBestWpm = await db.TypingAttempts
            .Where(item => item.UserProfileId == profileId && item.Id != attempt.Id && item.Completed && item.Official)
            .Select(item => (double?)item.Wpm)
            .MaxAsync(cancellationToken) ?? 0d;
        var performance = MotivationPerformance.FromAttempt(profileId, attempt, errors);
        var xp = CalculateXp(performance, previousBestWpm, attempt.TrainingTextId is not null);
        var outcome = await ApplyPerformanceAsync(performance, xp, previousBestWpm, cancellationToken);
        attempt.ExperienceAwarded = true;

        return outcome;
    }

    public async Task<MotivationOutcome> ApplyArenaResultAsync(
        Guid profileId,
        string sourceId,
        double wpm,
        double accuracy,
        int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new InvalidOperationException("Die Arena-Quelle ist ungültig.");
        }

        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId, cancellationToken);
        var outcomes = await ApplyArenaResultsAsync(
            [new ArenaMotivationInput(profile, sourceId, wpm, accuracy, durationMilliseconds)],
            cancellationToken);
        return outcomes[profileId];
    }

    internal async Task<IReadOnlyDictionary<Guid, MotivationOutcome>> ApplyArenaResultsAsync(
        IReadOnlyCollection<ArenaMotivationInput> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return new Dictionary<Guid, MotivationOutcome>();
        }

        var items = inputs
            .Select(input => new ArenaBatchItem(
                input.Profile,
                CreateArenaPerformance(input),
                0))
            .Select(item => item with { Xp = CalculateXp(item.Performance, 0d, false) })
            .ToList();
        if (items.Select(item => item.Profile.Id).Distinct().Count() != items.Count)
        {
            throw new InvalidOperationException("Ein Arena-Batch darf jedes Profil nur einmal enthalten.");
        }

        var positiveItems = items.Where(item => item.Xp > 0).ToList();
        var knownLedgerEntries = await LoadArenaLedgerEntriesAsync(positiveItems, cancellationToken);
        var activeItems = positiveItems
            .Where(item => !knownLedgerEntries.Contains(new RewardLedgerIdentity(item.Profile.Id, SourceArena, item.Performance.SourceId)))
            .ToList();
        var outcomes = new Dictionary<Guid, MotivationOutcome>(items.Count);
        foreach (var item in items.Except(activeItems))
        {
            var level = CalculateLevel(item.Profile.ExperiencePoints);
            item.Profile.Level = level;
            outcomes[item.Profile.Id] = BuildOutcome(item.Profile, level, 0, []);
        }

        if (activeItems.Count == 0)
        {
            return outcomes;
        }

        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var activeProfileIds = activeItems.Select(item => item.Profile.Id).ToArray();
        var missionLoad = await LoadOrCreateCurrentMissionsAsync(activeProfileIds, today, cancellationToken);
        var missionsByProfile = missionLoad.MissionsByProfile;
        var missionSourceIds = missionsByProfile.Values
            .SelectMany(missions => missions)
            .Select(mission => mission.Id.ToString("N"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await AddKnownMissionLedgerEntriesAsync(activeProfileIds, missionSourceIds, knownLedgerEntries, cancellationToken);
        var achievementBaselines = await LoadArenaAchievementBaselinesAsync(activeProfileIds, cancellationToken);
        var knownEvents = await LoadKnownArenaEventsAsync(
            activeProfileIds,
            activeItems
                .Select(item => GamificationEventWriter.NormalizeSourceId(item.Performance.SourceId))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            missionSourceIds,
            cancellationToken);

        foreach (var item in activeItems)
        {
            var profile = item.Profile;
            var performance = item.Performance;
            var levelBefore = CalculateLevel(profile.ExperiencePoints);
            profile.Level = levelBefore;
            var awardedXp = AwardXpPrepared(profile, SourceArena, performance.SourceId, item.Xp, now, knownLedgerEntries);
            profile.CurrentStreakDays = CalculateStreak(profile.LastActivityDate, today, profile.CurrentStreakDays);
            profile.LastActivityDate = today;

            var completedMissions = ApplyMissionProgress(profile, performance, missionsByProfile[profile.Id], now, knownLedgerEntries);
            profile.Level = CalculateLevel(profile.ExperiencePoints);
            var baseline = achievementBaselines.GetValueOrDefault(profile.Id) ?? ArenaAchievementBaseline.Empty;
            var unlockedAchievements = UnlockAchievements(
                profile,
                performance,
                0d,
                now,
                BuildArenaAchievementSnapshot(profile.Id, baseline));
            var levelAfter = profile.Level;
            var events = new List<GamificationEvent>();
            foreach (var draft in BuildEventDrafts(performance, awardedXp, completedMissions, unlockedAchievements, levelBefore, levelAfter))
            {
                gamificationEvents.AddPrepared(events, profile, draft, now, knownEvents);
            }

            outcomes[profile.Id] = BuildOutcome(
                profile,
                levelBefore,
                awardedXp + completedMissions.Sum(item => item.XpDelta),
                events);
        }

        return outcomes;
    }

    public async Task EnsureDailyMissionsAsync(Guid profileId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await EnsureCurrentMissionsAsync(profileId, date, cancellationToken);
    }

    public async Task EnsureCurrentMissionsAsync(Guid profileId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var result = await LoadOrCreateCurrentMissionsAsync([profileId], date, cancellationToken);
        if (result.AddedCount > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<CoachRecommendation> RecommendAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var attempts = (await db.TypingAttempts
            .Where(item => item.UserProfileId == profileId && item.Completed)
            .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .Take(10)
            .ToList();

        if (attempts.Count == 0)
        {
            return new CoachRecommendation("Starte mit einem ruhigen 60-Sekunden-Test und achte zuerst auf saubere Anschläge.", TrainingMode.Sprint60, 1);
        }

        var averageAccuracy = attempts.Average(item => item.Accuracy);
        if (averageAccuracy < 94)
        {
            return new CoachRecommendation("Deine Genauigkeit liegt zuletzt unter 94 %. Eine kurze Präzisionsübung ist heute sinnvoll.", TrainingMode.Precision, 3);
        }

        var observations = await db.WeaknessObservations.Where(item => item.UserProfileId == profileId && item.Attempts >= 5).ToListAsync(cancellationToken);
        var weak = observations.OrderByDescending(item => (double)item.Errors / Math.Max(1, item.Attempts)).FirstOrDefault();
        if (weak is not null && weak.Errors > 0)
        {
            return new CoachRecommendation($"Bei „{weak.Pattern}“ treten aktuell überdurchschnittlich viele Fehler auf. Starte eine Fehlerfokus-Runde.", TrainingMode.WeaknessFocus, 3);
        }

        return new CoachRecommendation("Dein Verlauf ist stabil. Ein klassisches Live-Rennen oder ein 60-Sekunden-Sprint setzt einen guten neuen Reiz.", TrainingMode.Sprint60, 1);
    }

    public static LevelProgress GetLevelProgress(int experiencePoints)
    {
        var level = CalculateLevel(experiencePoints);
        var start = XpRequiredForLevel(level);
        var next = XpRequiredForLevel(level + 1);
        var progress = Math.Max(0, experiencePoints - start);
        var span = Math.Max(1, next - start);
        var remaining = Math.Max(0, next - experiencePoints);
        return new LevelProgress(level, experiencePoints, start, next, progress, remaining, Math.Clamp(progress * 100d / span, 0d, 100d));
    }

    public static int CalculateLevel(int experiencePoints)
    {
        var level = 1;
        while (experiencePoints >= XpRequiredForLevel(level + 1))
        {
            level++;
        }

        return level;
    }

    private async Task<MotivationOutcome> ApplyPerformanceAsync(MotivationPerformance performance, int xp, double previousBestWpm, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == performance.ProfileId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var levelBefore = CalculateLevel(profile.ExperiencePoints);
        profile.Level = levelBefore;

        if (xp <= 0)
        {
            profile.Level = CalculateLevel(profile.ExperiencePoints);
            UpdateWeaknesses(performance, now);
            return BuildOutcome(profile, levelBefore, 0, []);
        }

        var awardedXp = await AwardXpAsync(profile, performance.Source, performance.SourceId, xp, now, cancellationToken);
        if (awardedXp <= 0)
        {
            profile.Level = CalculateLevel(profile.ExperiencePoints);
            return BuildOutcome(profile, levelBefore, 0, []);
        }

        profile.CurrentStreakDays = CalculateStreak(profile.LastActivityDate, today, profile.CurrentStreakDays);
        profile.LastActivityDate = today;
        if (performance.CountsForSeason)
        {
            profile.SeasonPoints += Math.Max(1, (int)Math.Round(performance.Wpm / 10));
        }

        var missionLoad = await LoadOrCreateCurrentMissionsAsync([profile.Id], today, cancellationToken);
        var missions = missionLoad.MissionsByProfile[profile.Id];
        var completedMissions = new List<(Mission Mission, int XpDelta)>();
        foreach (var mission in missions)
        {
            var delta = MissionProgressDelta(mission, performance);
            if (delta <= 0)
            {
                continue;
            }

            var wasCompleted = mission.Completed;
            mission.CurrentValue = Math.Min(mission.TargetValue, mission.CurrentValue + delta);
            mission.Completed = mission.CurrentValue >= mission.TargetValue;
            if (!wasCompleted && mission.Completed)
            {
                var missionXp = await AwardXpAsync(profile, SourceMission, mission.Id.ToString("N"), mission.XpReward, now, cancellationToken);
                if (missionXp > 0)
                {
                    completedMissions.Add((mission, missionXp));
                }
            }
        }

        profile.Level = CalculateLevel(profile.ExperiencePoints);
        var unlockedAchievements = await UnlockAchievementsAsync(profile, performance, previousBestWpm, now, cancellationToken);
        UpdateWeaknesses(performance, now);
        var levelAfter = profile.Level;
        var events = new List<GamificationEvent>();
        foreach (var draft in BuildEventDrafts(
                     performance,
                     awardedXp,
                     completedMissions,
                     unlockedAchievements,
                     levelBefore,
                     levelAfter,
                     previousBestWpm))
        {
            await gamificationEvents.AddAsync(events, profile, draft, now, cancellationToken);
        }

        return BuildOutcome(profile, levelBefore, awardedXp + completedMissions.Sum(item => item.XpDelta), events);
    }

    private async Task<MotivationOutcome> BuildCurrentOutcomeAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId, cancellationToken);
        return MotivationOutcome.Empty(profile);
    }

    private static MotivationOutcome BuildOutcome(UserProfile profile, int levelBefore, int xpDelta, IReadOnlyList<GamificationEvent> events)
    {
        var progress = GetLevelProgress(profile.ExperiencePoints);
        return new MotivationOutcome(xpDelta, levelBefore, progress.Level, progress.ProgressPercent, events);
    }

    private static IReadOnlyList<GamificationEventDraft> BuildEventDrafts(
        MotivationPerformance performance,
        int awardedXp,
        IReadOnlyList<(Mission Mission, int XpDelta)> completedMissions,
        IReadOnlyList<AchievementDefinition> unlockedAchievements,
        int levelBefore,
        int levelAfter,
        double previousBestWpm = 0d)
    {
        var drafts = new List<GamificationEventDraft>
        {
            new(
                GamificationEventType.XpAwarded,
                "xp-awarded",
                $"+{awardedXp} XP",
                "Gültige Runde abgeschlossen.",
                awardedXp,
                levelBefore,
                levelAfter,
                GamificationRarity.Common,
                performance.Source,
                performance.SourceId)
        };
        if (performance.Source == SourceArena)
        {
            drafts.Add(new GamificationEventDraft(
                GamificationEventType.ArenaResult,
                "arena-result",
                "Arena-Rennen gewertet",
                $"{performance.Wpm:0.0} WPM bei {performance.Accuracy:0.0} % Genauigkeit.",
                0,
                levelBefore,
                levelAfter,
                performance.Accuracy >= 99.9 ? GamificationRarity.Rare : GamificationRarity.Common,
                performance.Source,
                performance.SourceId));
        }

        if (previousBestWpm > 0 && performance.Wpm >= previousBestWpm + 2)
        {
            drafts.Add(new GamificationEventDraft(
                GamificationEventType.PersonalBest,
                "personal-best",
                "Neue Bestleistung",
                $"{performance.Wpm:0.0} WPM verbessern deine bisherige Marke von {previousBestWpm:0.0} WPM.",
                0,
                levelBefore,
                levelAfter,
                GamificationRarity.Rare,
                performance.Source,
                performance.SourceId));
        }

        drafts.AddRange(completedMissions.Select(item => new GamificationEventDraft(
            GamificationEventType.MissionCompleted,
            "mission-completed",
            item.Mission.Title,
            item.Mission.Description,
            item.XpDelta,
            levelBefore,
            levelAfter,
            RarityForMission(item.Mission),
            SourceMission,
            item.Mission.Id.ToString("N"))));
        drafts.AddRange(unlockedAchievements.Select(achievement => new GamificationEventDraft(
            GamificationEventType.AchievementUnlocked,
            "achievement-unlocked",
            achievement.Title,
            achievement.Description,
            0,
            levelBefore,
            levelAfter,
            RarityForAchievement(achievement),
            SourceAchievement,
            achievement.Key)));
        if (levelAfter > levelBefore)
        {
            drafts.Add(new GamificationEventDraft(
                GamificationEventType.LevelUp,
                $"level-up-{levelAfter}",
                $"Level {levelAfter} erreicht",
                $"Du bist von Level {levelBefore} auf Level {levelAfter} gestiegen.",
                0,
                levelBefore,
                levelAfter,
                RarityForLevel(levelAfter),
                performance.Source,
                performance.SourceId));
        }

        return drafts;
    }

    private static GamificationRarity RarityForMission(Mission mission) =>
        mission.Key.StartsWith("weekly-", StringComparison.Ordinal) ? GamificationRarity.Rare : GamificationRarity.Common;

    private static GamificationRarity RarityForLevel(int level) =>
        level % 10 == 0 ? GamificationRarity.Epic : GamificationRarity.Rare;

    private static GamificationRarity RarityForAchievement(AchievementDefinition achievement) => achievement.Key switch
    {
        "speed-100" or "streak-30" or "training-100-attempts" => GamificationRarity.Epic,
        "speed-80" or "streak-14" or "arena-10" or "precision-100" or "training-50-attempts" => GamificationRarity.Rare,
        _ when achievement.Category is "Arena" or "Missionen" => GamificationRarity.Rare,
        _ => GamificationRarity.Common
    };

    private async Task<MissionLoadResult> LoadOrCreateCurrentMissionsAsync(
        IReadOnlyCollection<Guid> profileIds,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0)
        {
            return new MissionLoadResult(new Dictionary<Guid, List<Mission>>(), 0);
        }

        var distinctProfileIds = profileIds.Distinct().ToArray();
        var weekStart = GetWeekStart(date);
        var activeDates = new[] { date, weekStart }.Distinct().ToArray();
        var missions = await db.Missions
            .Where(item => distinctProfileIds.Contains(item.UserProfileId) && activeDates.Contains(item.MissionDate))
            .ToListAsync(cancellationToken);
        foreach (var local in db.Missions.Local.Where(item =>
                     distinctProfileIds.Contains(item.UserProfileId) &&
                     activeDates.Contains(item.MissionDate)))
        {
            if (missions.All(item => item.Id != local.Id))
            {
                missions.Add(local);
            }
        }

        var existing = missions
            .Select(item => (item.UserProfileId, item.MissionDate, item.Key))
            .ToHashSet();
        var addedCount = 0;
        foreach (var profileId in distinctProfileIds)
        {
            foreach (var definition in MotivationCatalog.MissionDefinitions)
            {
                var missionDate = definition.Cadence == MissionCadence.Daily ? date : weekStart;
                if (!existing.Add((profileId, missionDate, definition.Key)))
                {
                    continue;
                }

                var mission = new Mission
                {
                    UserProfileId = profileId,
                    MissionDate = missionDate,
                    Key = definition.Key,
                    Title = definition.Title,
                    Description = definition.Description,
                    TargetValue = definition.TargetValue,
                    XpReward = definition.XpReward
                };
                db.Missions.Add(mission);
                missions.Add(mission);
                addedCount++;
            }
        }

        return new MissionLoadResult(
            missions
                .GroupBy(item => item.UserProfileId)
                .ToDictionary(group => group.Key, group => group.ToList()),
            addedCount);
    }

    private static MotivationPerformance CreateArenaPerformance(ArenaMotivationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.SourceId))
        {
            throw new InvalidOperationException("Die Arena-Quelle ist ungültig.");
        }

        var totalCharacters = Math.Max(
            MinimumXpCharacters,
            (int)Math.Round(input.Wpm * input.DurationMilliseconds / 12_000d));
        return new MotivationPerformance(
            input.Profile.Id,
            SourceArena,
            input.SourceId.Length <= 80 ? input.SourceId : input.SourceId[..80],
            null,
            TrainingMode.Text,
            input.Wpm,
            input.Accuracy,
            100,
            input.DurationMilliseconds,
            totalCharacters,
            totalCharacters,
            true,
            true,
            null,
            false,
            []);
    }

    private static int CalculateXp(MotivationPerformance performance, double previousBestWpm, bool demandingText)
    {
        if (!performance.Completed ||
            !performance.Official ||
            Math.Max(performance.CorrectCharacters, performance.TotalCharacters) < MinimumXpCharacters ||
            performance.DurationMilliseconds < MinimumXpDurationMilliseconds)
        {
            return 0;
        }

        var baseXp = Math.Clamp((int)Math.Round(performance.Wpm), 5, 80);
        var accuracyBonus = performance.Accuracy >= 99.9 ? 25 : performance.Accuracy >= 98 ? 20 : performance.Accuracy >= 95 ? 10 : 0;
        var improvementBonus = previousBestWpm > 0 && performance.Wpm >= previousBestWpm + 5
            ? 15
            : previousBestWpm > 0 && performance.Wpm >= previousBestWpm + 2
                ? 8
                : 0;
        var textBonus = demandingText && performance.TotalCharacters >= 120 ? 10 : 0;
        var arenaBonus = performance.Source == SourceArena ? 10 : 0;
        return Math.Min(140, baseXp + accuracyBonus + improvementBonus + textBonus + arenaBonus);
    }

    private static int XpRequiredForLevel(int level)
    {
        var completedLevels = Math.Max(0, level - 1);
        return (200 * completedLevels) + (25 * completedLevels * (completedLevels - 1));
    }

    private async Task<int> AwardXpAsync(UserProfile profile, string source, string sourceId, int xp, DateTimeOffset awardedAt, CancellationToken cancellationToken)
    {
        if (xp <= 0)
        {
            return 0;
        }

        var localExists = db.RewardLedgerEntries.Local.Any(item =>
            item.UserProfileId == profile.Id &&
            item.Source == source &&
            item.SourceId == sourceId);
        var exists = localExists || await db.RewardLedgerEntries.AnyAsync(item =>
            item.UserProfileId == profile.Id &&
            item.Source == source &&
            item.SourceId == sourceId, cancellationToken);
        if (exists)
        {
            return 0;
        }

        db.RewardLedgerEntries.Add(new RewardLedgerEntry
        {
            UserProfileId = profile.Id,
            Source = source,
            SourceId = sourceId,
            Xp = xp,
            AwardedAt = awardedAt
        });
        profile.ExperiencePoints += xp;
        return xp;
    }

    private async Task<HashSet<RewardLedgerIdentity>> LoadArenaLedgerEntriesAsync(
        IReadOnlyCollection<ArenaBatchItem> items,
        CancellationToken cancellationToken)
    {
        var known = new HashSet<RewardLedgerIdentity>();
        if (items.Count > 0)
        {
            var profileIds = items.Select(item => item.Profile.Id).Distinct().ToArray();
            var sourceIds = items.Select(item => item.Performance.SourceId).Distinct(StringComparer.Ordinal).ToArray();
            var stored = await db.RewardLedgerEntries
                .AsNoTracking()
                .Where(item =>
                    profileIds.Contains(item.UserProfileId) &&
                    item.Source == SourceArena &&
                    sourceIds.Contains(item.SourceId))
                .Select(item => new { item.UserProfileId, item.Source, item.SourceId })
                .ToListAsync(cancellationToken);
            known.UnionWith(stored.Select(item => new RewardLedgerIdentity(item.UserProfileId, item.Source, item.SourceId)));
        }

        known.UnionWith(db.RewardLedgerEntries.Local.Select(item =>
            new RewardLedgerIdentity(item.UserProfileId, item.Source, item.SourceId)));
        return known;
    }

    private async Task AddKnownMissionLedgerEntriesAsync(
        IReadOnlyCollection<Guid> profileIds,
        IReadOnlyCollection<string> sourceIds,
        ISet<RewardLedgerIdentity> known,
        CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0 || sourceIds.Count == 0)
        {
            return;
        }

        var stored = await db.RewardLedgerEntries
            .AsNoTracking()
            .Where(item =>
                profileIds.Contains(item.UserProfileId) &&
                item.Source == SourceMission &&
                sourceIds.Contains(item.SourceId))
            .Select(item => new { item.UserProfileId, item.Source, item.SourceId })
            .ToListAsync(cancellationToken);
        known.UnionWith(stored.Select(item => new RewardLedgerIdentity(item.UserProfileId, item.Source, item.SourceId)));
        known.UnionWith(db.RewardLedgerEntries.Local.Select(item =>
            new RewardLedgerIdentity(item.UserProfileId, item.Source, item.SourceId)));
    }

    private int AwardXpPrepared(
        UserProfile profile,
        string source,
        string sourceId,
        int xp,
        DateTimeOffset awardedAt,
        ISet<RewardLedgerIdentity> known)
    {
        if (xp <= 0 || !known.Add(new RewardLedgerIdentity(profile.Id, source, sourceId)))
        {
            return 0;
        }

        db.RewardLedgerEntries.Add(new RewardLedgerEntry
        {
            UserProfileId = profile.Id,
            Source = source,
            SourceId = sourceId,
            Xp = xp,
            AwardedAt = awardedAt
        });
        profile.ExperiencePoints += xp;
        return xp;
    }

    private List<(Mission Mission, int XpDelta)> ApplyMissionProgress(
        UserProfile profile,
        MotivationPerformance performance,
        IReadOnlyList<Mission> missions,
        DateTimeOffset now,
        ISet<RewardLedgerIdentity> knownLedgerEntries)
    {
        var completedMissions = new List<(Mission Mission, int XpDelta)>();
        foreach (var mission in missions)
        {
            var delta = MissionProgressDelta(mission, performance);
            if (delta <= 0)
            {
                continue;
            }

            var wasCompleted = mission.Completed;
            mission.CurrentValue = Math.Min(mission.TargetValue, mission.CurrentValue + delta);
            mission.Completed = mission.CurrentValue >= mission.TargetValue;
            if (!wasCompleted && mission.Completed)
            {
                var missionXp = AwardXpPrepared(
                    profile,
                    SourceMission,
                    mission.Id.ToString("N"),
                    mission.XpReward,
                    now,
                    knownLedgerEntries);
                if (missionXp > 0)
                {
                    completedMissions.Add((mission, missionXp));
                }
            }
        }

        return completedMissions;
    }

    private async Task<IReadOnlyList<AchievementDefinition>> UnlockAchievementsAsync(UserProfile profile, MotivationPerformance performance, double previousBestWpm, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var completedAttemptsQuery = db.TypingAttempts.Where(item =>
            item.UserProfileId == profile.Id &&
            item.Completed &&
            item.Official);
        if (performance.AttemptId is { } attemptId)
        {
            completedAttemptsQuery = completedAttemptsQuery.Where(item => item.Id != attemptId);
        }

        var completedAttempts = await completedAttemptsQuery.CountAsync(cancellationToken);
        if (performance.Source == SourceAttempt)
        {
            completedAttempts++;
        }

        var precise98Query = db.TypingAttempts.Where(item =>
            item.UserProfileId == profile.Id &&
            item.Completed &&
            item.Official &&
            item.Accuracy >= 98);
        if (performance.AttemptId is { } preciseAttemptId)
        {
            precise98Query = precise98Query.Where(item => item.Id != preciseAttemptId);
        }

        var precise98Attempts = await precise98Query.CountAsync(cancellationToken);
        if (performance.Source == SourceAttempt && performance.Accuracy >= 98)
        {
            precise98Attempts++;
        }

        var precise95Query = db.TypingAttempts.Where(item =>
            item.UserProfileId == profile.Id &&
            item.Completed &&
            item.Official &&
            item.Accuracy >= 95);
        if (performance.AttemptId is { } precise95AttemptId)
        {
            precise95Query = precise95Query.Where(item => item.Id != precise95AttemptId);
        }

        var precise95Attempts = await precise95Query.CountAsync(cancellationToken);
        if (performance.Source == SourceAttempt && performance.Accuracy >= 95)
        {
            precise95Attempts++;
        }

        var authoredTexts = await db.TrainingTexts.CountAsync(item => item.OwnerProfileId == profile.Id, cancellationToken);
        var collections = await db.TextCollections.CountAsync(item => item.OwnerProfileId == profile.Id, cancellationToken);
        var challengeResults = await db.ChallengeRoundResults.CountAsync(item => item.UserProfileId == profile.Id && item.Status == ParticipantStatus.Finished, cancellationToken);
        var arenaResults = await db.LiveRoomParticipantSummaries.CountAsync(item => item.UserProfileId == profile.Id && item.Status == ParticipantStatus.Finished, cancellationToken);
        if (performance.Source == SourceArena)
        {
            arenaResults++;
        }

        var completedMissionIds = new HashSet<Guid>(
            await db.Missions.Where(item => item.UserProfileId == profile.Id && item.Completed).Select(item => item.Id).ToListAsync(cancellationToken));
        foreach (var mission in db.Missions.Local.Where(item => item.UserProfileId == profile.Id && item.Completed))
        {
            completedMissionIds.Add(mission.Id);
        }

        var weeklyMissionCompleted = await db.Missions.AnyAsync(item =>
            item.UserProfileId == profile.Id &&
            item.Completed &&
            item.Key.StartsWith("weekly-"), cancellationToken);
        weeklyMissionCompleted = weeklyMissionCompleted || db.Missions.Local.Any(item =>
            item.UserProfileId == profile.Id &&
            item.Completed &&
            item.Key.StartsWith("weekly-", StringComparison.Ordinal));
        var existing = new HashSet<string>(
            await db.Achievements.Where(item => item.UserProfileId == profile.Id).Select(item => item.Key).ToListAsync(cancellationToken),
            StringComparer.Ordinal);
        foreach (var local in db.Achievements.Local.Where(item => item.UserProfileId == profile.Id))
        {
            existing.Add(local.Key);
        }

        return UnlockAchievements(
            profile,
            performance,
            previousBestWpm,
            now,
            new AchievementSnapshot(
                completedAttempts,
                precise98Attempts,
                precise95Attempts,
                authoredTexts,
                collections,
                challengeResults,
                arenaResults,
                completedMissionIds.Count,
                weeklyMissionCompleted,
                existing));
    }

    private IReadOnlyList<AchievementDefinition> UnlockAchievements(
        UserProfile profile,
        MotivationPerformance performance,
        double previousBestWpm,
        DateTimeOffset now,
        AchievementSnapshot snapshot)
    {
        var unlock = BuildAchievementKeys(profile, performance, previousBestWpm, snapshot);
        var unlockedDefinitions = new List<AchievementDefinition>();
        foreach (var definition in AchievementDefinitions.Where(item => unlock.Contains(item.Key)))
        {
            if (!snapshot.ExistingAchievementKeys.Contains(definition.Key))
            {
                db.Achievements.Add(new Achievement
                {
                    UserProfileId = profile.Id,
                    Key = definition.Key,
                    Title = definition.Title,
                    Description = definition.Description,
                    UnlockedAt = now
                });
                snapshot.ExistingAchievementKeys.Add(definition.Key);
                unlockedDefinitions.Add(definition);
            }
        }

        return unlockedDefinitions;
    }

    private static HashSet<string> BuildAchievementKeys(
        UserProfile profile,
        MotivationPerformance performance,
        double previousBestWpm,
        AchievementSnapshot snapshot)
    {
        var unlock = new HashSet<string>(StringComparer.Ordinal);
        AddThresholds(unlock, snapshot.CompletedAttempts, [
            (1, "erster-versuch"),
            (5, "training-5-attempts"),
            (10, "training-10-attempts"),
            (25, "training-25-attempts"),
            (50, "training-50-attempts"),
            (100, "training-100-attempts")
        ]);
        if (performance.Mode == TrainingMode.Text)
        {
            unlock.Add("training-text-round");
        }

        if (performance.Mode is TrainingMode.Words10 or TrainingMode.Words25 or TrainingMode.Words50 or TrainingMode.Words100)
        {
            unlock.Add("training-words-round");
        }

        if (performance.Mode is TrainingMode.Sprint15 or TrainingMode.Sprint30 or TrainingMode.Sprint60 or TrainingMode.Sprint120)
        {
            unlock.Add("training-sprint-round");
        }

        if (performance.Mode == TrainingMode.WeaknessFocus)
        {
            unlock.Add("training-weakness-focus");
        }

        if (performance.Accuracy >= 95)
        {
            unlock.Add("precision-95");
        }

        if (performance.Accuracy >= 98)
        {
            unlock.Add("praezise");
        }

        if (performance.Accuracy >= 99.9)
        {
            unlock.Add("precision-100");
        }

        AddThresholds(unlock, snapshot.Precise98Attempts, [(3, "precision-3x-98")]);
        AddThresholds(unlock, snapshot.Precise95Attempts, [(10, "precision-10x-95")]);
        AddSpeedThresholds(unlock, Math.Max(previousBestWpm, performance.Wpm));
        if (previousBestWpm > 0 && performance.Wpm >= previousBestWpm + 2)
        {
            unlock.Add("speed-personal-best");
        }

        AddThresholds(unlock, profile.CurrentStreakDays, [
            (3, "streak-3"),
            (7, "streak-7"),
            (14, "streak-14"),
            (30, "streak-30")
        ]);
        AddThresholds(unlock, Math.Max(profile.RatedMatchCount, snapshot.ArenaResults), [
            (1, "arena-first"),
            (5, "arena-5"),
            (10, "arena-10")
        ]);
        if (profile.ArenaRating >= 1050)
        {
            unlock.Add("arena-rating-1050");
        }

        if (profile.ArenaRating >= 1100)
        {
            unlock.Add("arena-rating-1100");
        }

        if (performance.Source == SourceArena && performance.Accuracy >= 99.9)
        {
            unlock.Add("arena-perfect-accuracy");
        }

        AddThresholds(unlock, snapshot.AuthoredTexts, [
            (1, "text-author-first"),
            (3, "text-author-3")
        ]);
        if (snapshot.Collections >= 1)
        {
            unlock.Add("text-collection-first");
        }

        AddThresholds(unlock, snapshot.ChallengeResults, [
            (1, "team-first-challenge"),
            (3, "team-3-challenges")
        ]);
        if (snapshot.ChallengeResults >= 1 && performance.Accuracy >= 98)
        {
            unlock.Add("team-precise");
        }

        AddThresholds(unlock, snapshot.CompletedMissions, [
            (1, "mission-first"),
            (5, "mission-5")
        ]);
        if (snapshot.WeeklyMissionCompleted)
        {
            unlock.Add("mission-weekly");
        }

        return unlock;
    }

    private async Task<Dictionary<Guid, ArenaAchievementBaseline>> LoadArenaAchievementBaselinesAsync(
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        var ids = profileIds.Distinct().ToArray();
        var attemptRows = await db.TypingAttempts
            .AsNoTracking()
            .Where(item => ids.Contains(item.UserProfileId) && item.Completed && item.Official)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new
            {
                ProfileId = group.Key,
                Completed = group.Count(),
                Precise98 = group.Count(item => item.Accuracy >= 98),
                Precise95 = group.Count(item => item.Accuracy >= 95)
            })
            .ToListAsync(cancellationToken);
        var authoredTextRows = await db.TrainingTexts
            .AsNoTracking()
            .Where(item => item.OwnerProfileId != null && ids.Contains(item.OwnerProfileId.Value))
            .GroupBy(item => item.OwnerProfileId!.Value)
            .Select(group => new { ProfileId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var collectionRows = await db.TextCollections
            .AsNoTracking()
            .Where(item => ids.Contains(item.OwnerProfileId))
            .GroupBy(item => item.OwnerProfileId)
            .Select(group => new { ProfileId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var challengeRows = await db.ChallengeRoundResults
            .AsNoTracking()
            .Where(item => ids.Contains(item.UserProfileId) && item.Status == ParticipantStatus.Finished)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new { ProfileId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var arenaRows = await db.LiveRoomParticipantSummaries
            .AsNoTracking()
            .Where(item => ids.Contains(item.UserProfileId) && item.Status == ParticipantStatus.Finished)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new { ProfileId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var missionRows = await db.Missions
            .AsNoTracking()
            .Where(item => ids.Contains(item.UserProfileId) && item.Completed)
            .GroupBy(item => item.UserProfileId)
            .Select(group => new
            {
                ProfileId = group.Key,
                Count = group.Count(),
                WeeklyCount = group.Count(item => item.Key.StartsWith("weekly-"))
            })
            .ToListAsync(cancellationToken);
        var achievementRows = await db.Achievements
            .AsNoTracking()
            .Where(item => ids.Contains(item.UserProfileId))
            .Select(item => new { item.UserProfileId, item.Key })
            .ToListAsync(cancellationToken);

        var attemptsByProfile = attemptRows.ToDictionary(item => item.ProfileId);
        var authoredTextsByProfile = authoredTextRows.ToDictionary(item => item.ProfileId, item => item.Count);
        var collectionsByProfile = collectionRows.ToDictionary(item => item.ProfileId, item => item.Count);
        var challengesByProfile = challengeRows.ToDictionary(item => item.ProfileId, item => item.Count);
        var arenasByProfile = arenaRows.ToDictionary(item => item.ProfileId, item => item.Count);
        var missionsByProfile = missionRows.ToDictionary(
            item => item.ProfileId,
            item => new CompletedMissionBaseline(item.Count, item.WeeklyCount > 0));
        var achievementsByProfile = achievementRows
            .GroupBy(item => item.UserProfileId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Key).ToHashSet(StringComparer.Ordinal));

        return ids.ToDictionary(
            profileId => profileId,
            profileId =>
            {
                attemptsByProfile.TryGetValue(profileId, out var attempts);
                var missions = missionsByProfile.GetValueOrDefault(profileId) ?? CompletedMissionBaseline.Empty;
                return new ArenaAchievementBaseline(
                    attempts?.Completed ?? 0,
                    attempts?.Precise98 ?? 0,
                    attempts?.Precise95 ?? 0,
                    authoredTextsByProfile.GetValueOrDefault(profileId),
                    collectionsByProfile.GetValueOrDefault(profileId),
                    challengesByProfile.GetValueOrDefault(profileId),
                    arenasByProfile.GetValueOrDefault(profileId),
                    missions.Count,
                    missions.WeeklyCompleted,
                    achievementsByProfile.GetValueOrDefault(profileId) ?? new HashSet<string>(StringComparer.Ordinal));
            });
    }

    private AchievementSnapshot BuildArenaAchievementSnapshot(Guid profileId, ArenaAchievementBaseline baseline)
    {
        var newlyCompletedMissions = db.ChangeTracker.Entries<Mission>()
            .Where(entry =>
                entry.Entity.UserProfileId == profileId &&
                entry.Entity.Completed &&
                (entry.State == EntityState.Added ||
                 (entry.State == EntityState.Modified &&
                  !entry.OriginalValues.GetValue<bool>(nameof(Mission.Completed)))))
            .Select(entry => entry.Entity)
            .ToList();
        var weeklyMissionCompleted = baseline.WeeklyMissionCompleted;
        weeklyMissionCompleted |= newlyCompletedMissions.Any(mission =>
            mission.Key.StartsWith("weekly-", StringComparison.Ordinal));

        var existingAchievements = baseline.ExistingAchievementKeys.ToHashSet(StringComparer.Ordinal);
        existingAchievements.UnionWith(db.Achievements.Local
            .Where(item => item.UserProfileId == profileId)
            .Select(item => item.Key));
        return new AchievementSnapshot(
            baseline.CompletedAttempts,
            baseline.Precise98Attempts,
            baseline.Precise95Attempts,
            baseline.AuthoredTexts,
            baseline.Collections,
            baseline.ChallengeResults,
            baseline.ArenaResults + 1,
            baseline.CompletedMissions + newlyCompletedMissions.Count,
            weeklyMissionCompleted,
            existingAchievements);
    }

    private async Task<HashSet<GamificationEventIdentity>> LoadKnownArenaEventsAsync(
        IReadOnlyCollection<Guid> profileIds,
        IReadOnlyCollection<string> arenaSourceIds,
        IReadOnlyCollection<string> missionSourceIds,
        CancellationToken cancellationToken)
    {
        var stored = await db.GamificationEvents
            .AsNoTracking()
            .Where(item =>
                profileIds.Contains(item.UserProfileId) &&
                ((item.Source == SourceArena && arenaSourceIds.Contains(item.SourceId)) ||
                 (item.Source == SourceMission && missionSourceIds.Contains(item.SourceId)) ||
                 item.Source == SourceAchievement))
            .Select(item => new { item.UserProfileId, item.Source, item.SourceId, item.EventKey })
            .ToListAsync(cancellationToken);
        var known = stored
            .Select(item => new GamificationEventIdentity(item.UserProfileId, item.Source, item.SourceId, item.EventKey))
            .ToHashSet();
        known.UnionWith(db.GamificationEvents.Local.Select(item =>
            new GamificationEventIdentity(item.UserProfileId, item.Source, item.SourceId, item.EventKey)));
        return known;
    }

    private void UpdateWeaknesses(MotivationPerformance performance, DateTimeOffset now)
    {
        var patterns = ExtractErrorPatterns(performance.Errors).Distinct(StringComparer.Ordinal).Take(80).ToList();
        if (patterns.Count == 0)
        {
            return;
        }

        var observations = db.WeaknessObservations
            .Where(item => item.UserProfileId == performance.ProfileId && patterns.Contains(item.Pattern))
            .ToDictionary(item => item.Pattern, StringComparer.Ordinal);

        foreach (var pattern in patterns)
        {
            if (!observations.TryGetValue(pattern, out var observation))
            {
                observation = new WeaknessObservation { UserProfileId = performance.ProfileId, Pattern = pattern };
                db.WeaknessObservations.Add(observation);
                observations[pattern] = observation;
            }

            observation.Attempts++;
            observation.Errors++;
            observation.AverageMilliseconds = observation.AverageMilliseconds == 0 ? performance.MeanWordMilliseconds : (observation.AverageMilliseconds * 0.8d) + (performance.MeanWordMilliseconds * 0.2d);
            observation.LastSeenAt = now;
        }
    }

    private static int MissionProgressDelta(Mission mission, MotivationPerformance performance) => mission.Key switch
    {
        MissionKeys.DailyThreeRounds => 1,
        MissionKeys.DailyAccuracy => performance.Accuracy >= 95 ? 1 : 0,
        MissionKeys.DailyTempo => IsTempoMode(performance.Mode) ? 1 : 0,
        MissionKeys.DailyArenaOrTeam => performance.Source == SourceArena ? 1 : 0,
        MissionKeys.WeeklyRounds => 1,
        MissionKeys.WeeklyPrecision => performance.Accuracy >= 98 ? 1 : 0,
        MissionKeys.WeeklyArena => performance.Source == SourceArena ? 1 : 0,
        MissionKeys.WeeklyTexts => performance.TrainingTextId is not null ? 1 : 0,
        _ => 0
    };

    private static bool IsTempoMode(TrainingMode mode) =>
        mode is TrainingMode.Sprint15 or TrainingMode.Sprint30 or TrainingMode.Sprint60 or TrainingMode.Sprint120 or TrainingMode.Text;

    public static DateOnly GetWeekStart(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }

    private static int CalculateStreak(DateOnly? lastActivity, DateOnly today, int current)
    {
        if (lastActivity == today)
        {
            return Math.Max(1, current);
        }

        return lastActivity == today.AddDays(-1) ? current + 1 : 1;
    }

    private static void AddThresholds(HashSet<string> unlock, int value, IReadOnlyList<(int Threshold, string Key)> thresholds)
    {
        foreach (var (threshold, key) in thresholds)
        {
            if (value >= threshold)
            {
                unlock.Add(key);
            }
        }
    }

    private static void AddSpeedThresholds(HashSet<string> unlock, double bestWpm)
    {
        if (bestWpm >= 40)
        {
            unlock.Add("speed-40");
        }

        if (bestWpm >= 60)
        {
            unlock.Add("speed-60");
        }

        if (bestWpm >= 80)
        {
            unlock.Add("speed-80");
        }

        if (bestWpm >= 100)
        {
            unlock.Add("speed-100");
        }
    }

    private static IEnumerable<string> ExtractErrorPatterns(IReadOnlyList<TypingError> errors)
    {
        foreach (var error in errors)
        {
            if (!string.IsNullOrWhiteSpace(error.Expected))
            {
                yield return error.Expected;
            }

            if (error.Kind == TypingErrorKind.Insertion && !string.IsNullOrWhiteSpace(error.Actual))
            {
                yield return error.Actual;
            }

            if (!string.IsNullOrWhiteSpace(error.Pattern))
            {
                yield return error.Pattern;
            }
        }
    }

    private sealed record ArenaBatchItem(
        UserProfile Profile,
        MotivationPerformance Performance,
        int Xp);

    private readonly record struct RewardLedgerIdentity(
        Guid UserProfileId,
        string Source,
        string SourceId);

    private sealed record MissionLoadResult(
        Dictionary<Guid, List<Mission>> MissionsByProfile,
        int AddedCount);

    private sealed record AchievementSnapshot(
        int CompletedAttempts,
        int Precise98Attempts,
        int Precise95Attempts,
        int AuthoredTexts,
        int Collections,
        int ChallengeResults,
        int ArenaResults,
        int CompletedMissions,
        bool WeeklyMissionCompleted,
        HashSet<string> ExistingAchievementKeys);

    private sealed record ArenaAchievementBaseline(
        int CompletedAttempts,
        int Precise98Attempts,
        int Precise95Attempts,
        int AuthoredTexts,
        int Collections,
        int ChallengeResults,
        int ArenaResults,
        int CompletedMissions,
        bool WeeklyMissionCompleted,
        IReadOnlySet<string> ExistingAchievementKeys)
    {
        public static ArenaAchievementBaseline Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed record CompletedMissionBaseline(
        int Count,
        bool WeeklyCompleted)
    {
        public static CompletedMissionBaseline Empty { get; } = new(0, false);
    }

    private sealed record MotivationPerformance(
        Guid ProfileId,
        string Source,
        string SourceId,
        Guid? AttemptId,
        TrainingMode Mode,
        double Wpm,
        double Accuracy,
        double Consistency,
        int DurationMilliseconds,
        int CorrectCharacters,
        int TotalCharacters,
        bool Completed,
        bool Official,
        Guid? TrainingTextId,
        bool CountsForSeason,
        IReadOnlyList<TypingError> Errors)
    {
        public double MeanWordMilliseconds { get; private init; }

        public static MotivationPerformance FromAttempt(Guid profileId, TypingAttempt attempt, IReadOnlyList<TypingError> errors) =>
            new(
                profileId,
                SourceAttempt,
                attempt.Id.ToString("N"),
                attempt.Id,
                attempt.Mode,
                attempt.Wpm,
                attempt.Accuracy,
                attempt.Consistency,
                attempt.DurationMilliseconds,
                attempt.CorrectCharacters,
                attempt.TotalCharacters,
                attempt.Completed,
                attempt.Official,
                attempt.TrainingTextId,
                true,
                errors)
            {
                MeanWordMilliseconds = attempt.MeanWordMilliseconds
            };
    }
}
