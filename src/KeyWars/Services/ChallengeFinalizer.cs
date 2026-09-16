using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

internal enum ChallengeExpiryOutcome
{
    NotDue,
    ExpiredNeutral,
    FinishedForfeit
}

internal static class ChallengeFinalizer
{
    public static async Task<ChallengeExpiryOutcome> FinalizeExpiredAsync(
        KeyWarsDbContext db,
        Challenge challenge,
        ChallengeTransactionContext challengeTransaction,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        if (challenge.Status is not (ChallengeStatus.Open or ChallengeStatus.Running) ||
            challenge.ExpiresAt > finishedAt)
        {
            return ChallengeExpiryOutcome.NotDue;
        }

        var terminalAt = challenge.ExpiresAt;
        await challengeTransaction.AbortBoundAttemptsAsync(challenge.Id, terminalAt, cancellationToken);
        var participants = await db.ChallengeParticipants
            .Where(item => item.ChallengeId == challenge.Id)
            .ToListAsync(cancellationToken);
        var rounds = await db.ChallengeRounds
            .Where(item => item.ChallengeId == challenge.Id)
            .OrderBy(item => item.RoundNumber)
            .ToListAsync(cancellationToken);
        var roundIds = rounds.Select(item => item.Id).ToArray();
        var results = await db.ChallengeRoundResults
            .Where(item => roundIds.Contains(item.ChallengeRoundId))
            .ToListAsync(cancellationToken);
        var resultProfileIds = results.Select(item => item.UserProfileId).ToHashSet();
        var committed = participants
            .Where(item =>
                item.Status is ParticipantStatus.Joined or ParticipantStatus.Running or ParticipantStatus.Finished or ParticipantStatus.Dnf ||
                resultProfileIds.Contains(item.UserProfileId))
            .ToArray();
        foreach (var participant in participants.Where(item =>
                     item.Status == ParticipantStatus.Invited &&
                     !resultProfileIds.Contains(item.UserProfileId)))
        {
            participant.Status = ParticipantStatus.LeftBeforeStart;
            participant.RespondedAt ??= terminalAt;
            participant.FinishedAt ??= terminalAt;
        }

        if (committed.Length < 2)
        {
            challenge.Status = ChallengeStatus.Expired;
            challenge.FinishedAt ??= terminalAt;
            await db.SaveChangesAsync(cancellationToken);
            return ChallengeExpiryOutcome.ExpiredNeutral;
        }

        var existingResultKeys = results
            .Select(item => (item.ChallengeRoundId, item.UserProfileId))
            .ToHashSet();
        foreach (var participant in committed)
        {
            foreach (var round in rounds)
            {
                if (existingResultKeys.Add((round.Id, participant.UserProfileId)))
                {
                    var forfeit = new ChallengeRoundResult
                    {
                        ChallengeRoundId = round.Id,
                        UserProfileId = participant.UserProfileId,
                        Status = ParticipantStatus.Dnf,
                        CompetitionEligible = true,
                        FinishedAt = terminalAt
                    };
                    db.ChallengeRoundResults.Add(forfeit);
                    results.Add(forfeit);
                }
            }

            if (participant.Status is ParticipantStatus.Declined or ParticipantStatus.Cancelled)
            {
                continue;
            }

            participant.Status = results.Any(item =>
                item.UserProfileId == participant.UserProfileId &&
                item.Status == ParticipantStatus.Finished)
                ? ParticipantStatus.Finished
                : ParticipantStatus.Dnf;
            participant.FinishedAt ??= terminalAt;
        }

        if (challenge.Status == ChallengeStatus.Open)
        {
            challenge.Status = ChallengeStatus.Running;
        }

        await db.SaveChangesAsync(cancellationToken);
        await TryCloseAsync(db, challenge, terminalAt, cancellationToken);
        return ChallengeExpiryOutcome.FinishedForfeit;
    }

