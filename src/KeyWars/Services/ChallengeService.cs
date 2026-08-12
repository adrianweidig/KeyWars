using System.Security.Cryptography;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record CreateChallengeRequest(string Title, Guid TrainingTextId, ChallengeMode Mode, IReadOnlyCollection<Guid> ParticipantIds, int RoundCount, int ExpiryDays);

public enum ChallengeListFilter
{
    All,
    Invitations,
    Active,
    Completed
}

public sealed record ChallengeListItem(
    Challenge Challenge,
    ParticipantStatus ParticipantStatus,
    bool IsUnread);

public sealed record ChallengeListPage(
    IReadOnlyList<ChallengeListItem> Items,
    ChallengeListFilter Filter,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    int UnreadCount);

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
    TimeProvider timeProvider,
    IChallengeLockProvider? lockProvider = null)
{
    private readonly IChallengeLockProvider challengeLocks = lockProvider ?? LocalChallengeLockProvider.Shared;
    private const int BadRequestStatus = 400;
    private const int NotFoundStatus = 404;
    private const int ConflictStatus = 409;
    private const int GoneStatus = 410;

    public async Task<Challenge> CreateAsync(Guid creatorProfileId, CreateChallengeRequest request, CancellationToken cancellationToken = default)
    {
        var challengeId = Guid.CreateVersion7();
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        return await CreateCoreAsync(challengeId, creatorProfileId, request, null, cancellationToken);
    }

    private async Task<Challenge> CreateCoreAsync(
        Guid challengeId,
        Guid creatorProfileId,
        CreateChallengeRequest request,
        Guid? rematchOfChallengeId,
        CancellationToken cancellationToken)
    {
        if (request.Mode is not (ChallengeMode.Classic or ChallengeMode.BestOf))
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Verfügbar sind klassische Rennen und Best-of-Serien.");
        }

        if (request.Mode == ChallengeMode.Classic && request.RoundCount != 1)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Klassische Herausforderungen laufen über genau eine Runde.");
        }

        if (request.Mode == ChallengeMode.BestOf && request.RoundCount is not (3 or 5))
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Best-of-Serien laufen über drei oder fünf Runden.");
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
            !item.IsQuarantined &&
            (item.IsStandard || item.Visibility == TrainingTextVisibility.Organization || item.OwnerProfileId == creatorProfileId), cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Der Trainingstext ist für diese Herausforderung nicht verfügbar.");
        var now = timeProvider.GetUtcNow();
        var title = string.IsNullOrWhiteSpace(request.Title) ? text.Title : request.Title.Trim();
        if (title.Length > 160)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidRequest, BadRequestStatus, "Der Titel darf höchstens 160 Zeichen lang sein.");
        }

        var challenge = new Challenge
        {
            Id = challengeId,
            RematchOfChallengeId = rematchOfChallengeId,
            CreatorProfileId = creatorProfileId,
            TrainingTextId = text.Id,
            Title = title,
            Mode = request.Mode,
            RoundCount = request.RoundCount,
            RatingEligible = text.RatingEligible && request.Mode is ChallengeMode.Classic or ChallengeMode.BestOf,
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(request.ExpiryDays, 1, 30))
        };
        db.Challenges.Add(challenge);
        for (var roundNumber = 1; roundNumber <= challenge.RoundCount; roundNumber++)
        {
            db.ChallengeRounds.Add(new ChallengeRound
            {
                ChallengeId = challenge.Id,
                RoundNumber = roundNumber,
                CreatedAt = now
            });
        }

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
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
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

        var round = await RequireNextRoundAsync(challengeId, profileId, cancellationToken);

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
            TextSnapshotHash = TextHash.Compute(session.Text),
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
        var page = await ListPageForProfileAsync(profileId, ChallengeListFilter.All, 1, 50, cancellationToken);
        return page.Items.Select(item => item.Challenge).ToArray();
    }

    public async Task<ChallengeListPage> ListPageForProfileAsync(
        Guid profileId,
        ChallengeListFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await ExpireDueChallengesAsync(cancellationToken);
        var boundedPage = Math.Max(1, page);
        var boundedPageSize = Math.Clamp(pageSize, 1, 50);
        var activeStatuses = new[] { ChallengeStatus.Open, ChallengeStatus.Running };
        var query =
            from participant in db.ChallengeParticipants.AsNoTracking()
            join challenge in db.Challenges.AsNoTracking() on participant.ChallengeId equals challenge.Id
            where participant.UserProfileId == profileId
            select new { Challenge = challenge, participant.Status };

        query = filter switch
        {
            ChallengeListFilter.Invitations => query.Where(item =>
                activeStatuses.Contains(item.Challenge.Status) &&
                item.Status == ParticipantStatus.Invited),
            ChallengeListFilter.Active => query.Where(item =>
                activeStatuses.Contains(item.Challenge.Status) &&
                item.Status != ParticipantStatus.Declined &&
                item.Status != ParticipantStatus.Cancelled),
            ChallengeListFilter.Completed => query.Where(item =>
                item.Challenge.Status == ChallengeStatus.Finished ||
                item.Challenge.Status == ChallengeStatus.Expired ||
                item.Challenge.Status == ChallengeStatus.Cancelled),
            _ => query
        };

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)boundedPageSize));
        boundedPage = Math.Min(boundedPage, totalPages);
        var rows = await query
            .OrderByDescending(item => item.Status == ParticipantStatus.Invited)
            .ThenByDescending(item => item.Challenge.Id)
            .Skip((boundedPage - 1) * boundedPageSize)
            .Take(boundedPageSize)
            .ToListAsync(cancellationToken);
        var unreadCount = await (
                from participant in db.ChallengeParticipants.AsNoTracking()
                join challenge in db.Challenges.AsNoTracking() on participant.ChallengeId equals challenge.Id
                where participant.UserProfileId == profileId &&
                    participant.Status == ParticipantStatus.Invited &&
                    (challenge.Status == ChallengeStatus.Open || challenge.Status == ChallengeStatus.Running)
                select participant)
            .CountAsync(cancellationToken);

        return new ChallengeListPage(
            rows.Select(item => new ChallengeListItem(
                item.Challenge,
                item.Status,
                item.Status == ParticipantStatus.Invited && activeStatuses.Contains(item.Challenge.Status)))
                .ToArray(),
            filter,
            boundedPage,
            boundedPageSize,
            totalCount,
            totalPages,
            unreadCount);
    }

    public async Task<Challenge> CancelAsync(Guid challengeId, Guid creatorProfileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        var challenge = await RequireChallengeAsync(challengeId, cancellationToken);
        EnsureCreator(challenge, creatorProfileId);
        if (challenge.Status == ChallengeStatus.Cancelled)
        {
            return challenge;
        }

        if (challenge.Status is ChallengeStatus.Finished or ChallengeStatus.Expired)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist bereits abgeschlossen und kann nicht mehr abgebrochen werden.");
        }

        var now = timeProvider.GetUtcNow();
        challenge.Status = ChallengeStatus.Cancelled;
        challenge.FinishedAt = now;
        var participants = await db.ChallengeParticipants
            .Where(item => item.ChallengeId == challengeId)
            .ToListAsync(cancellationToken);
        foreach (var participant in participants.Where(item => item.Status is not (ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)))
        {
            participant.Status = ParticipantStatus.Cancelled;
            participant.FinishedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    public async Task<Challenge> CreateRematchAsync(Guid challengeId, Guid creatorProfileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        var existing = await db.Challenges
            .SingleOrDefaultAsync(item => item.RematchOfChallengeId == challengeId, cancellationToken);
        if (existing is not null)
        {
            EnsureCreator(existing, creatorProfileId);
            return existing;
        }

        var source = await RequireChallengeAsync(challengeId, cancellationToken);
        EnsureCreator(source, creatorProfileId);
        if (source.Status is ChallengeStatus.Open or ChallengeStatus.Running)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Eine Revanche ist erst nach Abschluss oder Abbruch möglich.");
        }

        var participantIds = await db.ChallengeParticipants
            .Where(item => item.ChallengeId == source.Id && item.UserProfileId != creatorProfileId)
            .Select(item => item.UserProfileId)
            .ToArrayAsync(cancellationToken);
        var expiryDays = Math.Clamp((int)Math.Ceiling((source.ExpiresAt - source.CreatedAt).TotalDays), 1, 30);
        var request = new CreateChallengeRequest(
            BuildRematchTitle(source.Title),
            source.TrainingTextId,
            source.Mode,
            participantIds,
            source.RoundCount,
            expiryDays);
        try
        {
            return await CreateCoreAsync(
                Guid.CreateVersion7(),
                creatorProfileId,
                request,
                source.Id,
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await db.Challenges
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RematchOfChallengeId == challengeId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    public async Task JoinAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
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
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
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
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
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
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        await FinishRoundCoreAsync(challengeId, profileId, attempt, cancellationToken);
    }

    private async Task FinishRoundCoreAsync(Guid challengeId, Guid profileId, TypingAttempt attempt, CancellationToken cancellationToken)
    {
        var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
        if (participant.Status == ParticipantStatus.Invited)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Die Herausforderung muss vor dem Abschluss angenommen werden.");
        }

        var binding = await db.ChallengeAttemptBindings.SingleOrDefaultAsync(item =>
            item.ChallengeId == challengeId &&
            item.UserProfileId == profileId &&
            item.TypingAttemptId == attempt.Id, cancellationToken);
        if (binding is null)
        {
            throw ChallengeError(ChallengeErrorCodes.InvalidAttempt, ConflictStatus, "Der Versuch gehört nicht zu dieser Herausforderung.");
        }

        var round = await db.ChallengeRounds.SingleOrDefaultAsync(
                item => item.Id == binding.ChallengeRoundId && item.ChallengeId == challengeId,
                cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.InvalidAttempt, ConflictStatus, "Der Versuch gehört nicht zu dieser Herausforderung.");
        var existingResult = await db.ChallengeRoundResults.SingleOrDefaultAsync(
            item => item.ChallengeRoundId == round.Id && item.UserProfileId == profileId,
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

        var challenge = await RequireActiveChallengeAsync(challengeId, cancellationToken);
        if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Challenge-Runde ist für dich bereits abgeschlossen.");
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
            var roundResultStatus = attempt.Completed ? ParticipantStatus.Finished : ParticipantStatus.Dnf;
            db.ChallengeRoundResults.Add(new ChallengeRoundResult
            {
                ChallengeRoundId = round.Id,
                UserProfileId = profileId,
                TypingAttemptId = attempt.Id,
                Status = roundResultStatus,
                DurationMilliseconds = attempt.DurationMilliseconds,
                Accuracy = attempt.Accuracy,
                Consistency = attempt.Consistency,
                Wpm = attempt.Wpm,
                FinishedAt = now
            });

            if (round.RoundNumber < challenge.RoundCount)
            {
                participant.Status = ParticipantStatus.Joined;
                participant.FinishedAt = null;
            }
            else
            {
                var hasFinishedRound = attempt.Completed || await db.ChallengeRoundResults.AnyAsync(item =>
                    item.UserProfileId == profileId &&
                    db.ChallengeRounds
                        .Where(challengeRound => challengeRound.ChallengeId == challengeId)
                        .Select(challengeRound => challengeRound.Id)
                        .Contains(item.ChallengeRoundId) &&
                    item.Status == ParticipantStatus.Finished,
                    cancellationToken);
                participant.Status = hasFinishedRound ? ParticipantStatus.Finished : ParticipantStatus.Dnf;
                participant.FinishedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            await TryCloseCoreAsync(challenge.Id, cancellationToken);
        }, cancellationToken);
    }

    public async Task TryCloseAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
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
        var rounds = await db.ChallengeRounds
            .Where(item => item.ChallengeId == challengeId)
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
            item.Status is ParticipantStatus.Declined or ParticipantStatus.Cancelled ||
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
            .Where(item =>
                item.Status == ChallengeStatus.Open || item.Status == ChallengeStatus.Running)
            .Select(item => new { item.Id, item.ExpiresAt })
            .ToListAsync(cancellationToken);
        var candidateIds = candidates
            .Where(item => item.ExpiresAt <= now)
            .Select(item => item.Id)
            .ToArray();
        foreach (var challengeId in candidateIds)
        {
            await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
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

    private async Task<ChallengeRound> RequireNextRoundAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken)
    {
        var completedRoundIds = db.ChallengeRoundResults
            .Where(item => item.UserProfileId == profileId)
            .Select(item => item.ChallengeRoundId);
        return await db.ChallengeRounds
                .Where(item => item.ChallengeId == challengeId && !completedRoundIds.Contains(item.Id))
                .OrderBy(item => item.RoundNumber)
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Alle Runden dieser Herausforderung sind bereits abgeschlossen.");
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
        IReadOnlyCollection<ChallengeRoundResult> results)
    {
        var activeParticipants = participants
            .Where(item => item.Status is not (ParticipantStatus.Declined or ParticipantStatus.Cancelled))
            .ToArray();
        var participantCount = activeParticipants.Length;
        var rankedSeries = ArenaScoring.RankSeries(activeParticipants.Select(participant =>
        {
            var participantResults = results.Where(item => item.UserProfileId == participant.UserProfileId).ToArray();
            return new ArenaSeriesScore(
                participant.UserProfileId,
                participantResults.Sum(item => ArenaScoring.PointsForRound(item.Status, item.Placement, participantCount)),
                participantResults.Count(item => item.Status == ParticipantStatus.Finished && item.Placement == 1),
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

    private static void EnsureCreator(Challenge challenge, Guid creatorProfileId)
    {
        if (challenge.CreatorProfileId != creatorProfileId)
        {
            throw ChallengeError(ChallengeErrorCodes.NotFound, NotFoundStatus, "Diese Herausforderung wurde nicht gefunden.");
        }
    }

    private static string BuildRematchTitle(string title)
    {
        const string suffix = " – Revanche";
        var prefix = title.Length <= 160 - suffix.Length
            ? title
            : title[..(160 - suffix.Length)].TrimEnd();
        return $"{prefix}{suffix}";
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

    private static string CreateBindingToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    }

    private static ChallengeLifecycleException ChallengeError(string code, int statusCode, string message) =>
        new(code, statusCode, message);
}
