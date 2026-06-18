using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record ProfileExportPayload(
    int Version,
    DateTimeOffset GeneratedAt,
    UserProfile Profile,
    IReadOnlyList<TypingAttempt> Attempts,
    IReadOnlyList<TypingAttemptError> AttemptErrors,
    IReadOnlyList<Mission> Missions,
    IReadOnlyList<Achievement> Achievements,
    IReadOnlyList<WeaknessObservation> WeaknessObservations,
    IReadOnlyList<TrainingText> OwnedTexts,
    IReadOnlyList<TextCollection> OwnedCollections,
    IReadOnlyList<ChallengeParticipant> ChallengeParticipations,
    IReadOnlyList<ChallengeRoundResult> ChallengeRoundResults,
    IReadOnlyList<LiveRoomParticipantSummary> LiveRoomResults);

public sealed class ProfilePrivacyService(KeyWarsDbContext db, LiveRoomManager liveRooms, TimeProvider timeProvider)
{
    public async Task<ProfileExportPayload> BuildExportAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId && !item.Deleted, cancellationToken);
        return new ProfileExportPayload(
            1,
            timeProvider.GetUtcNow(),
            profile,
            await db.TypingAttempts.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.TypingAttemptErrors.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.Missions.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.Achievements.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.WeaknessObservations.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.TrainingTexts.Where(item => item.OwnerProfileId == profileId).ToListAsync(cancellationToken),
            await db.TextCollections.Where(item => item.OwnerProfileId == profileId).ToListAsync(cancellationToken),
            await db.ChallengeParticipants.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.ChallengeRoundResults.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken),
            await db.LiveRoomParticipantSummaries.Where(item => item.UserProfileId == profileId).ToListAsync(cancellationToken));
    }

    public async Task ResetStatisticsAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId && !item.Deleted, cancellationToken);
        await DeleteDerivedStatisticsAsync(profileId, cancellationToken);
        profile.ExperiencePoints = 0;
        profile.Level = 1;
        profile.SeasonPoints = 0;
        profile.CurrentStreakDays = 0;
        profile.LastActivityDate = null;
        profile.ArenaRating = 1000;
        profile.RatedMatchCount = 0;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        liveRooms.RemoveProfile(profileId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId && !item.Deleted, cancellationToken);
        await DeleteDerivedStatisticsAsync(profileId, cancellationToken);
        await RemoveOwnedCollectionsAsync(profileId, cancellationToken);
        await PseudonymizeOwnedTextsAsync(profileId, now, cancellationToken);
        await MarkActiveChallengesDeclinedAsync(profileId, now, cancellationToken);

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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeleteDerivedStatisticsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await db.TypingAttemptErrors.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.ChallengeAttemptBindings.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.TypingAttempts.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.Missions.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
        await db.Achievements.Where(item => item.UserProfileId == profileId).ExecuteDeleteAsync(cancellationToken);
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
