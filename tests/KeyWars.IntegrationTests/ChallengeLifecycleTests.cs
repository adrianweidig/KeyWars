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
        Assert.Equal(ChallengeStatus.Expired, await context.Db.Challenges.Where(item => item.Id == challenge.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(AttemptPhase.Aborted, await context.Db.TypingAttempts.Where(item => item.Id == pending.Id).Select(item => item.Phase).SingleAsync());
        Assert.Equal(AttemptPhase.Finished, await context.Db.TypingAttempts.Where(item => item.Id == session.Id).Select(item => item.Phase).SingleAsync());
        Assert.True(await context.Db.ChallengeAttemptBindings.Where(item => item.TypingAttemptId == session.Id).Select(item => item.Consumed).SingleAsync());
        Assert.Single(await context.Db.ChallengeRoundResults.Where(item => item.TypingAttemptId == session.Id).ToListAsync());
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
    public async Task CancelAbortsBoundAttemptRemovesSessionAndReplaysSafely()
    {
        await using var context = await ChallengeTestContext.CreateAsync();
        var challenge = await context.Service.CreateAsync(
            context.Creator.Id,
            new CreateChallengeRequest("Abbruch mit Versuch", context.Text.Id, ChallengeMode.Classic, [context.Invitee.Id], 1, 1));
        await context.Service.JoinAsync(challenge.Id, context.Invitee.Id);
        var session = await context.Service.StartAttemptAsync(challenge.Id, context.Invitee.Id, context.Attempts);
        await context.Attempts.BeginAsync(context.Invitee.Id, new BeginAttemptRequest(session.Id, session.Nonce));

        await context.Service.CancelAsync(challenge.Id, context.Creator.Id);
        await context.Service.CancelAsync(challenge.Id, context.Creator.Id);

        var attempt = await context.Db.TypingAttempts.AsNoTracking().SingleAsync(item => item.Id == session.Id);
        Assert.Equal(AttemptPhase.Aborted, attempt.Phase);
        Assert.NotNull(attempt.FinishedAt);
        Assert.Empty(await context.Db.ChallengeAttemptBindings.AsNoTracking().Where(item => item.TypingAttemptId == session.Id).ToListAsync());
        Assert.False(context.Sessions.TryGet(session.Id, out _));
        Assert.Empty(await context.Db.RewardLedgerEntries.AsNoTracking().Where(item => item.Source == "attempt" && item.SourceId == session.Id.ToString("N")).ToListAsync());
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
        var declineTask = Task.Run(() => decliner.DeclineAsync(challenge.Id, context.Invitee.Id));
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
