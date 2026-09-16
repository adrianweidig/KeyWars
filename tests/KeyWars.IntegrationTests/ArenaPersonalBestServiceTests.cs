using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.IntegrationTests;

public sealed class ArenaPersonalBestServiceTests
{
    [Fact]
    public async Task ConfirmedBestUsesOnlyOwnFinishedResultsForSameTextAndMode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var profile = Profile("bestwert", "Bestwert Person");
        var otherProfile = Profile("andere", "Andere Person");
        db.UserProfiles.AddRange(profile, otherProfile);
        var targetHash = TextHash.Compute("Dieser Zieltext bleibt für den Vergleich identisch.");
        AddResult(db, profile.Id, targetHash, LiveRoomMode.Classic, 42, "own-previous");
        AddResult(db, profile.Id, TextHash.Compute("Anderer Text"), LiveRoomMode.Classic, 210, "other-text");
        AddResult(db, profile.Id, targetHash, LiveRoomMode.Precision, 190, "other-mode");
        AddResult(db, profile.Id, targetHash, LiveRoomMode.Classic, 230, "aborted", aborted: true);
        AddResult(db, otherProfile.Id, targetHash, LiveRoomMode.Classic, 300, "other-profile");
        var currentRoomId = AddResult(db, profile.Id, targetHash, LiveRoomMode.Classic, 54, "current");
        await db.SaveChangesAsync();

        var snapshot = await new ArenaPersonalBestService(db).GetConfirmedAsync(currentRoomId, profile.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(54, snapshot.CurrentWpm);
        Assert.Equal(42, snapshot.PreviousBestWpm);
        Assert.Equal(54, snapshot.BestWpm);
        Assert.True(snapshot.IsNewBest);
        Assert.Equal("Klassisches Rennen · gleicher Zieltext", snapshot.ScopeLabel);
    }

    [Fact]
    public async Task ReadModelDoesNotConfirmMissingDnfOrUnscopedResults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var profile = Profile("privacy", "Private Person");
        db.UserProfiles.Add(profile);
        var targetHash = TextHash.Compute("Exakter Bestwerttext");
        AddResult(db, profile.Id, targetHash, LiveRoomMode.Classic, 64, "previous");
        var slowerRoomId = AddResult(db, profile.Id, targetHash, LiveRoomMode.Classic, 51, "slower");
        var dnfRoomId = AddResult(db, profile.Id, targetHash, LiveRoomMode.Classic, 99, "dnf", status: ParticipantStatus.Dnf);
        var legacyRoomId = AddResult(db, profile.Id, "", LiveRoomMode.Classic, 88, "no-text-scope");
        await db.SaveChangesAsync();

        var service = new ArenaPersonalBestService(db);
        var slower = await service.GetConfirmedAsync(slowerRoomId, profile.Id);

        Assert.NotNull(slower);
        Assert.Equal(64, slower.PreviousBestWpm);
        Assert.Equal(64, slower.BestWpm);
        Assert.False(slower.IsNewBest);
        Assert.Null(await service.GetConfirmedAsync(dnfRoomId, profile.Id));
        Assert.Null(await service.GetConfirmedAsync(legacyRoomId, profile.Id));
        Assert.Null(await service.GetConfirmedAsync(Guid.CreateVersion7(), profile.Id));
        Assert.Null(await service.GetConfirmedAsync(slowerRoomId, Guid.CreateVersion7()));
    }

    private static UserProfile Profile(string account, string displayName) => new()
    {
        DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
        DirectorySid = $"S-1-5-21-{Random.Shared.Next(100_000, 999_999)}",
        SamAccountName = account,
        UserPrincipalName = $"{account}@keywars.local",
        DisplayName = displayName
    };

    private static Guid AddResult(
        KeyWarsDbContext db,
        Guid profileId,
        string targetTextHash,
        LiveRoomMode mode,
        double wpm,
        string key,
        bool aborted = false,
        ParticipantStatus status = ParticipantStatus.Finished)
    {
        var room = new LiveRoomSummary
        {
            Id = Guid.CreateVersion7(),
            CreatorProfileId = profileId,
            IdempotencyKey = key,
            RoomCode = key[..Math.Min(key.Length, 16)],
            TargetTextHash = targetTextHash,
            Mode = mode,
            Visibility = LiveRoomVisibility.InvitationOnly,
            RoundCount = 1,
            FinishedAt = DateTimeOffset.UtcNow,
            AbortedByServer = aborted
        };
        db.LiveRoomSummaries.Add(room);
        db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
        {
            LiveRoomSummaryId = room.Id,
            UserProfileId = profileId,
            Status = status,
            CompetitionEligible = true,
            Placement = status == ParticipantStatus.Finished ? 1 : null,
            DurationMilliseconds = 30_000,
            Wpm = wpm,
            Accuracy = 98
        });
        return room.Id;
    }
}
