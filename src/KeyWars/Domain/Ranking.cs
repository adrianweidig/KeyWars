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
