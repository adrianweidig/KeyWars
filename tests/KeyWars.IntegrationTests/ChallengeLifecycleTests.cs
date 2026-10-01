using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class ChallengeLifecycleTests
{
    [Fact]
    public async Task CreateRequestIdMakesRetriesIdempotent()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var requestId = Guid.CreateVersion7();
        var request = new CreateChallengeRequest(
            "Einmalig",
            context.Text.Id,
            ChallengeMode.Classic,
            [context.Invitee.Id],
            1,
            7,
            requestId);

        var first = await context.Service.CreateAsync(context.Creator.Id, request);
        var retry = await context.Service.CreateAsync(context.Creator.Id, request);

        Assert.Equal(requestId, first.Id);
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, await context.Db.Challenges.CountAsync());
    }

    [Fact]
    public async Task CreateRequestRetryKeepsSnapshotAfterTrainingTextMutation()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var request = new CreateChallengeRequest(
            "",
            context.Text.Id,
            ChallengeMode.Classic,
            [context.Invitee.Id],
            1,
            7,
            Guid.CreateVersion7());
        var first = await context.Service.CreateAsync(context.Creator.Id, request);
        var originalSnapshot = first.TargetTextSnapshot;
        var originalHash = first.TargetTextHash;
        context.Text.Title = "Geänderter Titel";
        context.Text.Body = "Ein nachträglich geänderter Standardtext.";
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();

        var retry = await context.Service.CreateAsync(context.Creator.Id, request);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal("Challenge-Text", retry.Title);
        Assert.Equal(originalSnapshot, retry.TargetTextSnapshot);
        Assert.Equal(originalHash, retry.TargetTextHash);
        Assert.Equal(TextHash.Compute(originalSnapshot!), originalHash);
    }

    [Fact]
    public async Task BestOfUsesOneImmutableTargetSnapshotAcrossTextChangesAndRounds()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Fester Zieltext", context.Text.Id, ChallengeMode.BestOf, [context.Invitee.Id], 3, 7));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var snapshot = Assert.IsType<string>(challenge.TargetTextSnapshot);
        var hash = Assert.IsType<string>(challenge.TargetTextHash);

        await CompleteNextRoundAsync(context, challenge.Id, context.Creator.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Invitee.Id);
        context.Text.Body = "Dieser Deploy-Text darf die laufende Serie nicht verändern.";
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();

        for (var round = 1; round < challenge.RoundCount; round++)
        {
            foreach (var profileId in new[] { context.Creator.Id, context.Invitee.Id })
            {
                var session = await context.Service.StartAttemptAsync(challenge.Id, profileId, context.Attempts);
                Assert.Equal(snapshot, session.Text);
                Assert.Equal(hash, TextHash.Compute(session.Text));
                await context.Attempts.BeginAsync(profileId, new BeginAttemptRequest(session.Id, session.Nonce));
                context.Time.Advance(TimeSpan.FromSeconds(10));
                await context.Service.FinishAttemptAsync(
                    challenge.Id,
                    profileId,
                    new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
                    context.Attempts);
            }
        }

        Assert.Equal(ChallengeStatus.Finished, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.All(
            await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.ChallengeId == challenge.Id).ToListAsync(),
            binding => Assert.Equal(hash, binding.TextSnapshotHash));
        Assert.All(
            await context.Db.TypingAttempts.AsNoTracking().Where(item => item.TrainingTextId == context.Text.Id).ToListAsync(),
            attempt => Assert.Equal(hash, attempt.TextHash));
        Assert.All(
            await context.Db.UserProfiles.AsNoTracking().Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id).ToListAsync(),
            profile => Assert.Equal(1, profile.RatedMatchCount));
    }

    [Fact]
    public async Task RematchCopiesSourceSnapshotInsteadOfMutatedTrainingText()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var source = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Quelle", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));
        await context.Service.CancelAsync(source.Id, context.Creator.Id);
        context.Text.Body = "Geänderter Text nach Abschluss";
        context.Text.RatingEligible = false;
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();

        var rematch = await context.Service.CreateRematchAsync(source.Id, context.Creator.Id);
        var retry = await context.Service.CreateRematchAsync(source.Id, context.Creator.Id);
        var session = await context.Service.StartAttemptAsync(rematch.Id, context.Creator.Id, context.Attempts);

        Assert.Equal(rematch.Id, retry.Id);
        Assert.Equal(source.TargetTextSnapshot, rematch.TargetTextSnapshot);
        Assert.Equal(source.TargetTextHash, rematch.TargetTextHash);
        Assert.Equal(source.RatingEligible, rematch.RatingEligible);
        Assert.Equal(source.TargetTextSnapshot, session.Text);
    }

    [Fact]
    public async Task LegacyChallengeInitializesSnapshotOnlyBeforeFirstAttemptState()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Legacy", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));
        challenge.TargetTextSnapshot = null;
        challenge.TargetTextHash = null;
        context.Text.Body = "Legacy-Ziel zum ersten Start";
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();

        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Creator.Id, context.Attempts);

        Assert.Equal(TypingEngine.NormalizeText(context.Text.Body), session.Text);
        Assert.Equal(TextHash.Compute(session.Text), challenge.TargetTextHash);
        Assert.Equal(session.Text, challenge.TargetTextSnapshot);

        challenge.TargetTextSnapshot = null;
        challenge.TargetTextHash = null;
        await context.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.StartAttemptAsync(challenge.Id, context.Creator.Id, context.Attempts));
        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (error.Code, error.StatusCode));
    }

    [Fact]
    public async Task FinishRejectsTamperedChallengeSnapshotHashWithoutConsumingBinding()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Hashschutz", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Creator.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Creator.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        challenge.TargetTextHash = TextHash.Compute("Manipuliert");
        await context.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.FinishAttemptAsync(
                challenge.Id,
                context.Creator.Id,
                new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
                context.Attempts));

        Assert.Equal((ChallengeErrorCodes.InvalidAttempt, 409), (error.Code, error.StatusCode));
        Assert.False(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).Select(item => item.Consumed).SingleAsync());
        Assert.Empty(await context.Db.ChallengeRoundResults.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).ToListAsync());
    }

    [Fact]
    public async Task CreateRequestIdRejectsDifferentPayload()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var requestId = Guid.CreateVersion7();
        await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest(
                "Einmalig",
                context.Text.Id,
                ChallengeMode.Classic,
                [context.Invitee.Id],
                1,
                7,
                requestId));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.CreateAsync(
                context.Creator.Id,
                new CreateChallengeRequest(
                    "Abweichend",
                    context.Text.Id,
                    ChallengeMode.Classic,
                    [context.Invitee.Id],
                    1,
                    7,
                    requestId)));

        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (error.Code, error.StatusCode));
        Assert.Equal("Einmalig", (await context.Db.Challenges.SingleAsync()).Title);
    }

    [Fact]
    public async Task CreateRejectsQuarantinedTrainingText()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.IsQuarantined = true;
        await context.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.CreateAsync(
                context.Creator.Id,
                new CreateChallengeRequest("Nicht sichtbar", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1)));

        Assert.Equal((ChallengeErrorCodes.InvalidRequest, 400), (error.Code, error.StatusCode));
        Assert.Empty(await context.Db.Challenges.ToListAsync());
    }

    [Fact]
    public async Task JoinExpiresPastDueChallenge()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Ablauf", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        context.Time.Advance(TimeSpan.FromDays(2));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.JoinAsync(challenge.Id, context.Invitee.Id));

        Assert.Equal((ChallengeErrorCodes.Expired, 410), (error.Code, error.StatusCode));
        var stored = await context.Db.Challenges.SingleAsync(item => item.Id == challenge.Id);
        Assert.Equal(ChallengeStatus.Expired, stored.Status);
        Assert.NotNull(stored.FinishedAt);
    }

    [Fact]
    public async Task BestOfExpiryMaterializesMissingRoundsAndRatesCommittedForfeitExactlyOnce()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var spectator = new UserProfile
        {
            DisplayName = "Nina Neutral",
            SamAccountName = "neutral",
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}"
        };
        context.Db.UserProfiles.Add(spectator);
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Forfeit", context.Text.Id, ChallengeMode.BestOf, [context.Invitee.Id, spectator.Id], 3, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Creator.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Invitee.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Invitee.Id);
        context.Time.Advance(TimeSpan.FromDays(2));

        var expired = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.RequirePlayableAsync(challenge.Id, context.Creator.Id));
        var replay = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.RequirePlayableAsync(challenge.Id, context.Creator.Id));

        Assert.Equal((ChallengeErrorCodes.Expired, 410), (expired.Code, expired.StatusCode));
        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (replay.Code, replay.StatusCode));
        var stored = await context.Db.Challenges.AsNoTracking().SingleAsync(item => item.Id == challenge.Id);
        Assert.Equal(ChallengeStatus.Finished, stored.Status);
        Assert.Equal(challenge.ExpiresAt, stored.FinishedAt);
        Assert.Equal(6, await context.Db.ChallengeRoundResults.AsNoTracking().CountAsync());
        Assert.Equal(3, await context.Db.ChallengeRoundResults.AsNoTracking().CountAsync(item => item.TypingAttemptId == null && item.Status == ParticipantStatus.Dnf && item.CompetitionEligible));
        var neutral = await context.Db.ChallengeParticipants.AsNoTracking()
            .SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == spectator.Id);
        Assert.Equal(ParticipantStatus.LeftBeforeStart, neutral.Status);
        Assert.Equal(challenge.ExpiresAt, neutral.RespondedAt);
        Assert.Equal(challenge.ExpiresAt, neutral.FinishedAt);
        Assert.Null(neutral.Placement);
        var profiles = await context.Db.UserProfiles.AsNoTracking()
            .Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id)
            .ToDictionaryAsync(item => item.Id);
        Assert.Equal(1, profiles[context.Creator.Id].RatedMatchCount);
        Assert.Equal(1, profiles[context.Invitee.Id].RatedMatchCount);
        Assert.True(profiles[context.Invitee.Id].ArenaRating > profiles[context.Creator.Id].ArenaRating);
    }

    [Fact]
    public async Task ExpiryKeepsUnacceptedInviteeNeutral()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Neutral", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        context.Time.Advance(TimeSpan.FromDays(2));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.RequirePlayableAsync(challenge.Id, context.Creator.Id));

        Assert.Equal((ChallengeErrorCodes.Expired, 410), (error.Code, error.StatusCode));
        Assert.Equal(ChallengeStatus.Expired, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Empty(await context.Db.ChallengeRoundResults.AsNoTracking().Where(item => item.ChallengeRoundId != Guid.Empty).ToListAsync());
        var invitee = await context.Db.ChallengeParticipants.AsNoTracking()
            .SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id);
        Assert.Equal(ParticipantStatus.LeftBeforeStart, invitee.Status);
        Assert.Equal(challenge.ExpiresAt, invitee.RespondedAt);
        Assert.Equal(challenge.ExpiresAt, invitee.FinishedAt);
        Assert.Equal(0, context.Creator.RatedMatchCount);
        Assert.Equal(0, context.Invitee.RatedMatchCount);
    }

    [Fact]
    public async Task ExpiryTreatsLegacyResultBearingInviteeAsCommitted()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Legacy-Ergebnis", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        var roundId = await context.Db.ChallengeRounds
            .Where(item => item.ChallengeId == challenge.Id)
            .Select(item => item.Id)
            .SingleAsync();
        context.Db.ChallengeRoundResults.Add(new ChallengeRoundResult
        {
            ChallengeRoundId = roundId,
            UserProfileId = context.Invitee.Id,
            Status = ParticipantStatus.Finished,
            DurationMilliseconds = 10_000,
            Wpm = 60,
            Accuracy = 100,
            Consistency = 100,
            CompetitionEligible = true,
            FinishedAt = context.Time.GetUtcNow()
        });
        await context.Db.SaveChangesAsync();
        context.Time.Advance(TimeSpan.FromDays(2));

        await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.RequirePlayableAsync(challenge.Id, context.Creator.Id));

        var participant = await context.Db.ChallengeParticipants.AsNoTracking()
            .SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id);
        Assert.Equal(ChallengeStatus.Finished, await context.Db.Challenges.AsNoTracking()
            .Where(item => item.Id == challenge.Id)
            .Select(item => item.Status)
            .SingleAsync());
        Assert.Equal(ParticipantStatus.Finished, participant.Status);
        Assert.Equal(2, await context.Db.ChallengeRoundResults.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task SnapshotStartStillRejectsQuarantinedOrHiddenTrainingText()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Moderation", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        context.Text.IsQuarantined = true;
        await context.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AttemptLifecycleException>(() =>
            context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts));

        Assert.Equal((AttemptErrorCodes.InvalidRequest, 400), (error.Code, error.StatusCode));
        Assert.Equal(ChallengeStatus.Open, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Empty(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.ChallengeId == challenge.Id).ToListAsync());
    }

    [Fact]
    public async Task InvitedParticipantCanPlayPrivateChallengeSnapshotWithoutLibraryAccess()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.Visibility = TrainingTextVisibility.Private;
        context.Text.RatingEligible = false;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Privates Training", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);

        await Assert.ThrowsAsync<AttemptLifecycleException>(() => context.Attempts.StartAsync(
            context.Invitee.Id, new StartAttemptRequest(TrainingMode.Text, context.Text.Id, null, null)));
        await Assert.ThrowsAsync<ChallengeLifecycleException>(() => context.Service.StartAttemptAsync(
            challenge.Id, Guid.CreateVersion7(), context.Attempts));

        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);

        Assert.Equal(challenge.TargetTextSnapshot, session.Text);
        Assert.Equal(TrainingTextVisibility.Private, context.Text.Visibility);
        Assert.False(challenge.RatingEligible);
        Assert.Single(await context.Db.ChallengeAttemptBindings.AsNoTracking()
            .Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id).ToListAsync());
    }

    [Fact]
    public async Task NonRatedChallengeResultNeverEntersPublicChallengeBoard()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.RatingEligible = false;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Privatwertung", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Creator.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Invitee.Id);

        var board = await new CompetitionLeaderboardService(context.Db, context.Time).GetAsync(
            context.Creator,
            new LeaderboardQuery(CompetitionBoardKind.Challenge, CompetitionPeriod.AllTime, TrainingMode.Text, null));

        Assert.False(challenge.RatingEligible);
        Assert.All(
            await context.Db.ChallengeRoundResults.AsNoTracking().ToListAsync(),
            result => Assert.False(result.CompetitionEligible));
        Assert.Empty(board.Board.Entries);
        Assert.Equal(0, context.Creator.RatedMatchCount);
        Assert.Equal(0, context.Invitee.RatedMatchCount);
    }

    [Fact]
    public async Task FinishRequiresAcceptedParticipant()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Direktfinish", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        var attempt = context.CreateAttempt(context.Invitee.Id, context.Text.Id, TrainingMode.Text, challenge.CreatedAt.AddMinutes(1));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.FinishRoundAsync(challenge.Id, context.Invitee.Id, attempt));

        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (error.Code, error.StatusCode));
    }

    [Fact]
    public async Task FinishRejectsWrongAttemptMode()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Modusbindung", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var attempt = context.CreateAttempt(context.Invitee.Id, context.Text.Id, TrainingMode.Sprint60, challenge.CreatedAt.AddMinutes(1));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.FinishRoundAsync(challenge.Id, context.Invitee.Id, attempt));

        Assert.Equal((ChallengeErrorCodes.InvalidAttempt, 409), (error.Code, error.StatusCode));
    }

    [Fact]
    public async Task ChallengeStartCreatesBoundAttemptAndFinishConsumesIt()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Bindung", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);

        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        var attempt = await context.Service.FinishAttemptAsync(
            challenge.Id,
            context.Invitee.Id,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
            context.Attempts);

        var binding = await context.Db.ChallengeAttemptBindings.SingleAsync(item => item.TypingAttemptId == attempt.Id);
        var result = await context.Db.ChallengeRoundResults.SingleAsync(item => item.TypingAttemptId == attempt.Id);
        var participant = await context.Db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id);

        Assert.True(binding.Consumed);
        Assert.NotNull(binding.ConsumedAt);
        Assert.Equal(ParticipantStatus.Finished, participant.Status);
        Assert.Equal(ParticipantStatus.Finished, result.Status);
        Assert.True(result.CompetitionEligible);
    }

    [Fact]
    public async Task InstantChallengeFinishPersistsWithoutRewardsButCountsAsRatingForfeit()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.Body = new string('a', 151);
        context.Text.CharacterCount = 151;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Integritätsprüfung", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);

        var instantSession = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(instantSession.Id, instantSession.Nonce));
        var instantRequest = new FinishAttemptRequest(
            instantSession.Id,
            instantSession.Text,
            0,
            0,
            600_000) { Nonce = instantSession.Nonce };
        var instant = await context.Service.FinishAttemptAsync(
            challenge.Id,
            context.Invitee.Id,
            instantRequest,
            context.Attempts);
        var replay = await context.Service.FinishAttemptAsync(
            challenge.Id,
            context.Invitee.Id,
            instantRequest,
            context.Attempts);

        Assert.Equal(instant.Attempt.Id, replay.Attempt.Id);
        var instantResult = await context.Db.ChallengeRoundResults
            .SingleAsync(item => item.TypingAttemptId == instantSession.Id);
        Assert.Equal(ParticipantStatus.Finished, instantResult.Status);
        Assert.False(instantResult.CompetitionEligible);
        Assert.False(instant.Attempt.LeaderboardEligible);
        Assert.False(instant.ExperienceAwarded);
        Assert.Single(await context.Db.ChallengeRoundResults
            .Where(item => item.TypingAttemptId == instantSession.Id)
            .ToListAsync());

        var plausibleSession = await context.Service.StartAttemptAsync(challenge.Id, context.Creator.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Creator.Id, new BeginAttemptRequest(plausibleSession.Id, plausibleSession.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(6));
        await context.Service.FinishAttemptAsync(
            challenge.Id,
            context.Creator.Id,
            new FinishAttemptRequest(plausibleSession.Id, plausibleSession.Text, 0, 0, 1) { Nonce = plausibleSession.Nonce },
            context.Attempts);

        var results = await context.Db.ChallengeRoundResults
            .Where(item => item.ChallengeRoundId == instantResult.ChallengeRoundId)
            .ToListAsync();
        Assert.Contains(results, item => item.UserProfileId == context.Creator.Id && item.CompetitionEligible);
        var invitee = await context.Db.UserProfiles.SingleAsync(item => item.Id == context.Invitee.Id);
        var creator = await context.Db.UserProfiles.SingleAsync(item => item.Id == context.Creator.Id);
        Assert.Equal(1, invitee.RatedMatchCount);
        Assert.Equal(1, creator.RatedMatchCount);
        Assert.True(invitee.ArenaRating < 1000);
        Assert.True(creator.ArenaRating > 1000);
        Assert.Equal(0, invitee.ExperiencePoints);
        Assert.Equal(0, invitee.SeasonPoints);
        Assert.False(await context.Db.RewardLedgerEntries.AnyAsync(item => item.UserProfileId == invitee.Id));
        Assert.False(await context.Db.SeasonScores.AnyAsync(item => item.UserProfileId == invitee.Id));

        var leaderboard = await new CompetitionLeaderboardService(context.Db, context.Time).GetAsync(
            creator,
            new LeaderboardQuery(CompetitionBoardKind.Challenge, CompetitionPeriod.Day, TrainingMode.Text, null));
        Assert.Contains(leaderboard.Board.Entries, item => item.UserProfileId == creator.Id);
        Assert.DoesNotContain(leaderboard.Board.Entries, item => item.UserProfileId == invitee.Id);
    }

    [Fact]
    public async Task AtomicChallengeFinishCommitsAttemptRewardBindingAndResultTogether()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.Body = "Atomarer Challenge-Text mit genügend Zeichen";
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Atomarer Abschluss", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        var request = new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce };

        var completion = await context.Service.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, context.Attempts);
        var pending = await context.Service.StartAttemptAsync(challenge.Id, context.Creator.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Creator.Id, new BeginAttemptRequest(pending.Id, pending.Nonce));
        challenge.ExpiresAt = context.Time.GetUtcNow().AddSeconds(-1);
        await context.Db.SaveChangesAsync();

        var dueReplay = await context.Service.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, context.Attempts);

        Assert.Equal(completion.Attempt.Id, dueReplay.Attempt.Id);
        Assert.Equal(ChallengeStatus.Running, await context.Db.Challenges.Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(AttemptPhase.Started, await context.Db.TypingAttempts.Where(item => item.Id == pending.Id).Select(item => item.Phase).SingleAsync());

        await context.Service.ListPageForProfileAsync(context.Creator.Id, ChallengeListFilter.All, 1, 10);
        var expiredReplay = await context.Service.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, context.Attempts);

        Assert.Equal(completion.Attempt.Id, expiredReplay.Attempt.Id);
        Assert.Equal(ChallengeStatus.Finished, await context.Db.Challenges.Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(AttemptPhase.Aborted, await context.Db.TypingAttempts.Where(item => item.Id == pending.Id).Select(item => item.Phase).SingleAsync());
        Assert.Equal(AttemptPhase.Finished, await context.Db.TypingAttempts.Where(item => item.Id == session.Id).Select(item => item.Phase).SingleAsync());
        Assert.True(await context.Db.ChallengeAttemptBindings.Where(item => item.TypingAttemptId == session.Id).Select(item => item.Consumed).SingleAsync());
        Assert.Single(await context.Db.ChallengeRoundResults.Where(item => item.TypingAttemptId == session.Id).ToListAsync());
        Assert.Single(await context.Db.ChallengeRoundResults.Where(item => item.UserProfileId == context.Creator.Id && item.Status == ParticipantStatus.Dnf && item.TypingAttemptId == null).ToListAsync());
        Assert.Single(await context.Db.RewardLedgerEntries.Where(item => item.UserProfileId == context.Invitee.Id && item.Source == "attempt" && item.SourceId == session.Id.ToString("N")).ToListAsync());
    }

    [Fact]
    public async Task AtomicChallengeFinishRollsBackAttemptRewardAndResultWhenChallengeWriteFails()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.Body = "Atomarer Challenge-Text mit genügend Zeichen";
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Atomarer Fehler", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        await context.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_challenge_result
            BEFORE INSERT ON ChallengeRoundResults
            BEGIN
                SELECT RAISE(ABORT, 'forced atomic challenge failure');
            END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.Service.FinishAttemptAsync(
            challenge.Id,
            context.Invitee.Id,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
            context.Attempts));

        Assert.Equal(AttemptPhase.Started, await context.Db.TypingAttempts.AsNoTracking().Where(item => item.Id == session.Id).Select(item => item.Phase).SingleAsync());
        Assert.False(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).Select(item => item.Consumed).SingleAsync());
        Assert.Empty(await context.Db.ChallengeRoundResults.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).ToListAsync());
        Assert.Empty(await context.Db.RewardLedgerEntries.AsNoTracking().Where(item => item.Source == "attempt" && item.SourceId == session.Id.ToString("N")).ToListAsync());

        await context.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_challenge_result;");
        var completion = await context.Service.FinishAttemptAsync(
            challenge.Id,
            context.Invitee.Id,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
            context.Attempts);
        Assert.Equal(AttemptPhase.Finished, completion.Attempt.Phase);
        Assert.Single(await context.Db.RewardLedgerEntries.Where(item => item.Source == "attempt" && item.SourceId == session.Id.ToString("N")).ToListAsync());
    }

    [Fact]
    public async Task GenericFinishRejectsUnconsumedChallengeAttemptWithoutReward()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Text.Body = "Challengegebundener Versuch mit genügend Zeichen";
        context.Text.CharacterCount = TypingEngine.SplitGraphemes(context.Text.Body).Count;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Kein Bypass", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<AttemptLifecycleException>(() => context.Attempts.FinishAsync(
            context.Invitee.Id,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce }));

        Assert.Equal((AttemptErrorCodes.ChallengeBound, 409), (error.Code, error.StatusCode));
        Assert.Equal(AttemptPhase.Started, await context.Db.TypingAttempts.AsNoTracking().Where(item => item.Id == session.Id).Select(item => item.Phase).SingleAsync());
        Assert.False(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).Select(item => item.Consumed).SingleAsync());
        Assert.Empty(await context.Db.RewardLedgerEntries.AsNoTracking().Where(item => item.Source == "attempt" && item.SourceId == session.Id.ToString("N")).ToListAsync());
    }

    [Fact]
    public async Task CancelAfterAttemptStartedIsRejectedWithoutMutation()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Abbruch mit Versuch", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.CancelAsync(challenge.Id, context.Creator.Id));

        var attempt = await context.Db.TypingAttempts.AsNoTracking().SingleAsync(item => item.Id == session.Id);
        var binding = await context.Db.ChallengeAttemptBindings.AsNoTracking().SingleAsync(item => item.TypingAttemptId == session.Id);
        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (error.Code, error.StatusCode));
        Assert.Equal(ChallengeStatus.Running, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(ParticipantStatus.Running, await context.Db.ChallengeParticipants.AsNoTracking().Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(AttemptPhase.Started, attempt.Phase);
        Assert.Null(attempt.FinishedAt);
        Assert.False(binding.Consumed);
        Assert.True(context.Sessions.TryGet(session.Id, out _));
        Assert.Empty(await context.Db.RewardLedgerEntries.AsNoTracking().Where(item => item.Source == "attempt" && item.SourceId == session.Id.ToString("N")).ToListAsync());
    }

    [Fact]
    public async Task JoinedParticipantCanDeclineBeforeAnyAttemptStarts()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Ablehnen vor Start", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);

        await context.Service.DeclineAsync(challenge.Id, context.Invitee.Id);
        await context.Service.DeclineAsync(challenge.Id, context.Invitee.Id);

        Assert.Equal(ChallengeStatus.Open, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(ParticipantStatus.Declined, await context.Db.ChallengeParticipants.AsNoTracking().Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id).Select(item => item.Status).SingleAsync());
        Assert.Empty(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.ChallengeId == challenge.Id).ToListAsync());
    }

    [Fact]
    public async Task DeclineAfterOwnAttemptStartedIsRejectedWithoutMutation()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Ablehnen nach Start", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.DeclineAsync(challenge.Id, context.Invitee.Id));

        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (error.Code, error.StatusCode));
        Assert.Equal(ParticipantStatus.Running, await context.Db.ChallengeParticipants.AsNoTracking().Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(AttemptPhase.Started, await context.Db.TypingAttempts.AsNoTracking().Where(item => item.Id == session.Id).Select(item => item.Phase).SingleAsync());
        Assert.Single(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).ToListAsync());
        Assert.True(context.Sessions.TryGet(session.Id, out _));
    }

    [Fact]
    public async Task DeclineAfterOpponentFinishedIsRejectedWithoutMutation()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Ablehnen nach Ergebnis", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Creator.Id);

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.DeclineAsync(challenge.Id, context.Invitee.Id));

        Assert.Equal((ChallengeErrorCodes.Conflict, 409), (error.Code, error.StatusCode));
        Assert.Equal(ChallengeStatus.Running, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(ParticipantStatus.Joined, await context.Db.ChallengeParticipants.AsNoTracking().Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id).Select(item => item.Status).SingleAsync());
        Assert.Single(await context.Db.ChallengeRoundResults.AsNoTracking().Where(item => item.UserProfileId == context.Creator.Id).ToListAsync());
    }

    [Fact]
    public async Task ServiceExpiryAbortsBoundAttemptAndRemovesSession()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Ablauf mit Versuch", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        challenge.ExpiresAt = context.Time.GetUtcNow().AddMinutes(1);
        await context.Db.SaveChangesAsync();
        context.Time.Advance(TimeSpan.FromMinutes(2));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.RequirePlayableAsync(challenge.Id, context.Invitee.Id));

        Assert.Equal((ChallengeErrorCodes.Expired, 410), (error.Code, error.StatusCode));
        Assert.Equal(AttemptPhase.Aborted, await context.Db.TypingAttempts.AsNoTracking().Where(item => item.Id == session.Id).Select(item => item.Phase).SingleAsync());
        Assert.Empty(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).ToListAsync());
        Assert.False(context.Sessions.TryGet(session.Id, out _));
    }

    [Fact]
    public async Task ChallengeStartSweepsExpiredForeignAttemptBeforeOpeningChallengeTransaction()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var expired = await context.Attempts.StartAsync(
            context.Creator.Id,
            new StartAttemptRequest(TrainingMode.Words10, null, null, 10));
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Sweep vor Start", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        context.Time.Advance(TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(1)));

        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Creator.Id, context.Attempts);

        Assert.NotEqual(expired.Id, session.Id);
        Assert.Equal(
            AttemptPhase.Expired,
            await context.Db.TypingAttempts.Where(item => item.Id == expired.Id).Select(item => item.Phase).SingleAsync());
        Assert.True(await context.Db.ChallengeAttemptBindings.AnyAsync(item => item.TypingAttemptId == session.Id));
    }

    [Fact]
    public async Task ConcurrentDuplicateChallengeFinishIsIdempotent()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Doppelfinish", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        await using var firstDb = new KeyWarsDbContext(context.Options);
        await using var secondDb = new KeyWarsDbContext(context.Options);
        var first = new ChallengeService(firstDb, Options.Create(new ChallengeOptions()), context.Time, attemptSessionStore: context.Sessions);
        var second = new ChallengeService(secondDb, Options.Create(new ChallengeOptions()), context.Time, attemptSessionStore: context.Sessions);
        var request = new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce };
        var firstAttempts = new AttemptService(firstDb, new TypingEngine(context.Time), new MotivationService(firstDb, context.Time), context.Time, context.Sessions);
        var secondAttempts = new AttemptService(secondDb, new TypingEngine(context.Time), new MotivationService(secondDb, context.Time), context.Time, context.Sessions);

        var completions = await Task.WhenAll(
            first.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, firstAttempts),
            second.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, secondAttempts));
        var completion = completions[0];

        await using var verificationDb = new KeyWarsDbContext(context.Options);
        Assert.Single(await verificationDb.ChallengeRoundResults
            .Where(item => item.UserProfileId == context.Invitee.Id && item.TypingAttemptId == completion.Id)
            .ToListAsync());
        Assert.True(await verificationDb.ChallengeAttemptBindings
            .Where(item => item.TypingAttemptId == completion.Id)
            .Select(item => item.Consumed)
            .SingleAsync());
    }

    [Fact]
    public async Task ChallengeStartReturnsExistingPreparedAttemptWhenStillActive()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Fortsetzen", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);

        var first = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        var second = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Nonce, second.Nonce);
        Assert.Equal(first.Text, second.Text);
        Assert.Single(await context.Db.ChallengeAttemptBindings.ToListAsync());

        var participant = await context.Db.ChallengeParticipants.SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id);
        Assert.Equal(ParticipantStatus.Running, participant.Status);
    }

    [Fact]
    public async Task NormalTrainingAttemptCannotFinishChallenge()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Manipulation", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var attempt = context.CreateAttempt(context.Invitee.Id, context.Text.Id, TrainingMode.Text, challenge.CreatedAt.AddMinutes(1));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.FinishRoundAsync(challenge.Id, context.Invitee.Id, attempt));

        Assert.Equal((ChallengeErrorCodes.InvalidAttempt, 409), (error.Code, error.StatusCode));
        Assert.Empty(await context.Db.ChallengeRoundResults.ToListAsync());
    }

    [Fact]
    public async Task ChallengeAttemptCannotBeReusedForAnotherChallenge()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var first = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Erste", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(first.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(first.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        var attempt = await context.Service.FinishAttemptAsync(
            first.Id,
            context.Invitee.Id,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
            context.Attempts);

        var second = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Zweite", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(second.Id, context.Invitee.Id);

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.FinishRoundAsync(second.Id, context.Invitee.Id, attempt));

        Assert.Equal((ChallengeErrorCodes.InvalidAttempt, 409), (error.Code, error.StatusCode));
        Assert.Single(await context.Db.ChallengeRoundResults.ToListAsync());
    }

    [Fact]
    public async Task AbortedBoundAttemptCanBeStartedAgain()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Recovery", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var abortedSession = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        var abortedAttempt = await context.Db.TypingAttempts.SingleAsync(item => item.Id == abortedSession.Id);
        abortedAttempt.Phase = AttemptPhase.Aborted;
        await context.Db.SaveChangesAsync();
        Assert.True(context.Sessions.TryRemove(abortedSession.Id, out _));

        var replacement = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        var binding = await context.Db.ChallengeAttemptBindings.SingleAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id);

        Assert.NotEqual(abortedSession.Id, replacement.Id);
        Assert.Equal(replacement.Id, binding.TypingAttemptId);
        Assert.False(binding.Consumed);
        Assert.Equal(AttemptPhase.Aborted, abortedAttempt.Phase);
    }

    [Fact]
    public async Task ChallengeFinishRollsBackResultWhenClosingFails()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Atomar", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        await context.Service.DeclineAsync(challenge.Id, context.Creator.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        var request = new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce };
        await context.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_challenge_close
            BEFORE UPDATE OF Status ON Challenges
            WHEN NEW.Status = 'Finished'
            BEGIN
                SELECT RAISE(ABORT, 'forced challenge close failure');
            END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.Service.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, context.Attempts));

        Assert.Empty(await context.Db.ChallengeRoundResults.AsNoTracking().Where(item => item.ChallengeRoundId != Guid.Empty).ToListAsync());
        Assert.False(await context.Db.ChallengeAttemptBindings.AsNoTracking()
            .Where(item => item.TypingAttemptId == session.Id)
            .Select(item => item.Consumed)
            .SingleAsync());
        Assert.Equal(
            ParticipantStatus.Running,
            await context.Db.ChallengeParticipants.AsNoTracking()
                .Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id)
                .Select(item => item.Status)
                .SingleAsync());
        Assert.Equal(
            ChallengeStatus.Running,
            await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());

        await context.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_challenge_close;");
        await context.Service.FinishAttemptAsync(challenge.Id, context.Invitee.Id, request, context.Attempts);
        Assert.Single(await context.Db.ChallengeRoundResults.AsNoTracking().ToListAsync());
        Assert.Equal(
            ChallengeStatus.Finished,
            await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task ConcurrentStartAndDeclineLeaveOneConsistentParticipantState()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Statuslock", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        await using var startDb = new KeyWarsDbContext(context.Options);
        await using var declineDb = new KeyWarsDbContext(context.Options);
        var sessions = new AttemptSessionStore();
        var attempts = new AttemptService(startDb, new TypingEngine(context.Time), new MotivationService(startDb, context.Time), context.Time, sessions);
        var starter = new ChallengeService(startDb, Options.Create(new ChallengeOptions()), context.Time, attemptSessionStore: sessions);
        var decliner = new ChallengeService(declineDb, Options.Create(new ChallengeOptions()), context.Time, attemptSessionStore: sessions);

        var startTask = Task.Run(async () =>
        {
            try
            {
                await starter.StartAttemptAsync(challenge.Id, context.Invitee.Id, attempts);
            }
            catch (ChallengeLifecycleException exception) when (exception.Code == ChallengeErrorCodes.Conflict)
            {
            }
        });
        var declineTask = Task.Run(async () =>
        {
            try
            {
                await decliner.DeclineAsync(challenge.Id, context.Invitee.Id);
            }
            catch (ChallengeLifecycleException exception) when (exception.Code == ChallengeErrorCodes.Conflict)
            {
            }
        });
        await Task.WhenAll(startTask, declineTask);

        await using var verificationDb = new KeyWarsDbContext(context.Options);
        var status = await verificationDb.ChallengeParticipants
            .Where(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id)
            .Select(item => item.Status)
            .SingleAsync();
        var bindings = await verificationDb.ChallengeAttemptBindings.CountAsync(item => item.ChallengeId == challenge.Id && item.UserProfileId == context.Invitee.Id);
        Assert.True(
            (status == ParticipantStatus.Running && bindings == 1) ||
            (status == ParticipantStatus.Declined && bindings == 0));
    }

    [Fact]
    public async Task ExpiredChallengeCannotStartAttempt()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Abgelaufen", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        context.Time.Advance(TimeSpan.FromDays(2));

        var error = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts));

        Assert.Equal((ChallengeErrorCodes.Expired, 410), (error.Code, error.StatusCode));
        Assert.Empty(await context.Db.ChallengeAttemptBindings.ToListAsync());
    }

    [Fact]
    public async Task CreatorCanCancelChallengeIdempotently()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Abbruch", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));

        var hidden = await Assert.ThrowsAsync<ChallengeLifecycleException>(() =>
            context.Service.CancelAsync(challenge.Id, context.Invitee.Id));
        Assert.Equal((ChallengeErrorCodes.NotFound, 404), (hidden.Code, hidden.StatusCode));

        var first = await context.Service.CancelAsync(challenge.Id, context.Creator.Id);
        var second = await context.Service.CancelAsync(challenge.Id, context.Creator.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(ChallengeStatus.Cancelled, second.Status);
        Assert.All(
            await context.Db.ChallengeParticipants.Where(item => item.ChallengeId == challenge.Id).ToListAsync(),
            participant => Assert.Equal(ParticipantStatus.Cancelled, participant.Status));
    }

    [Fact]
    public async Task RematchReusesSourceExactlyOnce()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var source = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Serie", context.Text.Id, ChallengeMode.BestOf, [context.Invitee.Id], 3, 7));
        await context.Service.CancelAsync(source.Id, context.Creator.Id);

        var first = await context.Service.CreateRematchAsync(source.Id, context.Creator.Id);
        var second = await context.Service.CreateRematchAsync(source.Id, context.Creator.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(source.Id, first.RematchOfChallengeId);
        Assert.Equal((ChallengeMode.BestOf, 3), (first.Mode, first.RoundCount));
        Assert.Equal(2, await context.Db.ChallengeParticipants.CountAsync(item => item.ChallengeId == first.Id));
        Assert.Equal(3, await context.Db.ChallengeRounds.CountAsync(item => item.ChallengeId == first.Id));
        Assert.Single(await context.Db.Challenges.Where(item => item.RematchOfChallengeId == source.Id).ToListAsync());
    }

    [Fact]
    public async Task ChallengeListSupportsFiltersPagingAndUnreadCount()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var active = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Aktiv", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));
        var completed = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Abgebrochen", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));
        await context.Service.CancelAsync(completed.Id, context.Creator.Id);

        var invitations = await context.Service.ListPageForProfileAsync(
            context.Invitee.Id, ChallengeListFilter.Invitations, 1, 1);
        var finished = await context.Service.ListPageForProfileAsync(
            context.Invitee.Id, ChallengeListFilter.Completed, 1, 1);

        Assert.Equal(1, invitations.TotalCount);
        Assert.Equal(1, invitations.UnreadCount);
        Assert.Equal(active.Id, Assert.Single(invitations.Items).Challenge.Id);
        Assert.Equal(completed.Id, Assert.Single(finished.Items).Challenge.Id);

        await context.Service.JoinAsync(active.Id, context.Invitee.Id);
        var afterJoin = await context.Service.ListPageForProfileAsync(
            context.Invitee.Id, ChallengeListFilter.Invitations, 99, 1);
        Assert.Empty(afterJoin.Items);
        Assert.Equal(0, afterJoin.UnreadCount);
        Assert.Equal(1, afterJoin.Page);
    }

    [Fact]
    public async Task BestOfRunsAllRoundsAndRatesSeriesOnce()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Best of 3", context.Text.Id, ChallengeMode.BestOf, [context.Invitee.Id], 3, 7));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);

        for (var round = 0; round < 3; round++)
        {
            await CompleteNextRoundAsync(context, challenge.Id, context.Invitee.Id);
            await CompleteNextRoundAsync(context, challenge.Id, context.Creator.Id);
        }

        var stored = await context.Db.Challenges.SingleAsync(item => item.Id == challenge.Id);
        var participants = await context.Db.ChallengeParticipants
            .Where(item => item.ChallengeId == challenge.Id)
            .ToListAsync();
        var results = await context.Db.ChallengeRoundResults
            .Where(item => context.Db.ChallengeRounds
                .Where(challengeRound => challengeRound.ChallengeId == challenge.Id)
                .Select(challengeRound => challengeRound.Id)
                .Contains(item.ChallengeRoundId))
            .ToListAsync();

        Assert.Equal(ChallengeStatus.Finished, stored.Status);
        Assert.Equal(6, results.Count);
        Assert.All(results, result => Assert.NotNull(result.Placement));
        Assert.All(participants, participant =>
        {
            Assert.Equal(ParticipantStatus.Finished, participant.Status);
            Assert.NotNull(participant.Placement);
        });
        Assert.All(
            await context.Db.UserProfiles.Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id).ToListAsync(),
            profile => Assert.Equal(1, profile.RatedMatchCount));
    }

    [Theory]
    [InlineData(ParticipantStatus.Finished)]
    [InlineData(ParticipantStatus.Dnf)]
    public async Task BestOfRatingTreatsEachIneligibleOrDnfRoundAsForfeit(
        ParticipantStatus ineligibleStatus)
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var third = new UserProfile
        {
            DisplayName = "Tina Third",
            SamAccountName = "third",
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}"
        };
        var now = context.Time.GetUtcNow();
        var challenge = new Challenge
        {
            CreatorProfileId = context.Creator.Id,
            TrainingTextId = context.Text.Id,
            Title = "Best-of-Integrität",
            Mode = ChallengeMode.BestOf,
            Status = ChallengeStatus.Running,
            RoundCount = 3,
            RatingEligible = true,
            CreatedAt = now.AddMinutes(-10),
            ExpiresAt = now.AddDays(1)
        };
        var rounds = Enumerable.Range(1, 3)
            .Select(roundNumber => new ChallengeRound
            {
                ChallengeId = challenge.Id,
                RoundNumber = roundNumber,
                CreatedAt = now.AddMinutes(-5)
            })
            .ToArray();
        context.Db.UserProfiles.Add(third);
        context.Db.Challenges.Add(challenge);
        context.Db.ChallengeRounds.AddRange(rounds);
        context.Db.ChallengeParticipants.AddRange(
            FinishedParticipant(challenge.Id, context.Creator.Id, now),
            FinishedParticipant(challenge.Id, context.Invitee.Id, now),
            FinishedParticipant(challenge.Id, third.Id, now));

        AddResult(rounds[0], context.Creator.Id, 20_000, competitionEligible: false, status: ineligibleStatus);
        AddResult(rounds[0], context.Invitee.Id, 10_000, competitionEligible: true);
        AddResult(rounds[0], third.Id, 30_000, competitionEligible: true);
        for (var index = 1; index < rounds.Length; index++)
        {
            AddResult(rounds[index], context.Creator.Id, 10_000, competitionEligible: false, status: ineligibleStatus);
            AddResult(rounds[index], context.Invitee.Id, 30_000, competitionEligible: true);
            AddResult(rounds[index], third.Id, 20_000, competitionEligible: true);
        }

        await context.Db.SaveChangesAsync();
        await context.Service.TryCloseAsync(challenge.Id);

        var profiles = await context.Db.UserProfiles
            .AsNoTracking()
            .Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id || item.Id == third.Id)
            .ToDictionaryAsync(item => item.Id);
        var participants = await context.Db.ChallengeParticipants
            .AsNoTracking()
            .Where(item => item.ChallengeId == challenge.Id)
            .ToDictionaryAsync(item => item.UserProfileId);
        Assert.True(profiles[context.Creator.Id].ArenaRating < 1000);
        Assert.Equal(1, profiles[context.Creator.Id].RatedMatchCount);
        Assert.True(profiles[third.Id].ArenaRating > 1000);
        Assert.Equal(1, profiles[third.Id].RatedMatchCount);
        Assert.Equal(1, profiles[context.Invitee.Id].RatedMatchCount);
        if (ineligibleStatus == ParticipantStatus.Finished)
        {
            Assert.Equal(2, participants[context.Invitee.Id].Placement);
            Assert.Equal(3, participants[third.Id].Placement);
        }

        void AddResult(
            ChallengeRound round,
            Guid profileId,
            int durationMilliseconds,
            bool competitionEligible,
            ParticipantStatus status = ParticipantStatus.Finished)
        {
            context.Db.ChallengeRoundResults.Add(new ChallengeRoundResult
            {
                ChallengeRoundId = round.Id,
                UserProfileId = profileId,
                Status = status,
                DurationMilliseconds = durationMilliseconds,
                Wpm = 60,
                Accuracy = 100,
                Consistency = 100,
                CompetitionEligible = competitionEligible,
                FinishedAt = now
            });
        }

        static ChallengeParticipant FinishedParticipant(
            Guid challengeId,
            Guid profileId,
            DateTimeOffset finishedAt) => new()
        {
            ChallengeId = challengeId,
            UserProfileId = profileId,
            Status = ParticipantStatus.Finished,
            RatingBefore = 1000,
            RatingAfter = 1000,
            InvitedAt = finishedAt.AddMinutes(-10),
            RespondedAt = finishedAt.AddMinutes(-9),
            FinishedAt = finishedAt
        };
    }

    [Fact]
    public async Task ClassicRatingTiesDnfAndIneligibleFinishAsLastForfeits()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var third = new UserProfile
        {
            DisplayName = "Fiona Forfeit",
            SamAccountName = "forfeit",
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}",
            ArenaRating = 1000
        };
        var now = context.Time.GetUtcNow();
        var challenge = new Challenge
        {
            CreatorProfileId = context.Creator.Id,
            TrainingTextId = context.Text.Id,
            Title = "Forfeit-Rang",
            Mode = ChallengeMode.Classic,
            Status = ChallengeStatus.Running,
            RoundCount = 1,
            RatingEligible = true,
            CreatedAt = now.AddMinutes(-10),
            ExpiresAt = now.AddDays(1)
        };
        var round = new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1, CreatedAt = now.AddMinutes(-5) };
        context.Db.UserProfiles.Add(third);
        context.Db.Challenges.Add(challenge);
        context.Db.ChallengeRounds.Add(round);
        context.Db.ChallengeParticipants.AddRange(
            Participant(context.Creator.Id),
            Participant(context.Invitee.Id),
            Participant(third.Id));
        context.Db.ChallengeRoundResults.AddRange(
            Result(context.Creator.Id, ParticipantStatus.Finished, true, 20_000),
            Result(context.Invitee.Id, ParticipantStatus.Finished, false, 10_000),
            Result(third.Id, ParticipantStatus.Dnf, true, 0));
        await context.Db.SaveChangesAsync();

        await context.Service.TryCloseAsync(challenge.Id);

        var profiles = await context.Db.UserProfiles.AsNoTracking()
            .Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id || item.Id == third.Id)
            .ToDictionaryAsync(item => item.Id);
        var participants = await context.Db.ChallengeParticipants.AsNoTracking()
            .Where(item => item.ChallengeId == challenge.Id)
            .ToDictionaryAsync(item => item.UserProfileId);
        Assert.True(profiles[context.Creator.Id].ArenaRating > 1000);
        Assert.Equal(profiles[context.Invitee.Id].ArenaRating, profiles[third.Id].ArenaRating);
        Assert.Equal(participants[context.Invitee.Id].RatingDelta, participants[third.Id].RatingDelta);
        Assert.Equal(1, participants[context.Invitee.Id].Placement);
        Assert.Equal(2, participants[context.Creator.Id].Placement);
        Assert.Equal(3, participants[third.Id].Placement);

        ChallengeParticipant Participant(Guid profileId) => new()
        {
            ChallengeId = challenge.Id,
            UserProfileId = profileId,
            Status = ParticipantStatus.Finished,
            InvitedAt = now.AddMinutes(-10),
            FinishedAt = now
        };

        ChallengeRoundResult Result(Guid profileId, ParticipantStatus status, bool eligible, int duration) => new()
        {
            ChallengeRoundId = round.Id,
            UserProfileId = profileId,
            Status = status,
            DurationMilliseconds = duration,
            Wpm = status == ParticipantStatus.Finished ? 60 : 0,
            Accuracy = status == ParticipantStatus.Finished ? 100 : 0,
            Consistency = status == ParticipantStatus.Finished ? 100 : 0,
            CompetitionEligible = eligible,
            FinishedAt = now
        };
    }

    [Fact]
    public async Task AllDnfChallengeFinishesWithoutRatingOrMatchCount()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var now = context.Time.GetUtcNow();
        var challenge = new Challenge
        {
            CreatorProfileId = context.Creator.Id,
            TrainingTextId = context.Text.Id,
            Title = "Nur DNF",
            Mode = ChallengeMode.Classic,
            Status = ChallengeStatus.Running,
            RoundCount = 1,
            RatingEligible = true,
            CreatedAt = now.AddMinutes(-10),
            ExpiresAt = now.AddDays(1)
        };
        var round = new ChallengeRound { ChallengeId = challenge.Id, RoundNumber = 1, CreatedAt = now.AddMinutes(-5) };
        context.Db.Challenges.Add(challenge);
        context.Db.ChallengeRounds.Add(round);
        foreach (var profileId in new[] { context.Creator.Id, context.Invitee.Id })
        {
            context.Db.ChallengeParticipants.Add(new ChallengeParticipant
            {
                ChallengeId = challenge.Id,
                UserProfileId = profileId,
                Status = ParticipantStatus.Dnf,
                InvitedAt = now.AddMinutes(-10),
                FinishedAt = now
            });
            context.Db.ChallengeRoundResults.Add(new ChallengeRoundResult
            {
                ChallengeRoundId = round.Id,
                UserProfileId = profileId,
                Status = ParticipantStatus.Dnf,
                CompetitionEligible = true,
                FinishedAt = now
            });
        }

        await context.Db.SaveChangesAsync();
        await context.Service.TryCloseAsync(challenge.Id);

        var profiles = await context.Db.UserProfiles.AsNoTracking()
            .Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id)
            .ToArrayAsync();
        Assert.Equal(ChallengeStatus.Finished, challenge.Status);
        Assert.All(profiles, profile =>
        {
            Assert.Equal(1000, profile.ArenaRating);
            Assert.Equal(0, profile.RatedMatchCount);
        });
    }

    [Fact]
    public async Task BestOfRatingIgnoresLegacyResultsFromDeclinedParticipant()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Creator.ArenaRating = 1000;
        context.Invitee.ArenaRating = 1000;
        var declined = new UserProfile
        {
            DisplayName = "Dana Declined",
            SamAccountName = "declined",
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}",
            ArenaRating = 1000
        };
        var now = context.Time.GetUtcNow();
        var challenge = new Challenge
        {
            CreatorProfileId = context.Creator.Id,
            TrainingTextId = context.Text.Id,
            Title = "Best-of-Ablehnung",
            Mode = ChallengeMode.BestOf,
            Status = ChallengeStatus.Running,
            RoundCount = 3,
            RatingEligible = true,
            CreatedAt = now.AddMinutes(-10),
            ExpiresAt = now.AddDays(1)
        };
        var rounds = Enumerable.Range(1, 3)
            .Select(roundNumber => new ChallengeRound
            {
                ChallengeId = challenge.Id,
                RoundNumber = roundNumber,
                CreatedAt = now.AddMinutes(-5)
            })
            .ToArray();
        context.Db.UserProfiles.Add(declined);
        context.Db.Challenges.Add(challenge);
        context.Db.ChallengeRounds.AddRange(rounds);
        context.Db.ChallengeParticipants.AddRange(
            Participant(context.Creator.Id, ParticipantStatus.Finished),
            Participant(context.Invitee.Id, ParticipantStatus.Finished),
            Participant(declined.Id, ParticipantStatus.Declined));
        AddResult(rounds[0], declined.Id, 5_000);
        AddResult(rounds[0], context.Creator.Id, 10_000);
        AddResult(rounds[0], context.Invitee.Id, 20_000);
        AddResult(rounds[1], context.Invitee.Id, 10_000);
        AddResult(rounds[1], context.Creator.Id, 20_000);
        AddResult(rounds[2], context.Creator.Id, 15_000);
        AddResult(rounds[2], context.Invitee.Id, 15_000);
        await context.Db.SaveChangesAsync();

        await context.Service.TryCloseAsync(challenge.Id);

        var profiles = await context.Db.UserProfiles
            .AsNoTracking()
            .Where(item => item.Id == context.Creator.Id || item.Id == context.Invitee.Id || item.Id == declined.Id)
            .ToDictionaryAsync(item => item.Id);
        var participants = await context.Db.ChallengeParticipants
            .AsNoTracking()
            .Where(item => item.ChallengeId == challenge.Id)
            .ToDictionaryAsync(item => item.UserProfileId);
        Assert.Equal(ChallengeStatus.Finished, await context.Db.Challenges.AsNoTracking().Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal((1000, 1), (profiles[context.Creator.Id].ArenaRating, profiles[context.Creator.Id].RatedMatchCount));
        Assert.Equal((1000, 1), (profiles[context.Invitee.Id].ArenaRating, profiles[context.Invitee.Id].RatedMatchCount));
        Assert.Equal((1000, 0), (profiles[declined.Id].ArenaRating, profiles[declined.Id].RatedMatchCount));
        Assert.Equal(0, participants[context.Creator.Id].RatingDelta);
        Assert.Equal(0, participants[context.Invitee.Id].RatingDelta);
        Assert.Equal(0, participants[declined.Id].RatingDelta);

        ChallengeParticipant Participant(Guid profileId, ParticipantStatus status) => new()
        {
            ChallengeId = challenge.Id,
            UserProfileId = profileId,
            Status = status,
            RatingBefore = 1000,
            RatingAfter = 1000,
            InvitedAt = now.AddMinutes(-10),
            RespondedAt = now.AddMinutes(-9),
            FinishedAt = now
        };

        void AddResult(ChallengeRound round, Guid profileId, int durationMilliseconds)
        {
            context.Db.ChallengeRoundResults.Add(new ChallengeRoundResult
            {
                ChallengeRoundId = round.Id,
                UserProfileId = profileId,
                Status = ParticipantStatus.Finished,
                DurationMilliseconds = durationMilliseconds,
                Wpm = 60,
                Accuracy = 100,
                Consistency = 100,
                CompetitionEligible = true,
                FinishedAt = now
            });
        }
    }

    [Fact]
    public async Task RatedClassicFinishesWithoutRatingWhenFinishedParticipantWasDeleted()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        context.Creator.ArenaRating = 1200;
        context.Invitee.ArenaRating = 1400;
        await context.Db.SaveChangesAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Löschung nach Ergebnis", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 7));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        await CompleteNextRoundAsync(context, challenge.Id, context.Invitee.Id);

        context.Invitee.Deleted = true;
        context.Invitee.ArenaRating = 1000;
        context.Invitee.RatedMatchCount = 0;
        await context.Db.SaveChangesAsync();

        await CompleteNextRoundAsync(context, challenge.Id, context.Creator.Id);

        var stored = await context.Db.Challenges.AsNoTracking().SingleAsync(item => item.Id == challenge.Id);
        var creator = await context.Db.UserProfiles.AsNoTracking().SingleAsync(item => item.Id == context.Creator.Id);
        Assert.Equal(ChallengeStatus.Finished, stored.Status);
        Assert.False(stored.RatingEligible);
        Assert.Equal(1200, creator.ArenaRating);
        Assert.Equal(0, creator.RatedMatchCount);
        Assert.All(
            await context.Db.ChallengeParticipants.AsNoTracking().Where(item => item.ChallengeId == challenge.Id).ToListAsync(),
            participant => Assert.Equal(ParticipantStatus.Finished, participant.Status));
    }

    private static async Task CompleteNextRoundAsync(ChallengeTestContext context, Guid challengeId, Guid profileId)
    {
        var session = await context.Service.StartAttemptAsync(challengeId, profileId, context.Attempts);
        await context.Attempts.BeginAsync(profileId, new BeginAttemptRequest(session.Id, session.Nonce));
        context.Time.Advance(TimeSpan.FromSeconds(10));
        await context.Service.FinishAttemptAsync(
            challengeId,
            profileId,
            new FinishAttemptRequest(session.Id, session.Text, 0, 0, 10_000) { Nonce = session.Nonce },
            context.Attempts);
    }

    private sealed class ChallengeTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ChallengeTestContext(
            SqliteConnection connection,
            DbContextOptions<KeyWarsDbContext> options,
            KeyWarsDbContext db,
            ManualTimeProvider time,
            UserProfile creator,
            UserProfile invitee,
            TrainingText text)
        {
            this.connection = connection;
            Options = options;
            Db = db;
            Time = time;
            Creator = creator;
            Invitee = invitee;
            Text = text;
            Service = new ChallengeService(
                db,
                global::Microsoft.Extensions.Options.Options.Create(new ChallengeOptions()),
                time,
                attemptSessionStore: Sessions);
            Attempts = new AttemptService(db, new TypingEngine(time), new MotivationService(db, time), time, Sessions);
        }

        public KeyWarsDbContext Db { get; }
        public DbContextOptions<KeyWarsDbContext> Options { get; }
        public ManualTimeProvider Time { get; }
        public ChallengeService Service { get; }
        public AttemptService Attempts { get; }
        public AttemptSessionStore Sessions { get; } = new();
        public UserProfile Creator { get; }
        public UserProfile Invitee { get; }
        public TrainingText Text { get; }

        public static async Task<ChallengeTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var creator = Profile("creator", "Carla Creator");
            var invitee = Profile("invitee", "Iris Invitee");
            var text = new TrainingText
            {
                OwnerProfileId = creator.Id,
                Title = "Challenge-Text",
                SourceKey = "challenge-text",
                Body = "Text",
                Visibility = TrainingTextVisibility.Organization,
                IsStandard = false,
                RatingEligible = true,
                CharacterCount = TypingEngine.SplitGraphemes("Text").Count
            };
            db.UserProfiles.AddRange(creator, invitee);
            db.TrainingTexts.Add(text);
            await db.SaveChangesAsync();
            return new ChallengeTestContext(connection, options, db, new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z")), creator, invitee, text);
        }

        public TypingAttempt CreateAttempt(Guid profileId, Guid textId, TrainingMode mode, DateTimeOffset startedAt)
        {
            var attempt = new TypingAttempt
            {
                UserProfileId = profileId,
                TrainingTextId = textId,
                Mode = mode,
                Phase = AttemptPhase.Finished,
                Nonce = Guid.CreateVersion7().ToString("N")[..24],
                PreparedAt = startedAt,
                StartedAt = startedAt,
                FinishedAt = startedAt.AddSeconds(10),
                DurationMilliseconds = 10_000,
                CorrectCharacters = Text.CharacterCount,
                TotalCharacters = Text.CharacterCount,
                Wpm = 48,
                RawWpm = 48,
                CharactersPerMinute = 240,
                Accuracy = 100,
                Consistency = 100,
                Completed = true,
                Official = true,
                LeaderboardEligible = true
            };
            Db.TypingAttempts.Add(attempt);
            Db.SaveChanges();
            return attempt;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static UserProfile Profile(string account, string displayName) => new()
        {
            DisplayName = displayName,
            SamAccountName = account,
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{Guid.CreateVersion7():N}"
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
