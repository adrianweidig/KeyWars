namespace KeyWars.Domain;

public sealed record RaceResult(
    Guid UserProfileId,
    ParticipantStatus Status,
    int DurationMilliseconds,
    double Accuracy,
    int IncorrectCharacters,
    double Consistency,
    double RawWpm,
    int CorrectCharacters);

public sealed record RankedRaceResult(RaceResult Result, int Placement);

public sealed record RatingChange(Guid UserProfileId, int RatingBefore, int RatingDelta, int RatingAfter);

public sealed record ArenaSeriesScore(
    Guid UserProfileId,
    int Points,
    int RoundWins,
    int FinishedRounds,
    int TotalDurationMilliseconds,
    double AverageAccuracy);

public sealed record RankedArenaSeriesScore(ArenaSeriesScore Score, int Placement);

public sealed record ArenaTeamScore(
    int TeamNumber,
    int Points,
    int RoundWins,
    int FinishedRounds,
    int TotalDurationMilliseconds);

public sealed record RankedArenaTeamScore(ArenaTeamScore Score, int Placement);

public static class ArenaScoring
{
    public static int PointsForRound(ParticipantStatus status, int? placement, int participantCount)
    {
        if (status != ParticipantStatus.Finished || placement is null || participantCount < 1)
        {
            return 0;
        }

        return Math.Max(1, participantCount - placement.Value + 1);
    }

    public static IReadOnlyList<RankedArenaSeriesScore> RankSeries(IEnumerable<ArenaSeriesScore> scores)
    {
        var ordered = scores
            .OrderByDescending(item => item.Points)
            .ThenByDescending(item => item.RoundWins)
            .ThenByDescending(item => item.FinishedRounds)
            .ThenBy(item => item.TotalDurationMilliseconds)
            .ThenByDescending(item => item.AverageAccuracy)
            .ThenBy(item => item.UserProfileId)
            .ToArray();

        var ranked = new List<RankedArenaSeriesScore>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var placement = index + 1;
            if (index > 0 && IsSeriesTie(ordered[index - 1], ordered[index]))
            {
                placement = ranked[index - 1].Placement;
            }

            ranked.Add(new RankedArenaSeriesScore(ordered[index], placement));
        }

        return ranked;
    }

    public static IReadOnlyList<RankedArenaTeamScore> RankTeams(IEnumerable<ArenaTeamScore> scores)
    {
        var ordered = scores
            .OrderByDescending(item => item.Points)
            .ThenByDescending(item => item.RoundWins)
            .ThenByDescending(item => item.FinishedRounds)
            .ThenBy(item => item.TotalDurationMilliseconds)
            .ThenBy(item => item.TeamNumber)
            .ToArray();

        var ranked = new List<RankedArenaTeamScore>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var placement = index + 1;
            if (index > 0 && IsTeamTie(ordered[index - 1], ordered[index]))
            {
                placement = ranked[index - 1].Placement;
            }

            ranked.Add(new RankedArenaTeamScore(ordered[index], placement));
        }

        return ranked;
    }

    private static bool IsSeriesTie(ArenaSeriesScore left, ArenaSeriesScore right) =>
        left.Points == right.Points &&
        left.RoundWins == right.RoundWins &&
        left.FinishedRounds == right.FinishedRounds &&
        left.TotalDurationMilliseconds == right.TotalDurationMilliseconds &&
        Math.Abs(left.AverageAccuracy - right.AverageAccuracy) < 0.001d;

    private static bool IsTeamTie(ArenaTeamScore left, ArenaTeamScore right) =>
        left.Points == right.Points &&
        left.RoundWins == right.RoundWins &&
        left.FinishedRounds == right.FinishedRounds &&
        left.TotalDurationMilliseconds == right.TotalDurationMilliseconds;
}

public static class RaceRanking
{
    public static IReadOnlyList<RankedRaceResult> RankClassic(IEnumerable<RaceResult> results)
    {
        var ordered = results
            .OrderBy(result => result.Status == ParticipantStatus.Finished ? 0 : 1)
            .ThenBy(result => result.DurationMilliseconds)
            .ThenByDescending(result => result.Accuracy)
            .ThenBy(result => result.IncorrectCharacters)
            .ThenByDescending(result => result.Consistency)
            .ThenByDescending(result => result.RawWpm)
            .ThenBy(result => result.UserProfileId)
            .ToArray();

        var ranked = new List<RankedRaceResult>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var placement = index + 1;
            if (index > 0 && IsTie(ordered[index - 1], ordered[index]))
            {
                placement = ranked[index - 1].Placement;
            }

            ranked.Add(new RankedRaceResult(ordered[index], placement));
        }

        return ranked;
    }

    private static bool IsTie(RaceResult left, RaceResult right)
    {
        return left.Status == right.Status
            && left.DurationMilliseconds == right.DurationMilliseconds
            && Math.Abs(left.Accuracy - right.Accuracy) < 0.001d
            && left.IncorrectCharacters == right.IncorrectCharacters
            && Math.Abs(left.Consistency - right.Consistency) < 0.001d
            && Math.Abs(left.RawWpm - right.RawWpm) < 0.001d;
    }
}

public static class MultiplayerRating
{
    public static IReadOnlyDictionary<Guid, RatingChange> CalculatePairwiseEloChanges(
        IReadOnlyDictionary<Guid, int> currentRatings,
        IReadOnlyList<RankedRaceResult> rankedResults,
        int kFactor = 24)
    {
        var deltas = CalculatePairwiseElo(currentRatings, rankedResults, kFactor);
        return currentRatings.ToDictionary(pair =>
            pair.Key,
            pair => new RatingChange(pair.Key, pair.Value, deltas[pair.Key], pair.Value + deltas[pair.Key]));
    }

    public static IReadOnlyDictionary<Guid, int> CalculatePairwiseElo(
        IReadOnlyDictionary<Guid, int> currentRatings,
        IReadOnlyList<RankedRaceResult> rankedResults,
        int kFactor = 24)
    {
        if (rankedResults.Count < 2)
        {
            return currentRatings.ToDictionary(pair => pair.Key, pair => 0);
        }

        var deltas = currentRatings.Keys.ToDictionary(id => id, _ => 0d);
        for (var i = 0; i < rankedResults.Count; i++)
        {
            for (var j = i + 1; j < rankedResults.Count; j++)
            {
                var left = rankedResults[i];
                var right = rankedResults[j];
                var leftRating = currentRatings[left.Result.UserProfileId];
                var rightRating = currentRatings[right.Result.UserProfileId];
                var expectedLeft = 1d / (1d + Math.Pow(10d, (rightRating - leftRating) / 400d));
                var scoreLeft = left.Placement == right.Placement ? 0.5d : left.Placement < right.Placement ? 1d : 0d;
                var pairDelta = kFactor * (scoreLeft - expectedLeft) / Math.Max(1, rankedResults.Count - 1);
                deltas[left.Result.UserProfileId] += pairDelta;
                deltas[right.Result.UserProfileId] -= pairDelta;
            }
        }

        return deltas.ToDictionary(pair => pair.Key, pair => (int)Math.Round(pair.Value, MidpointRounding.AwayFromZero));
    }
}
