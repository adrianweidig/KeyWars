using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record CoachRecommendation(string Text, TrainingMode Mode, int Minutes);

public sealed class MotivationService(KeyWarsDbContext db, TimeProvider timeProvider)
{
    public async Task ApplyAttemptAsync(Guid profileId, TypingAttempt attempt, string targetText, CancellationToken cancellationToken = default)
    {
        if (!attempt.Completed || attempt.ExperienceAwarded)
        {
            return;
        }

        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId, cancellationToken);
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        profile.ExperiencePoints += CalculateXp(attempt);
        profile.Level = Math.Max(1, profile.ExperiencePoints / 250 + 1);
        profile.CurrentStreakDays = CalculateStreak(profile.LastActivityDate, today, profile.CurrentStreakDays);
        profile.LastActivityDate = today;
        profile.SeasonPoints += Math.Max(1, (int)Math.Round(attempt.Wpm / 10));
        attempt.ExperienceAwarded = true;

        await EnsureDailyMissionsAsync(profileId, today, cancellationToken);
        var missions = await db.Missions.Where(item => item.UserProfileId == profileId && item.MissionDate == today).ToListAsync(cancellationToken);
        foreach (var mission in missions)
        {
            mission.CurrentValue += mission.Title.Contains("Genauigkeit", StringComparison.OrdinalIgnoreCase)
                ? attempt.Accuracy >= 95 ? 1 : 0
                : 1;
            mission.Completed = mission.CurrentValue >= mission.TargetValue;
        }

        await UnlockAchievementAsync(profileId, "erster-versuch", "Erster gültiger Versuch", "Du hast deinen ersten gültigen KeyWars-Versuch abgeschlossen.", cancellationToken);
        if (attempt.Accuracy >= 98)
        {
            await UnlockAchievementAsync(profileId, "praezise", "Präzise Hände", "Ein gültiger Versuch mit mindestens 98 % Genauigkeit.", cancellationToken);
        }

        var patterns = ExtractPatterns(targetText).Take(80).ToList();
        var observations = await db.WeaknessObservations
            .Where(item => item.UserProfileId == profileId && patterns.Contains(item.Pattern))
            .ToDictionaryAsync(item => item.Pattern, cancellationToken);

        foreach (var pattern in patterns)
        {
            if (!observations.TryGetValue(pattern, out var observation))
            {
                observation = new WeaknessObservation { UserProfileId = profileId, Pattern = pattern };
                db.WeaknessObservations.Add(observation);
                observations[pattern] = observation;
            }

            observation.Attempts++;
            if (attempt.IncorrectCharacters > 0 && pattern.Length > 1)
            {
                observation.Errors++;
            }

            observation.AverageMilliseconds = observation.AverageMilliseconds == 0 ? 350 : (observation.AverageMilliseconds * 0.9d) + 35d;
            observation.LastSeenAt = timeProvider.GetUtcNow();
        }
    }

    public async Task EnsureDailyMissionsAsync(Guid profileId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var existing = await db.Missions.CountAsync(item => item.UserProfileId == profileId && item.MissionDate == date, cancellationToken);
        if (existing >= 3)
        {
            return;
        }

        var missions = new[]
        {
            new Mission { UserProfileId = profileId, MissionDate = date, Title = "Drei kurze Runden", Description = "Schließe heute drei gültige Versuche ab.", TargetValue = 3, XpReward = 30 },
            new Mission { UserProfileId = profileId, MissionDate = date, Title = "Genauigkeit halten", Description = "Erreiche einmal mindestens 95 % Genauigkeit.", TargetValue = 1, XpReward = 35 },
            new Mission { UserProfileId = profileId, MissionDate = date, Title = "Tempo festigen", Description = "Beende zwei Sprint- oder Textrunden.", TargetValue = 2, XpReward = 25 }
        };
        db.Missions.AddRange(missions.Skip(existing));
        await db.SaveChangesAsync(cancellationToken);
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

    private static int CalculateXp(TypingAttempt attempt)
    {
        var baseXp = Math.Clamp((int)Math.Round(attempt.Wpm), 5, 120);
        var accuracyBonus = attempt.Accuracy >= 98 ? 20 : attempt.Accuracy >= 95 ? 10 : 0;
        return baseXp + accuracyBonus;
    }

    private static int CalculateStreak(DateOnly? lastActivity, DateOnly today, int current)
    {
        if (lastActivity == today)
        {
            return Math.Max(1, current);
        }

        return lastActivity == today.AddDays(-1) ? current + 1 : 1;
    }

    private async Task UnlockAchievementAsync(Guid profileId, string key, string title, string description, CancellationToken cancellationToken)
    {
        var exists = await db.Achievements.AnyAsync(item => item.UserProfileId == profileId && item.Key == key, cancellationToken);
        if (!exists)
        {
            db.Achievements.Add(new Achievement { UserProfileId = profileId, Key = key, Title = title, Description = description });
        }
    }

    private static IEnumerable<string> ExtractPatterns(string text)
    {
        var elements = TypingEngine.SplitGraphemes(text);
        foreach (var element in elements)
        {
            if (!string.IsNullOrWhiteSpace(element))
            {
                yield return element;
            }
        }

        for (var index = 0; index < elements.Count - 1; index++)
        {
            var pattern = elements[index] + elements[index + 1];
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                yield return pattern;
            }
        }
    }
}
