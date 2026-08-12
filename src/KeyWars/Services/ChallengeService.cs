using System.Security.Cryptography;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record CreateChallengeRequest(
    string Title,
    Guid TrainingTextId,
    ChallengeMode Mode,
    IReadOnlyCollection<Guid> ParticipantIds,
    int RoundCount,
    int ExpiryDays,
    Guid? RequestId = null);

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
    IChallengeLockProvider? lockProvider = null,
    IAttemptSessionStateStore? attemptSessionStore = null)
{
    private readonly IChallengeLockProvider challengeLocks = lockProvider ?? LocalChallengeLockProvider.Shared;
    private readonly ChallengeAttemptTerminalizer attemptTerminalizer = new(
        db,
        attemptSessionStore ?? new AttemptSessionStore());
    private const int BadRequestStatus = 400;
    private const int NotFoundStatus = 404;
    private const int ConflictStatus = 409;
    private const int GoneStatus = 410;

    public async Task<Challenge> CreateAsync(Guid creatorProfileId, CreateChallengeRequest request, CancellationToken cancellationToken = default)
    {
        var challengeId = request.RequestId is { } requestId && requestId != Guid.Empty
            ? requestId
            : Guid.CreateVersion7();
        if (request.RequestId is not null)
        {
            var existing = await db.Challenges
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == challengeId, cancellationToken);
            if (existing is not null)
            {
                await EnsureMatchingCreateRequestAsync(existing, creatorProfileId, request, cancellationToken);
                return existing;
            }
        }

        try
        {
            return await CreateCoreAsync(challengeId, creatorProfileId, request, null, cancellationToken);
        }
        catch (DbUpdateException) when (request.RequestId is not null)
        {
            db.ChangeTracker.Clear();
            var existing = await db.Challenges
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == challengeId, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            await EnsureMatchingCreateRequestAsync(existing, creatorProfileId, request, cancellationToken);
            return existing;
        }
    }

    private async Task EnsureMatchingCreateRequestAsync(
        Challenge challenge,
        Guid creatorProfileId,
        CreateChallengeRequest request,
        CancellationToken cancellationToken)
    {
        EnsureCreator(challenge, creatorProfileId);
        if (request.ParticipantIds is null ||
            request.ParticipantIds.Count != request.ParticipantIds.Distinct().Count())
        {
            throw ChallengeError(
                ChallengeErrorCodes.Conflict,
                ConflictStatus,
                "Diese Request-ID wurde bereits für eine andere Herausforderung verwendet.");
        }

        var expectedParticipants = request.ParticipantIds.Append(creatorProfileId).Distinct().Order().ToArray();
        var storedParticipants = (await db.ChallengeParticipants
                .AsNoTracking()
                .Where(item => item.ChallengeId == challenge.Id)
                .Select(item => item.UserProfileId)
                .ToArrayAsync(cancellationToken))
            .Order()
            .ToArray();
        var expectedTitle = string.IsNullOrWhiteSpace(request.Title)
            ? await db.TrainingTexts
                .Where(item => item.Id == request.TrainingTextId)
                .Select(item => item.Title)
                .SingleOrDefaultAsync(cancellationToken) ?? ""
            : request.Title.Trim();
        var expectedExpiryDays = Math.Clamp(request.ExpiryDays, 1, 30);

        if (challenge.TrainingTextId != request.TrainingTextId ||
            challenge.Title != expectedTitle ||
            challenge.Mode != request.Mode ||
            challenge.RoundCount != request.RoundCount ||
            challenge.ExpiresAt != challenge.CreatedAt.AddDays(expectedExpiryDays) ||
            !storedParticipants.SequenceEqual(expectedParticipants))
        {
            throw ChallengeError(
                ChallengeErrorCodes.Conflict,
                ConflictStatus,
                "Diese Request-ID wurde bereits für eine andere Herausforderung verwendet.");
        }
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
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        var operationToken = operationCancellation.Token;
        await attempts.SweepExpiredSessionsAsync(operationToken);
        var session = await StartAttemptCoreAsync(challengeId, profileId, attempts, operationToken);
        return session;
    }

    private async Task<AttemptSession> StartAttemptCoreAsync(Guid challengeId, Guid profileId, AttemptService attempts, CancellationToken cancellationToken)
    {
        await using var challengeTransaction = new ChallengeTransactionContext(attemptTerminalizer);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ChallengeWriteFence.AcquireAsync(db, challengeId, cancellationToken);
        Challenge challenge;
        try
        {
            challenge = await RequireActiveChallengeAsync(challengeId, challengeTransaction, cancellationToken);
        }
        catch (ChallengeLifecycleException exception) when (exception.Code == ChallengeErrorCodes.Expired)
        {
            challengeTransaction.ThrowIfLost();
            await transaction.CommitAsync(CancellationToken.None);
            await challengeTransaction.CompleteAsync();
            throw;
        }
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
            if (!existingBinding.Consumed && await attempts.TryGetActiveSessionWithoutExpirationSweepAsync(profileId, existingBinding.TypingAttemptId, cancellationToken) is { } existingSession)
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
                challengeTransaction.ThrowIfLost();
                await transaction.CommitAsync(cancellationToken);
                await challengeTransaction.CompleteAsync();
                return existingSession;
            }

            var boundAttemptPhase = await db.TypingAttempts
                .Where(item => item.Id == existingBinding.TypingAttemptId)
                .Select(item => (AttemptPhase?)item.Phase)
                .SingleOrDefaultAsync(cancellationToken);
            if (!existingBinding.Consumed && boundAttemptPhase is AttemptPhase.Prepared or AttemptPhase.Started or AttemptPhase.Expired or AttemptPhase.Aborted)
            {
                await attempts.AbortPreparedAsync(profileId, existingBinding.TypingAttemptId);
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

        AttemptSession? session = null;
        try
        {
            session = await attempts.StartWithoutExpirationSweepAsync(profileId, new StartAttemptRequest(TrainingMode.Text, challenge.TrainingTextId, null, null), cancellationToken);
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
            challengeTransaction.ThrowIfLost();
            await transaction.CommitAsync(cancellationToken);
            await challengeTransaction.CompleteAsync();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await attempts.AbortPreparedAsync(profileId, session.Id);
            }

            throw;
        }
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
        await ExpireDueChallengesForProfileAsync(profileId, cancellationToken);
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
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        cancellationToken = operationCancellation.Token;
        Challenge? challenge = null;
        await ExecuteChallengeTransactionAsync(challengeId, async challengeTransaction =>
        {
            challenge = await RequireChallengeAsync(challengeId, cancellationToken);
            EnsureCreator(challenge, creatorProfileId);
            if (challenge.Status == ChallengeStatus.Cancelled)
            {
                await challengeTransaction.AbortBoundAttemptsAsync(
                    challengeId,
                    challenge.FinishedAt ?? timeProvider.GetUtcNow(),
                    cancellationToken);
                return;
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

            await challengeTransaction.AbortBoundAttemptsAsync(challengeId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return challenge ?? throw new InvalidOperationException("Der transaktionale Challenge-Status fehlt.");
    }

    public async Task<Challenge> CreateRematchAsync(Guid challengeId, Guid creatorProfileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        cancellationToken = operationCancellation.Token;
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
            var rematch = await CreateCoreAsync(
                Guid.CreateVersion7(),
                creatorProfileId,
                request,
                source.Id,
                cancellationToken);
            challengeLock.ThrowIfLost();
            return rematch;
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
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        cancellationToken = operationCancellation.Token;
        await ExecuteChallengeTransactionAsync(challengeId, async challengeTransaction =>
        {
            await RequireActiveChallengeAsync(challengeId, challengeTransaction, cancellationToken);
            var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
            if (participant.Status == ParticipantStatus.Invited)
            {
                participant.Status = ParticipantStatus.Joined;
                participant.RespondedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    public async Task DeclineAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        cancellationToken = operationCancellation.Token;
        await ExecuteChallengeTransactionAsync(challengeId, async challengeTransaction =>
        {
            await RequireActiveChallengeAsync(challengeId, challengeTransaction, cancellationToken);
            var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
            if (participant.Status is ParticipantStatus.Invited or ParticipantStatus.Joined)
            {
                participant.Status = ParticipantStatus.Declined;
                participant.RespondedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    public async Task RequirePlayableAsync(Guid challengeId, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        cancellationToken = operationCancellation.Token;
        await ExecuteChallengeTransactionAsync(challengeId, async challengeTransaction =>
        {
            await RequireActiveChallengeAsync(challengeId, challengeTransaction, cancellationToken);
            var participant = await RequireParticipantAsync(challengeId, profileId, cancellationToken);
            if (participant.Status == ParticipantStatus.Invited)
            {
                throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Die Herausforderung muss vor dem Start angenommen werden.");
            }

            if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Declined)
            {
                throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist für dich abgeschlossen.");
            }
        }, cancellationToken);
    }

    public async Task FinishRoundAsync(Guid challengeId, Guid profileId, TypingAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        await ExecuteChallengeTransactionAsync(
            challengeId,
            challengeTransaction => FinishRoundCoreAsync(
                challengeId,
                profileId,
                attempt,
                challengeTransaction,
                false,
                operationCancellation.Token),
            operationCancellation.Token);
    }

    public async Task<AttemptCompletion> FinishAttemptAsync(
        Guid challengeId,
        Guid profileId,
        FinishAttemptRequest request,
        AttemptService attempts,
        CancellationToken cancellationToken = default)
    {
        await attempts.SweepExpiredSessionsAsync(cancellationToken);
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        var operationToken = operationCancellation.Token;
        await using var challengeTransaction = new ChallengeTransactionContext(attemptTerminalizer);
        await using var transaction = await db.Database.BeginTransactionAsync(operationToken);
        await ChallengeWriteFence.AcquireAsync(db, challengeId, operationToken);
        try
        {
            await RequireFinishableChallengeAsync(
                challengeId,
                profileId,
                request.AttemptId,
                challengeTransaction,
                operationToken);
        }
        catch (ChallengeLifecycleException exception) when (exception.Code == ChallengeErrorCodes.Expired)
        {
            challengeTransaction.ThrowIfLost();
            await transaction.CommitAsync(CancellationToken.None);
            await challengeTransaction.CompleteAsync();
            throw;
        }
        var participantIds = await db.ChallengeParticipants
            .Where(item => item.ChallengeId == challengeId)
            .Select(item => item.UserProfileId)
            .ToArrayAsync(operationToken);
        if (participantIds.Length == 0)
        {
            throw ChallengeError(ChallengeErrorCodes.NotFound, NotFoundStatus, "Diese Herausforderung wurde nicht gefunden.");
        }
        return await attempts.FinishInCurrentTransactionAsync(
            profileId,
            request,
            participantIds,
            (attempt, token) => FinishRoundCoreAsync(
                challengeId,
                profileId,
                attempt,
                challengeTransaction,
                true,
                token),
            async token =>
            {
                challengeLock.ThrowIfLost();
                challengeTransaction.ThrowIfLost();
                await transaction.CommitAsync(token);
                await challengeTransaction.CompleteAsync();
            },
            operationToken);
    }

    private async Task FinishRoundCoreAsync(
        Guid challengeId,
        Guid profileId,
        TypingAttempt attempt,
        ChallengeTransactionContext challengeTransaction,
        bool expirationAlreadyChecked,
        CancellationToken cancellationToken)
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

            await TryCloseCoreAsync(challengeId, challengeTransaction, expirationAlreadyChecked, cancellationToken);
            return;
        }

        var challenge = expirationAlreadyChecked
            ? await RequireChallengeAsync(challengeId, cancellationToken)
            : await RequireActiveChallengeAsync(challengeId, challengeTransaction, cancellationToken);
        if (expirationAlreadyChecked && challenge.Status is not (ChallengeStatus.Open or ChallengeStatus.Running))
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist nicht mehr aktiv.");
        }
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
        await TryCloseCoreAsync(challenge.Id, challengeTransaction, expirationAlreadyChecked, cancellationToken);
    }

    public async Task TryCloseAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
        await ExecuteChallengeTransactionAsync(
            challengeId,
            challengeTransaction => TryCloseCoreAsync(challengeId, challengeTransaction, false, operationCancellation.Token),
            operationCancellation.Token);
    }

    private async Task TryCloseCoreAsync(
        Guid challengeId,
        ChallengeTransactionContext challengeTransaction,
        bool skipExpiration,
        CancellationToken cancellationToken)
    {
        var challenge = await RequireChallengeAsync(challengeId, cancellationToken);
        // Atomic finish already validated expiry before taking profile fences; never acquire another attempt lease here.
        if (!skipExpiration && await ExpireIfDueAsync(challenge, challengeTransaction, cancellationToken))
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
            await ProfileWriteFence.AcquireAsync(db, ids, cancellationToken);
            if (!await ProfileWriteFence.IsAvailableAsync(db, ids, cancellationToken))
            {
                // Challenge ratings are all-or-nothing; a deleted participant invalidates the match rating.
                challenge.RatingEligible = false;
            }
            else
            {
                var profiles = await db.UserProfiles.Where(item => ids.Contains(item.Id) && !item.Deleted).ToListAsync(cancellationToken);
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
        }

        challenge.Status = ChallengeStatus.Finished;
        challenge.FinishedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Challenge> RequireActiveChallengeAsync(
        Guid challengeId,
        ChallengeTransactionContext challengeTransaction,
        CancellationToken cancellationToken)
    {
        var challenge = await RequireChallengeAsync(challengeId, cancellationToken);
        var expiredNow = await ExpireIfDueAsync(challenge, challengeTransaction, cancellationToken);
        if (challenge.Status == ChallengeStatus.Expired)
        {
            if (!expiredNow)
            {
                await challengeTransaction.AbortBoundAttemptsAsync(
                    challenge.Id,
                    challenge.FinishedAt ?? timeProvider.GetUtcNow(),
                    cancellationToken);
            }

            throw ChallengeError(ChallengeErrorCodes.Expired, GoneStatus, "Diese Herausforderung ist abgelaufen.");
        }

        if (challenge.Status is ChallengeStatus.Finished or ChallengeStatus.Cancelled)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist nicht mehr aktiv.");
        }

        return challenge;
    }

    private async Task RequireFinishableChallengeAsync(
        Guid challengeId,
        Guid profileId,
        Guid attemptId,
        ChallengeTransactionContext challengeTransaction,
        CancellationToken cancellationToken)
    {
        var challenge = await RequireChallengeAsync(challengeId, cancellationToken);
        var isExactReplay = await (
                from binding in db.ChallengeAttemptBindings
                join result in db.ChallengeRoundResults
                    on new { binding.ChallengeRoundId, binding.UserProfileId }
                    equals new { result.ChallengeRoundId, result.UserProfileId }
                where binding.ChallengeId == challengeId &&
                    binding.UserProfileId == profileId &&
                    binding.TypingAttemptId == attemptId &&
                    binding.Consumed &&
                    result.TypingAttemptId == attemptId
                select result.Id)
            .AnyAsync(cancellationToken);

        // A persisted replay only reads its already-consumed attempt. Taking leases for other
        // bound attempts here would invert the globally sorted attempt-lock order.
        if (isExactReplay)
        {
            return;
        }

        var expiredNow = await ExpireIfDueAsync(challenge, challengeTransaction, cancellationToken);
        if (challenge.Status == ChallengeStatus.Expired)
        {
            if (!expiredNow)
            {
                await challengeTransaction.AbortBoundAttemptsAsync(
                    challenge.Id,
                    challenge.FinishedAt ?? timeProvider.GetUtcNow(),
                    cancellationToken);
            }

            throw ChallengeError(ChallengeErrorCodes.Expired, GoneStatus, "Diese Herausforderung ist abgelaufen.");
        }

        if (challenge.Status == ChallengeStatus.Cancelled)
        {
            throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist nicht mehr aktiv.");
        }

        if (challenge.Status is ChallengeStatus.Open or ChallengeStatus.Running)
        {
            return;
        }

        throw ChallengeError(ChallengeErrorCodes.Conflict, ConflictStatus, "Diese Herausforderung ist nicht mehr aktiv.");
    }

    private async Task ExpireDueChallengesForProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidateIds = db.Database.IsSqlite()
            ? await db.Database.SqlQuery<Guid>($"""
                    SELECT c.Id AS Value
                    FROM ChallengeParticipants p
                    INNER JOIN Challenges c ON c.Id = p.ChallengeId
                    WHERE p.UserProfileId = {profileId.ToString().ToUpperInvariant()}
                      AND c.Status IN ('Open', 'Running')
                      AND c.ExpiresAt <= {now}
                    ORDER BY c.ExpiresAt, c.Id
                    LIMIT 50
                    """)
                .ToArrayAsync(cancellationToken)
            : await (
                    from participant in db.ChallengeParticipants.AsNoTracking()
                    join challenge in db.Challenges.AsNoTracking() on participant.ChallengeId equals challenge.Id
                    where participant.UserProfileId == profileId &&
                        (challenge.Status == ChallengeStatus.Open || challenge.Status == ChallengeStatus.Running) &&
                        challenge.ExpiresAt <= now
                    orderby challenge.ExpiresAt, challenge.Id
                    select challenge.Id)
                .Take(50)
                .ToArrayAsync(cancellationToken);
        foreach (var challengeId in candidateIds)
        {
            await using var challengeLock = await challengeLocks.AcquireAsync(challengeId, cancellationToken);
            using var operationCancellation = LinkToLease(cancellationToken, challengeLock);
            var operationToken = operationCancellation.Token;
            await ExecuteChallengeTransactionAsync(challengeId, async challengeTransaction =>
            {
                var challenge = await db.Challenges.SingleOrDefaultAsync(item => item.Id == challengeId, operationToken);
                if (challenge is not null)
                {
                    await ExpireIfDueAsync(challenge, challengeTransaction, operationToken);
                }
            }, operationToken);
        }
    }

    private static CancellationTokenSource LinkToLease(
        CancellationToken cancellationToken,
        IOperationLease lease) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LeaseLost);

    private Task ExecuteChallengeTransactionAsync(
        Guid challengeId,
        Func<Task> operation,
        CancellationToken cancellationToken) =>
        ExecuteChallengeTransactionAsync(challengeId, _ => operation(), cancellationToken);

    private async Task ExecuteChallengeTransactionAsync(
        Guid challengeId,
        Func<ChallengeTransactionContext, Task> operation,
        CancellationToken cancellationToken)
    {
        await using var challengeTransaction = new ChallengeTransactionContext(attemptTerminalizer);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await ChallengeWriteFence.AcquireAsync(db, challengeId, cancellationToken);
            await operation(challengeTransaction);
            challengeTransaction.ThrowIfLost();
            await transaction.CommitAsync(cancellationToken);
            await challengeTransaction.CompleteAsync();
        }
        catch (ChallengeLifecycleException exception) when (exception.Code == ChallengeErrorCodes.Expired)
        {
            challengeTransaction.ThrowIfLost();
            await transaction.CommitAsync(CancellationToken.None);
            await challengeTransaction.CompleteAsync();
            throw;
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

    private async Task<bool> ExpireIfDueAsync(
        Challenge challenge,
        ChallengeTransactionContext challengeTransaction,
        CancellationToken cancellationToken)
    {
        if (challenge.Status is not (ChallengeStatus.Open or ChallengeStatus.Running) ||
            challenge.ExpiresAt > timeProvider.GetUtcNow())
        {
            return false;
        }

        challenge.Status = ChallengeStatus.Expired;
        challenge.FinishedAt ??= timeProvider.GetUtcNow();
        await challengeTransaction.AbortBoundAttemptsAsync(challenge.Id, challenge.FinishedAt.Value, cancellationToken);
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

    private static string CreateBindingToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    }

    private static ChallengeLifecycleException ChallengeError(string code, int statusCode, string message) =>
        new(code, statusCode, message);
}
