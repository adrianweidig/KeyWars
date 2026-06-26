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

public sealed class MotivationService(KeyWarsDbContext db, TimeProvider timeProvider)
{
    private const string SourceAttempt = "attempt";
    private const string SourceArena = "arena";
    private const string SourceMission = "mission";
    private const string SourceAchievement = "achievement";

    private const string MissionThreeRounds = "daily-three-rounds";
    private const string MissionAccuracy = "daily-accuracy";
    private const string MissionTempo = "daily-tempo";
    private const string MissionArenaOrTeam = "daily-arena-or-team";
    private const string MissionWeeklyRounds = "weekly-ten-rounds";
    private const string MissionWeeklyPrecision = "weekly-three-precise";
    private const string MissionWeeklyArena = "weekly-two-arena";
    private const string MissionWeeklyTexts = "weekly-text-training";

    private const int MinimumXpCharacters = 20;
    private const int MinimumXpDurationMilliseconds = 5_000;

    public static IReadOnlyList<AchievementDefinition> AchievementDefinitions { get; } =
    [
        new("erster-versuch", "Training", "Erster gültiger Versuch", "Du hast deinen ersten gültigen KeyWars-Versuch abgeschlossen."),
        new("training-5-attempts", "Training", "Fünf Runden", "Du hast fünf gültige Trainingsrunden abgeschlossen."),
        new("training-10-attempts", "Training", "Zehn Runden", "Du hast zehn gültige Trainingsrunden abgeschlossen."),
        new("training-25-attempts", "Training", "Trainingsroutine", "Du hast 25 gültige Trainingsrunden abgeschlossen."),
        new("training-50-attempts", "Training", "Feste Gewohnheit", "Du hast 50 gültige Trainingsrunden abgeschlossen."),
        new("training-100-attempts", "Training", "Hundert Runden", "Du hast 100 gültige Trainingsrunden abgeschlossen."),
        new("training-text-round", "Training", "Text gemeistert", "Du hast eine Textrunde abgeschlossen."),
        new("training-words-round", "Training", "Worttest erledigt", "Du hast einen Worttest abgeschlossen."),
        new("training-sprint-round", "Training", "Sprint abgeschlossen", "Du hast einen Zeitsprint abgeschlossen."),
        new("training-weakness-focus", "Training", "Fehlerfokus genutzt", "Du hast eine Fehlerfokus-Runde abgeschlossen."),
        new("precision-95", "Präzision", "Saubere Runde", "Du hast eine Runde mit mindestens 95 % Genauigkeit abgeschlossen."),
        new("praezise", "Präzision", "Präzise Hände", "Ein gültiger Versuch mit mindestens 98 % Genauigkeit."),
        new("precision-100", "Präzision", "Fehlerfrei", "Du hast eine Runde ohne verbleibende Fehler abgeschlossen."),
        new("precision-3x-98", "Präzision", "Dreimal sehr präzise", "Du hast drei Runden mit mindestens 98 % Genauigkeit abgeschlossen."),
        new("precision-10x-95", "Präzision", "Verlässliche Genauigkeit", "Du hast zehn Runden mit mindestens 95 % Genauigkeit abgeschlossen."),
        new("speed-40", "Tempo", "40 WPM", "Du hast 40 WPM erreicht."),
        new("speed-60", "Tempo", "60 WPM", "Du hast 60 WPM erreicht."),
        new("speed-80", "Tempo", "80 WPM", "Du hast 80 WPM erreicht."),
        new("speed-100", "Tempo", "100 WPM", "Du hast 100 WPM erreicht."),
        new("speed-personal-best", "Tempo", "Neue Bestleistung", "Du hast deine bisherige WPM-Bestleistung verbessert."),
        new("streak-3", "Serie", "Drei Tage aktiv", "Du hast an drei Trainingstagen in Folge geübt."),
        new("streak-7", "Serie", "Sieben Tage aktiv", "Du hast an sieben Trainingstagen in Folge geübt."),
        new("streak-14", "Serie", "Zwei Wochen aktiv", "Du hast an vierzehn Trainingstagen in Folge geübt."),
        new("streak-30", "Serie", "Monatsserie", "Du hast an dreißig Trainingstagen in Folge geübt."),
        new("arena-first", "Arena", "Erstes Rennen", "Du hast deine erste Arena-Runde abgeschlossen."),
        new("arena-5", "Arena", "Fünf Rennen", "Du hast fünf Arena-Runden abgeschlossen."),
        new("arena-10", "Arena", "Zehn Rennen", "Du hast zehn Arena-Runden abgeschlossen."),
        new("arena-rating-1050", "Arena", "Rating 1050", "Du hast ein Arena-Rating von 1050 erreicht."),
        new("arena-rating-1100", "Arena", "Rating 1100", "Du hast ein Arena-Rating von 1100 erreicht."),
        new("arena-perfect-accuracy", "Arena", "Arena ohne Fehler", "Du hast eine Arena-Runde mit 100 % Genauigkeit abgeschlossen."),
        new("text-author-first", "Texte", "Erster eigener Text", "Du hast einen eigenen Trainingstext angelegt."),
        new("text-author-3", "Texte", "Textsammlung wächst", "Du hast drei eigene Trainingstexte angelegt."),
        new("text-collection-first", "Texte", "Eigene Sammlung", "Du hast eine eigene Textsammlung angelegt."),
        new("team-first-challenge", "Team", "Erste Herausforderung", "Du hast deine erste Gruppenherausforderung abgeschlossen."),
        new("team-3-challenges", "Team", "Drei Herausforderungen", "Du hast drei Gruppenherausforderungen abgeschlossen."),
        new("team-precise", "Team", "Teampräzision", "Du hast eine Teamrunde mit mindestens 98 % Genauigkeit abgeschlossen."),
        new("mission-first", "Missionen", "Erste Mission", "Du hast deine erste Mission abgeschlossen."),
        new("mission-5", "Missionen", "Fünf Missionen", "Du hast fünf Missionen abgeschlossen."),
        new("mission-weekly", "Missionen", "Wochenziel erreicht", "Du hast eine Wochenmission abgeschlossen.")
    ];

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

