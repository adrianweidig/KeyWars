using KeyWars.Domain;

namespace KeyWars.Services;

internal static class LiveProgressEvidence
{
    internal const int MaximumObservations = 8;
    private const int BurstAllowance = 4;
    private static readonly TimeSpan MinimumObservationInterval = TimeSpan.FromMilliseconds(50);

    public static void Reset(LiveParticipantState participant)
    {
        participant.ProgressEvidenceRoundVersion = 0;
        participant.ProgressEvidenceRevision = 0;
        participant.ProgressEvidenceCorrectCharacters = 0;
        participant.ProgressEvidenceCount = 0;
        participant.ProgressEvidenceObservedAt = null;
        participant.ProgressEvidenceInvalid = false;
    }

    public static void Observe(
        LiveRoomState room,
        LiveParticipantState participant,
        LiveProgressInputDelta input,
        DateTimeOffset now)
    {
        if (room.RaceStartsAt is not { } raceStartsAt)
        {
            return;
        }

        if (participant.ProgressEvidenceRoundVersion != room.RoundVersion)
        {
            Reset(participant);
            participant.ProgressEvidenceRoundVersion = room.RoundVersion;
        }

        var correctCharacters = participant.CorrectCharacters;
        if (correctCharacters >= room.TargetCharacterCount)
        {
            if (participant.ProgressEvidenceCount == 0)
            {
                participant.ProgressEvidenceInvalid = true;
            }
            return;
        }

        if (correctCharacters <= participant.ProgressEvidenceCorrectCharacters || correctCharacters <= 0)
        {
            return;
        }

        if (input.ResyncInput is not null)
        {
            if (participant.ProgressEvidenceCount == 0)
            {
                participant.ProgressEvidenceInvalid = true;
            }
            return;
        }

        var baselineAt = participant.ProgressEvidenceObservedAt ?? raceStartsAt;
        var elapsed = now - baselineAt;
        if (participant.ProgressEvidenceObservedAt is not null && elapsed < MinimumObservationInterval)
        {
            return;
        }

        var growth = correctCharacters - participant.ProgressEvidenceCorrectCharacters;
        if (!IsPlausibleGrowth(growth, elapsed))
        {
            participant.ProgressEvidenceInvalid = true;
            return;
        }

        participant.ProgressEvidenceRoundVersion = room.RoundVersion;
        participant.ProgressEvidenceRevision = participant.Sequence;
        participant.ProgressEvidenceCorrectCharacters = correctCharacters;
        participant.ProgressEvidenceCount = Math.Min(
            MaximumObservations,
            participant.ProgressEvidenceCount + 1);
        participant.ProgressEvidenceObservedAt = now;
    }

    public static bool SupportsFinish(
        LiveRoomState room,
        LiveParticipantState participant,
        int finalCorrectCharacters,
        DateTimeOffset now)
    {
        if (room.RaceStartsAt is not { } raceStartsAt ||
            participant.ProgressEvidenceInvalid ||
            participant.ProgressEvidenceRoundVersion != room.RoundVersion ||
            participant.ProgressEvidenceCount < 2 ||
            participant.ProgressEvidenceObservedAt is not { } observedAt ||
            participant.ProgressEvidenceRevision <= 0 ||
            participant.ProgressEvidenceRevision > participant.Sequence ||
            participant.ProgressEvidenceCorrectCharacters <= 0 ||
            participant.ProgressEvidenceCorrectCharacters >= finalCorrectCharacters ||
            observedAt < raceStartsAt ||
            observedAt > now)
        {
            return false;
        }

        return CompetitionEligibility.HasPlausibleServerPace(
                   finalCorrectCharacters,
                   now - raceStartsAt) &&
               IsPlausibleGrowth(
                   finalCorrectCharacters - participant.ProgressEvidenceCorrectCharacters,
                   now - observedAt);
    }

    public static void Restore(
        LiveRoomState room,
        LiveParticipantState participant,
        LiveParticipantMemento memento)
    {
        Reset(participant);
        participant.ProgressEvidenceInvalid = memento.ProgressEvidenceInvalid;
        if (memento.ProgressEvidenceCount == 0)
        {
            return;
        }

        var deadline = room.RoundDeadlineAt ?? DateTimeOffset.MaxValue;
        var valid = room.Phase == LiveRoomPhase.Running &&
                    room.RaceStartsAt is { } raceStartsAt &&
                    memento.ProgressEvidenceRoundVersion == room.RoundVersion &&
                    memento.ProgressEvidenceCount is > 0 and <= MaximumObservations &&
                    memento.ProgressEvidenceRevision is > 0 &&
                    memento.ProgressEvidenceRevision <= participant.Sequence &&
                    memento.ProgressEvidenceCorrectCharacters is > 0 &&
                    memento.ProgressEvidenceCorrectCharacters < room.TargetCharacterCount &&
                    memento.ProgressEvidenceObservedAt is { } observedAt &&
                    observedAt >= raceStartsAt &&
                    observedAt <= deadline;
        if (!valid)
        {
            participant.ProgressEvidenceInvalid = true;
            return;
        }

        participant.ProgressEvidenceRoundVersion = memento.ProgressEvidenceRoundVersion;
        participant.ProgressEvidenceRevision = memento.ProgressEvidenceRevision;
        participant.ProgressEvidenceCorrectCharacters = memento.ProgressEvidenceCorrectCharacters;
        participant.ProgressEvidenceCount = memento.ProgressEvidenceCount;
        participant.ProgressEvidenceObservedAt = memento.ProgressEvidenceObservedAt;
    }

    private static bool IsPlausibleGrowth(int growth, TimeSpan elapsed)
    {
        if (growth <= 0 || elapsed < TimeSpan.Zero)
        {
            return false;
        }

        var elapsedMilliseconds = Math.Max(0L, (long)Math.Floor(elapsed.TotalMilliseconds));
        var pacedAllowance = Math.Min(
            int.MaxValue - BurstAllowance,
            elapsedMilliseconds * CompetitionEligibility.MaximumPlausibleGraphemesPerSecond / 1000L);
        return growth <= pacedAllowance + BurstAllowance;
    }
}
