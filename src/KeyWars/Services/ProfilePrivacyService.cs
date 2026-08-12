using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace KeyWars.Services;

public sealed record TypingAttemptExport(
    Guid Id,
    Guid UserProfileId,
    Guid? TrainingTextId,
    TrainingMode Mode,
    AttemptPhase Phase,
    string StandardTextKey,
    string TextHash,
    DateTimeOffset PreparedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int DurationMilliseconds,
    int ClientDurationMilliseconds,
    int CorrectCharacters,
    int IncorrectCharacters,
    int Backspaces,
    int FocusLosses,
    int TotalCharacters,
    double Wpm,
    double RawWpm,
    double CharactersPerMinute,
    double Accuracy,
    double Consistency,
    int ConsistencySampleCount,
    double MeanWordMilliseconds,
    double WordTimingVariation,
    bool Completed,
    bool Official,
    bool LeaderboardEligible,
    bool ExperienceAwarded,
    DateTimeOffset CreatedAt);

public sealed record ChallengeAttemptBindingExport(
    Guid Id,
    Guid ChallengeId,
    Guid ChallengeRoundId,
    Guid UserProfileId,
    Guid TypingAttemptId,
    string TextSnapshotHash,
    TrainingMode Mode,
    bool Consumed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConsumedAt);

public sealed record LiveRoomSummaryExport(
    Guid Id,
    int RoundNumber,
    int RoundVersion,
    Guid CreatorProfileId,
    string RoomCode,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool AbortedByServer);

public sealed record ProfileExportPayload(
    int Version,
    DateTimeOffset GeneratedAt,
    UserProfile Profile,
    IReadOnlyList<TypingAttemptExport> Attempts,
    IReadOnlyList<TypingAttemptError> AttemptErrors,
    IReadOnlyList<RewardLedgerEntry> RewardLedger,
    IReadOnlyList<Mission> Missions,
    IReadOnlyList<Achievement> Achievements,
    IReadOnlyList<GamificationEvent> GamificationEvents,
    IReadOnlyList<WeaknessObservation> WeaknessObservations,
    IReadOnlyList<TrainingText> OwnedTexts,
    IReadOnlyList<TextCollection> OwnedCollections,
    IReadOnlyList<TextCollectionItem> OwnedCollectionItems,
    IReadOnlyList<ContentModerationAuditEntry> ContentModerationAudit,
    IReadOnlyList<Challenge> CreatedChallenges,
    IReadOnlyList<ChallengeRound> ChallengeRounds,
    IReadOnlyList<ChallengeParticipant> ChallengeParticipations,
    IReadOnlyList<ChallengeRoundResult> ChallengeRoundResults,
    IReadOnlyList<ChallengeAttemptBindingExport> ChallengeAttemptBindings,
    IReadOnlyList<LiveRoomSummaryExport> CreatedLiveRooms,
    IReadOnlyList<LiveRoomParticipantSummary> LiveRoomResults);

