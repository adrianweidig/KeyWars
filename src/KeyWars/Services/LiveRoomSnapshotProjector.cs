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
                .Select(item =>
                {
                    var preview = exposeTargetText
                        ? item.TypedTextPreview[..Math.Min(
                            item.TypedTextPreview.Length,
                            LiveRoomProgress.MaxTypedStateDetailCharacters)]
                        : "";
                    var offset = exposeTargetText
                        ? Math.Clamp(
                            item.TypedStateOffset,
                            0,
                            Math.Max(0, room.TargetCharacterCount - preview.Length))
                        : 0;
                    var typedCharacters = exposeTargetText
                        ? Math.Clamp(
                            Math.Max(
                                item.CorrectCharacters,
                                Math.Max(item.TypedCharacters, offset + preview.Length)),
                            0,
                            room.TargetCharacterCount + LiveRoomProgress.MaxInputOverrunCharacters)
                        : 0;
                    return new LiveParticipantSnapshot(
                        item.ProfileId,
                        item.DisplayName,
                        item.Status,
                        item.Ready,
                        item.Sequence,
                        item.CorrectCharacters,
                        preview,
                        item.Wpm,
                        item.Placement,
                        item.DurationMilliseconds,
                        item.Accuracy,
                        item.TeamNumber,
                        item.SeriesPoints,
                        item.RoundWins,
                        offset,
                        typedCharacters);
                })
                .ToArray(),
            persistenceState,
            LiveRoomScoring.BuildTeamSnapshots(room),
            room.RoundEndsAt,
            room.StateVersion,
            room.LobbyLocked);
    }
}
