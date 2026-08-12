using KeyWars.Domain;

namespace KeyWars.Services;

internal static class LiveRoomDisconnectRules
{
    public static bool MarkDisconnected(
        LiveRoomState room,
        LiveParticipantState participant,
        DateTimeOffset now)
    {
        if (participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready or ParticipantStatus.Running)
        {
            participant.Status = ParticipantStatus.Disconnected;
            participant.DisconnectedAt = now;
            return true;
        }

        if (room.Phase == LiveRoomPhase.RoundResults)
        {
            if (participant.DisconnectedAt is null || participant.Ready)
            {
                participant.DisconnectedAt = now;
                participant.Ready = false;
                return true;
            }

            return false;
        }

        if ((participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf) &&
            participant.DisconnectedAt is null)
        {
            participant.DisconnectedAt = now;
            return true;
        }

        return false;
    }

    public static bool ApplyTimeouts(
        LiveRoomState room,
        DateTimeOffset now,
        TimeSpan grace)
    {
        var changed = false;
        foreach (var participant in room.Participants.Values)
        {
            if (participant.Status != ParticipantStatus.Disconnected ||
                participant.DisconnectedAt is null ||
                now - participant.DisconnectedAt.Value < grace)
            {
                continue;
            }

            if (room.Phase == LiveRoomPhase.Lobby)
            {
                participant.Status = ParticipantStatus.LeftBeforeStart;
                participant.Ready = false;
                participant.FinishedAt = participant.DisconnectedAt;
                LiveRoomHostRules.ApplyAutomaticTransfer(room);
            }
            else
            {
                participant.Status = ParticipantStatus.Dnf;
                participant.Ready = false;
                participant.FinishedAt = participant.DisconnectedAt;
                participant.DurationMilliseconds = room.RaceStartsAt is { } raceStartsAt
                    ? (int)Math.Round(LiveRoomProgress.NormalizeDuration(
                        participant.DisconnectedAt.Value - raceStartsAt).TotalMilliseconds)
                    : 0;
            }

            changed = true;
        }

        if (changed)
        {
            LiveRoomScoring.ApplyPlacements(room);
            room.RoundVersion++;
            LiveRoomManager.Touch(room);
        }

        return changed;
    }
}
