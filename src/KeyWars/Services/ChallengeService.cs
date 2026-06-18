using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record CreateChallengeRequest(string Title, Guid TrainingTextId, ChallengeMode Mode, IReadOnlyCollection<Guid> ParticipantIds, int RoundCount, int ExpiryDays);

public sealed class ChallengeService(
    KeyWarsDbContext db,
    IOptions<ChallengeOptions> options,
    TimeProvider timeProvider)
{
    public async Task<Challenge> CreateAsync(Guid creatorProfileId, CreateChallengeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Mode != ChallengeMode.Classic)
        {
            throw new InvalidOperationException("Aktuell ist nur der Challenge-Modus \"Klassisches Rennen\" implementiert.");
        }

        var participants = request.ParticipantIds.Append(creatorProfileId).Distinct().ToArray();
        if (participants.Length < 2)
        {
            throw new InvalidOperationException("Eine Herausforderung benötigt mindestens zwei Personen.");
        }

        if (participants.Length > options.Value.MaxParticipants)
        {
            throw new InvalidOperationException($"Maximal {options.Value.MaxParticipants} Personen sind erlaubt.");
        }

        var text = await db.TrainingTexts.SingleAsync(item =>
            item.Id == request.TrainingTextId &&
            (item.IsStandard || item.Visibility == TrainingTextVisibility.Organization || item.OwnerProfileId == creatorProfileId), cancellationToken);
        var challenge = new Challenge
        {
            CreatorProfileId = creatorProfileId,
            TrainingTextId = text.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? text.Title : request.Title.Trim(),
            Mode = request.Mode,
            RoundCount = 1,
            RatingEligible = text.RatingEligible && request.Mode is ChallengeMode.Classic or ChallengeMode.BestOf,
            ExpiresAt = timeProvider.GetUtcNow().AddDays(Math.Clamp(request.ExpiryDays, 1, 30))
        };
        db.Challenges.Add(challenge);
        db.ChallengeRounds.Add(new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1 });

        foreach (var participantId in participants)
        {
            db.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = participantId,
                Status = participantId == creatorProfileId ? ParticipantStatus.Joined : ParticipantStatus.Invited
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    public async Task<IReadOnlyList<Challenge>> ListForProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var ids = await db.ChallengeParticipants
            .Where(item => item.UserProfileId == profileId)
            .Select(item => item.ChallengeId)
            .ToListAsync(cancellationToken);

        return (await db.Challenges
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .ToList();
    }

    public async Task JoinAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            participant.Status = ParticipantStatus.Joined;
            participant.RespondedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeclineAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status is ParticipantStatus.Invited or ParticipantStatus.Joined)
        {
            participant.Status = ParticipantStatus.Declined;
            participant.RespondedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task FinishRoundAsync(Guid challengeId, Guid profileId, TypingAttempt attempt, CancellationToken cancellationToken = default)
    {
        var challenge = await db.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
        if (challenge.Status is ChallengeStatus.Finished or ChallengeStatus.Cancelled or ChallengeStatus.Expired)
        {
            throw new InvalidOperationException("Diese Herausforderung ist nicht mehr aktiv.");
        }

        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            return;
        }

        if (attempt.TrainingTextId != challenge.TrainingTextId || attempt.UserProfileId != profileId)
        {
            throw new InvalidOperationException("Der Versuch gehört nicht zu dieser Herausforderung.");
        }

        var duplicateResult = await db.ChallengeRoundResults.AnyAsync(item =>
            item.UserProfileId == profileId &&
            db.ChallengeRounds.Any(round => round.Id == item.ChallengeRoundId && round.ChallengeId == challengeId), cancellationToken);
        if (duplicateResult)
        {
            return;
        }

        var round = await db.ChallengeRounds.SingleAsync(item => item.ChallengeId == challengeId && item.RoundNumber == 1, cancellationToken);
        participant.Status = attempt.Completed ? ParticipantStatus.Finished : ParticipantStatus.Dnf;
        participant.FinishedAt = timeProvider.GetUtcNow();
        db.ChallengeRoundResults.Add(new ChallengeRoundResult
        {
            ChallengeRoundId = round.Id,
            UserProfileId = profileId,
            TypingAttemptId = attempt.Id,
            Status = participant.Status,
            DurationMilliseconds = attempt.DurationMilliseconds,
            Accuracy = attempt.Accuracy,
            Consistency = attempt.Consistency,
            Wpm = attempt.Wpm,
            FinishedAt = participant.FinishedAt
        });

        await db.SaveChangesAsync(cancellationToken);
        await TryCloseAsync(challenge.Id, cancellationToken);
    }

    public async Task TryCloseAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        var challenge = await db.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
        var participants = await db.ChallengeParticipants.Where(item => item.ChallengeId == challengeId).ToListAsync(cancellationToken);
        var terminal = participants.All(item => item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined);
        if (!terminal)
        {
            return;
        }

        var round = await db.ChallengeRounds.SingleAsync(item => item.ChallengeId == challengeId && item.RoundNumber == 1, cancellationToken);
        var results = await db.ChallengeRoundResults.Where(item => item.ChallengeRoundId == round.Id).ToListAsync(cancellationToken);
        var ranked = RaceRanking.RankClassic(results.Select(result => new RaceResult(
            result.UserProfileId,
            result.Status,
            result.DurationMilliseconds,
            result.Accuracy,
            0,
            result.Consistency,
            result.Wpm,
            0)));

        foreach (var rankedResult in ranked)
        {
            var participant = participants.Single(item => item.UserProfileId == rankedResult.Result.UserProfileId);
            participant.Placement = rankedResult.Placement;
            var result = results.Single(item => item.UserProfileId == rankedResult.Result.UserProfileId);
            result.Placement = rankedResult.Placement;
        }

        if (challenge.RatingEligible && ranked.Count >= 2)
        {
            var ids = ranked.Select(item => item.Result.UserProfileId).ToArray();
            var profiles = await db.UserProfiles.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
            var ratings = profiles.ToDictionary(item => item.Id, item => item.ArenaRating);
            var deltas = MultiplayerRating.CalculatePairwiseElo(ratings, ranked);
            foreach (var profile in profiles)
            {
                profile.ArenaRating += deltas[profile.Id];
                profile.RatedMatchCount++;
                var participant = participants.Single(item => item.UserProfileId == profile.Id);
                participant.RatingDelta = deltas[profile.Id];
            }
        }

        challenge.Status = ChallengeStatus.Finished;
        challenge.FinishedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
