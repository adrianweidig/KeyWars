using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class ChallengeLifecycleTests
{
    [Fact]
    public async Task JoinExpiresPastDueChallenge()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Ablauf", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        context.Time.Advance(TimeSpan.FromDays(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.JoinAsync(challenge.Id, context.Invitee.Id));

        var stored = await context.Db.Challenges.SingleAsync(item => item.Id == challenge.Id);
        Assert.Equal(ChallengeStatus.Expired, stored.Status);
        Assert.NotNull(stored.FinishedAt);
    }

    [Fact]
    public async Task FinishRequiresAcceptedParticipant()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Direktfinish", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        var attempt = context.CreateAttempt(context.Invitee.Id, context.Text.Id, TrainingMode.Text, challenge.CreatedAt.AddMinutes(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.FinishRoundAsync(challenge.Id, context.Invitee.Id, attempt));
    }

    [Fact]
    public async Task FinishRejectsWrongAttemptMode()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Modusbindung", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var attempt = context.CreateAttempt(context.Invitee.Id, context.Text.Id, TrainingMode.Sprint60, challenge.CreatedAt.AddMinutes(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.FinishRoundAsync(challenge.Id, context.Invitee.Id, attempt));
    }

    private sealed class ChallengeTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ChallengeTestContext(SqliteConnection connection, KeyWarsDbContext db, ManualTimeProvider time, UserProfile creator, UserProfile invitee, TrainingText text)
        {
            this.connection = connection;
            Db = db;
            Time = time;
            Creator = creator;
            Invitee = invitee;
            Text = text;
            Service = new ChallengeService(db, Options.Create(new ChallengeOptions()), time);
        }

        public KeyWarsDbContext Db { get; }
        public ManualTimeProvider Time { get; }
        public ChallengeService Service { get; }
        public UserProfile Creator { get; }
        public UserProfile Invitee { get; }
        public TrainingText Text { get; }

        public static async Task<ChallengeTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var creator = Profile("creator", "Carla Creator");
            var invitee = Profile("invitee", "Iris Invitee");
            var text = new TrainingText
            {
                OwnerProfileId = creator.Id,
                Title = "Challenge-Text",
                SourceKey = "challenge-text",
                Body = "Text",
                Visibility = TrainingTextVisibility.Organization,
                IsStandard = false,
                RatingEligible = true,
                CharacterCount = TypingEngine.SplitGraphemes("Text").Count
            };
            db.UserProfiles.AddRange(creator, invitee);
            db.TrainingTexts.Add(text);
            await db.SaveChangesAsync();
            return new ChallengeTestContext(connection, db, new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z")), creator, invitee, text);
        }

        public TypingAttempt CreateAttempt(Guid profileId, Guid textId, TrainingMode mode, DateTimeOffset startedAt)
        {
            var attempt = new TypingAttempt
            {
                UserProfileId = profileId,
                TrainingTextId = textId,
                Mode = mode,
                Nonce = Guid.CreateVersion7().ToString("N")[..24],
                StartedAt = startedAt,
                FinishedAt = startedAt.AddSeconds(10),
                DurationMilliseconds = 10_000,
                CorrectCharacters = Text.CharacterCount,
                TotalCharacters = Text.CharacterCount,
                Wpm = 48,
                RawWpm = 48,
                CharactersPerMinute = 240,
                Accuracy = 100,
                Consistency = 100,
                Completed = true,
                Official = true,
                LeaderboardEligible = true
            };
            Db.TypingAttempts.Add(attempt);
            Db.SaveChanges();
            return attempt;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static UserProfile Profile(string account, string displayName) => new()
        {
            DisplayName = displayName,
            SamAccountName = account,
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}"
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
