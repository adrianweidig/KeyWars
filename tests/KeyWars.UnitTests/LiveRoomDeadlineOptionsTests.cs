using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.Extensions.Configuration;

namespace KeyWars.UnitTests;

public sealed class LiveRoomDeadlineOptionsTests
{
    [Fact]
    public void ComposeStyleDeadlineOptionsBindWithinSafeBounds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:LIVE:ROUND_DEADLINE_BASE_SECONDS"] = "45",
                ["KEYWARS:LIVE:ROUND_DEADLINE_MILLISECONDS_PER_GRAPHEME"] = "750",
                ["KEYWARS:LIVE:ROUND_DEADLINE_MAX_SECONDS"] = "1800",
                ["KEYWARS:LIVE:ROUND_RESULTS_IDLE_SECONDS"] = "90"
            })
            .Build();
        var options = new LiveOptions();

        ConfigurationAliases.BindLive(configuration, options);

        Assert.Equal(45, options.RoundDeadlineBaseSeconds);
        Assert.Equal(750, options.RoundDeadlineMillisecondsPerGrapheme);
        Assert.Equal(1800, options.RoundDeadlineMaxSeconds);
        Assert.Equal(90, options.RoundResultsIdleSeconds);
    }

    [Theory]
    [InlineData("ROUND_DEADLINE_BASE_SECONDS", "14")]
    [InlineData("ROUND_DEADLINE_MILLISECONDS_PER_GRAPHEME", "5001")]
    [InlineData("ROUND_DEADLINE_MAX_SECONDS", "3601")]
    [InlineData("ROUND_RESULTS_IDLE_SECONDS", "601")]
    public void UnsafeDeadlineOptionsAreRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"KEYWARS:LIVE:{key}"] = value
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationAliases.BindLive(configuration, new LiveOptions()));
    }

    [Fact]
    public void RoundDeadlineUsesTargetLengthAndHardMaximum()
    {
        var start = DateTimeOffset.Parse("2026-08-13T10:00:00Z");
        var options = new LiveOptions
        {
            RoundDeadlineBaseSeconds = 30,
            RoundDeadlineMillisecondsPerGrapheme = 500,
            RoundDeadlineMaxSeconds = 90
        };

        Assert.Equal(start.AddSeconds(40), LiveRoomDeadline.ForNewRound(start, 20, options));
        Assert.Equal(start.AddSeconds(90), LiveRoomDeadline.ForNewRound(start, 2000, options));
    }

    [Fact]
    public void LegacyDeadlineIsAbsoluteDeterministicAndFailClosedBounded()
    {
        var start = DateTimeOffset.Parse("2026-08-13T10:00:00Z");

        Assert.Equal(start.AddSeconds(80), LiveRoomDeadline.Resolve(start, 20, null));
        Assert.Equal(start, LiveRoomDeadline.Resolve(start, 20, start.AddSeconds(-1)));
        Assert.Equal(
            start.AddSeconds(LiveOptions.MaximumSafeRoundDeadlineSeconds),
            LiveRoomDeadline.Resolve(start, 20, start.AddDays(1)));
    }
}
