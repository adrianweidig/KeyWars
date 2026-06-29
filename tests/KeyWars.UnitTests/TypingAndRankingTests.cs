using KeyWars.Domain;
using KeyWars.Services;

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
    public void AlignmentDoesNotCascadeAfterSingleInsertion()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("abcdef", "abcXdef", TimeSpan.FromSeconds(10), 0, 0);

        var error = Assert.Single(metrics.Errors);
        Assert.Equal(6, metrics.CorrectCharacters);
        Assert.Equal(1, metrics.IncorrectCharacters);
        Assert.Equal(TypingErrorKind.Insertion, error.Kind);
        Assert.Equal(3, error.Position);
        Assert.Equal("", error.Expected);
        Assert.Equal("X", error.Actual);
    }

    [Fact]
    public void AlignmentDoesNotCascadeAfterSingleOmission()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var metrics = engine.Analyze("abcdef", "abdef", TimeSpan.FromSeconds(10), 0, 0);

        var error = Assert.Single(metrics.Errors);
        Assert.Equal(5, metrics.CorrectCharacters);
        Assert.Equal(1, metrics.IncorrectCharacters);
        Assert.Equal(TypingErrorKind.Deletion, error.Kind);
        Assert.Equal(2, error.Position);
        Assert.Equal("c", error.Expected);
        Assert.Equal("", error.Actual);
    }

    [Fact]
    public void SingleInsertionPropertyCreatesOnlyOneErrorAcrossPositions()
    {
        var engine = new TypingEngine(TimeProvider.System);
        const string target = "abcdefghij";

        for (var position = 0; position <= target.Length; position++)
        {
            var input = target.Insert(position, "X");

            var metrics = engine.Analyze(target, input, TimeSpan.FromSeconds(10), 0, 0);

            Assert.Equal(target.Length, metrics.CorrectCharacters);
            Assert.Equal(1, metrics.IncorrectCharacters);
            Assert.Single(metrics.Errors);
        }
    }

    [Fact]
    public void ConsistencyUsesWordTimingVarianceInsteadOfErrorPenalty()
    {
        var engine = new TypingEngine(TimeProvider.System);

        var stable = engine.Analyze("eins zwei drei", "eins zwei drei", TimeSpan.FromSeconds(10), 0, 0, wordDurationsMilliseconds: [1000, 1000, 1000]);
        var stableWithError = engine.Analyze("eins zwei drei", "eins xwei drei", TimeSpan.FromSeconds(10), 0, 0, wordDurationsMilliseconds: [1000, 1000, 1000]);
        var uneven = engine.Analyze("eins zwei drei", "eins zwei drei", TimeSpan.FromSeconds(10), 0, 0, wordDurationsMilliseconds: [500, 1500, 500]);

        Assert.Equal(100, stable.Consistency);
        Assert.Equal(stable.Consistency, stableWithError.Consistency);
        Assert.True(uneven.Consistency < stable.Consistency);
        Assert.Equal(3, stable.ConsistencySampleCount);
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

    [Fact]
    public void PairwiseEloChangesKeepBeforeDeltaAndAfterTogether()
    {
        var winner = Guid.CreateVersion7();
        var runnerUp = Guid.CreateVersion7();
        var ranked = RaceRanking.RankClassic(
        [
            new RaceResult(winner, ParticipantStatus.Finished, 20_000, 99, 0, 99, 90, 300),
            new RaceResult(runnerUp, ParticipantStatus.Finished, 25_000, 98, 1, 95, 70, 280)
        ]);
        var ratings = new Dictionary<Guid, int>
        {
            [winner] = 1030,
            [runnerUp] = 970
        };

        var changes = MultiplayerRating.CalculatePairwiseEloChanges(ratings, ranked);

        Assert.Equal(1030, changes[winner].RatingBefore);
        Assert.Equal(changes[winner].RatingBefore + changes[winner].RatingDelta, changes[winner].RatingAfter);
        Assert.Equal(970, changes[runnerUp].RatingBefore);
        Assert.Equal(changes[runnerUp].RatingBefore + changes[runnerUp].RatingDelta, changes[runnerUp].RatingAfter);
        Assert.Equal(0, changes.Values.Sum(item => item.RatingDelta));
    }

    [Fact]
    public void CompetitionRankingUsesMetricTieBreakers()
    {
        var now = DateTimeOffset.Parse("2026-06-27T10:00:00Z");
        var alpha = Guid.CreateVersion7();
        var beta = Guid.CreateVersion7();
        var gamma = Guid.CreateVersion7();
        var ranked = CompetitionLeaderboardService.RankEntries(
        [
            new LeaderboardEntry { UserProfileId = alpha, DisplayName = "Alpha", Score = 80, Wpm = 80, Accuracy = 97, Consistency = 95, FinishedAt = now.AddMinutes(2) },
            new LeaderboardEntry { UserProfileId = beta, DisplayName = "Beta", Score = 80, Wpm = 80, Accuracy = 98, Consistency = 90, FinishedAt = now.AddMinutes(3) },
            new LeaderboardEntry { UserProfileId = gamma, DisplayName = "Gamma", Score = 80, Wpm = 80, Accuracy = 98, Consistency = 90, FinishedAt = now.AddMinutes(1) }
        ]);

        Assert.Equal(gamma, ranked[0].UserProfileId);
        Assert.Equal(beta, ranked[1].UserProfileId);
        Assert.Equal(alpha, ranked[2].UserProfileId);
        Assert.Equal([1, 2, 3], ranked.Select(entry => entry.Rank).ToArray());
    }

    [Fact]
    public void CompetitionEligibilityExcludesPracticeAndLowQualityAttempts()
    {
        var eligible = Attempt(TrainingMode.Sprint60, 95, official: true);

        Assert.True(CompetitionEligibility.IsAttemptEligible(eligible));
        Assert.False(CompetitionEligibility.IsAttemptEligible(Attempt(TrainingMode.Ghost, 95, official: true)));
        Assert.False(CompetitionEligibility.IsAttemptEligible(Attempt(TrainingMode.Sprint60, 89.9, official: true)));
        Assert.False(CompetitionEligibility.IsAttemptEligible(Attempt(TrainingMode.Sprint60, 95, official: false)));

        static TypingAttempt Attempt(TrainingMode mode, double accuracy, bool official) => new()
        {
            Mode = mode,
            Phase = AttemptPhase.Finished,
            Completed = true,
            Official = official,
            LeaderboardEligible = true,
            Accuracy = accuracy
        };
    }

    [Fact]
    public void StandardTextsAreMeaningfulLongExamples()
    {
        Assert.True(GermanWordBank.StandardTexts.Length >= 6);
        Assert.Equal(
            GermanWordBank.StandardTexts.Length,
            GermanWordBank.StandardTexts.Select(text => text.Key).Distinct(StringComparer.Ordinal).Count());

        foreach (var text in GermanWordBank.StandardTexts)
        {
            var normalized = TypingEngine.NormalizeText(text.Body);
            Assert.False(string.IsNullOrWhiteSpace(text.Title), $"{text.Key} braucht einen Titel.");
            Assert.False(text.Title.Contains("Kurztext", StringComparison.OrdinalIgnoreCase), $"{text.Key} darf kein Kurztext-Placeholder sein.");
            Assert.True(TypingEngine.CountWords(normalized) >= 55, $"{text.Key} ist zu kurz.");
            Assert.True(TypingEngine.SplitGraphemes(normalized).Count >= 350, $"{text.Key} braucht einen größeren Beispieltext.");
        }
    }

    [Fact]
    public void GeneratedWordTestsUseCoherentTrainingText()
    {
        var text = TypingEngine.BuildWordTest(80);

        Assert.Equal(80, TypingEngine.CountWords(text));
        Assert.Contains('.', text);
        Assert.Contains("Training", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(text.StartsWith("aber achten Änderung", StringComparison.Ordinal));
    }
}
