using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record ArenaPersonalBestSnapshot(
    double CurrentWpm,
    double? PreviousBestWpm,
    double BestWpm,
    bool IsNewBest,
    string ScopeLabel);

public sealed class ArenaPersonalBestService(KeyWarsDbContext db)
{
    public async Task<ArenaPersonalBestSnapshot?> GetConfirmedAsync(
        Guid roomId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var current = await (
                from participant in db.LiveRoomParticipantSummaries.AsNoTracking()
                join room in db.LiveRoomSummaries.AsNoTracking()
                    on participant.LiveRoomSummaryId equals room.Id
                where room.Id == roomId &&
                      participant.UserProfileId == profileId &&
                      participant.Status == ParticipantStatus.Finished &&
                      participant.CompetitionEligible &&
                      !room.AbortedByServer
                select new
                {
                    participant.Wpm,
                    room.Mode,
                    room.TargetTextHash,
                    PreviousBestWpm = (
                        from previousParticipant in db.LiveRoomParticipantSummaries.AsNoTracking()
                        join previousRoom in db.LiveRoomSummaries.AsNoTracking()
                            on previousParticipant.LiveRoomSummaryId equals previousRoom.Id
                        where previousParticipant.UserProfileId == profileId &&
                              previousParticipant.Status == ParticipantStatus.Finished &&
                              previousParticipant.CompetitionEligible &&
                              previousRoom.Id != roomId &&
                              !previousRoom.AbortedByServer &&
                              previousRoom.Mode == room.Mode &&
                              previousRoom.TargetTextHash == room.TargetTextHash
                        select (double?)previousParticipant.Wpm)
                        .Max()
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (current is null || string.IsNullOrWhiteSpace(current.TargetTextHash))
        {
            return null;
        }

        var isNewBest = current.PreviousBestWpm is null || current.Wpm > current.PreviousBestWpm.Value;
        return new ArenaPersonalBestSnapshot(
            current.Wpm,
            current.PreviousBestWpm,
            current.PreviousBestWpm is null ? current.Wpm : Math.Max(current.Wpm, current.PreviousBestWpm.Value),
            isNewBest,
            $"{DisplayNames.For(current.Mode)} · gleicher Zieltext");
    }
}
