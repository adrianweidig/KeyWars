using System.Data.Common;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class LiveRoomCompletionQueueTests
{
    [Fact]
    public async Task FlushPersistsRoomResultsAndRatingExactlyOnce()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var first = context.FirstProfileId;
        var second = context.SecondProfileId;
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, first, second);

        var firstReceipt = context.Queue.Enqueue(record);
        var duplicateReceipt = context.Queue.Enqueue(record);
        Assert.Equal(CompletionState.Pending, firstReceipt.State);
        Assert.Equal(firstReceipt, duplicateReceipt);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var room = await db.LiveRoomSummaries.SingleAsync();
        var participants = await db.LiveRoomParticipantSummaries.OrderBy(item => item.Placement).ToListAsync();
        var profiles = await db.UserProfiles.OrderBy(item => item.DisplayName).ToListAsync();

        Assert.Equal(record.Id, room.Id);
        Assert.Equal(record.IdempotencyKey, room.IdempotencyKey);
        Assert.Equal(record.TargetTextHash, room.TargetTextHash);
        Assert.StartsWith("sha256:", room.TargetTextHash, StringComparison.Ordinal);
        Assert.Equal(1, room.RoundNumber);
        Assert.Equal(2, participants.Count);
        Assert.Equal(1, profiles[0].RatedMatchCount);
        Assert.Equal(1, profiles[1].RatedMatchCount);
        Assert.True(profiles.Single(item => item.Id == first).ArenaRating > 1000);
        Assert.True(profiles.Single(item => item.Id == second).ArenaRating < 1000);
        var firstProfile = profiles.Single(item => item.Id == first);
        var secondProfile = profiles.Single(item => item.Id == second);
        var firstSummary = participants.Single(item => item.UserProfileId == first);
        var secondSummary = participants.Single(item => item.UserProfileId == second);
        Assert.Equal(1000, firstSummary.RatingBefore);
        Assert.Equal(firstProfile.ArenaRating, firstSummary.RatingAfter);
        Assert.Equal(firstProfile.ArenaRating - firstSummary.RatingBefore, firstSummary.RatingDelta);
        Assert.Equal(1000, secondSummary.RatingBefore);
        Assert.Equal(secondProfile.ArenaRating, secondSummary.RatingAfter);
        Assert.Equal(secondProfile.ArenaRating - secondSummary.RatingBefore, secondSummary.RatingDelta);
        Assert.Equal(2, await db.RewardLedgerEntries.CountAsync(item => item.Source == "arena"));
        Assert.Equal(2, await db.GamificationEvents.CountAsync(item => item.Type == GamificationEventType.ArenaResult));
        Assert.Contains(await db.Missions.ToListAsync(), item => item.UserProfileId == first && item.Key == "daily-arena-or-team" && item.Completed);
        Assert.Equal(CompletionState.Persisted, context.Queue.GetStatus(roomId).State);
        Assert.Equal(0, context.Queue.PendingCount);
    }

    [Fact]
    public async Task SqliteWriterIsIdempotentAcrossFreshScopes()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var record = CreateRecord(Guid.CreateVersion7(), context.ProfileIds);

        await context.Writer.PersistAsync(record, CancellationToken.None);
        var firstState = await ReadWriterStateAsync(context.Services);
        await context.Writer.PersistAsync(record, CancellationToken.None);
        var secondState = await ReadWriterStateAsync(context.Services);

        Assert.Equal(1, firstState.RoomCount);
        Assert.Equal(4, firstState.ParticipantCount);
        Assert.Equal(4, firstState.Ledgers.Count(item => item.Source == "arena"));
        Assert.All(firstState.Profiles, profile => Assert.Equal(1, profile.RatedMatchCount));
        var profilesById = firstState.Profiles.ToDictionary(profile => profile.Id);
        for (var index = 0; index < context.ProfileIds.Count; index++)
        {
            var profileId = context.ProfileIds[index];
            var profile = profilesById[profileId];
            Assert.Equal(175 - index, profile.ExperiencePoints);
            Assert.Equal(8, profile.SeasonPoints);
            Assert.Equal(
                115 - index,
                Assert.Single(firstState.Ledgers, item =>
                    item.UserProfileId == profileId && item.Source == "arena").Xp);
            Assert.Equal(
                profile.ExperiencePoints,
                firstState.Ledgers.Where(item => item.UserProfileId == profileId).Sum(item => item.Xp));
        }

        Assert.Equal(firstState.Profiles, secondState.Profiles);
        Assert.Equal(firstState.Missions, secondState.Missions);
        Assert.Equal(firstState.Ledgers, secondState.Ledgers);
        Assert.Equal(firstState.Achievements, secondState.Achievements);
        Assert.Equal(firstState.Events, secondState.Events);
    }

    [Fact]
    public async Task IneligibleClassicFinishIsRatedAsForfeitWithoutRewardsSeasonOrPersonalBest()
    {
        await using var context = await WriterTestContext.CreateAsync(2);
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, context.ProfileIds);
        var ineligibleProfileId = context.ProfileIds[0];
        record = record with
        {
            Participants = record.Participants
                .Select(item => item.UserProfileId == ineligibleProfileId
                    ? item with { CompetitionEligible = false }
                    : item)
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);
        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var ineligibleSummary = await db.LiveRoomParticipantSummaries
            .SingleAsync(item => item.LiveRoomSummaryId == roomId && item.UserProfileId == ineligibleProfileId);
        var ineligibleProfile = await db.UserProfiles.SingleAsync(item => item.Id == ineligibleProfileId);
        var eligibleProfile = await db.UserProfiles.SingleAsync(item => item.Id != ineligibleProfileId);
        Assert.Equal(ParticipantStatus.Finished, ineligibleSummary.Status);
        Assert.False(ineligibleSummary.CompetitionEligible);
        Assert.True(ineligibleSummary.RatingDelta < 0);
        Assert.True(ineligibleProfile.ArenaRating < 1000);
        Assert.Equal(1, ineligibleProfile.RatedMatchCount);
        Assert.True(eligibleProfile.ArenaRating > 1000);
        Assert.Equal(1, eligibleProfile.RatedMatchCount);
        Assert.Equal(0, ineligibleProfile.ExperiencePoints);
        Assert.Equal(0, ineligibleProfile.SeasonPoints);
        Assert.False(await db.RewardLedgerEntries.AnyAsync(item => item.UserProfileId == ineligibleProfileId));
        Assert.False(await db.SeasonScores.AnyAsync(item => item.UserProfileId == ineligibleProfileId));
        Assert.Null(await new ArenaPersonalBestService(db).GetConfirmedAsync(roomId, ineligibleProfileId));
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync(item => item.Id == roomId));
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync(item => item.LiveRoomSummaryId == roomId));
    }

    [Fact]
    public async Task ClassicDnfForfeitsShareLastRatingPlacementRegardlessOfGiveUpDuration()
    {
        await using var context = await WriterTestContext.CreateAsync(3);
        var roomId = Guid.CreateVersion7();
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Participants = baseRecord.Participants
                .Select((participant, index) => index == 0
                    ? participant with { Placement = 1 }
                    : participant with
                    {
                        Status = ParticipantStatus.Dnf,
                        Placement = index + 1,
                        DurationMilliseconds = index == 1 ? 1_000 : 10_000,
                        CorrectCharacters = index == 1 ? 10 : 100,
                        Wpm = 0,
                        Accuracy = 0,
                        CompetitionEligible = true
                    })
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(participant => participant.LiveRoomSummaryId == roomId)
            .ToDictionaryAsync(participant => participant.UserProfileId);
        var firstDnf = summaries[context.ProfileIds[1]];
        var secondDnf = summaries[context.ProfileIds[2]];

        Assert.Equal(2, firstDnf.Placement);
        Assert.Equal(3, secondDnf.Placement);
        Assert.Equal(firstDnf.RatingDelta, secondDnf.RatingDelta);
        Assert.True(firstDnf.RatingDelta < 0);
        Assert.True(summaries[context.ProfileIds[0]].RatingDelta > 0);
        Assert.Equal(0, summaries.Values.Sum(participant => participant.RatingDelta));
    }

    [Fact]
    public async Task ClassicDnfAndIneligibleFinishShareLastRatingPlacement()
    {
        await using var context = await WriterTestContext.CreateAsync(3);
        var roomId = Guid.CreateVersion7();
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Participants =
            [
                baseRecord.Participants[0] with { Placement = 2 },
                baseRecord.Participants[1] with
                {
                    Status = ParticipantStatus.Dnf,
                    Placement = 3,
                    DurationMilliseconds = 1_000,
                    CorrectCharacters = 5,
                    Wpm = 0,
                    Accuracy = 0,
                    CompetitionEligible = true
                },
                baseRecord.Participants[2] with
                {
                    Status = ParticipantStatus.Finished,
                    Placement = 1,
                    DurationMilliseconds = 100,
                    CompetitionEligible = false
                }
            ]
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(participant => participant.LiveRoomSummaryId == roomId)
            .ToDictionaryAsync(participant => participant.UserProfileId);

        Assert.Equal(summaries[context.ProfileIds[1]].RatingDelta, summaries[context.ProfileIds[2]].RatingDelta);
        Assert.True(summaries[context.ProfileIds[1]].RatingDelta < 0);
        Assert.True(summaries[context.ProfileIds[0]].RatingDelta > 0);
        Assert.False(summaries[context.ProfileIds[2]].CompetitionEligible);
        Assert.Equal(0, summaries.Values.Sum(participant => participant.RatingDelta));
    }

    [Fact]
    public async Task EligibleDnfIsRatedAsLossWithoutReceivingRewardsSeasonOrPersonalBest()
    {
        await using var context = await WriterTestContext.CreateAsync(2);
        var roomId = Guid.CreateVersion7();
        var winnerProfileId = context.ProfileIds[0];
        var dnfProfileId = context.ProfileIds[1];
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Participants = baseRecord.Participants
                .Select(participant => participant.UserProfileId == dnfProfileId
                    ? participant with
                    {
                        Status = ParticipantStatus.Dnf,
                        Placement = 2,
                        CorrectCharacters = 25,
                        Wpm = 0,
                        Accuracy = 0,
                        CompetitionEligible = true
                    }
                    : participant)
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var winner = await db.UserProfiles.SingleAsync(profile => profile.Id == winnerProfileId);
        var dnf = await db.UserProfiles.SingleAsync(profile => profile.Id == dnfProfileId);
        var dnfSummary = await db.LiveRoomParticipantSummaries
            .SingleAsync(participant => participant.LiveRoomSummaryId == roomId && participant.UserProfileId == dnfProfileId);

        Assert.Equal(1, winner.RatedMatchCount);
        Assert.True(winner.ArenaRating > 1000);
        Assert.Equal(1, dnf.RatedMatchCount);
        Assert.True(dnf.ArenaRating < 1000);
        Assert.Equal(ParticipantStatus.Dnf, dnfSummary.Status);
        Assert.False(dnfSummary.CompetitionEligible);
        Assert.True(dnfSummary.RatingDelta < 0);
        Assert.Equal(0, dnf.ExperiencePoints);
        Assert.Equal(0, dnf.SeasonPoints);
        Assert.False(await db.RewardLedgerEntries.AnyAsync(entry => entry.UserProfileId == dnfProfileId));
        Assert.False(await db.SeasonScores.AnyAsync(score => score.UserProfileId == dnfProfileId));
        Assert.Null(await new ArenaPersonalBestService(db).GetConfirmedAsync(roomId, dnfProfileId));
        Assert.True(await db.RewardLedgerEntries.AnyAsync(entry => entry.UserProfileId == winnerProfileId));
    }

    [Fact]
    public async Task AllDnfRoomIsUnratedRegardlessOfGiveUpOrder()
    {
        await using var context = await WriterTestContext.CreateAsync(2);
        var roomId = Guid.CreateVersion7();
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Participants = baseRecord.Participants
                .Select((participant, index) => participant with
                {
                    Status = ParticipantStatus.Dnf,
                    Placement = index + 1,
                    DurationMilliseconds = index == 0 ? 1_000 : 10_000,
                    CorrectCharacters = 0,
                    Wpm = 0,
                    Accuracy = 0,
                    CompetitionEligible = true
                })
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(participant => participant.LiveRoomSummaryId == roomId)
            .ToListAsync();

        Assert.All(profiles, profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
            Assert.Equal(0, profile.ExperiencePoints);
            Assert.Equal(0, profile.SeasonPoints);
        });
        Assert.All(summaries, summary =>
        {
            Assert.Equal(ParticipantStatus.Dnf, summary.Status);
            Assert.False(summary.CompetitionEligible);
            Assert.Equal(0, summary.RatingDelta);
        });
        Assert.Empty(await db.RewardLedgerEntries.ToListAsync());
        Assert.Empty(await db.SeasonScores.ToListAsync());
    }

    [Fact]
    public async Task RoomLevelRatingFenceKeepsEligibleRewardsWithoutRating()
    {
        await using var context = await WriterTestContext.CreateAsync(2);
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, context.ProfileIds) with { RatingEligible = false };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(participant => participant.LiveRoomSummaryId == roomId)
            .ToListAsync();

        Assert.All(profiles, profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
            Assert.True(profile.ExperiencePoints > 0);
            Assert.True(profile.SeasonPoints > 0);
        });
        Assert.All(summaries, summary =>
        {
            Assert.True(summary.CompetitionEligible);
            Assert.Equal(0, summary.RatingDelta);
        });
        Assert.Equal(2, await db.RewardLedgerEntries.CountAsync(entry => entry.Source == "arena"));
        Assert.Equal(2, await db.SeasonScores.CountAsync());
    }

    [Fact]
    public async Task NonCompetitionTargetPersistsVisibleResultWithoutRatingRewardsSeasonOrPersonalBest()
    {
        await using var context = await WriterTestContext.CreateAsync(2);
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, context.ProfileIds) with
        {
            RatingEligible = false,
            TargetCompetitionEligible = false
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(participant => participant.LiveRoomSummaryId == roomId)
            .ToListAsync();

        Assert.Equal(2, summaries.Count);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(ParticipantStatus.Finished, summary.Status);
            Assert.False(summary.CompetitionEligible);
            Assert.Equal(0, summary.RatingDelta);
        });
        Assert.All(profiles, profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
            Assert.Equal(0, profile.ExperiencePoints);
            Assert.Equal(0, profile.SeasonPoints);
        });
        Assert.Empty(await db.RewardLedgerEntries.ToListAsync());
        Assert.Empty(await db.SeasonScores.ToListAsync());
        foreach (var profileId in context.ProfileIds)
        {
            Assert.Null(await new ArenaPersonalBestService(db).GetConfirmedAsync(roomId, profileId));
        }
    }

    [Fact]
    public async Task IneligibleParticipantMakesWholeBestOfSeriesUnratedButKeepsEligibleRewards()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var roomId = Guid.CreateVersion7();
        var ineligibleProfileIds = new HashSet<Guid> { context.ProfileIds[0], context.ProfileIds[3] };
        var placements = new Dictionary<Guid, int>
        {
            [context.ProfileIds[0]] = 3,
            [context.ProfileIds[1]] = 2,
            [context.ProfileIds[2]] = 1,
            [context.ProfileIds[3]] = 4
        };
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Mode = LiveRoomMode.BestOf,
            RoundNumber = 3,
            RoundCount = 3,
            IdempotencyKey = $"{roomId:N}:3:2",
            Participants = baseRecord.Participants
                .Select(item => item with
                {
                    Placement = placements[item.UserProfileId],
                    CompetitionEligible = !ineligibleProfileIds.Contains(item.UserProfileId)
                })
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);
        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(item => item.LiveRoomSummaryId == roomId)
            .ToListAsync();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.Equal(4, summaries.Count);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(ParticipantStatus.Finished, summary.Status);
            Assert.Equal(0, summary.RatingDelta);
            Assert.Equal(summary.RatingBefore, summary.RatingAfter);
        });
        Assert.All(profiles, profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
        });

        var personalBestService = new ArenaPersonalBestService(db);
        foreach (var profileId in ineligibleProfileIds)
        {
            Assert.False(summaries.Single(item => item.UserProfileId == profileId).CompetitionEligible);
            Assert.Equal(0, profiles.Single(item => item.Id == profileId).ExperiencePoints);
            Assert.Equal(0, profiles.Single(item => item.Id == profileId).SeasonPoints);
            Assert.False(await db.RewardLedgerEntries.AnyAsync(item =>
                item.UserProfileId == profileId && item.Source == "arena"));
            Assert.Null(await personalBestService.GetConfirmedAsync(roomId, profileId));
        }

        foreach (var profileId in context.ProfileIds.Except(ineligibleProfileIds))
        {
            Assert.True(summaries.Single(item => item.UserProfileId == profileId).CompetitionEligible);
            Assert.True(profiles.Single(item => item.Id == profileId).ExperiencePoints > 0);
            Assert.True(profiles.Single(item => item.Id == profileId).SeasonPoints > 0);
            Assert.True(await db.RewardLedgerEntries.AnyAsync(item =>
                item.UserProfileId == profileId && item.Source == "arena"));
            Assert.NotNull(await personalBestService.GetConfirmedAsync(roomId, profileId));
        }

        Assert.Equal(2, await db.RewardLedgerEntries.CountAsync(item => item.Source == "arena"));
        Assert.Equal(2, await db.SeasonScores.CountAsync());
    }

    [Fact]
    public async Task EligibleBestOfSeriesRemainsRated()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, context.ProfileIds) with
        {
            Mode = LiveRoomMode.BestOf,
            RoundNumber = 3,
            RoundCount = 3,
            IdempotencyKey = $"{roomId:N}:3:2"
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(item => item.LiveRoomSummaryId == roomId)
            .ToListAsync();

        Assert.All(profiles, profile => Assert.Equal(1, profile.RatedMatchCount));
        Assert.Contains(profiles, profile => profile.ArenaRating > 1000);
        Assert.Contains(profiles, profile => profile.ArenaRating < 1000);
        Assert.All(summaries, summary => Assert.True(summary.CompetitionEligible));
        Assert.Contains(summaries, summary => summary.RatingDelta != 0);
        Assert.Equal(4, await db.RewardLedgerEntries.CountAsync(item => item.Source == "arena"));
    }

    [Fact]
    public async Task IneligibleParticipantMakesSingleRoundTeamMatchUnratedButKeepsEligibleRewards()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var roomId = Guid.CreateVersion7();
        var ineligibleProfileId = context.ProfileIds[0];
        var placements = new[] { 1, 4, 2, 3 };
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Mode = LiveRoomMode.Team,
            RoundCount = 1,
            Participants = baseRecord.Participants
                .Select((participant, index) => participant with
                {
                    TeamNumber = index < 2 ? 1 : 2,
                    Placement = placements[index],
                    CompetitionEligible = participant.UserProfileId != ineligibleProfileId
                })
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(item => item.LiveRoomSummaryId == roomId)
            .ToListAsync();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.Equal(4, summaries.Count);
        Assert.Equal(2, summaries.Count(item => item.TeamNumber == 1));
        Assert.Equal(2, summaries.Count(item => item.TeamNumber == 2));
        Assert.All(summaries, summary =>
        {
            Assert.Equal(ParticipantStatus.Finished, summary.Status);
            Assert.Equal(0, summary.RatingDelta);
            Assert.Equal(summary.RatingBefore, summary.RatingAfter);
        });
        Assert.All(profiles, profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
        });

        var personalBestService = new ArenaPersonalBestService(db);
        Assert.False(summaries.Single(item => item.UserProfileId == ineligibleProfileId).CompetitionEligible);
        Assert.Equal(0, profiles.Single(item => item.Id == ineligibleProfileId).ExperiencePoints);
        Assert.False(await db.RewardLedgerEntries.AnyAsync(item =>
            item.UserProfileId == ineligibleProfileId && item.Source == "arena"));
        Assert.Null(await personalBestService.GetConfirmedAsync(roomId, ineligibleProfileId));

        foreach (var profileId in context.ProfileIds.Where(item => item != ineligibleProfileId))
        {
            Assert.True(summaries.Single(item => item.UserProfileId == profileId).CompetitionEligible);
            Assert.True(profiles.Single(item => item.Id == profileId).ExperiencePoints > 0);
            Assert.True(await db.RewardLedgerEntries.AnyAsync(item =>
                item.UserProfileId == profileId && item.Source == "arena"));
            Assert.NotNull(await personalBestService.GetConfirmedAsync(roomId, profileId));
        }

        Assert.Equal(3, await db.RewardLedgerEntries.CountAsync(item => item.Source == "arena"));
        Assert.Equal(3, await db.SeasonScores.CountAsync());
    }

    [Fact]
    public async Task TeamRatingSkipsTeammatePairsAndUsesOnlyEqualOpponentField()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        await using (var setupScope = context.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var setupProfiles = await setupDb.UserProfiles.ToDictionaryAsync(profile => profile.Id);
            setupProfiles[context.ProfileIds[0]].ArenaRating = 2000;
            await setupDb.SaveChangesAsync();
        }

        var roomId = Guid.CreateVersion7();
        var baseRecord = CreateRecord(roomId, context.ProfileIds);
        var record = baseRecord with
        {
            Mode = LiveRoomMode.Team,
            Participants = baseRecord.Participants
                .Select((participant, index) => participant with
                {
                    TeamNumber = index < 2 ? 1 : 2,
                    Placement = index < 2 ? 1 : 2
                })
                .ToArray()
        };

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToDictionaryAsync(profile => profile.Id);
        var summaries = await db.LiveRoomParticipantSummaries
            .Where(participant => participant.LiveRoomSummaryId == roomId)
            .ToDictionaryAsync(participant => participant.UserProfileId);

        Assert.Equal(2000, profiles[context.ProfileIds[0]].ArenaRating);
        Assert.Equal(1012, profiles[context.ProfileIds[1]].ArenaRating);
        Assert.Equal(994, profiles[context.ProfileIds[2]].ArenaRating);
        Assert.Equal(994, profiles[context.ProfileIds[3]].ArenaRating);
        Assert.Equal(0, summaries.Values.Sum(participant => participant.RatingDelta));
        Assert.All(profiles.Values, profile => Assert.Equal(1, profile.RatedMatchCount));
    }

    [Fact]
    public async Task DelayedArenaPersistenceAwardsTheSeasonOfTheFinishedRoom()
    {
        await using var context = await WriterTestContext.CreateAsync(
            2,
            DateTimeOffset.Parse("2026-07-01T00:05:00Z"));
        var record = CreateRecord(
            Guid.CreateVersion7(),
            context.ProfileIds,
            finishedAt: DateTimeOffset.Parse("2026-06-30T23:59:00Z"));
        await using (var setupScope = context.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profiles = await setupDb.UserProfiles.ToListAsync();
            foreach (var profile in profiles)
            {
                profile.SeasonPoints = 3;
            }

            await setupDb.SaveChangesAsync();
        }

        await context.Writer.PersistAsync(record, CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var season = await db.Seasons.SingleAsync();
        var rewards = await db.RewardLedgerEntries
            .Where(item => item.Source == "arena")
            .ToListAsync();
        Assert.Equal("2026-06", season.Key);
        Assert.All(rewards, reward =>
        {
            Assert.Equal(season.Id, reward.SeasonId);
            Assert.Equal(record.FinishedAt, reward.AwardedAt);
        });
        Assert.Equal(2, await db.SeasonScores.CountAsync(item => item.SeasonId == season.Id));
        Assert.All(await db.UserProfiles.ToListAsync(), profile => Assert.Equal(3, profile.SeasonPoints));
    }

    [Fact]
    public async Task ArenaPersistenceUsesParticipantIndependentReadCountAndOneSave()
    {
        var twoParticipantReads = await PersistAndMeasureAsync(2);
        var sixtyFourParticipantReads = await PersistAndMeasureAsync(64);

        Assert.Equal(twoParticipantReads, sixtyFourParticipantReads);
        // Enthält genau einen gebündelten SeasonScores-Read für alle Teilnehmer.
        Assert.Equal(17, sixtyFourParticipantReads);
    }

    [Fact]
    public async Task SqliteWriterRollsBackEveryEffectWhenFinalSaveFails()
    {
        await using var context = await WriterTestContext.CreateAsync(4);
        var record = CreateRecord(Guid.CreateVersion7(), context.ProfileIds);
        context.SaveProbe.ArmFailureAfterSave();

        await Assert.ThrowsAsync<InjectedSaveFailureException>(() =>
            context.Writer.PersistAsync(record, CancellationToken.None));

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            Assert.Empty(await db.LiveRoomSummaries.ToListAsync());
            Assert.Empty(await db.LiveRoomParticipantSummaries.ToListAsync());
            Assert.Empty(await db.RewardLedgerEntries.ToListAsync());
            Assert.Empty(await db.Missions.ToListAsync());
            Assert.Empty(await db.Achievements.ToListAsync());
            Assert.Empty(await db.GamificationEvents.ToListAsync());
            Assert.All(await db.UserProfiles.ToListAsync(), profile =>
            {
                Assert.Equal(1000, profile.ArenaRating);
                Assert.Equal(0, profile.RatedMatchCount);
                Assert.Equal(0, profile.SeasonPoints);
                Assert.Equal(0, profile.ExperiencePoints);
                Assert.Equal(1, profile.Level);
                Assert.Equal(0, profile.CurrentStreakDays);
                Assert.Null(profile.LastActivityDate);
            });
        }

        context.SaveProbe.Reset();
        await context.Writer.PersistAsync(record, CancellationToken.None);
        Assert.Equal(1, context.SaveProbe.SavingChangesCount);
        var persisted = await ReadWriterStateAsync(context.Services);
        Assert.Equal(1, persisted.RoomCount);
        Assert.Equal(4, persisted.ParticipantCount);
        Assert.Equal(4, persisted.Ledgers.Count(item => item.Source == "arena"));
        Assert.All(persisted.Profiles, profile => Assert.Equal(1, profile.RatedMatchCount));
    }

    private static async Task<int> PersistAndMeasureAsync(int participantCount)
    {
        await using var context = await WriterTestContext.CreateAsync(participantCount);
        var record = CreateRecord(Guid.CreateVersion7(), context.ProfileIds);
        context.QueryCounter.Reset();
        context.SaveProbe.Reset();

        await context.Writer.PersistAsync(record, CancellationToken.None);

        Assert.Equal(1, context.SaveProbe.SavingChangesCount);
        return context.QueryCounter.LinqReadCount;
    }

    private static async Task<WriterPersistenceState> ReadWriterStateAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = (await db.UserProfiles.AsNoTracking().OrderBy(item => item.Id).ToListAsync())
            .Select(item => new ProfilePersistenceState(
                item.Id,
                item.ArenaRating,
                item.RatedMatchCount,
                item.SeasonPoints,
                item.ExperiencePoints,
                item.Level,
                item.CurrentStreakDays,
                item.LastActivityDate))
            .ToArray();
        var missions = (await db.Missions
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.MissionDate)
                .ThenBy(item => item.Key)
                .ToListAsync())
            .Select(item => new MissionPersistenceState(
                item.UserProfileId,
                item.MissionDate,
                item.Key,
                item.CurrentValue,
                item.Completed))
            .ToArray();
        var ledgers = (await db.RewardLedgerEntries
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.Source)
                .ThenBy(item => item.SourceId)
                .ToListAsync())
            .Select(item => new LedgerPersistenceState(
                item.UserProfileId,
                item.Source,
                item.SourceId,
                item.Xp))
            .ToArray();
        var achievements = (await db.Achievements
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.Key)
                .ToListAsync())
            .Select(item => new AchievementPersistenceState(item.UserProfileId, item.Key))
            .ToArray();
        var events = (await db.GamificationEvents
                .AsNoTracking()
                .OrderBy(item => item.UserProfileId)
                .ThenBy(item => item.Source)
                .ThenBy(item => item.SourceId)
                .ThenBy(item => item.EventKey)
                .ToListAsync())
            .Select(item => new EventPersistenceState(
                item.UserProfileId,
                item.Source,
                item.SourceId,
                item.EventKey,
                item.Type,
                item.XpDelta))
            .ToArray();

        return new WriterPersistenceState(
            await db.LiveRoomSummaries.CountAsync(),
            await db.LiveRoomParticipantSummaries.CountAsync(),
            profiles,
            missions,
            ledgers,
            achievements,
            events);
    }

    [Fact]
    public async Task StopAsyncFlushesQueuedCompletions()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);

        context.Queue.Enqueue(record);
        await context.Queue.StopAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync());
        Assert.Equal(0, context.Queue.PendingCount);
    }

    [Fact]
    public async Task ServerAbortPersistsWithoutRatingChange()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId, abortedByServer: true);

        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var room = await db.LiveRoomSummaries.SingleAsync();
        var participants = await db.LiveRoomParticipantSummaries.ToListAsync();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.True(room.AbortedByServer);
        Assert.All(participants, participant => Assert.Equal(0, participant.RatingDelta));
        Assert.All(participants, participant => Assert.Equal(participant.RatingBefore, participant.RatingAfter));
        Assert.All(profiles, profile => Assert.Equal(1000, profile.ArenaRating));
        Assert.All(profiles, profile => Assert.Equal(0, profile.RatedMatchCount));
        Assert.Empty(await db.GamificationEvents.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRoomCompletionsUpdateRatingsOncePerRoom()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var firstRecord = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);
        var secondRecord = CreateRecord(Guid.CreateVersion7(), context.SecondProfileId, context.FirstProfileId);

        await Task.WhenAll(
            Task.Run(() => context.Queue.Enqueue(firstRecord)),
            Task.Run(() => context.Queue.Enqueue(secondRecord)));
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.Equal(2, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(4, await db.LiveRoomParticipantSummaries.CountAsync());
        Assert.All(profiles, profile => Assert.Equal(2, profile.RatedMatchCount));
    }

    [Fact]
    public async Task FlushRetriesAfterTransientSqliteFailure()
    {
        await using var context = await CompletionTestContext.CreateAsync(transientFailureOnFirstWrite: true);
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);

        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Equal(2, context.WriterAttempts);
        Assert.Equal(1, context.Queue.RetryAttempts);
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync());
    }

    [Fact]
    public async Task PermanentFailureRemainsFailedWithoutRatingOrRewards()
    {
        await using var context = await CompletionTestContext.CreateAsync(permanentFailure: true);
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, context.FirstProfileId, context.SecondProfileId);

        var receipt = context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);
        await context.Queue.FlushAsync(CancellationToken.None);
        var drain = await context.Queue.DrainProfileAsync(context.FirstProfileId);

        Assert.Equal(CompletionState.Pending, receipt.State);
        Assert.Equal(CompletionState.Failed, context.Queue.GetStatus(roomId).State);
        Assert.Equal(CompletionDrainStatus.Failed, drain.Status);
        Assert.Equal(1, context.Queue.FailedRecordCount);
        Assert.Equal(1, context.Queue.GetMetrics().FailedCompletions);
        Assert.Equal(3, context.WriterAttempts);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Empty(await db.LiveRoomSummaries.ToListAsync());
        Assert.Empty(await db.LiveRoomParticipantSummaries.ToListAsync());
        Assert.Empty(await db.RewardLedgerEntries.ToListAsync());
        Assert.All(await db.UserProfiles.ToListAsync(), profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
        });
    }

    [Fact]
    public async Task DrainTimesOutWhileRelatedCompletionIsPending()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);
        context.Queue.Enqueue(record);

        var result = await context.Queue.DrainProfileAsync(
            context.FirstProfileId,
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(CompletionDrainStatus.Timeout, result.Status);
        Assert.Equal(1, result.PendingJobs);
    }

    [Fact]
    public async Task DrainWaitsForRuntimePersistenceAndReturnsSuccess()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var queue = new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions { MaxConcurrentRooms = 1, CompletionQueueCapacity = 1 }),
            new NoopCompletionWriter(),
            context.Services.GetRequiredService<LiveRoomCompletionOutbox>(),
            TimeProvider.System,
            NullLogger<LiveRoomCompletionQueue>.Instance);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, first, second);
        await queue.StartAsync(CancellationToken.None);
        queue.Enqueue(record);

        var result = await queue.DrainProfileAsync(
            first,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(CompletionDrainStatus.Success, result.Status);
        Assert.Equal(CompletionState.Persisted, queue.GetStatus(roomId).State);
        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FullQueueReturnsFailedReceiptAndDoesNotReportPending()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var queue = new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions { MaxConcurrentRooms = 1, CompletionQueueCapacity = 1 }),
            new NoopCompletionWriter(),
            context.Services.GetRequiredService<LiveRoomCompletionOutbox>(),
            TimeProvider.System,
            NullLogger<LiveRoomCompletionQueue>.Instance);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var firstRecord = CreateRecord(Guid.CreateVersion7(), first, second);
        var rejectedRecord = CreateRecord(Guid.CreateVersion7(), first, second);

        var pending = queue.Enqueue(firstRecord);
        var rejected = queue.Enqueue(rejectedRecord);

        Assert.Equal(CompletionState.Pending, pending.State);
        Assert.Equal(CompletionState.Failed, rejected.State);
        Assert.Equal(CompletionState.Failed, queue.GetStatus(rejectedRecord.Id).State);
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.FailedRecordCount);
        Assert.False(queue.CanAcceptNewRoom(0));
    }

    [Fact]
    public void QueueRejectsCapacityBelowMaximumConcurrentRooms()
    {
        using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<LiveRoomCompletionOutbox>()
            .BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions { MaxConcurrentRooms = 2, CompletionQueueCapacity = 1 }),
            new NoopCompletionWriter(),
            services.GetRequiredService<LiveRoomCompletionOutbox>(),
            TimeProvider.System,
            NullLogger<LiveRoomCompletionQueue>.Instance));

        Assert.Contains("mindestens MaxConcurrentRooms", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueRejectsInvalidProfileDrainTimeout()
    {
        using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<LiveRoomCompletionOutbox>()
            .BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => new LiveRoomCompletionQueue(
            Options.Create(new LiveOptions
            {
                MaxConcurrentRooms = 1,
                CompletionQueueCapacity = 1,
                CompletionDrainTimeoutSeconds = 0
            }),
            new NoopCompletionWriter(),
            services.GetRequiredService<LiveRoomCompletionOutbox>(),
            TimeProvider.System,
            NullLogger<LiveRoomCompletionQueue>.Instance));

        Assert.Contains("CompletionDrainTimeoutSeconds", exception.Message, StringComparison.Ordinal);
    }

    private static CompletedRoomRecord CreateRecord(Guid roomId, Guid first, Guid second, bool abortedByServer = false)
        => CreateRecord(roomId, [first, second], abortedByServer);

    private static CompletedRoomRecord CreateRecord(
        Guid roomId,
        IReadOnlyList<Guid> profileIds,
        bool abortedByServer = false,
        DateTimeOffset? finishedAt = null)
    {
        var createdAt = DateTimeOffset.Parse("2026-06-18T12:00:00Z");
        return new CompletedRoomRecord(
            roomId,
            1,
            2,
            $"{roomId:N}:1:2",
            profileIds[0],
            "ABC123",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            createdAt,
            createdAt.AddSeconds(3),
            finishedAt ?? createdAt.AddSeconds(35),
            profileIds
                .Select((profileId, index) => new CompletedParticipantRecord(
                    profileId,
                    abortedByServer ? ParticipantStatus.AbortedByServer : ParticipantStatus.Finished,
                    abortedByServer ? null : index + 1,
                    32_000 + (index * 100),
                    Math.Max(20, 80 - index),
                    100,
                    CorrectCharacters: 200,
                    CompetitionEligible: !abortedByServer))
                .ToList(),
            TextHash.Compute("Ein stabiler Arena-Zieltext für persistierte Bestwerte."),
            RatingEligible: true,
            TargetCompetitionEligible: true);
    }

    private sealed class WriterTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private WriterTestContext(
            SqliteConnection connection,
            ServiceProvider services,
            RelationalLiveRoomCompletionWriter writer,
            IReadOnlyList<Guid> profileIds,
            SelectCommandCounter queryCounter,
            SaveChangesProbe saveProbe)
        {
            this.connection = connection;
            Services = services;
            Writer = writer;
            ProfileIds = profileIds;
            QueryCounter = queryCounter;
            SaveProbe = saveProbe;
        }

        public ServiceProvider Services { get; }
        public RelationalLiveRoomCompletionWriter Writer { get; }
        public IReadOnlyList<Guid> ProfileIds { get; }
        public SelectCommandCounter QueryCounter { get; }
        public SaveChangesProbe SaveProbe { get; }

        public static async Task<WriterTestContext> CreateAsync(
            int participantCount,
            DateTimeOffset? currentTime = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var queryCounter = new SelectCommandCounter();
            var saveProbe = new SaveChangesProbe();
            var services = new ServiceCollection();
            services.AddDbContext<KeyWarsDbContext>(options =>
                options
                    .UseSqlite(connection)
                    .AddInterceptors(queryCounter, saveProbe));
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(
                currentTime ?? DateTimeOffset.Parse("2026-06-18T12:02:00Z")));
            services.AddScoped<MotivationService>();
            services.AddSingleton<RelationalLiveRoomCompletionWriter>();
            var provider = services.BuildServiceProvider();

            var profileIds = new List<Guid>(participantCount);
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                await db.Database.EnsureCreatedAsync();
                var profiles = Enumerable.Range(1, participantCount)
                    .Select(index => new UserProfile
                    {
                        DisplayName = $"Arena Test {index}",
                        SamAccountName = $"arena-test-{index}",
                        DirectoryObjectGuid = Guid.NewGuid().ToString(),
                        DirectorySid = $"S-1-5-21-{index}"
                    })
                    .ToList();
                db.UserProfiles.AddRange(profiles);
                await db.SaveChangesAsync();
                profileIds.AddRange(profiles.Select(profile => profile.Id));
            }

            queryCounter.Reset();
            saveProbe.Reset();
            return new WriterTestContext(
                connection,
                provider,
                provider.GetRequiredService<RelationalLiveRoomCompletionWriter>(),
                profileIds,
                queryCounter,
                saveProbe);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        private int linqReadCount;

        public int LinqReadCount => Volatile.Read(ref linqReadCount);

        public void Reset() => Interlocked.Exchange(ref linqReadCount, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CountLinqRead(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountLinqRead(eventData);
            return ValueTask.FromResult(result);
        }

        private void CountLinqRead(CommandEventData eventData)
        {
            if (eventData.CommandSource == CommandSource.LinqQuery)
            {
                Interlocked.Increment(ref linqReadCount);
            }
        }
    }

    private sealed class SaveChangesProbe : SaveChangesInterceptor
    {
        private int failAfterSave;
        private int savingChangesCount;

        public int SavingChangesCount => Volatile.Read(ref savingChangesCount);

        public void Reset()
        {
            Interlocked.Exchange(ref failAfterSave, 0);
            Interlocked.Exchange(ref savingChangesCount, 0);
        }

        public void ArmFailureAfterSave() => Interlocked.Exchange(ref failAfterSave, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref savingChangesCount);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref failAfterSave, 0) == 1)
            {
                throw new InjectedSaveFailureException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedSaveFailureException : Exception;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record WriterPersistenceState(
        int RoomCount,
        int ParticipantCount,
        ProfilePersistenceState[] Profiles,
        MissionPersistenceState[] Missions,
        LedgerPersistenceState[] Ledgers,
        AchievementPersistenceState[] Achievements,
        EventPersistenceState[] Events);

    private sealed record ProfilePersistenceState(
        Guid Id,
        int ArenaRating,
        int RatedMatchCount,
        int SeasonPoints,
        int ExperiencePoints,
        int Level,
        int CurrentStreakDays,
        DateOnly? LastActivityDate);

    private sealed record MissionPersistenceState(
        Guid UserProfileId,
        DateOnly MissionDate,
        string Key,
        int CurrentValue,
        bool Completed);

    private sealed record LedgerPersistenceState(
        Guid UserProfileId,
        string Source,
        string SourceId,
        int Xp);

    private sealed record AchievementPersistenceState(Guid UserProfileId, string Key);

    private sealed record EventPersistenceState(
        Guid UserProfileId,
        string Source,
        string SourceId,
        string EventKey,
        GamificationEventType Type,
        int XpDelta);

    private sealed class CompletionTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CompletionTestContext(SqliteConnection connection, ServiceProvider services, LiveRoomCompletionQueue queue, Guid firstProfileId, Guid secondProfileId)
        {
            this.connection = connection;
            Services = services;
            Queue = queue;
            FirstProfileId = firstProfileId;
            SecondProfileId = secondProfileId;
        }

        public ServiceProvider Services { get; }
        public LiveRoomCompletionQueue Queue { get; }
        public Guid FirstProfileId { get; }
        public Guid SecondProfileId { get; }
        public int WriterAttempts => flakyWriter?.Attempts ?? 0;

        private FlakyCompletionWriter? flakyWriter;

        public static async Task<CompletionTestContext> CreateAsync(bool transientFailureOnFirstWrite = false, bool permanentFailure = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<KeyWarsDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton(Options.Create(new LiveOptions { MaxConcurrentRooms = 16, CompletionQueueCapacity = 16 }));
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddScoped<MotivationService>();
            services.AddSingleton<RelationalLiveRoomCompletionWriter>();
            services.AddSingleton<LiveRoomCompletionOutbox>();
            FlakyCompletionWriter? flakyWriter = null;
            if (transientFailureOnFirstWrite || permanentFailure)
            {
                services.AddSingleton<ILiveRoomCompletionWriter>(provider =>
                {
                    flakyWriter = new FlakyCompletionWriter(provider.GetRequiredService<RelationalLiveRoomCompletionWriter>(), permanentFailure);
                    return flakyWriter;
                });
            }
            else
            {
                services.AddSingleton<ILiveRoomCompletionWriter>(provider => provider.GetRequiredService<RelationalLiveRoomCompletionWriter>());
            }

            services.AddSingleton<LiveRoomCompletionQueue>();
            services.AddSingleton<ILiveRoomCompletionSink>(provider => provider.GetRequiredService<LiveRoomCompletionQueue>());
            services.AddSingleton<ILogger<LiveRoomCompletionQueue>>(NullLogger<LiveRoomCompletionQueue>.Instance);
            var provider = services.BuildServiceProvider();

            Guid first;
            Guid second;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                await db.Database.EnsureCreatedAsync();
                var firstProfile = new UserProfile
                {
                    DisplayName = "Anna Arena",
                    SamAccountName = "aarena",
                    DirectoryObjectGuid = Guid.NewGuid().ToString(),
                    DirectorySid = "S-1"
                };
                var secondProfile = new UserProfile
                {
                    DisplayName = "Bernd Arena",
                    SamAccountName = "barena",
                    DirectoryObjectGuid = Guid.NewGuid().ToString(),
                    DirectorySid = "S-2"
                };
                db.UserProfiles.AddRange(firstProfile, secondProfile);
                await db.SaveChangesAsync();
                first = firstProfile.Id;
                second = secondProfile.Id;
            }

            var queue = provider.GetRequiredService<LiveRoomCompletionQueue>();
            return new CompletionTestContext(connection, provider, queue, first, second)
            {
                flakyWriter = flakyWriter
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FlakyCompletionWriter(RelationalLiveRoomCompletionWriter inner, bool permanentFailure) : ILiveRoomCompletionWriter
    {
        public int Attempts { get; private set; }

        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
        {
            Attempts++;
            return permanentFailure || Attempts == 1
                ? Task.FromException(new SqliteException("database is locked", 5))
                : inner.PersistAsync(record, cancellationToken);
        }
    }

    private sealed class NoopCompletionWriter : ILiveRoomCompletionWriter
    {
        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