        var totalCharacters = Math.Max(MinimumXpCharacters, (int)Math.Round(wpm * durationMilliseconds / 12_000d));
        var performance = new MotivationPerformance(
            profileId,
            SourceArena,
            sourceId.Length <= 80 ? sourceId : sourceId[..80],
            null,
            TrainingMode.Text,
            wpm,
            accuracy,
            100,
            durationMilliseconds,
            totalCharacters,
            totalCharacters,
            true,
            true,
            null,
            false,
            []);
        return await ApplyPerformanceAsync(performance, CalculateXp(performance, 0d, false), 0d, cancellationToken);
    }

    public async Task EnsureDailyMissionsAsync(Guid profileId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await EnsureCurrentMissionsAsync(profileId, date, cancellationToken);
    }

    public async Task EnsureCurrentMissionsAsync(Guid profileId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await EnsureMissionsAsync(profileId, date, MissionCadence.Daily, cancellationToken);
        await EnsureMissionsAsync(profileId, GetWeekStart(date), MissionCadence.Weekly, cancellationToken);
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

        await EnsureCurrentMissionsAsync(profile.Id, today, cancellationToken);
        var activeMissionDates = new[] { today, GetWeekStart(today) }.Distinct().ToArray();
        var missions = await db.Missions
            .Where(item => item.UserProfileId == profile.Id && activeMissionDates.Contains(item.MissionDate))
            .ToListAsync(cancellationToken);
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
        await AddCreatedEventAsync(events, profile, GamificationEventType.XpAwarded, "xp-awarded",
            $"+{awardedXp} XP",
            "Gültige Runde abgeschlossen.",
            awardedXp,
            levelBefore,
            levelAfter,
            GamificationRarity.Common,
            performance.Source,
            performance.SourceId,
            now,
            cancellationToken);

        if (performance.Source == SourceArena)
        {
            await AddCreatedEventAsync(events, profile, GamificationEventType.ArenaResult, "arena-result",
                "Arena-Rennen gewertet",
                $"{performance.Wpm:0.0} WPM bei {performance.Accuracy:0.0} % Genauigkeit.",
                0,
                levelBefore,
                levelAfter,
                performance.Accuracy >= 99.9 ? GamificationRarity.Rare : GamificationRarity.Common,
                performance.Source,
                performance.SourceId,
                now,
                cancellationToken);
        }

        if (previousBestWpm > 0 && performance.Wpm >= previousBestWpm + 2)
        {
            await AddCreatedEventAsync(events, profile, GamificationEventType.PersonalBest, "personal-best",
                "Neue Bestleistung",
                $"{performance.Wpm:0.0} WPM verbessern deine bisherige Marke von {previousBestWpm:0.0} WPM.",
                0,
                levelBefore,
                levelAfter,
                GamificationRarity.Rare,
                performance.Source,
                performance.SourceId,
                now,
                cancellationToken);
        }

        foreach (var (mission, missionXp) in completedMissions)
        {
            await AddCreatedEventAsync(events, profile, GamificationEventType.MissionCompleted, "mission-completed",
                mission.Title,
                mission.Description,
                missionXp,
                levelBefore,
                levelAfter,
                RarityForMission(mission),
                SourceMission,
                mission.Id.ToString("N"),
                now,
                cancellationToken);
        }

        foreach (var achievement in unlockedAchievements)
        {
            await AddCreatedEventAsync(events, profile, GamificationEventType.AchievementUnlocked, "achievement-unlocked",
                achievement.Title,
                achievement.Description,
                0,
                levelBefore,
                levelAfter,
                RarityForAchievement(achievement),
                SourceAchievement,
                achievement.Key,
                now,
                cancellationToken);
        }

        if (levelAfter > levelBefore)
        {
            await AddCreatedEventAsync(events, profile, GamificationEventType.LevelUp, $"level-up-{levelAfter}",
                $"Level {levelAfter} erreicht",
                $"Du bist von Level {levelBefore} auf Level {levelAfter} gestiegen.",
                0,
                levelBefore,
                levelAfter,
                RarityForLevel(levelAfter),
                performance.Source,
                performance.SourceId,
                now,
                cancellationToken);
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

    private async Task AddCreatedEventAsync(
        ICollection<GamificationEvent> events,
        UserProfile profile,
        GamificationEventType type,
        string eventKey,
        string title,
        string description,
        int xpDelta,
        int levelBefore,
        int levelAfter,
        GamificationRarity rarity,
        string source,
        string sourceId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var normalizedSource = Truncate(source, 64);
        var normalizedSourceId = Truncate(sourceId, 80);
        var normalizedEventKey = Truncate(eventKey, 80);
        var localExists = db.GamificationEvents.Local.Any(item =>
            item.UserProfileId == profile.Id &&
            item.Source == normalizedSource &&
            item.SourceId == normalizedSourceId &&
            item.EventKey == normalizedEventKey);
        var exists = localExists || await db.GamificationEvents.AnyAsync(item =>
            item.UserProfileId == profile.Id &&
            item.Source == normalizedSource &&
            item.SourceId == normalizedSourceId &&
            item.EventKey == normalizedEventKey,
            cancellationToken);
        if (exists)
        {
            return;
        }

        var gamificationEvent = new GamificationEvent
        {
            UserProfileId = profile.Id,
            Type = type,
            EventKey = normalizedEventKey,
            Title = Truncate(title, 160),
            Description = Truncate(description, 360),
            XpDelta = xpDelta,
            LevelBefore = levelBefore,
            LevelAfter = levelAfter,
            Rarity = rarity,
            Source = normalizedSource,
            SourceId = normalizedSourceId,
            CreatedAt = createdAt
        };
        db.GamificationEvents.Add(gamificationEvent);
        events.Add(gamificationEvent);
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

    private static string Truncate(string value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private async Task EnsureMissionsAsync(Guid profileId, DateOnly periodStart, MissionCadence cadence, CancellationToken cancellationToken)
    {
        var definitions = MissionDefinitions.Where(definition => definition.Cadence == cadence).ToArray();
        var existingKeys = await db.Missions
            .Where(item => item.UserProfileId == profileId && item.MissionDate == periodStart)
            .Select(item => item.Key)
            .ToListAsync(cancellationToken);
        var missing = definitions
            .Where(definition => !existingKeys.Contains(definition.Key, StringComparer.Ordinal))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        db.Missions.AddRange(missing.Select(definition => new Mission
        {
            UserProfileId = profileId,
            MissionDate = periodStart,
            Key = definition.Key,
            Title = definition.Title,
            Description = definition.Description,
            TargetValue = definition.TargetValue,
            XpReward = definition.XpReward
        }));
        await db.SaveChangesAsync(cancellationToken);
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
        var bestWpm = Math.Max(previousBestWpm, performance.Wpm);
        var unlock = new HashSet<string>(StringComparer.Ordinal);

        AddThresholds(unlock, completedAttempts, [
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

        AddThresholds(unlock, precise98Attempts, [(3, "precision-3x-98")]);
        AddThresholds(unlock, precise95Attempts, [(10, "precision-10x-95")]);
        AddSpeedThresholds(unlock, bestWpm);
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
        AddThresholds(unlock, Math.Max(profile.RatedMatchCount, arenaResults), [
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

        AddThresholds(unlock, authoredTexts, [
            (1, "text-author-first"),
            (3, "text-author-3")
        ]);
        if (collections >= 1)
        {
            unlock.Add("text-collection-first");
        }

        AddThresholds(unlock, challengeResults, [
            (1, "team-first-challenge"),
            (3, "team-3-challenges")
        ]);
        if (challengeResults >= 1 && performance.Accuracy >= 98)
        {
            unlock.Add("team-precise");
        }

        AddThresholds(unlock, completedMissionIds.Count, [
            (1, "mission-first"),
            (5, "mission-5")
        ]);
        if (weeklyMissionCompleted)
        {
            unlock.Add("mission-weekly");
        }

        var existing = new HashSet<string>(
            await db.Achievements.Where(item => item.UserProfileId == profile.Id).Select(item => item.Key).ToListAsync(cancellationToken),
            StringComparer.Ordinal);
        foreach (var local in db.Achievements.Local.Where(item => item.UserProfileId == profile.Id))
        {
            existing.Add(local.Key);
        }

        var unlockedDefinitions = new List<AchievementDefinition>();
        foreach (var definition in AchievementDefinitions.Where(item => unlock.Contains(item.Key)))
        {
            if (!existing.Contains(definition.Key))
            {
                db.Achievements.Add(new Achievement
                {
                    UserProfileId = profile.Id,
                    Key = definition.Key,
                    Title = definition.Title,
                    Description = definition.Description,
                    UnlockedAt = now
                });
                existing.Add(definition.Key);
                unlockedDefinitions.Add(definition);
            }
        }

        return unlockedDefinitions;
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
        MissionThreeRounds => 1,
        MissionAccuracy => performance.Accuracy >= 95 ? 1 : 0,
        MissionTempo => IsTempoMode(performance.Mode) ? 1 : 0,
        MissionArenaOrTeam => performance.Source == SourceArena ? 1 : 0,
        MissionWeeklyRounds => 1,
        MissionWeeklyPrecision => performance.Accuracy >= 98 ? 1 : 0,
        MissionWeeklyArena => performance.Source == SourceArena ? 1 : 0,
        MissionWeeklyTexts => performance.TrainingTextId is not null ? 1 : 0,
        _ => 0
    };

    private static IReadOnlyList<MissionDefinition> MissionDefinitions =>
    [
        new(MissionThreeRounds, MissionCadence.Daily, "Drei kurze Runden", "Schließe heute drei gültige Versuche ab.", 3, 30),
        new(MissionAccuracy, MissionCadence.Daily, "Genauigkeit halten", "Erreiche einmal mindestens 95 % Genauigkeit.", 1, 35),
        new(MissionTempo, MissionCadence.Daily, "Tempo festigen", "Beende zwei Sprint- oder Textrunden.", 2, 25),
        new(MissionArenaOrTeam, MissionCadence.Daily, "Gemeinsam antreten", "Schließe heute eine Arena-Runde ab.", 1, 25),
        new(MissionWeeklyRounds, MissionCadence.Weekly, "Zehn Runden in der Woche", "Schließe in dieser Woche zehn gültige Runden ab.", 10, 80),
        new(MissionWeeklyPrecision, MissionCadence.Weekly, "Drei Präzisionsrunden", "Erreiche in dieser Woche dreimal mindestens 98 % Genauigkeit.", 3, 90),
        new(MissionWeeklyArena, MissionCadence.Weekly, "Zwei Arena-Runden", "Schließe in dieser Woche zwei Arena-Runden ab.", 2, 70),
        new(MissionWeeklyTexts, MissionCadence.Weekly, "Texte trainieren", "Schließe in dieser Woche drei Runden mit gespeicherten Texten ab.", 3, 60)
    ];

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
