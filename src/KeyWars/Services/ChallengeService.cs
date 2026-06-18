using System.Security.Cryptography;
using System.Text;
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

        if (request.RoundCount != 1)
        {
            throw new InvalidOperationException("Mehrere Runden werden erst mit der Serienlogik aktiviert.");
        }

        if (request.ParticipantIds.Count != request.ParticipantIds.Distinct().Count())
        {
            throw new InvalidOperationException("Teilnehmer dürfen nicht doppelt ausgewählt werden.");
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

        var profiles = await db.UserProfiles.Where(item => participants.Contains(item.Id) && !item.Deleted).ToListAsync(cancellationToken);
        if (profiles.Count != participants.Length)
        {
            throw new InvalidOperationException("Mindestens eine ausgewählte Person ist nicht verfügbar.");
        }

        if (profiles.Any(item => item.Id != creatorProfileId && !item.ChallengesEnabled))
        {
            throw new InvalidOperationException("Mindestens eine ausgewählte Person nimmt keine Herausforderungen an.");
        }

        var text = await db.TrainingTexts.SingleAsync(item =>
            item.Id == request.TrainingTextId &&
            (item.IsStandard || item.Visibility == TrainingTextVisibility.Organization || item.OwnerProfileId == creatorProfileId), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var challenge = new Challenge
        {
            CreatorProfileId = creatorProfileId,
            TrainingTextId = text.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? text.Title : request.Title.Trim(),
            Mode = request.Mode,
            RoundCount = 1,
            RatingEligible = text.RatingEligible && request.Mode is ChallengeMode.Classic or ChallengeMode.BestOf,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(request.ExpiryDays, 1, 30))
        };
        db.Challenges.Add(challenge);
        db.ChallengeRounds.Add(new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1, CreatedAt = now });

        foreach (var participantId in participants)
        {
            db.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = participantId,
                InvitedAt = now,
                Status = participantId == creatorProfileId ? ParticipantStatus.Joined : ParticipantStatus.Invited
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    public async Task<AttemptSession> StartAttemptAsync(Guid challengeId, Guid profileId, AttemptService attempts, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var challenge = await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw new InvalidOperationException("Die Herausforderung muss vor dem Start angenommen werden.");
        }

        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            throw new InvalidOperationException("Diese Herausforderung kann nicht mehr gestartet werden.");
        }

        var round = await db.ChallengeRounds.SingleAsync(item => item.ChallengeId == challengeId && item.RoundNumber == 1, cancellationToken);
        var existingBinding = await db.ChallengeAttemptBindings.AnyAsync(item => item.ChallengeRoundId == round.Id && item.UserProfileId == profileId, cancellationToken);
        if (existingBinding)
        {
            throw new InvalidOperationException("Für diese Challenge-Runde wurde bereits ein Versuch gestartet.");
        }

        var existingResult = await db.ChallengeRoundResults.AnyAsync(item => item.ChallengeRoundId == round.Id && item.UserProfileId == profileId, cancellationToken);
        if (existingResult)
        {
            throw new InvalidOperationException("Diese Challenge-Runde wurde bereits abgeschlossen.");
        }

        var session = await attempts.StartAsync(profileId, new StartAttemptRequest(TrainingMode.Text, challenge.TrainingTextId, null, null), cancellationToken);
        db.ChallengeAttemptBindings.Add(new ChallengeAttemptBinding
        {
            ChallengeId = challenge.Id,
            ChallengeRoundId = round.Id,
            UserProfileId = profileId,
            TypingAttemptId = session.Id,
            TextSnapshotHash = ComputeTextHash(session.Text),
            Mode = TrainingMode.Text,
            BindingToken = CreateBindingToken(),
            CreatedAt = timeProvider.GetUtcNow()
        });

        if (challenge.Status == ChallengeStatus.Open)
        {
            challenge.Status = ChallengeStatus.Running;
        }

        participant.Status = ParticipantStatus.Running;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<Challenge>> ListForProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await ExpireDueChallengesAsync(cancellationToken);
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
        await RequireActiveChallengeAsync(challengeId, cancellationToken);
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
        await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status is ParticipantStatus.Invited or ParticipantStatus.Joined)
        {
            participant.Status = ParticipantStatus.Declined;
            participant.RespondedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RequirePlayableAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw new InvalidOperationException("Die Herausforderung muss vor dem Start angenommen werden.");
        }

        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            throw new InvalidOperationException("Diese Herausforderung ist für dich abgeschlossen.");
        }
    }

    public async Task FinishRoundAsync(Guid challengeId, Guid profileId, TypingAttempt attempt, CancellationToken cancellationToken = default)
    {
        var challenge = await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challengeId && item.UserProfileId == profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw new InvalidOperationException("Die Herausforderung muss vor dem Abschluss angenommen werden.");
        }

        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            return;
        }

        var round = await db.ChallengeRounds.SingleAsync(item => item.ChallengeId == challengeId && item.RoundNumber == 1, cancellationToken);
        var binding = await db.ChallengeAttemptBindings.SingleOrDefaultAsync(item =>
            item.ChallengeId == challengeId &&
            item.ChallengeRoundId == round.Id &&
            item.UserProfileId == profileId &&
            item.TypingAttemptId == attempt.Id, cancellationToken);
        if (binding is null)
        {
            throw new InvalidOperationException("Der Versuch gehört nicht zu dieser Herausforderung.");
        }

        var duplicateResult = await db.ChallengeRoundResults.AnyAsync(item =>
            item.ChallengeRoundId == round.Id &&
            item.UserProfileId == profileId, cancellationToken);
        if (duplicateResult)
        {
            return;
        }

        if (binding.Consumed ||
            binding.Mode != attempt.Mode ||
            binding.TextSnapshotHash != attempt.TextHash ||
            attempt.TrainingTextId != challenge.TrainingTextId ||
            attempt.UserProfileId != profileId ||
            attempt.Mode != TrainingMode.Text ||
            !attempt.Official ||
            attempt.FinishedAt is null ||
            attempt.StartedAt < challenge.CreatedAt ||
            attempt.FinishedAt > challenge.ExpiresAt)
        {
            throw new InvalidOperationException("Der Versuch gehört nicht zu dieser Herausforderung.");
        }

        if (challenge.Status == ChallengeStatus.Open)
        {
            challenge.Status = ChallengeStatus.Running;
        }

        binding.Consumed = true;
        binding.ConsumedAt = timeProvider.GetUtcNow();
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
        await ExpireDueChallengesAsync(cancellationToken);
        var challenge = await db.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
        if (challenge.Status is ChallengeStatus.Expired or ChallengeStatus.Cancelled or ChallengeStatus.Finished)
        {
            return;
        }

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

    private async Task<Challenge> RequireActiveChallengeAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        await ExpireDueChallengesAsync(cancellationToken);
        var challenge = await db.Challenges.SingleAsync(item => item.Id == challengeId, cancellationToken);
        if (challenge.Status is ChallengeStatus.Finished or ChallengeStatus.Cancelled or ChallengeStatus.Expired)
        {
            throw new InvalidOperationException("Diese Herausforderung ist nicht mehr aktiv.");
        }

        return challenge;
    }

    private async Task ExpireDueChallengesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await db.Challenges.ToListAsync(cancellationToken);
        var expired = candidates
            .Where(item => item.Status is (ChallengeStatus.Open or ChallengeStatus.Running) && item.ExpiresAt <= now)
            .ToList();
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var challenge in expired)
        {
            challenge.Status = ChallengeStatus.Expired;
            challenge.FinishedAt ??= now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string ComputeTextHash(string text)
    {
        var normalized = TypingEngine.NormalizeText(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string CreateBindingToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    }
}
