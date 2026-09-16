using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class ProfilePrivacyServiceTests
{
    [Fact]
    public void ActiveChallengeDeclineStaysServerSideAndProfileScoped()
    {
        var source = File.ReadAllText(FindPrivacyServiceSource());

        Assert.DoesNotContain("db.Challenges.ToListAsync", source, StringComparison.Ordinal);
        Assert.Contains("db.Challenges.Any(challenge =>", source, StringComparison.Ordinal);
        Assert.Contains("item.UserProfileId == profileId", source, StringComparison.Ordinal);
        Assert.Contains("ExecuteUpdateAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetStatisticsClearsDerivedDataAndRatings()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        var profile = context.Profile;
        await context.SeedStatisticsAsync();
        await context.SeedActiveAttemptAsync(AttemptPhase.Prepared);
        await context.SeedActiveAttemptAsync(AttemptPhase.Started);
        var completion = CreateCompletionRecord(profile.Id);
        context.Completions.Enqueue(completion);
        await context.Completions.FlushAsync();
        var room = context.LiveRooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Reset", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        context.Presence.EnterRoom(profile.Id, "reset-tab", room.RoomId);
        var service = context.CreatePrivacyService();

        await service.ResetStatisticsAsync(profile.Id);

        Assert.Empty(await context.Db.TypingAttempts.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.TypingAttemptErrors.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.RewardLedgerEntries.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.Missions.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.Achievements.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.GamificationEvents.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Empty(await context.Db.WeaknessObservations.Where(item => item.UserProfileId == profile.Id).ToListAsync());
        Assert.Equal(0, profile.ExperiencePoints);
        Assert.Equal(1, profile.Level);
        Assert.Equal(0, profile.SeasonPoints);
        Assert.Equal(0, profile.CurrentStreakDays);
        Assert.Equal(1000, profile.ArenaRating);
        Assert.Equal(0, profile.RatedMatchCount);
        Assert.Null(profile.LastActivityDate);
        Assert.Empty(context.Sessions.RemoveProfile(profile.Id));
        Assert.Equal(ProfileAccessState.Available, context.AccessGate.GetState(profile.Id));
        Assert.Equal(CompletionState.Persisted, context.Completions.GetStatus(completion.Id).State);
        Assert.Empty(context.LiveRooms.Snapshot(room.RoomId).Participants);
        Assert.Equal(0, context.Presence.CountRoomConnections(profile.Id, room.RoomId));
        Assert.Equal(profile.Id, Assert.Single(context.HubConnections.RevokedProfiles));
    }

    [Fact]
    public async Task DeleteProfilePseudonymizesAndAllowsFreshProvisioning()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        var profile = context.Profile;
        var room = context.LiveRooms.CreateRoom(new CreateLiveRoomRequest(profile.Id, profile.DisplayName, "Privat", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        await context.SeedStatisticsAsync();
        var text = await context.SeedOwnedTextAndCollectionAsync();
        var challenge = await context.SeedActiveChallengeAsync();
        challenge.TrainingTextId = text.Id;
        challenge.TargetTextSnapshot = text.Body;
        challenge.TargetTextHash = TextHash.Compute(text.Body);
        challenge.RatingEligible = true;
        await context.Db.SaveChangesAsync();
        await context.SeedActiveAttemptAsync(AttemptPhase.Started);
        var presenceRoomId = Guid.CreateVersion7();
        context.Presence.EnterRoom(profile.Id, "privacy-tab", presenceRoomId);
        var service = context.CreatePrivacyService();

        await service.DeleteProfileAsync(profile.Id);
        var reprovisioned = await new ProfileProvisioner(context.Db, context.Time)
            .ProvisionAsync(context.Identity, CancellationToken.None);

        Assert.True(profile.Deleted);
        Assert.Equal(profile.Id, Assert.Single(context.HubConnections.RevokedProfiles));
        Assert.NotEqual(context.Identity.ObjectGuid, profile.DirectoryObjectGuid);
        Assert.Equal("Gelöschtes Profil", profile.DisplayName);
        Assert.False(profile.LeaderboardVisible);
        Assert.False(profile.ChallengesEnabled);
        Assert.NotEqual(profile.Id, reprovisioned.Id);
        Assert.Equal(context.Identity.ObjectGuid, reprovisioned.DirectoryObjectGuid);
        Assert.False(reprovisioned.Deleted);
        Assert.Empty(await context.Db.TextCollections.Where(item => item.OwnerProfileId == profile.Id).ToListAsync());
        var storedText = await context.Db.TrainingTexts.AsNoTracking().SingleAsync(item => item.Id == text.Id);
        Assert.Equal("Gelöschter Text", storedText.Title);
        Assert.Equal("", storedText.Body);
        Assert.Equal(0, storedText.CharacterCount);
        var participant = await context.Db.ChallengeParticipants.AsNoTracking().SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == profile.Id);
        Assert.Equal(ParticipantStatus.Declined, participant.Status);
        var storedChallenge = await context.Db.Challenges.AsNoTracking().SingleAsync(item => item.Id == challenge.Id);
        Assert.Null(storedChallenge.TargetTextSnapshot);
        Assert.Equal(TextHash.Compute("Geheimer Text"), storedChallenge.TargetTextHash);
        Assert.False(storedChallenge.RatingEligible);
        Assert.Empty(context.LiveRooms.Snapshot(room.RoomId).Participants);
        Assert.Equal(0, context.Presence.CountRoomConnections(profile.Id, presenceRoomId));
        Assert.Null(context.Presence.RemoveConnection(profile.Id, "privacy-tab"));
        Assert.Empty(context.Sessions.RemoveProfile(profile.Id));
        Assert.Equal(ProfileAccessState.Deleted, context.AccessGate.GetState(profile.Id));
        Assert.True(context.AccessGate.IsBlocked(profile.Id));
        var repeatedDelete = await Assert.ThrowsAsync<ProfileOperationException>(() => service.DeleteProfileAsync(profile.Id));
        Assert.Equal("profile_deleted", repeatedDelete.Code);
    }

    [Fact]
    public async Task ExistingGateOperationRejectsResetWithoutRemovingSession()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        await context.SeedStatisticsAsync();
        var attempt = await context.SeedActiveAttemptAsync(AttemptPhase.Prepared);
        Assert.True(context.AccessGate.TryBeginOperation(context.Profile.Id));

        var exception = await Assert.ThrowsAsync<ProfileOperationException>(() =>
            context.CreatePrivacyService().ResetStatisticsAsync(context.Profile.Id));

        Assert.Equal("profile_operation_in_progress", exception.Code);
        Assert.True(context.Sessions.TryGet(attempt.Id, out _));
        Assert.Equal(900, context.Profile.ExperiencePoints);
        context.AccessGate.CompleteOperation(context.Profile.Id);
    }

    [Fact]
    public async Task HubRevocationFailureStopsPrivacyMutationAndReleasesGate()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        await context.SeedStatisticsAsync();
        context.HubConnections.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.CreatePrivacyService().ResetStatisticsAsync(context.Profile.Id));

        Assert.Equal(900, context.Profile.ExperiencePoints);
        Assert.Equal(context.Profile.Id, Assert.Single(context.HubConnections.RevokedProfiles));
        Assert.Equal(ProfileAccessState.Available, context.AccessGate.GetState(context.Profile.Id));
    }

    [Fact]
    public async Task ResetWaitsForPreviouslyAdmittedProfileOperation()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        await context.SeedStatisticsAsync();
        var admittedRequest = context.AccessGate.Acquire(context.Profile.Id);

        var reset = context.CreatePrivacyService().ResetStatisticsAsync(context.Profile.Id);

        Assert.Equal(ProfileAccessState.OperationInProgress, context.AccessGate.GetState(context.Profile.Id));
        Assert.False(reset.IsCompleted);
        Assert.Equal(900, context.Profile.ExperiencePoints);

        admittedRequest.Dispose();
        await reset;

        Assert.Equal(0, context.Profile.ExperiencePoints);
        Assert.Equal(ProfileAccessState.Available, context.AccessGate.GetState(context.Profile.Id));
    }

    [Fact]
    public async Task LostExclusiveLeaseCancelsPrivacyOperationAndDoesNotMutateProfile()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        await context.SeedStatisticsAsync();
        var accessGate = new LostLeaseProfileAccessGate();
        var service = new ProfilePrivacyService(
            context.Db,
            new LocalLiveRoomDispatcher(context.LiveRooms),
            context.Completions,
            context.Sessions,
            context.Presence,
            accessGate,
            context.Time,
            context.HubConnections);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ResetStatisticsAsync(context.Profile.Id));

        Assert.Equal(900, context.Profile.ExperiencePoints);
        Assert.True(accessGate.Disposed);
    }

    [Fact]
    public async Task DrainTimeoutAbortsAttemptsButLeavesStatisticsAndReleasesGate()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        await context.SeedStatisticsAsync();
        var attempt = await context.SeedActiveAttemptAsync(AttemptPhase.Started);
        context.Completions.Enqueue(CreateCompletionRecord(context.Profile.Id));

        var exception = await Assert.ThrowsAsync<ProfileOperationException>(() =>
            context.CreatePrivacyService().ResetStatisticsAsync(context.Profile.Id));

        await context.Db.Entry(attempt).ReloadAsync();
        Assert.Equal("profile_completion_drain_timeout", exception.Code);
        Assert.Equal(AttemptPhase.Aborted, attempt.Phase);
        Assert.False(context.Sessions.TryGet(attempt.Id, out _));
        Assert.Equal(900, context.Profile.ExperiencePoints);
        Assert.NotEmpty(await context.Db.RewardLedgerEntries.Where(item => item.UserProfileId == context.Profile.Id).ToListAsync());
        Assert.Equal(ProfileAccessState.Available, context.AccessGate.GetState(context.Profile.Id));
    }

    [Fact]
    public async Task FailedDrainDoesNotDeleteProfileAndReleasesGate()
    {
        await using var context = await PrivacyTestContext.CreateAsync(failCompletionWrites: true);
        await context.SeedStatisticsAsync();
        var completion = CreateCompletionRecord(context.Profile.Id);
        context.Completions.Enqueue(completion);
        await context.Completions.FlushAsync();

        var exception = await Assert.ThrowsAsync<ProfileOperationException>(() =>
            context.CreatePrivacyService().DeleteProfileAsync(context.Profile.Id));

        Assert.Equal("profile_completion_drain_failed", exception.Code);
        Assert.False(context.Profile.Deleted);
        Assert.Equal(900, context.Profile.ExperiencePoints);
        Assert.Equal(ProfileAccessState.Available, context.AccessGate.GetState(context.Profile.Id));
    }

    [Fact]
    public async Task ExportContainsOnlyCurrentProfileInventory()
    {
        await using var context = await PrivacyTestContext.CreateAsync();
        var other = PrivacyTestContext.CreateProfile("other", "Andere Person", "22222222-2222-2222-2222-222222222222");
        context.Db.UserProfiles.Add(other);
        await context.SeedStatisticsAsync();
        var ownedText = await context.SeedOwnedTextAndCollectionAsync();
        var ownedCollection = await context.Db.TextCollections.SingleAsync(item => item.OwnerProfileId == context.Profile.Id);
        var profileAttempt = await context.Db.TypingAttempts.SingleAsync(item => item.UserProfileId == context.Profile.Id);
        profileAttempt.Nonce = "attempt-nonce-current";
        var now = context.Time.GetUtcNow();
        var otherAttempt = new TypingAttempt
        {
            UserProfileId = other.Id,
            Mode = TrainingMode.Words10,
            Phase = AttemptPhase.Finished,
            Nonce = "attempt-nonce-other",
            PreparedAt = now,
            StartedAt = now,
            FinishedAt = now.AddSeconds(15),
            Completed = true
        };
        var otherText = new TrainingText
        {
            OwnerProfileId = other.Id,
            Title = "Fremder Text",
            SourceKey = $"other-{Guid.CreateVersion7():N}",
            Body = "Nicht exportieren",
            Visibility = TrainingTextVisibility.Private,
            CharacterCount = 17
        };
        var otherCollection = new TextCollection
        {
            OwnerProfileId = other.Id,
            Name = "Fremde Sammlung",
            Visibility = TrainingTextVisibility.Private
        };
        var createdChallenge = new Challenge
        {
            CreatorProfileId = context.Profile.Id,
            TrainingTextId = ownedText.Id,
            Title = "Eigene Challenge",
            Status = ChallengeStatus.Running,
            ExpiresAt = now.AddDays(1)
        };
        var createdRound = new ChallengeRound { ChallengeId = createdChallenge.Id, RoundNumber = 1 };
        var participatedChallenge = new Challenge
        {
            CreatorProfileId = other.Id,
            TrainingTextId = otherText.Id,
            Title = "Teilgenommene Challenge",
            Status = ChallengeStatus.Running,
            ExpiresAt = now.AddDays(1)
        };
        var participatedRound = new ChallengeRound { ChallengeId = participatedChallenge.Id, RoundNumber = 1 };
        var unrelatedChallenge = new Challenge
        {
            CreatorProfileId = other.Id,
            TrainingTextId = otherText.Id,
            Title = "Fremde Challenge",
            Status = ChallengeStatus.Running,
            ExpiresAt = now.AddDays(1)
        };
        var unrelatedRound = new ChallengeRound { ChallengeId = unrelatedChallenge.Id, RoundNumber = 1 };
        var profileBinding = new ChallengeAttemptBinding
        {
            ChallengeId = participatedChallenge.Id,
            ChallengeRoundId = participatedRound.Id,
            UserProfileId = context.Profile.Id,
            TypingAttemptId = profileAttempt.Id,
            TextSnapshotHash = "profile-hash",
            Mode = TrainingMode.Words10,
            BindingToken = "binding-secret-current",
            Consumed = true,
            ConsumedAt = now
        };
        var otherBinding = new ChallengeAttemptBinding
        {
            ChallengeId = unrelatedChallenge.Id,
            ChallengeRoundId = unrelatedRound.Id,
            UserProfileId = other.Id,
            TypingAttemptId = otherAttempt.Id,
            TextSnapshotHash = "other-hash",
            Mode = TrainingMode.Words10,
            BindingToken = "binding-secret-other"
        };
        var createdRoom = new LiveRoomSummary
        {
            Id = Guid.CreateVersion7(),
            IdempotencyKey = $"created-{Guid.CreateVersion7():N}",
            CreatorProfileId = context.Profile.Id,
            RoomCode = "OWN123",
            Mode = LiveRoomMode.Classic,
            Visibility = LiveRoomVisibility.InternalOpen,
            RoundCount = 1,
            CreatedAt = now,
            FinishedAt = now.AddMinutes(1)
        };
        var participatedRoom = new LiveRoomSummary
        {
            Id = Guid.CreateVersion7(),
            IdempotencyKey = $"participated-{Guid.CreateVersion7():N}",
            CreatorProfileId = other.Id,
            RoomCode = "OTH123",
            Mode = LiveRoomMode.Classic,
            Visibility = LiveRoomVisibility.InternalOpen,
            RoundCount = 1,
            CreatedAt = now,
            FinishedAt = now.AddMinutes(1)
        };
        var season = new Season
        {
            Key = "2026-06",
            Name = "Juni 2026",
            StartsAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedAt = now
        };
        context.Db.TypingAttempts.Add(otherAttempt);
        context.Db.TrainingTexts.Add(otherText);
        context.Db.TextCollections.Add(otherCollection);
        context.Db.TextCollectionItems.Add(new TextCollectionItem
        {
            TextCollectionId = otherCollection.Id,
            TrainingTextId = otherText.Id
        });
        context.Db.TypingAttemptErrors.Add(new TypingAttemptError
        {
            TypingAttemptId = otherAttempt.Id,
            UserProfileId = other.Id,
            Position = 1,
            Kind = TypingErrorKind.Substitution,
            Expected = "a",
            Actual = "x",
            Pattern = "an"
        });
        context.Db.RewardLedgerEntries.Add(new RewardLedgerEntry
        {
            UserProfileId = other.Id,
            Source = "attempt",
            SourceId = otherAttempt.Id.ToString("N"),
            Xp = 25,
            AwardedAt = now
        });
        context.Db.Seasons.Add(season);
        context.Db.SeasonScores.AddRange(
            new SeasonScore { SeasonId = season.Id, UserProfileId = context.Profile.Id, Points = 17, UpdatedAt = now },
            new SeasonScore { SeasonId = season.Id, UserProfileId = other.Id, Points = 99, UpdatedAt = now });
        context.Db.Missions.Add(new Mission
        {
            UserProfileId = other.Id,
            MissionDate = DateOnly.FromDateTime(now.Date),
            Key = "other-mission",
            Title = "Andere Mission",
            Description = "Nicht exportieren",
            TargetValue = 1
        });
        context.Db.Achievements.Add(new Achievement
        {
            UserProfileId = other.Id,
            Key = "other-achievement",
            Title = "Andere Auszeichnung",
            Description = "Nicht exportieren"
        });
        context.Db.GamificationEvents.Add(new GamificationEvent
        {
            UserProfileId = other.Id,
            Type = GamificationEventType.XpAwarded,
            EventKey = "xp-awarded",
            Title = "+25 XP",
            Description = "Andere Person",
            XpDelta = 25,
            LevelBefore = 1,
            LevelAfter = 1,
            Source = "attempt",
            SourceId = otherAttempt.Id.ToString("N"),
            CreatedAt = now
        });
        context.Db.WeaknessObservations.Add(new WeaknessObservation
        {
            UserProfileId = other.Id,
            Pattern = "an",
            Attempts = 3,
            Errors = 1,
            LastSeenAt = now
        });
        context.Db.Challenges.AddRange(createdChallenge, participatedChallenge, unrelatedChallenge);
        context.Db.ChallengeRounds.AddRange(createdRound, participatedRound, unrelatedRound);
        context.Db.ChallengeParticipants.AddRange(
            new ChallengeParticipant { ChallengeId = createdChallenge.Id, UserProfileId = context.Profile.Id, Status = ParticipantStatus.Running },
            new ChallengeParticipant { ChallengeId = createdChallenge.Id, UserProfileId = other.Id, Status = ParticipantStatus.Running },
            new ChallengeParticipant { ChallengeId = participatedChallenge.Id, UserProfileId = context.Profile.Id, Status = ParticipantStatus.Running },
            new ChallengeParticipant { ChallengeId = participatedChallenge.Id, UserProfileId = other.Id, Status = ParticipantStatus.Running },
            new ChallengeParticipant { ChallengeId = unrelatedChallenge.Id, UserProfileId = other.Id, Status = ParticipantStatus.Running });
        context.Db.ChallengeRoundResults.AddRange(
            new ChallengeRoundResult
            {
                ChallengeRoundId = participatedRound.Id,
                UserProfileId = context.Profile.Id,
                TypingAttemptId = profileAttempt.Id,
                Status = ParticipantStatus.Finished,
                FinishedAt = now
            },
            new ChallengeRoundResult
            {
                ChallengeRoundId = unrelatedRound.Id,
                UserProfileId = other.Id,
                TypingAttemptId = otherAttempt.Id,
                Status = ParticipantStatus.Finished,
                FinishedAt = now
            });
        context.Db.ChallengeAttemptBindings.AddRange(profileBinding, otherBinding);
        context.Db.LiveRoomSummaries.AddRange(createdRoom, participatedRoom);
        context.Db.LiveRoomParticipantSummaries.AddRange(
            new LiveRoomParticipantSummary
            {
                LiveRoomSummaryId = createdRoom.Id,
                UserProfileId = other.Id,
                Status = ParticipantStatus.Finished
            },
            new LiveRoomParticipantSummary
            {
                LiveRoomSummaryId = participatedRoom.Id,
                UserProfileId = context.Profile.Id,
                Status = ParticipantStatus.Finished
            });
        await context.Db.SaveChangesAsync();
        var service = context.CreatePrivacyService();

        var export = await service.BuildExportAsync(context.Profile.Id);

        Assert.Equal(ProfileExportService.SchemaVersion, export.Version);
        Assert.Equal(context.Profile.Id, export.Profile.Id);
        var exportedAttempt = Assert.Single(export.Attempts);
        Assert.Equal(profileAttempt.Id, exportedAttempt.Id);
        Assert.Equal(profileAttempt.Mode, exportedAttempt.Mode);
        Assert.NotEmpty(export.AttemptErrors);
        Assert.NotEmpty(export.RewardLedger);
        Assert.Equal(season.Id, Assert.Single(export.Seasons).Id);
        var exportedSeasonScore = Assert.Single(export.SeasonScores);
        Assert.Equal(context.Profile.Id, exportedSeasonScore.UserProfileId);
        Assert.Equal(17, exportedSeasonScore.Points);
        Assert.NotEmpty(export.Missions);
        Assert.NotEmpty(export.Achievements);
        Assert.NotEmpty(export.GamificationEvents);
        Assert.NotEmpty(export.WeaknessObservations);
        Assert.All(export.Attempts, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.AttemptErrors, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.RewardLedger, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.Missions, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.Achievements, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.GamificationEvents, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.All(export.WeaknessObservations, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.Equal(ownedText.Id, Assert.Single(export.OwnedTexts).Id);
        Assert.Equal(ownedCollection.Id, Assert.Single(export.OwnedCollections).Id);
        Assert.Equal(ownedCollection.Id, Assert.Single(export.OwnedCollectionItems).TextCollectionId);
        Assert.Equal(createdChallenge.Id, Assert.Single(export.CreatedChallenges).Id);
        Assert.Equal(2, export.ChallengeRounds.Count);
        Assert.Contains(export.ChallengeRounds, item => item.Id == createdRound.Id);
        Assert.Contains(export.ChallengeRounds, item => item.Id == participatedRound.Id);
        Assert.Equal(2, export.ChallengeParticipations.Count);
        Assert.All(export.ChallengeParticipations, item => Assert.Equal(context.Profile.Id, item.UserProfileId));
        Assert.Equal(participatedRound.Id, Assert.Single(export.ChallengeRoundResults).ChallengeRoundId);
        var exportedBinding = Assert.Single(export.ChallengeAttemptBindings);
        Assert.Equal(profileBinding.Id, exportedBinding.Id);
        Assert.Equal(context.Profile.Id, exportedBinding.UserProfileId);
        Assert.Equal("profile-hash", exportedBinding.TextSnapshotHash);
        var json = System.Text.Json.JsonSerializer.Serialize(export);
        Assert.DoesNotContain(nameof(TypingAttempt.Nonce), json);
        Assert.DoesNotContain(profileAttempt.Nonce, json);
        Assert.DoesNotContain(nameof(ChallengeAttemptBinding.BindingToken), json);
        Assert.DoesNotContain(profileBinding.BindingToken, json);
        Assert.DoesNotContain(nameof(LiveRoomSummary.IdempotencyKey), json);
        Assert.DoesNotContain(createdRoom.IdempotencyKey, json);
        var exportedRoom = Assert.Single(export.CreatedLiveRooms);
        Assert.Equal(createdRoom.Id, exportedRoom.Id);
        Assert.Equal(createdRoom.RoomCode, exportedRoom.RoomCode);
        Assert.Equal(participatedRoom.Id, Assert.Single(export.LiveRoomResults).LiveRoomSummaryId);
        Assert.DoesNotContain(export.ChallengeRounds, item => item.Id == unrelatedRound.Id);
        Assert.DoesNotContain(export.CreatedChallenges, item => item.Id == participatedChallenge.Id || item.Id == unrelatedChallenge.Id);
        Assert.DoesNotContain(export.CreatedLiveRooms, item => item.Id == participatedRoom.Id);
        Assert.Equal(
            typeof(TypingAttempt).GetProperties().Select(item => item.Name).Where(item => item != nameof(TypingAttempt.Nonce)).Order().ToArray(),
            typeof(TypingAttemptExport).GetProperties().Select(item => item.Name).Order().ToArray());
        Assert.Equal(
            typeof(ChallengeAttemptBinding).GetProperties().Select(item => item.Name).Where(item => item != nameof(ChallengeAttemptBinding.BindingToken)).Order().ToArray(),
            typeof(ChallengeAttemptBindingExport).GetProperties().Select(item => item.Name).Order().ToArray());
        Assert.Equal(
            typeof(LiveRoomSummary).GetProperties().Select(item => item.Name).Where(item => item != nameof(LiveRoomSummary.IdempotencyKey)).Order().ToArray(),
            typeof(LiveRoomSummaryExport).GetProperties().Select(item => item.Name).Order().ToArray());
        string[] coveredDbSets =
        [
            nameof(KeyWarsDbContext.UserProfiles),
            nameof(KeyWarsDbContext.TrainingTexts),
            nameof(KeyWarsDbContext.TextCollections),
            nameof(KeyWarsDbContext.TextCollectionItems),
            nameof(KeyWarsDbContext.ContentModerationAuditEntries),
            nameof(KeyWarsDbContext.TypingAttempts),
            nameof(KeyWarsDbContext.TypingAttemptErrors),
            nameof(KeyWarsDbContext.Challenges),
            nameof(KeyWarsDbContext.ChallengeParticipants),
            nameof(KeyWarsDbContext.ChallengeRounds),
            nameof(KeyWarsDbContext.ChallengeRoundResults),
            nameof(KeyWarsDbContext.ChallengeAttemptBindings),
            nameof(KeyWarsDbContext.LiveRoomSummaries),
            nameof(KeyWarsDbContext.LiveRoomParticipantSummaries),
            nameof(KeyWarsDbContext.Missions),
            nameof(KeyWarsDbContext.RewardLedgerEntries),
            nameof(KeyWarsDbContext.Seasons),
            nameof(KeyWarsDbContext.SeasonScores),
            nameof(KeyWarsDbContext.Achievements),
            nameof(KeyWarsDbContext.GamificationEvents),
            nameof(KeyWarsDbContext.WeaknessObservations)
        ];
        var currentDbSets = typeof(KeyWarsDbContext)
            .GetProperties()
            .Where(item => item.PropertyType.IsGenericType &&
                item.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(item => item.Name)
            .Order()
            .ToArray();
        Assert.Equal(currentDbSets, coveredDbSets.Order().ToArray());
    }

    private static CompletedRoomRecord CreateCompletionRecord(Guid profileId)
    {
        var roomId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-06-18T12:00:00Z");
        return new CompletedRoomRecord(
            roomId,
            1,
            2,
            $"{roomId:N}:1:2",
            profileId,
            "ABC123",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            now,
            now.AddSeconds(3),
            now.AddSeconds(30),
            [new CompletedParticipantRecord(profileId, ParticipantStatus.Finished, 1, 27000, 70, 100)]);
    }

    private static string FindPrivacyServiceSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "KeyWars", "Services", "ProfilePrivacyService.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("ProfilePrivacyService.cs wurde nicht gefunden.");
    }

    private sealed class PrivacyTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider outboxServices;

        private PrivacyTestContext(
            SqliteConnection connection,
            KeyWarsDbContext db,
            ManualTimeProvider time,
            UserProfile profile,
            DirectoryIdentity identity,
            LiveRoomManager liveRooms,
            LiveRoomCompletionQueue completions,
            AttemptSessionStore sessions,
            LivePresenceTracker presence,
            ProfileAccessGate accessGate,
            RecordingProfileHubConnectionRevoker hubConnections,
            ServiceProvider outboxServices)
        {
            this.connection = connection;
            this.outboxServices = outboxServices;
            Db = db;
            Time = time;
            Profile = profile;
            Identity = identity;
            LiveRooms = liveRooms;
            Completions = completions;
            Sessions = sessions;
            Presence = presence;
            AccessGate = accessGate;
            HubConnections = hubConnections;
        }

        public KeyWarsDbContext Db { get; }
        public ManualTimeProvider Time { get; }
        public UserProfile Profile { get; }
        public DirectoryIdentity Identity { get; }
        public LiveRoomManager LiveRooms { get; }
        public LiveRoomCompletionQueue Completions { get; }
        public AttemptSessionStore Sessions { get; }
        public LivePresenceTracker Presence { get; }
        public ProfileAccessGate AccessGate { get; }
        public RecordingProfileHubConnectionRevoker HubConnections { get; }

        public static async Task<PrivacyTestContext> CreateAsync(bool failCompletionWrites = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
            var identity = new DirectoryIdentity(
                "11111111-1111-1111-1111-111111111111",
                "S-1-5-21-privacy",
                "privacy",
                "privacy@example.local",
                "Paula Privacy",
                "Paula",
                "Privacy",
                "privacy@example.local",
                "IT",
                "Trainerin");
            var profile = CreateProfile("privacy", "Paula Privacy", identity.ObjectGuid);
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
            var liveOptions = new LiveOptions
            {
                MaxConcurrentRooms = 16,
                CompletionQueueCapacity = 16,
                CompletionDrainTimeoutSeconds = 1
            };
            ILiveRoomCompletionWriter completionWriter = failCompletionWrites
                ? new FailingCompletionWriter()
                : new NoopCompletionWriter();
            var outboxServices = new ServiceCollection()
                .AddScoped(_ => new KeyWarsDbContext(options))
                .AddSingleton<TimeProvider>(time)
                .AddSingleton<LiveRoomCompletionOutbox>()
                .BuildServiceProvider();
            var completions = new LiveRoomCompletionQueue(
                Options.Create(liveOptions),
                completionWriter,
                outboxServices.GetRequiredService<LiveRoomCompletionOutbox>(),
                time,
                NullLogger<LiveRoomCompletionQueue>.Instance);
            var sessions = new AttemptSessionStore();
            var presence = new LivePresenceTracker(Options.Create(liveOptions), time);
            var accessGate = new ProfileAccessGate();
            return new PrivacyTestContext(
                connection,
                db,
                time,
                profile,
                identity,
                CreateLiveRooms(time, liveOptions, completions),
                completions,
                sessions,
                presence,
                accessGate,
                new RecordingProfileHubConnectionRevoker(),
                outboxServices);
        }

        public ProfilePrivacyService CreatePrivacyService() => new(
            Db,
            new LocalLiveRoomDispatcher(LiveRooms),
            Completions,
            Sessions,
            Presence,
            AccessGate,
            Time,
            HubConnections);

        public async Task SeedStatisticsAsync()
        {
            Profile.ExperiencePoints = 900;
            Profile.Level = 4;
            Profile.SeasonPoints = 70;
            Profile.CurrentStreakDays = 5;
            Profile.LastActivityDate = DateOnly.FromDateTime(Time.GetUtcNow().Date);
            Profile.ArenaRating = 1240;
            Profile.RatedMatchCount = 12;
            var attempt = new TypingAttempt
            {
                UserProfileId = Profile.Id,
                Mode = TrainingMode.Words10,
                Phase = AttemptPhase.Finished,
                PreparedAt = Time.GetUtcNow(),
                StartedAt = Time.GetUtcNow(),
                FinishedAt = Time.GetUtcNow().AddSeconds(30),
                Completed = true,
                Wpm = 50,
                Accuracy = 99
            };
            Db.TypingAttempts.Add(attempt);
            Db.TypingAttemptErrors.Add(new TypingAttemptError
            {
                TypingAttemptId = attempt.Id,
                UserProfileId = Profile.Id,
                Position = 1,
                Kind = TypingErrorKind.Substitution,
                Expected = "t",
                Actual = "z",
                Pattern = "te"
            });
            Db.RewardLedgerEntries.Add(new RewardLedgerEntry
            {
                UserProfileId = Profile.Id,
                Source = "attempt",
                SourceId = attempt.Id.ToString("N"),
                Xp = 70,
                AwardedAt = Time.GetUtcNow()
            });
            Db.Missions.Add(new Mission { UserProfileId = Profile.Id, MissionDate = DateOnly.FromDateTime(Time.GetUtcNow().Date), Key = "test-mission", Title = "Test", Description = "Test", TargetValue = 1, CurrentValue = 1, Completed = true });
            Db.Achievements.Add(new Achievement { UserProfileId = Profile.Id, Key = "test", Title = "Test", Description = "Test" });
            Db.GamificationEvents.Add(new GamificationEvent
            {
                UserProfileId = Profile.Id,
                Type = GamificationEventType.XpAwarded,
                EventKey = "xp-awarded",
                Title = "+70 XP",
                Description = "Test",
                XpDelta = 70,
                LevelBefore = 3,
                LevelAfter = 4,
                Source = "attempt",
                SourceId = attempt.Id.ToString("N"),
                CreatedAt = Time.GetUtcNow()
            });
            Db.WeaknessObservations.Add(new WeaknessObservation { UserProfileId = Profile.Id, Pattern = "te", Attempts = 6, Errors = 2 });
            await Db.SaveChangesAsync();
        }

        public async Task<TypingAttempt> SeedActiveAttemptAsync(AttemptPhase phase)
        {
            var attempt = new TypingAttempt
            {
                UserProfileId = Profile.Id,
                Mode = TrainingMode.Words10,
                Phase = phase,
                Nonce = Guid.NewGuid().ToString("N"),
                PreparedAt = Time.GetUtcNow(),
                StartedAt = Time.GetUtcNow()
            };
            Db.TypingAttempts.Add(attempt);
            await Db.SaveChangesAsync();
            Sessions.Add(new AttemptSession(
                attempt.Id,
                Profile.Id,
                "Text",
                attempt.Mode,
                attempt.PreparedAt,
                phase == AttemptPhase.Started ? attempt.StartedAt : null,
                attempt.Nonce,
                phase));
            return attempt;
        }

        public async Task<TrainingText> SeedOwnedTextAndCollectionAsync()
        {
            var text = new TrainingText
            {
                OwnerProfileId = Profile.Id,
                Title = "Privater Text",
                SourceKey = $"user-{Guid.CreateVersion7():N}",
                Body = "Geheimer Text",
                Visibility = TrainingTextVisibility.Private,
                CharacterCount = 13
            };
            var collection = new TextCollection
            {
                OwnerProfileId = Profile.Id,
                Name = "Privat",
                Visibility = TrainingTextVisibility.Private
            };
            Db.TrainingTexts.Add(text);
            Db.TextCollections.Add(collection);
            Db.TextCollectionItems.Add(new TextCollectionItem { TextCollectionId = collection.Id, TrainingTextId = text.Id });
            await Db.SaveChangesAsync();
            return text;
        }

        public async Task<Challenge> SeedActiveChallengeAsync()
        {
            var other = CreateProfile("challenger", "Charlie Challenge", "33333333-3333-3333-3333-333333333333");
            var text = new TrainingText
            {
                OwnerProfileId = other.Id,
                Title = "Challenge",
                SourceKey = "challenge-privacy",
                Body = "Text",
                Visibility = TrainingTextVisibility.Organization,
                CharacterCount = 4
            };
            var challenge = new Challenge
            {
                CreatorProfileId = other.Id,
                TrainingTextId = text.Id,
                Title = "Challenge",
                Status = ChallengeStatus.Open,
                ExpiresAt = Time.GetUtcNow().AddDays(1)
            };
            Db.UserProfiles.Add(other);
            Db.TrainingTexts.Add(text);
            Db.Challenges.Add(challenge);
            Db.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserProfileId = Profile.Id, Status = ParticipantStatus.Invited });
            Db.ChallengeParticipants.Add(new ChallengeParticipant { ChallengeId = challenge.Id, UserProfileId = other.Id, Status = ParticipantStatus.Joined });
            await Db.SaveChangesAsync();
            return challenge;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await outboxServices.DisposeAsync();
            await connection.DisposeAsync();
        }

        public static UserProfile CreateProfile(string account, string displayName, string objectGuid) => new()
        {
            DirectoryObjectGuid = objectGuid,
            DirectorySid = $"S-1-5-21-{account}",
            SamAccountName = account,
            UserPrincipalName = $"{account}@example.local",
            DisplayName = displayName,
            Email = $"{account}@example.local",
            Department = "IT",
            Title = "Training"
        };

        private static LiveRoomManager CreateLiveRooms(
            TimeProvider timeProvider,
            LiveOptions liveOptions,
            ILiveRoomCompletionSink completionSink) => new(
            Options.Create(liveOptions),
            timeProvider,
            new TypingEngine(timeProvider),
            NullLogger<LiveRoomManager>.Instance,
            completionSink);
    }

    private sealed class RecordingProfileHubConnectionRevoker : IProfileHubConnectionRevoker
    {
        public List<Guid> RevokedProfiles { get; } = [];
        public bool Fail { get; set; }

        public Task RevokeProfileAsync(
            Guid profileId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokedProfiles.Add(profileId);
            return Fail
                ? Task.FromException(new InvalidOperationException("Hub-Widerruf fehlgeschlagen."))
                : Task.CompletedTask;
        }
    }

    private sealed class LostLeaseProfileAccessGate : IProfileAccessGate
    {
        private readonly CancellationTokenSource lost = new();

        public bool Disposed { get; private set; }

        public ValueTask<ProfileAccessState> GetStateAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ProfileAccessState.Available);

        public ValueTask<IOperationLease> AcquireAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IOperationLease> AcquireManyAsync(IEnumerable<Guid> profileIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IOperationLease?> TryBeginOperationAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            var lease = new LostLease(lost.Token, () => Disposed = true);
            lost.Cancel();
            return ValueTask.FromResult<IOperationLease?>(lease);
        }

        public Task WaitForIdleAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask.WaitAsync(cancellationToken);

        public ValueTask CompleteOperationAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask MarkDeletedAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        private sealed class LostLease(CancellationToken leaseLost, Action dispose) : IOperationLease
        {
            public CancellationToken LeaseLost => leaseLost;

            public void ThrowIfLost() => LeaseLost.ThrowIfCancellationRequested();

            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class NoopCompletionWriter : ILiveRoomCompletionWriter
    {
        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingCompletionWriter : ILiveRoomCompletionWriter
    {
        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Persistenzfehler"));
    }
}
