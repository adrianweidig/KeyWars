using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class LiveReactionServiceTests
{
    [Fact]
    public void ArenaHubSuppressesLocallyBeforeLoadingTheRoomSnapshot()
    {
        var source = File.ReadAllText(FindSource("Hubs/ArenaHub.cs"));
        var reaction = source.IndexOf(
            "var reaction = reactions.TrySubmit(roomId, profile.Id, profile.DisplayName, key);",
            StringComparison.Ordinal);
        var snapshot = source.IndexOf(
            "var snapshot = await rooms.SnapshotForViewerAsync(roomId, profile.Id",
            StringComparison.Ordinal);

        Assert.True(reaction >= 0 && snapshot > reaction);
    }

    [Fact]
    public void PresetReactionIsAllowedAndLocalized()
    {
        using var service = new LiveReactionService(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z")));

        var reaction = service.TrySubmit(Guid.CreateVersion7(), Guid.CreateVersion7(), "Anna Beispiel", "Stark");

        Assert.NotNull(reaction);
        Assert.Equal("stark", reaction.Key);
        Assert.Equal("Stark", reaction.Label);
        Assert.Equal(0, reaction.SuppressedCount);
    }

    [Fact]
    public void UnknownReactionIsRejected()
    {
        using var service = new LiveReactionService(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.TrySubmit(Guid.CreateVersion7(), Guid.CreateVersion7(), "Anna Beispiel", "<script>"));

        Assert.Contains("nicht erlaubt", error.Message);
    }

    [Fact]
    public void RepeatedReactionIsRateLimitedAndCollapsed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        using var service = new LiveReactionService(time);
        var roomId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();

        Assert.NotNull(service.TrySubmit(roomId, profileId, "Anna Beispiel", "sauber"));
        Assert.Null(service.TrySubmit(roomId, profileId, "Anna Beispiel", "sauber"));

        time.Advance(TimeSpan.FromSeconds(2));
        var next = service.TrySubmit(roomId, profileId, "Anna Beispiel", "respekt");

        Assert.NotNull(next);
        Assert.Equal("respekt", next.Key);
        Assert.Equal(1, next.SuppressedCount);
    }

    [Fact]
    public void ReactionWindowLimitsBursts()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        using var service = new LiveReactionService(time);
        var roomId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();

        for (var index = 0; index < 5; index += 1)
        {
            Assert.NotNull(service.TrySubmit(roomId, profileId, "Anna Beispiel", "knapp"));
            time.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Null(service.TrySubmit(roomId, profileId, "Anna Beispiel", "knapp"));
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.NotNull(service.TrySubmit(roomId, profileId, "Anna Beispiel", "knapp"));
    }

    [Fact]
    public async Task IndependentKeysRemainConsistentUnderParallelLoad()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        using var service = new LiveReactionService(time, maxTrackedStates: 4_096);
        var roomId = Guid.CreateVersion7();
        var profileIds = Enumerable.Range(0, 2_000)
            .Select(_ => Guid.CreateVersion7())
            .ToArray();

        var reactions = await Task.WhenAll(profileIds.Select(profileId => Task.Run(() =>
            service.TrySubmit(roomId, profileId, "Parallel", "stark"))));

        Assert.All(reactions, reaction => Assert.NotNull(reaction));
        Assert.Equal(profileIds.Length, service.TrackedStateCount);
        Assert.Equal(profileIds.Length, service.ScheduledExpiryCount);
    }

    [Fact]
    public async Task SameKeyStillSerializesRateLimitState()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        using var service = new LiveReactionService(time);
        var roomId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();

        var reactions = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            service.TrySubmit(roomId, profileId, "Parallel", "sauber"))));

        Assert.Single(reactions, reaction => reaction is not null);
        time.Advance(TimeSpan.FromSeconds(2));
        var next = service.TrySubmit(roomId, profileId, "Parallel", "sauber");
        Assert.NotNull(next);
        Assert.Equal(99, next.SuppressedCount);
    }

    [Fact]
    public void ExpiredKeysAreRemovedWithoutScanningActiveKeys()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        using var service = new LiveReactionService(time, maxTrackedStates: 128);
        var roomId = Guid.CreateVersion7();

        for (var index = 0; index < 100; index += 1)
        {
            Assert.NotNull(service.TrySubmit(roomId, Guid.CreateVersion7(), "Kurzzeitig", "respekt"));
        }

        time.Advance(TimeSpan.FromMinutes(10));
        service.SweepExpired();

        Assert.Equal(0, service.TrackedStateCount);
        Assert.Equal(0, service.ScheduledExpiryCount);
    }

    [Fact]
    public void StateCapacityIsBoundedAndReusableAfterExpiry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        using var service = new LiveReactionService(time, maxTrackedStates: 2);
        var roomId = Guid.CreateVersion7();

        Assert.NotNull(service.TrySubmit(roomId, Guid.CreateVersion7(), "A", "stark"));
        Assert.NotNull(service.TrySubmit(roomId, Guid.CreateVersion7(), "B", "stark"));
        Assert.Null(service.TrySubmit(roomId, Guid.CreateVersion7(), "C", "stark"));
        Assert.Equal(2, service.TrackedStateCount);

        time.Advance(TimeSpan.FromMinutes(10));
        service.SweepExpired();

        Assert.NotNull(service.TrySubmit(roomId, Guid.CreateVersion7(), "C", "stark"));
        Assert.Equal(1, service.TrackedStateCount);
    }

    private static string FindSource(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "KeyWars",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan value)
        {
            utcNow = utcNow.Add(value);
        }
    }
}