public sealed class ProfilePrivacyService(
    KeyWarsDbContext db,
    ILiveRoomDispatcher liveRooms,
    ILiveRoomCompletionDrain liveRoomCompletions,
    IAttemptSessionStateStore attemptSessions,
    IProfileAccessGate accessGate,
    TimeProvider timeProvider)
{
    public async Task<ProfileExportPayload> BuildExportAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId && !item.Deleted, cancellationToken);
        var attempts = await db.TypingAttempts
            .Where(item => item.UserProfileId == profileId)
            .Select(item => new TypingAttemptExport(
                item.Id,
                item.UserProfileId,
                item.TrainingTextId,
                item.Mode,
                item.Phase,
                item.StandardTextKey,
                item.TextHash,
                item.PreparedAt,
                item.StartedAt,
                item.FinishedAt,
                item.DurationMilliseconds,
                item.ClientDurationMilliseconds,
                item.CorrectCharacters,
                item.IncorrectCharacters,
                item.Backspaces,
                item.FocusLosses,
                item.TotalCharacters,
                item.Wpm,
                item.RawWpm,
                item.CharactersPerMinute,
                item.Accuracy,
                item.Consistency,
                item.ConsistencySampleCount,
                item.MeanWordMilliseconds,
                item.WordTimingVariation,
                item.Completed,
                item.Official,
                item.LeaderboardEligible,
                item.ExperienceAwarded,
                item.CreatedAt))
            .ToListAsync(cancellationToken);
        var ownedCollections = await db.TextCollections
            .Where(item => item.OwnerProfileId == profileId)
            .ToListAsync(cancellationToken);
        var ownedCollectionIds = ownedCollections.Select(item => item.Id).ToArray();
        List<TextCollectionItem> ownedCollectionItems = [];
        if (ownedCollectionIds.Length > 0)
        {
            ownedCollectionItems = await db.TextCollectionItems
                .Where(item => ownedCollectionIds.Contains(item.TextCollectionId))
                .ToListAsync(cancellationToken);
        }

        var createdChallenges = await db.Challenges
            .Where(item => item.CreatorProfileId == profileId)
            .ToListAsync(cancellationToken);
        var challengeParticipations = await db.ChallengeParticipants
            .Where(item => item.UserProfileId == profileId)
            .ToListAsync(cancellationToken);
        var challengeRoundResults = await db.ChallengeRoundResults
            .Where(item => item.UserProfileId == profileId)
            .ToListAsync(cancellationToken);
        var challengeAttemptBindings = await db.ChallengeAttemptBindings
            .Where(item => item.UserProfileId == profileId)
            .Select(item => new ChallengeAttemptBindingExport(
                item.Id,
                item.ChallengeId,
                item.ChallengeRoundId,
                item.UserProfileId,
                item.TypingAttemptId,
                item.TextSnapshotHash,
                item.Mode,
                item.Consumed,
                item.CreatedAt,
                item.ConsumedAt))
            .ToListAsync(cancellationToken);
        var relatedChallengeIds = createdChallenges
            .Select(item => item.Id)
            .Concat(challengeParticipations.Select(item => item.ChallengeId))
            .Concat(challengeAttemptBindings.Select(item => item.ChallengeId))
            .Distinct()
            .ToArray();
        var relatedChallengeRoundIds = challengeRoundResults
            .Select(item => item.ChallengeRoundId)
            .Concat(challengeAttemptBindings.Select(item => item.ChallengeRoundId))
            .Distinct()
            .ToArray();
        List<ChallengeRound> challengeRounds = [];
        if (relatedChallengeIds.Length > 0 || relatedChallengeRoundIds.Length > 0)
        {
            challengeRounds = await db.ChallengeRounds
                .Where(item => relatedChallengeIds.Contains(item.ChallengeId) || relatedChallengeRoundIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        }

        var createdLiveRooms = await db.LiveRoomSummaries
            .Where(item => item.CreatorProfileId == profileId)
            .Select(item => new LiveRoomSummaryExport(
                item.Id,
                item.RoundNumber,
                item.RoundVersion,
                item.CreatorProfileId,
                item.RoomCode,
                item.Mode,
                item.Visibility,
                item.RoundCount,
                item.CreatedAt,
                item.StartedAt,
                item.FinishedAt,
                item.AbortedByServer))
            .ToListAsync(cancellationToken);

        return new ProfileExportPayload(
            3,
            timeProvider.GetUtcNow(),
            profile,
            attempts,
            await db.TypingAttemptErrors.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.RewardLedgerEntries.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.Missions.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.Achievements.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.GamificationEvents.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.WeaknessObservations.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.TrainingTexts.Where(item => item.OwnerProfileId == profileId).ToListAsync(cancellationToken),
            ownedCollections,
            ownedCollectionItems,
            (await db.ContentModerationAuditEntries
                .Where(item => item.ActorProfileId == profileId || item.TargetOwnerProfileId == profileId)
                .ToListAsync(cancellationToken))
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToArray(),
            createdChallenges,
            challengeRounds,
            challengeParticipations,
            challengeRoundResults,
            challengeAttemptBindings,
            createdLiveRooms,
            await db.LiveRoomParticipantSummaries.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken));
    }

    public async Task ResetStatisticsAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await ExecuteExclusiveOperationAsync(profileId, markDeleted: false, async operationToken =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(operationToken);
            try
            {
                await ProfileWriteFence.AcquireAsync(db, profileId, operationToken);
                var profile = await ReloadAvailableProfileAsync(profileId, operationToken);
                await DeleteDerivedStatisticsAsync(profileId, operationToken);
                profile.ExperiencePoints = 0;
                profile.Level = 1;
                profile.SeasonPoints = 0;
                profile.CurrentStreakDays = 0;
                profile.LastActivityDate = null;
                profile.ArenaRating = 1000;
                profile.RatedMatchCount = 0;
                profile.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(operationToken);
                await transaction.CommitAsync(operationToken);
            }
            catch
            {
                await RollbackAsync(transaction);
                throw;
            }
        }, cancellationToken);
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await ExecuteExclusiveOperationAsync(profileId, markDeleted: true, async operationToken =>
        {
            var now = timeProvider.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(operationToken);
            try
            {
                await ProfileWriteFence.AcquireAsync(db, profileId, operationToken);
                var profile = await ReloadAvailableProfileAsync(profileId, operationToken);
                await DeleteDerivedStatisticsAsync(profileId, operationToken);
                await RemoveOwnedCollectionsAsync(profileId, operationToken);
                await PseudonymizeOwnedTextsAsync(profileId, now, operationToken);
                await MarkActiveChallengesDeclinedAsync(profileId, now, operationToken);

                var pseudonym = $"deleted-{profile.Id:N}";
                profile.DirectoryObjectGuid = pseudonym;
                profile.DirectorySid = "";
                profile.SamAccountName = pseudonym;
                profile.UserPrincipalName = $"{pseudonym}@deleted.local";
                profile.DisplayName = "Gelöschtes Profil";
                profile.GivenName = null;
                profile.Surname = null;
                profile.Email = null;
                profile.Department = null;
                profile.Title = null;
                profile.Motto = null;
                profile.LeaderboardVisible = false;
                profile.GhostSharingEnabled = false;
                profile.ChallengesEnabled = false;
                profile.ExperiencePoints = 0;
                profile.Level = 1;
                profile.SeasonPoints = 0;
                profile.CurrentStreakDays = 0;
                profile.LastActivityDate = null;
                profile.ArenaRating = 1000;
                profile.RatedMatchCount = 0;
                profile.LastLoginAt = null;
                profile.UpdatedAt = now;
                profile.Deleted = true;
                await db.SaveChangesAsync(operationToken);
                await transaction.CommitAsync(operationToken);
            }
            catch
            {
                await RollbackAsync(transaction);
                throw;
            }
        }, cancellationToken);
    }

    private async Task ExecuteExclusiveOperationAsync(
        Guid profileId,
        bool markDeleted,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var operationLease = await BeginExclusiveOperationAsync(profileId, cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationLease.LeaseLost);
        var operationToken = operationCancellation.Token;
        var lifecycleLocks = new List<IAsyncDisposable>();
        try
        {
            await accessGate.WaitForIdleAsync(profileId, operationToken);
            var removedSessions = await attemptSessions.RemoveProfileAsync(profileId, operationToken);
            var persistedAttemptIds = await db.TypingAttempts
                .Where(attempt => attempt.UserProfileId == profileId &&
                    (attempt.Phase == AttemptPhase.Prepared || attempt.Phase == AttemptPhase.Started))
                .Select(attempt => attempt.Id)
                .ToListAsync(operationToken);
            var attemptIds = removedSessions
                .Select(session => session.Id)
                .Concat(persistedAttemptIds)
                .Distinct()
                .Order()
                .ToArray();
            foreach (var attemptId in attemptIds)
            {
                lifecycleLocks.Add(await attemptSessions.AcquireLifecycleLockAsync(attemptId, operationToken));
            }

            await attemptSessions.RemoveProfileAsync(profileId, operationToken);
            await AbortActiveAttemptsAsync(profileId, operationToken);
            await liveRooms.RemoveProfileAsync(profileId, operationToken);
            var drain = await liveRoomCompletions.DrainProfileAsync(profileId, operationToken);
            EnsureDrainSucceeded(drain);
            operationLease.ThrowIfLost();
            await operation(operationToken);
            operationLease.ThrowIfLost();
            if (markDeleted)
            {
                await accessGate.MarkDeletedAsync(profileId, operationToken);
            }
        }
        finally
        {
            try
            {
                for (var index = lifecycleLocks.Count - 1; index >= 0; index--)
                {
                    await lifecycleLocks[index].DisposeAsync();
                }
            }
            finally
            {
                await operationLease.DisposeAsync();
            }
        }
    }

    private async Task<IOperationLease> BeginExclusiveOperationAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (await accessGate.TryBeginOperationAsync(profileId, cancellationToken) is { } lease)
        {
            return lease;
        }

        throw await accessGate.GetStateAsync(profileId, cancellationToken) == ProfileAccessState.Deleted
            ? new ProfileOperationException("profile_deleted", "Dieses Profil wurde bereits gelöscht.")
            : new ProfileOperationException("profile_operation_in_progress", "Für dieses Profil läuft bereits eine Datenschutzoperation.");
    }

    private static void EnsureDrainSucceeded(CompletionDrainResult drain)
    {
        switch (drain.Status)
        {
            case CompletionDrainStatus.Success:
                return;
            case CompletionDrainStatus.Timeout:
                throw new ProfileOperationException(
                    "profile_completion_drain_timeout",
                    "Offene Arena-Ergebnisse konnten nicht rechtzeitig abgeschlossen werden. Bitte versuche es erneut.");
            default:
                throw new ProfileOperationException(
                    "profile_completion_drain_failed",
                    "Mindestens ein Arena-Ergebnis konnte nicht sicher gespeichert werden. Bitte versuche es später erneut.");
        }
    }

    private async Task AbortActiveAttemptsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await db.TypingAttempts
            .Where(attempt => attempt.UserProfileId == profileId &&
                (attempt.Phase == AttemptPhase.Prepared || attempt.Phase == AttemptPhase.Started))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(attempt => attempt.Phase, AttemptPhase.Aborted),
                cancellationToken);
    }

    private static async Task RollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task<UserProfile> ReloadAvailableProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var tracked = db.UserProfiles.Local.SingleOrDefault(item => item.Id == profileId);
        if (tracked is not null)
        {
            var current = await db.UserProfiles
                .AsNoTracking()
                .SingleAsync(item => item.Id == profileId, cancellationToken);
            if (current.Deleted)
            {
                throw new InvalidOperationException("Dieses Profil wurde bereits gelöscht.");
            }

            db.Entry(tracked).CurrentValues.SetValues(current);
            return tracked;
        }

        return await db.UserProfiles.SingleAsync(
            item => item.Id == profileId && !item.Deleted,
            cancellationToken);
    }

    private async Task DeleteDerivedStatisticsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await db.TypingAttemptErrors.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.ChallengeAttemptBindings.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.TypingAttempts.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.RewardLedgerEntries.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.Missions.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.Achievements.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.GamificationEvents.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.WeaknessObservations.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task RemoveOwnedCollectionsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var collectionIds = await db.TextCollections
            .Where(item => item.OwnerProfileId == profileId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (collectionIds.Count == 0)
        {
            return;
        }

        await db.TextCollectionItems
            .Where(item => collectionIds.Contains(item.TextCollectionId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.TextCollections
            .Where(item => item.OwnerProfileId == profileId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task PseudonymizeOwnedTextsAsync(Guid profileId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.TrainingTexts
            .Where(item => item.OwnerProfileId == profileId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Title, "Gelöschter Text")
                .SetProperty(item => item.Body, "")
                .SetProperty(item => item.Visibility, TrainingTextVisibility.Private)
                .SetProperty(item => item.RatingEligible, false)
                .SetProperty(item => item.CharacterCount, 0)
                .SetProperty(item => item.UpdatedAt, now),
                cancellationToken);
    }

    private async Task MarkActiveChallengesDeclinedAsync(Guid profileId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var activeChallengeIds = (await db.Challenges.ToListAsync(cancellationToken))
            .Where(item => item.Status is ChallengeStatus.Open or ChallengeStatus.Running)
            .Select(item => item.Id)
            .ToList();
        if (activeChallengeIds.Count == 0)
        {
            return;
        }

        await db.ChallengeParticipants
            .Where(item => item.UserProfileId == profileId &&
                activeChallengeIds.Contains(item.ChallengeId) &&
                item.Status != ParticipantStatus.Finished &&
                item.Status != ParticipantStatus.Dnf &&
                item.Status != ParticipantStatus.Declined)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ParticipantStatus.Declined)
                .SetProperty(item => item.RespondedAt, now),
                cancellationToken);
    }
}
