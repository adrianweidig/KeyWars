using System.Security.Cryptography;
using System.Text;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record CreateChallengeRequest(string Title, Guid TrainingTextId, ChallengeMode Mode, IReadOnlyCollection<Guid> ParticipantIds, int RoundCount, int ExpiryDays);

public static class ChallengeErrorCodes
{
    public const string InvalidRequest = "challenge_invalid_request";
    public const string NotFound = "challenge_not_found";
    public const string Conflict = "challenge_conflict";
    public const string InvalidAttempt = "challenge_invalid_attempt";
    public const string Expired = "challenge_expired";
}

public sealed class ChallengeLifecycleException(string code, int statusCode, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class ChallengeService(
    KeyWarsDbContext db,
    IOptions<ChallengeOptions> options,
    TimeProvider timeProvider)
{
    private static readonly AsyncKeyedLock<Guid> ChallengeLocks = new();
    private const int BadRequestStatus = 400;
    private const int NotFoundStatus = 404;
    private const int ConflictStatus = 409;
    private const int GoneStatus = 410;

    public async Task<Challenge> CreateAsync(Guid creatorProfileId, CreateChallengeRequest request, CancellationToken cancellationToken = default)
    {
        var challengeId = Guid.CreateVersion7();
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        if (request.Mode != ChallengeMode.Classic)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Aktuell ist nur der Challenge-Modus \"Klassisches Rennen\" implementiert.");
        }

        if (request.RoundCount != 1)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Mehrere Runden werden erst mit der Serienlogik aktiviert.");
        }

        if (request.ParticipantIds is null)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Die Teilnehmerliste fehlt.");
        }

        if (request.ParticipantIds.Count != request.ParticipantIds.Distinct().Count())
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Teilnehmer dürfen nicht doppelt ausgewählt werden.");
        }

        var participants = request.ParticipantIds.Append(creatorProfileId).Distinct().ToArray();
        if (participants.Length < 2)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Eine Herausforderung benötigt mindestens zwei Personen.");
        }

        if (participants.Length > options.Value.MaxParticipants)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, $"Maximal {options.Value.MaxParticipants} Personen sind erlaubt.");
        }

        var profiles = await db.UserProfiles.Where(item => participants.Contains(item.Id) && !item.Deleted).ToListAsync(cancellationToken);
        if (profiles.Count != participants.Length)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Mindestens eine ausgewählte Person ist nicht verfügbar.");
        }

        if (profiles.Any(item => item.Id != creatorProfileId && !item.ChallengesEnabled))
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Mindestens eine ausgewählte Person nimmt keine Herausforderungen an.");
        }

        var text = await db.TrainingTexts.SingleOrDefaultAsync(item =>
            item.Id == request.TrainingTextId &&
            (item.IsStandard || item.Visibility == TrainingTextVisibility.Organization || item.OwnerProfileId == creatorProfileId), cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Der Trainingstext ist für diese Herausforderung nicht verfügbar.");
        var now = timeProvider.GetUtcNow();
        var challenge = new Challenge
        {
            Id = challengeId,
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
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        return await StartAttemptCoreAsync(challengeId, profileId, attempts, cancellationToken);
    }

    private async Task<AttemptSession> StartAttemptCoreAsync(Guid challengeId, Guid profileId, AttemptService attempts, CancellationToken cancellationToken)
    {
        var challenge = await RequireActiveChallengeAsync(challengeId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Die Herausforderung muss vor dem Start angenommen werden.");
        }

        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung kann nicht mehr gestartet werden.");
        }

        var round = await RequireRoundAsync(challengeId, cancellationToken);
        var existingResult = await db.ChallengeRoundResults.AnyAsync(item => item.ChallengeRoundId == round.Id && item.UserProfileId == profileId, cancellationToken);
        if (existingResult)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Challenge-Runde wurde bereits abgeschlossen.");
        }

        var existingBinding = await db.ChallengeAttemptBindings.SingleOrDefaultAsync(item => item.ChallengeRoundId == round.Id && item.UserProfileId == profileId, cancellationToken);
        if (existingBinding is not null)
        {
            if (!existingBinding.Consumed && await attempts.TryGetActiveSessionAsync(profileId, existingBinding.TypingAttemptId, cancellationToken) is { } existingSession)
            {
                if (challenge.Status == ChallengeStatus.Open)
                {
                    challenge.Status = ChallengeStatus.Running;
                }

                if (participant.Status == ParticipantStatus.Joined)
                {
                    participant.Status = ParticipantStatus.Running;
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return existingSession;
            }

            var boundAttemptPhase = await db.TypingAttempts
                .Where(item => item.Id == existingBinding.TypingAttemptId)
                .Select(item => (AttemptPhase?)item.Phase)
                .SingleOrDefaultAsync(cancellationToken);
            if (!existingBinding.Consumed && boundAttemptPhase == AttemptPhase.Aborted)
            {
                db.ChallengeAttemptBindings.Remove(existingBinding);
                if (participant.Status == ParticipantStatus.Running)
                {
                    participant.Status = ParticipantStatus.Joined;
                }

                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Für diese Challenge-Runde wurde bereits ein Versuch gestartet.");
            }
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
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            participant.Status = ParticipantStatus.Joined;
            participant.RespondedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeclineAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
        if (participant.Status is ParticipantStatus.Invited or ParticipantStatus.Joined)
        {
            participant.Status = ParticipantStatus.Declined;
            participant.RespondedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RequirePlayableAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Die Herausforderung muss vor dem Start angenommen werden.");
        }

        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist für dich abgeschlossen.");
        }
    }

    public async Task FinishRoundAsync(Guid challengeId, Guid profileId, TypingAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        await FinishRoundCoreAsync(challengeId, profileId, attempt, cancellationToken);
    }

    private async Task FinishRoundCoreAsync(Guid challengeId, Guid profileId, TypingAttempt attempt, CancellationToken cancellationToken)
    {
        var existingRound = await db.ChallengeRounds.SingleOrDefaultAsync(
            item => item.ChallengeId == challengeId && item.RoundNumber == 1,
            cancellationToken);
        if (existingRound is not null)
        {
            var existingResult = await db.ChallengeRoundResults.SingleOrDefaultAsync(
                item => item.ChallengeRoundId == existingRound.Id && item.UserProfileId == profileId,
                cancellationToken);
            if (existingResult is not null)
            {
                if (existingResult.TypingAttemptId != attempt.Id)
                {
                    throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Die Challenge-Runde wurde bereits mit einem anderen Versuch abgeschlossen.");
                }

                await ExecuteInTransactionAsync(
                    () => TryCloseCoreAsync(challengeId, cancellationToken),
                    cancellationToken);
                return;
            }
        }

        var challenge = await RequireActiveChallengeAsync(challengeId, cancellationToken);
        var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Die Herausforderung muss vor dem Abschluss angenommen werden.");
        }

        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Challenge-Runde ist für dich bereits abgeschlossen.");
        }

        var round = existingRound ?? await RequireRoundAsync(challengeId, cancellationToken);
        var binding = await db.ChallengeAttemptBindings.SingleOrDefaultAsync(item =>
            item.ChallengeId == challengeId &&
            item.ChallengeRoundId == round.Id &&
            item.UserProfileId == profileId &&
            item.TypingAttemptId == attempt.Id, cancellationToken);
        if (binding is null ||
            binding.Consumed ||
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
            throw ChallengeError(ChallengeErrorCodes.InvalidAttempt, ConflictStatus, "Der Versuch gehört nicht zu dieser Herausforderung.");
        }

        await ExecuteInTransactionAsync(async () =>
        {
            if (challenge.Status == ChallengeStatus.Open)
            {
                challenge.Status = ChallengeStatus.Running;
            }

            var now = timeProvider.GetUtcNow();
            binding.Consumed = true;
            binding.ConsumedAt = now;
            participant.Status = attempt.Completed ? ParticipantStatus.Finished : ParticipantStatus.Dnf;
            participant.FinishedAt = now;
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
            await TryCloseCoreAsync(challenge.Id, cancellationToken);
        }, cancellationToken);
    }

    public async Task TryCloseAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
        await TryCloseCoreAsync(challengeId, cancellationToken);
    }

    private async Task TryCloseCoreAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        var challenge = await RequireChallengeAsync(challengeId, cancellationToken);
        if (await ExpireIfDueAsync(challenge, cancellationToken))
        {
            return;
        }

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

        var round = await RequireRoundAsync(challengeId, cancellationToken);
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
            var ratingChanges = MultiplayerRating.CalculatePairwiseEloChanges(ratings, ranked);
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

        challenge.Status = ChallengeStatus.Finished;
        challenge.FinishedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Challenge> RequireActiveChallengeAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        var challenge = await RequireChallengeAsync(challengeId, cancellationToken);
        if (await ExpireIfDueAsync(challenge, cancellationToken) || challenge.Status == ChallengeStatus.Expired)
        {
            throw ChallengeError(ChallengeErrorCodes.Expired, GoneStatus, "Diese Herausforderung ist abgelaufen.");
        }

        if (challenge.Status is ChallengeStatus.Finished or ChallengeStatus.Cancelled)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist nicht mehr aktiv.");
        }

        return challenge;
    }

    private async Task ExpireDueChallengesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await db.Challenges
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var candidateIds = candidates
            .Where(item =>
                item.Status is (ChallengeStatus.Open or ChallengeStatus.Running) &&
                item.ExpiresAt <= now)
            .Select(item => item.Id)
            .ToArray();
        foreach (var challengeId in candidateIds)
        {
            await using var challengeLock = await ChallengeLocks.AcquireAsync(challengeId, cancellationToken);
            var challenge = await db.Challenges.SingleOrDefaultAsync(item => item.Id == challengeId, cancellationToken);
            if (challenge is not null)
            {
                await ExpireIfDueAsync(challenge, cancellationToken);
            }
        }
    }

    private async Task<bool> ExpireIfDueAsync(Challenge challenge, CancellationToken cancellationToken)
    {
        if (challenge.Status is not (ChallengeStatus.Open or ChallengeStatus.Running) ||
            challenge.ExpiresAt > timeProvider.GetUtcNow())
        {
            return false;
        }

        challenge.Status = ChallengeStatus.Expired;
        challenge.FinishedAt ??= timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Challenge> RequireChallengeAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        return await db.Challenges.SingleOrDefaultAsync(item => item.Id == challengeId, cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.NotFound, NotFoundStatus, "Diese Herausforderung wurde nicht gefunden.");
    }

    private async Task<ChallengeParticipant> RequireParticipantAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken)
    {
        return await db.ChallengeParticipants.SingleOrDefaultAsync(
                item => item.ChallengeId == challengeId && item.UserProfileId == profileId,
                cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.NotFound, NotFoundStatus, "Diese Herausforderung wurde nicht gefunden.");
    }

    private async Task<ChallengeRound> RequireRoundAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        return await db.ChallengeRounds.SingleOrDefaultAsync(
                item => item.ChallengeId == challengeId && item.RoundNumber == 1,
                cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.NotFound, NotFoundStatus, "Die Challenge-Runde wurde nicht gefunden.");
    }

    private async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await operation();
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original challenge failure.
            }

            db.ChangeTracker.Clear();
            throw;
        }
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

    private static ChallengeLifecycleException ChallengeError(string code, int statusCode, string message) =>
        new(code, statusCode, message);
}
