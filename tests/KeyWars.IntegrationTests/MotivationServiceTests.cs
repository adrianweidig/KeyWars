using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.IntegrationTests;

public sealed class MotivationServiceTests
{
    [Fact]
    public async Task CompletedMissionAwardsXpOnlyOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DisplayName = "Mona Mission",
            SamAccountName = "mmission",
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = "S-1-5-21-mission"
        };
        var attempt = new TypingAttempt
        {
            UserProfileId = profile.Id,
            Mode = TrainingMode.Sprint60,
            Phase = AttemptPhase.Finished,
            PreparedAt = DateTimeOffset.Parse("2026-06-18T12:00:00Z"),
            StartedAt = DateTimeOffset.Parse("2026-06-18T12:00:00Z"),
            FinishedAt = DateTimeOffset.Parse("2026-06-18T12:01:00Z"),
            DurationMilliseconds = 60_000,
            CorrectCharacters = 250,
            TotalCharacters = 300,
            Wpm = 50,
            Accuracy = 98,
            Consistency = 96,
            Completed = true,
            Official = true
        };
        db.UserProfiles.Add(profile);
        db.TypingAttempts.Add(attempt);
        await db.SaveChangesAsync();
        var service = new MotivationService(db, new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:02:00Z")));

        await service.ApplyAttemptAsync(profile.Id, attempt, "Sicher tippen", CancellationToken.None);
        await db.SaveChangesAsync();
        var firstXp = profile.ExperiencePoints;
        await service.ApplyAttemptAsync(profile.Id, attempt, "Sicher tippen", CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(105, firstXp);
        Assert.Equal(firstXp, profile.ExperiencePoints);
        Assert.True(attempt.ExperienceAwarded);
        Assert.Contains(await db.Missions.ToListAsync(), mission => mission.Completed && mission.XpReward == 35);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
