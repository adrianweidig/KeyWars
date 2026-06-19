using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class LiveReactionServiceTests
{
    [Fact]
    public void PresetReactionIsAllowedAndLocalized()
    {
        var service = new LiveReactionService(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z")));

        var reaction = service.TrySubmit(Guid.CreateVersion7(), Guid.CreateVersion7(), "Anna Beispiel", "Stark");

        Assert.NotNull(reaction);
        Assert.Equal("stark", reaction.Key);
        Assert.Equal("Stark", reaction.Label);
        Assert.Equal(0, reaction.SuppressedCount);
    }

    [Fact]
    public void UnknownReactionIsRejected()
    {
        var service = new LiveReactionService(new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.TrySubmit(Guid.CreateVersion7(), Guid.CreateVersion7(), "Anna Beispiel", "<script>"));

        Assert.Contains("nicht erlaubt", error.Message);
    }

    [Fact]
    public void RepeatedReactionIsRateLimitedAndCollapsed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-19T12:00:00Z"));
        var service = new LiveReactionService(time);
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
        var service = new LiveReactionService(time);
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
