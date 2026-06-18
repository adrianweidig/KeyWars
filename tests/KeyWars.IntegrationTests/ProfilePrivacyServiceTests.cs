using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class ProfilePrivacyServiceTests
{
    [Fact]
    public async Task ResetStatisticsClearsDerivedDataAndRatings()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        var profile = context.Profile;
        await context.SeedStatisticsAsync();
        var service = context.CreatePrivacyService();

        await service.ResetStatisticsAsync(profile.Id);

        Assert.Empty(await context.Db.TypingAttempts.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.TypingAttemptErrors.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.Missions.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.Achievements.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.WeaknessObservations.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Equal(0, profile.ExperiencePoints);
        Assert.Equal(1, profile.Level);
        Assert.Equal(0, profile.SeasonPoints);
        Assert.Equal(0, profile.CurrentStreakDays);
        Assert.Equal(1000, profile.ArenaRating);
        Assert.Equal(0, profile.RatedMatchCount);
        Assert.Null(profile.LastActivityDate);
    }

    [Fact]
    public async Task DeleteProfilePseudonymizesAndAllowsFreshProvisioning()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        var profile = context.Profile;
        var room = context.LiveRooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Privat", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        await context.SeedStatisticsAsync();
        var text = await context.SeedOwnedTextAndCollectionAsync();
        var challenge = await context.SeedActiveChallengeAsync();
        var service = context.CreatePrivacyService();

        await service.DeleteProfileAsync(profile.Id);
        var reprovisioned = await new ProfileProvisioner(context.Db, context.Time)
            .ProvisionAsync(context.Identity, CancellationToken.None);

        Assert.True(profile.Deleted);
        Assert.NotEqual(context.Identity.ObjectGuid, profile.DirectoryObjectGuid);
        Assert.Equal("Gelöschtes Profil", profile.DisplayName);
        Assert.False(profile.LeaderboardVisible);
        Assert.False(profile.ChallengesEnabled);
        Assert.NotEqual(profile.Id, reprovisioned.Id);
        Assert.Equal(context.Identity.ObjectGuid, reprovisioned.DirectoryObjectGuid);
        Assert.False(reprovisioned.Deleted);
        Assert.Empty(await context.Db.TextCollections.Where(item => item.OwnerProfileId == profile.Id).ToListAsync());
        var storedText = await context.Db.TrainingTexts.AsNoTracking().SingleAsync(item => item.Id == text.Id);
        Assert.Equal("Gelöschter Text", storedText.Title);
        Assert.Equal("", storedText.Body);
        Assert.Equal(0, storedText.CharacterCount);
        var participant = await context.Db.ChallengeParticipants.AsNoTracking().SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == profile.Id);
        Assert.Equal(ParticipantStatus.Declined, participant.Status);
        Assert.Equal(ParticipantStatus.LeftBeforeStart, context.LiveRooms.Snapshot(room.RoomId).Participants.Single().Status);
    }

    [Fact]
    public async Task ExportContainsOnlyCurrentProfileInventory()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        var other = PrivacyTestContext.CreateProfile("other", "Andere Person", "22222222-2222-2222-2222-222222222222");
        context.Db.UserProfiles.Add(other);
        await context.SeedStatisticsAsync();
        var otherAttempt = new TypingAttempt
        {
            UserProfileId = other.Id,
            Mode = TrainingMode.Words10,
            Phase = AttemptPhase.Finished,
            PreparedAt = context.Time.GetUtcNow(),
            StartedAt = context.Time.GetUtcNow(),
            FinishedAt = context.Time.GetUtcNow().AddSeconds(15),
            Completed = true
        };
        context.Db.TypingAttempts.Add(otherAttempt);
        context.Db.TypingAttemptErrors.Add(new TypingAttemptError
        {
            TypingAttemptId = otherAttempt.Id,
            UserProfileId = other.Id,
            Position = 1,
            Kind = TypingErrorKind.Substitution,
            Expected = "a",
            Actual = "x",
            Pattern = "an"
        });
        await context.Db.SaveChangesAsync();
        var service = context.CreatePrivacyService();

        var export = await service.BuildExportAsync(context.Profile.Id);

        Assert.Equal(1, export.Version);
        Assert.Equal(context.Profile.Id, export.Profile.Id);
        Assert.All(export.Attempts, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.Missions, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.Achievements, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.WeaknessObservations, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.AttemptErrors, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.DoesNotContain(export.Attempts, item => item.UserProfileId == other.Id);
        Assert.DoesNotContain(export.AttemptErrors, item => item.UserProfileId == other.Id);
    }

    private sealed class PrivacyTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private PrivacyTestContext(SqliteConnection connection, KeyWarsDbContext db, ManualTimeProvider time, UserProfile profile, DirectoryIdentity identity, LiveRoomManager liveRooms)
        {
            this.connection = connection;
            Db = db;
            Time = time;
            Profile = profile;
            Identity = identity;
            LiveRooms = liveRooms;
        }

        public KeyWarsDbContext Db { get; }
        public ManualTimeProvider Time { get; }
        public UserProfile Profile { get; }
        public DirectoryIdentity Identity { get; }
        public LiveRoomManager LiveRooms { get; }

        public static async Task<PrivacyTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
            var identity = new DirectoryIdentity(
                "11111111-1111-1111-1111-111111111111",
                "S-1-5-21-privacy",
                "privacy",
                "privacy@example.local",
                "Paula Privacy",
                "Paula",
                "Privacy",
                "privacy@example.local",
                "IT",
                "Trainerin");
            var profile = CreateProfile("privacy", "Paula Privacy", identity.ObjectGuid);
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
            return new PrivacyTestContext(connection, db, time, profile, identity, CreateLiveRooms(time));
        }

        public ProfilePrivacyService CreatePrivacyService() => new(Db, LiveRooms, Time);

        public async Task SeedStatisticsAsync()
        {
            Profile.ExperiencePoints = 900;
            Profile.Level = 4;
            Profile.SeasonPoints = 70;
            Profile.CurrentStreakDays = 5;
            Profile.LastActivityDate = DateOnly.FromDateTime(Time.GetUtcNow().Date);
            Profile.ArenaRating = 1240;
            Profile.RatedMatchCount = 12;
            var attempt = new TypingAttempt
            {
                UserProfileId = Profile.Id,
                Mode = TrainingMode.Words10,
                Phase = AttemptPhase.Finished,
                PreparedAt = Time.GetUtcNow(),
                StartedAt = Time.GetUtcNow(),
                FinishedAt = Time.GetUtcNow().AddSeconds(30),
                Completed = true,
                Wpm = 50,
                Accuracy = 99
            };
            Db.TypingAttempts.Add(attempt);
            Db.TypingAttemptErrors.Add(new TypingAttemptError
            {
                TypingAttemptId = attempt.Id,
                UserProfileId = Profile.Id,
                Position = 1,
                Kind = TypingErrorKind.Substitution,
                Expected = "t",
                Actual = "z",
                Pattern = "te"
            });
            Db.Missions.Add(new Mission { UserProfileId = Profile.Id, MissionDate = DateOnly.FromDateTime(Time.GetUtcNow().Date), Title = "Test", Description = "Test", TargetValue = 1, CurrentValue = 1, Completed = true });
            Db.Achievements.Add(new Achievement { UserProfileId = Profile.Id, Key = "test", Title = "Test", Description = "Test" });
            Db.WeaknessObservations.Add(new WeaknessObservation { UserProfileId = Profile.Id, Pattern = "te", Attempts = 6, Errors = 2 });
            await Db.SaveChangesAsync();
        }

        public async Task<TrainingText> SeedOwnedTextAndCollectionAsync()
        {
            var text = new TrainingText
            {
                OwnerProfileId = Profile.Id,
                Title = "Privater Text",
                SourceKey = $"user-{Guid.CreateVersion7():N}",
                Body = "Geheimer Text",
                Visibility = TrainingTextVisibility.Private,
                CharacterCount = 13
            };
            var collection = new TextCollection
            {
                OwnerProfileId = Profile.Id,
                Name = "Privat",
                Visibility = TrainingTextVisibility.Private
            };
            Db.TrainingTexts.Add(text);
            Db.TextCollections.Add(collection);
            Db.TextCollectionItems.Add(new TextCollectionItem { TextCollectionId = collection.Id, TrainingTextId = text.Id });
            await Db.SaveChangesAsync();
            return text;
        }

        public async Task<Challenge> SeedActiveChallengeAsync()
        {
            var other = CreateProfile("challenger", "Charlie Challenge", "33333333-3333-3333-3333-333333333333");
            var text = new TrainingText
            {
                OwnerProfileId = other.Id,
                Title = "Challenge",
                SourceKey = "challenge-privacy",
                Body = "Text",
                Visibility = TrainingTextVisibility.Organization,
                CharacterCount = 4
            };
            var challenge = new Challenge
            {
                CreatorProfileId = other.Id,
                TrainingTextId = text.Id,
                Title = "Challenge",
                Status = ChallengeStatus.Open,
                ExpiresAt = Time.GetUtcNow().AddDays(1)
            };
            Db.UserProfiles.Add(other);
            Db.TrainingTexts.Add(text);
            Db.Challenges.Add(challenge);
            Db.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserProfileId = Profile.Id, Status = ParticipantStatus.Invited });
            Db.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserProfileId = other.Id, Status = ParticipantStatus.Joined });
            await Db.SaveChangesAsync();
            return challenge;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        public static UserProfile CreateProfile(string account, string displayName, string objectGuid) => new()
        {
            DirectoryObjectGuid = objectGuid,
            DirectorySid = $"S-1-5-21-{account}",
            SamAccountName = account,
            UserPrincipalName = $"{account}@example.local",
            DisplayName = displayName,
            Email = $"{account}@example.local",
            Department = "IT",
            Title = "Training"
        };

        private static LiveRoomManager CreateLiveRooms(TimeProvider timeProvider) => new(
            Options.Create(new LiveOptions()),
            timeProvider,
            new TypingEngine(timeProvider),
            NullLogger<LiveRoomManager>.Instance);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
