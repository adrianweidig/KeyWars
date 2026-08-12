using KeyWars.Domain;

namespace KeyWars.Services;

internal static class LiveRoomSnapshotProjector
{
    public static LiveRoomSnapshot Create(
        LiveRoomState room,
        DateTimeOffset now,
        CompletionState? persistenceState)
    {
        var exposeTargetText = room.Phase is LiveRoomPhase.Running or
            LiveRoomPhase.RoundResults or
            LiveRoomPhase.SeriesResults or
            LiveRoomPhase.Closed;

        return new LiveRoomSnapshot(
            room.Id,
            room.CreatorProfileId,
            room.Code,
            room.Title,
            exposeTargetText ? room.Text : "",
            room.TargetCharacterCount,
            room.MaxParticipants,
            room.Mode,
            room.Visibility,
            room.RoundCount,
            room.CurrentRound,
            room.RoundVersion,
            room.Phase,
            room.Started,
            room.Finished,
            now,
            room.PhaseChangedAt,
            room.CountdownStartsAt,
            room.RaceStartsAt,
            room.StartedAt,
            room.FinishedAt,
            room.CloseReason,
            room.Participants.Values
                .OrderBy(item => item.Placement ?? int.MaxValue)
                .ThenByDescending(item => item.CorrectCharacters)
                .ThenBy(item => item.DisplayName)
                .Select(item => new LiveParticipantSnapshot(
                    item.ProfileId,
                    item.DisplayName,
                    item.Status,
                    item.Ready,
                    item.Sequence,
                    item.CorrectCharacters,
                    exposeTargetText ? item.TypedTextPreview : "",
                    item.Wpm,
                    item.Placement,
                    item.DurationMilliseconds,
                    item.Accuracy,
                    item.TeamNumber,
                    item.SeriesPoints,
                    item.RoundWins))
                .ToArray(),
            persistenceState,
            LiveRoomScoring.BuildTeamSnapshots(room),
            room.RoundEndsAt,
            room.StateVersion,
            room.LobbyLocked);
    }
}