    public static async Task TryCloseAsync(
        KeyWarsDbContext db,
        Challenge challenge,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        if (challenge.Status is ChallengeStatus.Expired or ChallengeStatus.Cancelled or ChallengeStatus.Finished)
        {
            return;
        }

        var participants = await db.ChallengeParticipants
            .Where(item => item.ChallengeId == challenge.Id)
            .ToListAsync(cancellationToken);
        var rounds = await db.ChallengeRounds
            .Where(item => item.ChallengeId == challenge.Id)
            .OrderBy(item => item.RoundNumber)
            .ToListAsync(cancellationToken);
        var roundIds = rounds.Select(item => item.Id).ToArray();
        var results = await db.ChallengeRoundResults
            .Where(item => roundIds.Contains(item.ChallengeRoundId))
            .ToListAsync(cancellationToken);
        var resultCounts = results
            .GroupBy(item => item.UserProfileId)
            .ToDictionary(group => group.Key, group => group.Count());
        var terminal = participants.All(item =>
            item.Status is ParticipantStatus.Declined or ParticipantStatus.Cancelled or ParticipantStatus.LeftBeforeStart ||
            resultCounts.GetValueOrDefault(item.UserProfileId) >= challenge.RoundCount);
        if (!terminal)
        {
            return;
        }

        foreach (var round in rounds)
        {
            var roundResults = results.Where(item => item.ChallengeRoundId == round.Id).ToArray();
            var roundRanking = RankRound(roundResults);
            foreach (var rankedResult in roundRanking)
            {
                roundResults.Single(item => item.UserProfileId == rankedResult.Result.UserProfileId).Placement = rankedResult.Placement;
            }
        }

        var ranked = challenge.Mode == ChallengeMode.BestOf
            ? RankSeries(participants, results)
            : RankRound(results);
        foreach (var rankedResult in ranked)
        {
            participants.Single(item => item.UserProfileId == rankedResult.Result.UserProfileId).Placement = rankedResult.Placement;
        }

        var ratingParticipants = participants
            .Where(item =>
                item.Status is not (ParticipantStatus.Declined or ParticipantStatus.Cancelled or ParticipantStatus.LeftBeforeStart) &&
                results.Any(result => result.UserProfileId == item.UserProfileId))
            .ToArray();
        var ratingProfileIds = ratingParticipants.Select(item => item.UserProfileId).ToHashSet();
        var ratingResults = results
            .Where(item => ratingProfileIds.Contains(item.UserProfileId))
            .Select(ForRating)
            .ToArray();
        var ratingRanked = challenge.Mode == ChallengeMode.BestOf
            ? RankSeriesForRating(ratingParticipants, ratingResults)
            : RankRound(ratingResults);
        if (challenge.RatingEligible &&
            ratingRanked.Count >= 2 &&
            ratingResults.Any(item => item.Status == ParticipantStatus.Finished))
        {
            var participantIds = participants.Select(item => item.UserProfileId).Distinct().Order().ToArray();
            await ProfileWriteFence.AcquireAsync(db, participantIds, cancellationToken);
            if (!await ProfileWriteFence.IsAvailableAsync(db, participantIds, cancellationToken))
            {
                challenge.RatingEligible = false;
            }
            else
            {
                var ids = ratingRanked.Select(item => item.Result.UserProfileId).Distinct().Order().ToArray();
                var profiles = await db.UserProfiles
                    .Where(item => ids.Contains(item.Id) && !item.Deleted)
                    .ToListAsync(cancellationToken);
                var ratings = profiles.ToDictionary(item => item.Id, item => item.ArenaRating);
                var ratingChanges = MultiplayerRating.CalculatePairwiseEloChanges(ratings, ratingRanked);
                foreach (var profile in profiles)
                {
                    var ratingChange = ratingChanges[profile.Id];
                    profile.ArenaRating = ratingChange.RatingAfter;
                    profile.RatedMatchCount++;
                    var participant = participants.Single(item => item.UserProfileId == profile.Id);
                    participant.RatingBefore = ratingChange.RatingBefore;
                    participant.RatingDelta = ratingChange.RatingDelta;
                    participant.RatingAfter = ratingChange.RatingAfter;
                }
            }
        }

        challenge.Status = ChallengeStatus.Finished;
        challenge.FinishedAt = finishedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ChallengeRoundResult ForRating(ChallengeRoundResult result)
    {
        if (result.Status == ParticipantStatus.Finished && result.CompetitionEligible)
        {
            return result;
        }

        return new ChallengeRoundResult
        {
            Id = result.Id,
            ChallengeRoundId = result.ChallengeRoundId,
            UserProfileId = result.UserProfileId,
            TypingAttemptId = result.TypingAttemptId,
            Status = ParticipantStatus.Dnf,
            CompetitionEligible = true,
            FinishedAt = result.FinishedAt
        };
    }

    private static IReadOnlyList<RankedRaceResult> RankRound(IEnumerable<ChallengeRoundResult> results) =>
        RaceRanking.RankClassic(results.Select(result => new RaceResult(
            result.UserProfileId,
            result.Status,
            result.DurationMilliseconds,
            result.Accuracy,
            0,
            result.Consistency,
            result.Wpm,
            0)));

    private static IReadOnlyList<RankedRaceResult> RankSeries(
        IReadOnlyCollection<ChallengeParticipant> participants,
        IReadOnlyCollection<ChallengeRoundResult> results) =>
        RankSeries(participants, results, result => result.Placement);

    private static IReadOnlyList<RankedRaceResult> RankSeriesForRating(
        IReadOnlyCollection<ChallengeParticipant> participants,
        IReadOnlyCollection<ChallengeRoundResult> results)
    {
        var activeParticipants = participants
            .Where(item => item.Status is not (ParticipantStatus.Declined or ParticipantStatus.Cancelled or ParticipantStatus.LeftBeforeStart))
            .ToArray();
        var activeProfileIds = activeParticipants
            .Select(item => item.UserProfileId)
            .ToHashSet();
        var activeResults = results
            .Where(item => activeProfileIds.Contains(item.UserProfileId))
            .ToArray();
        var rerankedPlacements = activeResults
            .GroupBy(item => item.ChallengeRoundId)
            .SelectMany(round => RankRound(round).Select(item => new
            {
                RoundId = round.Key,
                item.Result.UserProfileId,
                item.Placement
            }))
            .ToDictionary(
                item => (item.RoundId, item.UserProfileId),
                item => (int?)item.Placement);
        return RankSeries(
            activeParticipants,
            activeResults,
            result => rerankedPlacements.GetValueOrDefault(
                (result.ChallengeRoundId, result.UserProfileId)));
    }

    private static IReadOnlyList<RankedRaceResult> RankSeries(
        IReadOnlyCollection<ChallengeParticipant> participants,
        IReadOnlyCollection<ChallengeRoundResult> results,
        Func<ChallengeRoundResult, int?> placement)
    {
        var activeParticipants = participants
            .Where(item => item.Status is not (ParticipantStatus.Declined or ParticipantStatus.Cancelled or ParticipantStatus.LeftBeforeStart))
            .ToArray();
        var participantCount = activeParticipants.Length;
        var rankedSeries = ArenaScoring.RankSeries(activeParticipants.Select(participant =>
        {
            var participantResults = results.Where(item => item.UserProfileId == participant.UserProfileId).ToArray();
            return new ArenaSeriesScore(
                participant.UserProfileId,
                participantResults.Sum(item => ArenaScoring.PointsForRound(item.Status, placement(item), participantCount)),
                participantResults.Count(item => item.Status == ParticipantStatus.Finished && placement(item) == 1),
                participantResults.Count(item => item.Status == ParticipantStatus.Finished),
                participantResults.Sum(item => item.DurationMilliseconds),
                participantResults.Length == 0 ? 0 : participantResults.Average(item => item.Accuracy));
        }));

        return rankedSeries.Select(item => new RankedRaceResult(
            new RaceResult(
                item.Score.UserProfileId,
                item.Score.FinishedRounds > 0 ? ParticipantStatus.Finished : ParticipantStatus.Dnf,
                item.Score.TotalDurationMilliseconds,
                item.Score.AverageAccuracy,
                0,
                0,
                0,
                0),
            item.Placement)).ToArray();
    }
}
