using KeyWars.Domain;

namespace KeyWars.UnitTests;

public sealed class TypingAndRankingTests
{
    [Fact]
    public void TypingEngineCountsGermanCharactersAndCompletion()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("Äpfel und Straße", "Äpfel und Straße", TimeSpan.FromSeconds(30), 1, 0);

        Assert.True(metrics.Completed);
        Assert.Equal(16, metrics.CorrectCharacters);
        Assert.Equal(0, metrics.IncorrectCharacters);
        Assert.Equal(100, metrics.Accuracy);
        Assert.True(metrics.Wpm > 0);
    }

    [Fact]
    public void TypingEngineRejectsIncompleteTextAttempt()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("Schlüssel", "Schlussel", TimeSpan.FromSeconds(20), 0, 0);

        Assert.False(metrics.Completed);
        Assert.True(metrics.IncorrectCharacters > 0);
        Assert.True(metrics.Accuracy < 100);
    }

    [Fact]
    public void TypingEngineDoesNotCountUnwrittenRemainderAsError()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("Schlüssel und Straße", "Schlüssel", TimeSpan.FromSeconds(10), 0, 0);

        Assert.False(metrics.Completed);
        Assert.Equal(TypingEngine.SplitGraphemes("Schlüssel").Count, metrics.CorrectCharacters);
        Assert.Equal(0, metrics.IncorrectCharacters);
        Assert.Equal(100, metrics.Accuracy);
    }

    [Fact]
    public void TimeModeDoesNotMarkEmptyInputAsCompleted()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("Schlüssel", "", TimeSpan.FromSeconds(15), 0, 0, timeMode: true);

        Assert.False(metrics.Completed);
        Assert.Equal(0, metrics.CorrectCharacters);
        Assert.Equal(0, metrics.IncorrectCharacters);
    }

    [Fact]
    public void TimeModeTreatsPartialTypedInputAsCompletedSprint()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("Schlüssel und Straße", "Schlüssel", TimeSpan.FromSeconds(15), 0, 0, timeMode: true);

        Assert.True(metrics.Completed);
        Assert.Equal(TypingEngine.SplitGraphemes("Schlüssel").Count, metrics.CorrectCharacters);
        Assert.Equal(100, metrics.Accuracy);
    }

    [Fact]
    public void ClassicRankingWorksForMoreThanTwoPeople()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        var ranked = RaceRanking.RankClassic(
        [
            new RaceResult(ids[0], ParticipantStatus.Finished, 30000, 98, 1, 99, 70, 300),
            new RaceResult(ids[1], ParticipantStatus.Finished, 28000, 96, 0, 99, 72, 300),
            new RaceResult(ids[2], ParticipantStatus.Dnf, 0, 80, 20, 50, 20, 100),
            new RaceResult(ids[3], ParticipantStatus.Finished, 28000, 96, 0, 99, 72, 300),
            new RaceResult(ids[4], ParticipantStatus.Finished, 42000, 100, 0, 99, 50, 300)
        ]);

        Assert.Contains(ranked[0].Result.UserProfileId, new[] { ids[1], ids[3] });
        Assert.Equal(ranked[0].Placement, ranked[1].Placement);
        Assert.Equal(ids[2], ranked[^1].Result.UserProfileId);
    }

    [Fact]
    public void PairwiseEloProducesZeroSumDeltasForGroup()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
        var ranked = RaceRanking.RankClassic(ids.Select((id, index) =>
            new RaceResult(id, ParticipantStatus.Finished, 20_000 + index * 1000, 99, 0, 99, 80 - index, 300)).ToArray());
        var ratings = ids.ToDictionary(id => id, _ => 1000);

        var deltas = MultiplayerRating.CalculatePairwiseElo(ratings, ranked);

        Assert.Equal(0, deltas.Values.Sum());
        Assert.True(deltas[ranked[0].Result.UserProfileId] > 0);
        Assert.True(deltas[ranked[^1].Result.UserProfileId] < 0);
    }
}
