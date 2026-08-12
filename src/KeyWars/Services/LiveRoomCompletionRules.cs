using KeyWars.Domain;

namespace KeyWars.Services;

internal readonly record struct LiveRoomCompletionTransition(
    CompletedRoomRecord? Record,
    bool EnteredRoundResults);

internal static class LiveRoomCompletionRules
{
    public static LiveRoomCompletionTransition TryComplete(LiveRoomState room, DateTimeOffset now)
    {
        if (room.Finished || room.Phase != LiveRoomPhase.Running)
        {
            return default;
        }

        var competingParticipants = room.Participants.Values
            .Where(item =>
                !room.ExcludedProfileIds.Contains(item.ProfileId) &&
                item.Status != ParticipantStatus.LeftBeforeStart)
            .ToArray();
        if (competingParticipants.Length == 0)
        {
            room.Finished = true;
            room.FinishedAt = now;
            room.RoundEndsAt = now;
            room.Phase = LiveRoomPhase.Aborted;
            room.PhaseChangedAt = now;
            room.CloseReason = "Der Raum wurde beendet, weil keine wertbaren Teilnehmenden mehr vorhanden sind.";
            room.RoundVersion++;
            room.PersistenceState = CompletionState.AbortedUnconfirmed;
            LiveRoomManager.Touch(room);
            return default;
        }

        if (!competingParticipants.All(item => item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf))
        {
            return default;
        }

        LiveRoomScoring.ScoreCompletedRound(room, competingParticipants);
        room.RoundEndsAt = now;
        room.Phase = room.CurrentRound < room.RoundCount
            ? LiveRoomPhase.RoundResults
            : LiveRoomPhase.SeriesResults;
        room.PhaseChangedAt = now;
        room.RoundVersion++;
        LiveRoomManager.Touch(room);
        if (room.Phase == LiveRoomPhase.RoundResults)
        {
            room.PersistenceState = null;
            return new LiveRoomCompletionTransition(null, true);
        }

        room.Finished = true;
        room.FinishedAt = now;
        LiveRoomScoring.ApplyOverallPlacements(room);
        room.PersistenceState = CompletionState.Pending;
        return new LiveRoomCompletionTransition(BuildPersistenceRecord(room), false);
    }

    public static CompletedRoomRecord BuildPersistenceRecord(LiveRoomState room) => new(
        room.Id,
        room.CurrentRound,
        room.RoundVersion,
        $"{room.Id:N}:{room.CurrentRound}:{room.RoundVersion}",
        room.CreatorProfileId,
        room.Code,
        room.Mode,
        room.Visibility,
        room.RoundCount,
        room.CreatedAt,
        room.StartedAt,
        room.FinishedAt,
        room.Participants.Values
            .Where(item =>
                !room.ExcludedProfileIds.Contains(item.ProfileId) &&
                item.Status != ParticipantStatus.LeftBeforeStart)
            .Select(item => new CompletedParticipantRecord(
                item.ProfileId,
                item.Status == ParticipantStatus.AbortedByServer
                    ? ParticipantStatus.AbortedByServer
                    : item.FinishedRounds > 0 ? ParticipantStatus.Finished : ParticipantStatus.Dnf,
                item.Placement,
                item.CompletedRounds > 0 ? item.TotalDurationMilliseconds : item.DurationMilliseconds,
                item.CompletedRounds > 0 ? item.AverageWpm : item.Wpm,
                item.CompletedRounds > 0 ? item.AverageAccuracy : item.Accuracy,
                item.TeamNumber))
            .ToArray());
}
