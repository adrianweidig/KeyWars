using KeyWars.Domain;

namespace KeyWars.Services;

internal static class LiveRoomHostRules
{
    public static void RequireHost(LiveRoomState room, Guid profileId)
    {
        if (room.CreatorProfileId != profileId)
        {
            throw new InvalidOperationException("Nur die Raumleitung darf diese Aktion ausführen.");
        }
    }

    public static bool IsCandidate(LiveRoomState room, LiveParticipantState participant)
    {
        if (room.ExcludedProfileIds.Contains(participant.ProfileId) ||
            participant.Status == ParticipantStatus.LeftBeforeStart ||
            participant.DisconnectedAt is not null)
        {
            return false;
        }

        return room.Phase == LiveRoomPhase.Lobby
            ? participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready
            : participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf;
    }

    public static void ApplyAutomaticTransfer(LiveRoomState room)
    {
        if (room.Phase is not (LiveRoomPhase.Lobby or LiveRoomPhase.RoundResults))
        {
            return;
        }

        if (room.Participants.TryGetValue(room.CreatorProfileId, out var creator) && IsCandidate(room, creator))
        {
            return;
        }

        var nextHost = room.Participants.Values
            .Where(participant => IsCandidate(room, participant))
            .OrderBy(item => item.JoinedAt)
            .ThenBy(item => item.ProfileId)
            .FirstOrDefault();
        if (nextHost is null || nextHost.ProfileId == room.CreatorProfileId)
        {
            return;
        }

        room.CreatorProfileId = nextHost.ProfileId;
        room.RoundVersion++;
        LiveRoomManager.Touch(room);
    }
}
