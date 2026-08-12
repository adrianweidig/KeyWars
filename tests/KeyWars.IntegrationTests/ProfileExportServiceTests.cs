using System.Text.Json;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.IntegrationTests;

public sealed class ProfileExportServiceTests
{
    [Fact]
    public void RangeRejectsEndBeforeStart()
    {
        var exception = Assert.Throws<ProfileExportValidationException>(() =>
            ProfileExportRange.Create(new DateOnly(2026, 6, 20), new DateOnly(2026, 6, 10)));

        Assert.Contains("Von-Datum", exception.Message);
    }

    [Fact]
    public async Task FullExportStreamsCompleteVersionThreeSchemaWithoutTrackingOrInternalSecrets()
    {
        await using var context = await ExportTestContext.CreateAsync();
        var seeded = await context.SeedCompleteInventoryAsync(attemptCount: 140);
        context.Db.ChangeTracker.Clear();
        var service = context.CreateService();
        var range = ProfileExportRange.Create(null, null);

        var preview = await service.GetPreviewAsync(context.ProfileId, range);
        await using var output = new MemoryStream();
        await service.WriteAsync(context.ProfileId, range, context.Time.GetUtcNow(), output, CancellationToken.None);

        Assert.Empty(context.Db.ChangeTracker.Entries());
        Assert.Equal(140, preview.Attempts);
        Assert.True(output.Length > 0);
        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        var root = document.RootElement;
        Assert.Equal(ProfileExportService.SchemaVersion, root.GetProperty("Version").GetInt32());
        Assert.False(root.GetProperty("Range").GetProperty("Filtered").GetBoolean());
        Assert.Equal(140, root.GetProperty("Attempts").GetArrayLength());
        Assert.Single(root.GetProperty("AttemptErrors").EnumerateArray());
        Assert.Single(root.GetProperty("OwnedTexts").EnumerateArray());
        Assert.Single(root.GetProperty("OwnedCollections").EnumerateArray());
        Assert.Single(root.GetProperty("OwnedCollectionItems").EnumerateArray());
        Assert.Single(root.GetProperty("ContentModerationAuditEntries").EnumerateArray());
        Assert.Single(root.GetProperty("CreatedChallenges").EnumerateArray());
        Assert.Single(root.GetProperty("ChallengeRounds").EnumerateArray());
        Assert.Single(root.GetProperty("ChallengeParticipations").EnumerateArray());
        Assert.Single(root.GetProperty("ChallengeRoundResults").EnumerateArray());
        Assert.Single(root.GetProperty("ChallengeAttemptBindings").EnumerateArray());
        Assert.Single(root.GetProperty("CreatedLiveRooms").EnumerateArray());
        Assert.Single(root.GetProperty("LiveRoomResults").EnumerateArray());

        var expectedProperties = new[]
        {
            "Version", "GeneratedAt", "Range", "Profile", "Attempts", "AttemptErrors", "RewardLedger",
            "Missions", "Achievements", "GamificationEvents", "WeaknessObservations", "OwnedTexts",
            "OwnedCollections", "OwnedCollectionItems", "ContentModerationAuditEntries", "CreatedChallenges", "ChallengeRounds",
            "ChallengeParticipations", "ChallengeRoundResults", "ChallengeAttemptBindings",
            "CreatedLiveRooms", "LiveRoomResults"
        };
        Assert.Equal(expectedProperties, root.EnumerateObject().Select(property => property.Name).ToArray());
        var coveredDbSets = new[]
        {
            nameof(KeyWarsDbContext.UserProfiles),
            nameof(KeyWarsDbContext.TrainingTexts),
            nameof(KeyWarsDbContext.TextCollections),
            nameof(KeyWarsDbContext.TextCollectionItems),
            nameof(KeyWarsDbContext.ContentModerationAuditEntries),
            nameof(KeyWarsDbContext.TypingAttempts),
            nameof(KeyWarsDbContext.TypingAttemptErrors),
            nameof(KeyWarsDbContext.Challenges),
            nameof(KeyWarsDbContext.ChallengeParticipants),
            nameof(KeyWarsDbContext.ChallengeRounds),
            nameof(KeyWarsDbContext.ChallengeRoundResults),
            nameof(KeyWarsDbContext.ChallengeAttemptBindings),
            nameof(KeyWarsDbContext.LiveRoomSummaries),
            nameof(KeyWarsDbContext.LiveRoomParticipantSummaries),
            nameof(KeyWarsDbContext.Missions),
            nameof(KeyWarsDbContext.RewardLedgerEntries),
            nameof(KeyWarsDbContext.Achievements),
            nameof(KeyWarsDbContext.GamificationEvents),
            nameof(KeyWarsDbContext.WeaknessObservations)
        };
        var currentDbSets = typeof(KeyWarsDbContext)
            .GetProperties()
            .Where(property => property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(property => property.Name)
            .Order()
            .ToArray();
        Assert.Equal(currentDbSets, coveredDbSets.Order().ToArray());

        output.Position = 0;
        using var reader = new StreamReader(output);
        var json = await reader.ReadToEndAsync();
        Assert.DoesNotContain(seeded.AttemptNonce, json);
        Assert.DoesNotContain(seeded.BindingToken, json);
        Assert.DoesNotContain(seeded.RoomIdempotencyKey, json);
    }

    [Fact]
    public async Task FilterUsesInclusiveUtcDaysAndKeepsOwnedContentComplete()
    {
        await using var context = await ExportTestContext.CreateAsync();
        var ownedText = new TrainingText
        {
            OwnerProfileId = context.ProfileId,
            Title = "Alter eigener Text",
            SourceKey = "owned-export-range",
            Body = "Bleibt unabhängig vom Aktivitätszeitraum enthalten.",
            CreatedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z")
        };
        var attempts = new[]
        {
            CreateAttempt(context.ProfileId, "2026-06-09T23:59:59Z"),
            CreateAttempt(context.ProfileId, "2026-06-20T23:59:59Z"),
            CreateAttempt(context.ProfileId, "2026-06-21T00:00:00Z")
        };
        context.Db.TrainingTexts.Add(ownedText);
        context.Db.TypingAttempts.AddRange(attempts);
        context.Db.TypingAttemptErrors.AddRange(attempts.Select(attempt => new TypingAttemptError
        {
            TypingAttemptId = attempt.Id,
            UserProfileId = context.ProfileId,
            CreatedAt = attempt.CreatedAt,
            Kind = TypingErrorKind.Substitution,
            Expected = "a",
            Actual = "x"
        }));
        context.Db.RewardLedgerEntries.AddRange(
            new RewardLedgerEntry { UserProfileId = context.ProfileId, Source = "old", SourceId = "old", AwardedAt = attempts[0].CreatedAt },
            new RewardLedgerEntry { UserProfileId = context.ProfileId, Source = "in", SourceId = "in", AwardedAt = attempts[1].CreatedAt },
            new RewardLedgerEntry { UserProfileId = context.ProfileId, Source = "after", SourceId = "after", AwardedAt = attempts[2].CreatedAt });
        await context.Db.SaveChangesAsync();
        context.Db.ChangeTracker.Clear();
        var service = context.CreateService();
        var range = ProfileExportRange.Create(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 20));

        await using var output = new MemoryStream();
        await service.WriteAsync(context.ProfileId, range, context.Time.GetUtcNow(), output, CancellationToken.None);
        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        var root = document.RootElement;

        Assert.True(root.GetProperty("Range").GetProperty("Filtered").GetBoolean());
        Assert.Equal("2026-06-10", root.GetProperty("Range").GetProperty("From").GetString());
        Assert.Equal("2026-06-20", root.GetProperty("Range").GetProperty("To").GetString());
        Assert.Single(root.GetProperty("Attempts").EnumerateArray());
        Assert.Single(root.GetProperty("AttemptErrors").EnumerateArray());
        Assert.Single(root.GetProperty("RewardLedger").EnumerateArray());
        Assert.Equal(ownedText.Id, root.GetProperty("OwnedTexts")[0].GetProperty("Id").GetGuid());
        Assert.Empty(context.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DownloadResultUsesAttachmentHeadersAndStreamsJson()
    {
        await using var context = await ExportTestContext.CreateAsync();
        context.Db.ChangeTracker.Clear();
        var service = context.CreateService();
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());

        await service.CreateDownload(context.ProfileId, ProfileExportRange.Create(null, null))
            .ExecuteResultAsync(actionContext);

        Assert.Equal("application/json; charset=utf-8", httpContext.Response.ContentType);
        Assert.Contains("attachment", httpContext.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("no-store", httpContext.Response.Headers.CacheControl.ToString());
        Assert.True(responseBody.Length > 0);
        responseBody.Position = 0;
        using var document = await JsonDocument.ParseAsync(responseBody);
        Assert.Equal(ProfileExportService.SchemaVersion, document.RootElement.GetProperty("Version").GetInt32());
    }

    private static TypingAttempt CreateAttempt(Guid profileId, string createdAt)
    {
        var timestamp = DateTimeOffset.Parse(createdAt);
        return new TypingAttempt
        {
            UserProfileId = profileId,
            Mode = TrainingMode.Words10,
            Phase = AttemptPhase.Finished,
            PreparedAt = timestamp,
            StartedAt = timestamp,
            FinishedAt = timestamp.AddSeconds(30),
            CreatedAt = timestamp,
            Completed = true
        };
    }

    private sealed class ExportTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ExportTestContext(SqliteConnection connection, KeyWarsDbContext db, Guid profileId, FixedTimeProvider time)
        {
            this.connection = connection;
            Db = db;
            ProfileId = profileId;
            Time = time;
        }

        public KeyWarsDbContext Db { get; }
        public Guid ProfileId { get; }
        public FixedTimeProvider Time { get; }

        public static async Task<ExportTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var profile = new UserProfile
            {
                DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
                SamAccountName = "export",
                UserPrincipalName = "export@example.local",
                DisplayName = "Eva Export"
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
            return new ExportTestContext(
                connection,
                db,
                profile.Id,
                new FixedTimeProvider(DateTimeOffset.Parse("2026-06-30T12:00:00Z")));
        }

        public ProfileExportService CreateService() => new(Db, Time);

        public async Task<SeededSecrets> SeedCompleteInventoryAsync(int attemptCount)
        {
            var timestamp = DateTimeOffset.Parse("2026-06-15T12:00:00Z");
            var attempts = Enumerable.Range(0, attemptCount)
                .Select(index => CreateAttempt(ProfileId, timestamp.AddMinutes(index).ToString("O")))
                .ToArray();
            attempts[0].Nonce = "SECRET-ATTEMPT-NONCE";
            var text = new TrainingText
            {
                OwnerProfileId = ProfileId,
                Title = "Exporttext",
                SourceKey = "profile-export-test",
                Body = "Vollständiger eigener Inhalt",
                CreatedAt = timestamp
            };
            var collection = new TextCollection
            {
                OwnerProfileId = ProfileId,
                Name = "Export-Sammlung",
                CreatedAt = timestamp
            };
            var challenge = new Challenge
            {
                CreatorProfileId = ProfileId,
                TrainingTextId = text.Id,
                Title = "Export-Challenge",
                CreatedAt = timestamp,
                ExpiresAt = timestamp.AddDays(7)
            };
            var round = new ChallengeRound { ChallengeId = challenge.Id, CreatedAt = timestamp };
            var binding = new ChallengeAttemptBinding
            {
                ChallengeId = challenge.Id,
                ChallengeRoundId = round.Id,
                UserProfileId = ProfileId,
                TypingAttemptId = attempts[0].Id,
                TextSnapshotHash = "export-hash",
                BindingToken = "SECRET-BINDING-TOKEN",
                CreatedAt = timestamp
            };
            var room = new LiveRoomSummary
            {
                Id = Guid.CreateVersion7(),
                IdempotencyKey = "SECRET-ROOM-IDEMPOTENCY",
                CreatorProfileId = ProfileId,
                RoomCode = "EXPORT",
                CreatedAt = timestamp,
                FinishedAt = timestamp.AddMinutes(1)
            };

            Db.TypingAttempts.AddRange(attempts);
            Db.TypingAttemptErrors.Add(new TypingAttemptError
            {
                TypingAttemptId = attempts[0].Id,
                UserProfileId = ProfileId,
                CreatedAt = timestamp,
                Kind = TypingErrorKind.Substitution,
                Expected = "a",
                Actual = "x"
            });
            Db.RewardLedgerEntries.Add(new RewardLedgerEntry { UserProfileId = ProfileId, Source = "test", SourceId = "reward", AwardedAt = timestamp });
            Db.Missions.Add(new Mission { UserProfileId = ProfileId, Key = "export", Title = "Export", Description = "Export", MissionDate = DateOnly.FromDateTime(timestamp.Date), TargetValue = 1 });
            Db.Achievements.Add(new Achievement { UserProfileId = ProfileId, Key = "export", Title = "Export", Description = "Export", UnlockedAt = timestamp });
            Db.GamificationEvents.Add(new GamificationEvent { UserProfileId = ProfileId, EventKey = "export", Title = "Export", Description = "Export", CreatedAt = timestamp });
            Db.WeaknessObservations.Add(new WeaknessObservation { UserProfileId = ProfileId, Pattern = "ex", LastSeenAt = timestamp });
            Db.TrainingTexts.Add(text);
            Db.TextCollections.Add(collection);
            Db.TextCollectionItems.Add(new TextCollectionItem { TextCollectionId = collection.Id, TrainingTextId = text.Id });
            Db.ContentModerationAuditEntries.Add(new ContentModerationAuditEntry
            {
                ActorProfileId = ProfileId,
                ActorDisplayName = "Eva Export",
                TargetType = ContentModerationTargetType.TrainingText,
                TargetId = text.Id,
                TargetTitle = text.Title,
                Action = ContentModerationAction.Unpublish,
                Reason = "Exportnachweis",
                CreatedAt = timestamp
            });
            Db.Challenges.Add(challenge);
            Db.ChallengeRounds.Add(round);
            Db.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserProfileId = ProfileId, InvitedAt = timestamp });
            Db.ChallengeRoundResults.Add(new ChallengeRoundResult { ChallengeRoundId = round.Id, UserProfileId = ProfileId, TypingAttemptId = attempts[0].Id, FinishedAt = timestamp });
            Db.ChallengeAttemptBindings.Add(binding);
            Db.LiveRoomSummaries.Add(room);
            Db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary { LiveRoomSummaryId = room.Id, UserProfileId = ProfileId });
            await Db.SaveChangesAsync();
            return new SeededSecrets(attempts[0].Nonce, binding.BindingToken, room.IdempotencyKey);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SeededSecrets(string AttemptNonce, string BindingToken, string RoomIdempotencyKey);
}
