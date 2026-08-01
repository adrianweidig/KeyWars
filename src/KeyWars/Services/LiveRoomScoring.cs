using KeyWars.Domain;

namespace KeyWars.Services;

internal static class LiveRoomScoring
{
    public static void ApplyPlacements(LiveRoomState room)
    {
        var ranked = RaceRanking.RankClassic(room.Participants.Values
            .Where(item =>
                !room.ExcludedProfileIds.Contains(item.ProfileId) &&
                item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf)
            .Select(item => new RaceResult(
                item.ProfileId,
                item.Status,
                item.DurationMilliseconds,
                item.Accuracy,
                0,
                100,
                item.Wpm,
                item.CorrectCharacters)));

        foreach (var rankedResult in ranked)
        {
            room.Participants[rankedResult.Result.UserProfileId].Placement = rankedResult.Placement;
        }
    }

    public static void ScoreCompletedRound(
        LiveRoomState room,
        IReadOnlyCollection<LiveParticipantState> participants)
    {
        var participantCount = participants.Count;
        foreach (var participant in participants)
        {
            var points = ArenaScoring.PointsForRound(participant.Status, participant.Placement, participantCount);
            participant.SeriesPoints += points;
            participant.RoundWins += participant.Status == ParticipantStatus.Finished && participant.Placement == 1 ? 1 : 0;
            participant.FinishedRounds += participant.Status == ParticipantStatus.Finished ? 1 : 0;
            participant.CompletedRounds++;
            participant.TotalDurationMilliseconds += participant.DurationMilliseconds;
            participant.TotalWpm += participant.Wpm;
            participant.TotalAccuracy += participant.Accuracy;
        }

        if (room.Mode != LiveRoomMode.Team)
        {
            return;
        }

        var roundScores = participants
            .Where(item => item.TeamNumber is not null)
            .GroupBy(item => item.TeamNumber!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => ArenaScoring.PointsForRound(item.Status, item.Placement, participantCount)));
        if (roundScores.Count == 0)
        {
            return;
        }

        var winningScore = roundScores.Values.Max();
        foreach (var teamNumber in roundScores.Where(item => item.Value == winningScore).Select(item => item.Key))
        {
            room.TeamRoundWins[teamNumber] = room.TeamRoundWins.GetValueOrDefault(teamNumber) + 1;
        }
    }

    public static void ApplyOverallPlacements(LiveRoomState room)
    {
        var active = room.Participants.Values
            .Where(item => !room.ExcludedProfileIds.Contains(item.ProfileId) && item.Status != ParticipantStatus.LeftBeforeStart)
            .ToArray();

        if (room.Mode == LiveRoomMode.Team)
        {
            var teams = BuildTeamStandings(room, active);
            var placements = teams.ToDictionary(item => item.Score.TeamNumber, item => item.Placement);
            foreach (var participant in active)
            {
                participant.Placement = participant.TeamNumber is { } teamNumber && placements.TryGetValue(teamNumber, out var placement)
                    ? placement
                    : null;
            }

            return;
        }

        var ranked = ArenaScoring.RankSeries(active.Select(ToSeriesScore));
        foreach (var result in ranked)
        {
            room.Participants[result.Score.UserProfileId].Placement = result.Placement;
        }
    }

    public static IReadOnlyList<LiveTeamSnapshot> BuildTeamSnapshots(LiveRoomState room)
    {
        if (room.Mode != LiveRoomMode.Team)
        {
            return [];
        }

        return BuildTeamStandings(room)
            .Select(item => new LiveTeamSnapshot(
                item.Score.TeamNumber,
                item.Score.TeamNumber == 1 ? "Team Alpha" : "Team Bravo",
                item.Score.Points,
                item.Score.RoundWins,
                item.Score.FinishedRounds,
                item.Placement))
            .ToArray();
    }

    private static ArenaSeriesScore ToSeriesScore(LiveParticipantState participant) => new(
        participant.ProfileId,
        participant.SeriesPoints,
        participant.RoundWins,
        participant.FinishedRounds,
        participant.TotalDurationMilliseconds,
        participant.AverageAccuracy);

    private static IReadOnlyList<RankedArenaTeamScore> BuildTeamStandings(
        LiveRoomState room,
        IEnumerable<LiveParticipantState>? source = null)
    {
        var participants = source ?? room.Participants.Values.Where(item =>
            !room.ExcludedProfileIds.Contains(item.ProfileId) && item.Status != ParticipantStatus.LeftBeforeStart);
        return ArenaScoring.RankTeams(participants
            .Where(item => item.TeamNumber is not null)
            .GroupBy(item => item.TeamNumber!.Value)
            .Select(group => new ArenaTeamScore(
                group.Key,
                group.Sum(item => item.SeriesPoints),
                room.TeamRoundWins.GetValueOrDefault(group.Key),
                group.Sum(item => item.FinishedRounds),
                group.Sum(item => item.TotalDurationMilliseconds))));
    }
}
