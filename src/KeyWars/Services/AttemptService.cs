using System.Security.Cryptography;
using System.Text;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed class AttemptService(
    KeyWarsDbContext db,
    TypingEngine typingEngine,
    MotivationService motivationService,
    TimeProvider timeProvider,
    AttemptSessionStore sessionStore)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1);
    private const int BadRequestStatus = 400;
    private const int NotFoundStatus = 404;
    private const int ConflictStatus = 409;
    private const int GoneStatus = 410;
    private const int MaxInputOverrunCharacters = 20;
    private const int MaxTimingSamples = 200;
    private const int MaxPersistedErrors = 200;
    private const int SprintTargetWordCount = 120;
    private const int DefaultGeneratedWordCount = 80;

    public async Task<AttemptSession> StartAsync(Guid profileId, StartAttemptRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireSessionsAsync(now, cancellationToken);
        ValidateStartRequest(request);
        var resolvedText = await ResolveTextAsync(profileId, request, cancellationToken);
        var start = typingEngine.Start(resolvedText.Body);
        var session = new AttemptSession(start.AttemptId, profileId, start.Text, request.Mode, start.StartedAt, null, start.Nonce, AttemptPhase.Prepared);
        await using var lifecycleLock = await sessionStore.AcquireLifecycleLockAsync(session.Id, cancellationToken);
        db.TypingAttempts.Add(new TypingAttempt
        {
            Id = session.Id,
            UserProfileId = profileId,
            TrainingTextId = resolvedText.Text?.Id,
            Mode = request.Mode,
            Phase = AttemptPhase.Prepared,
            Nonce = session.Nonce,
            TextHash = ComputeTextHash(session.Text),
            PreparedAt = session.PreparedAt,
            StartedAt = session.PreparedAt,
            Official = request.Mode != TrainingMode.Endless,
            LeaderboardEligible = CompetitionEligibility.CanEnterLeaderboardAtStart(request.Mode, resolvedText.Text)
        });
        await db.SaveChangesAsync(cancellationToken);
        sessionStore.Add(session);
        return session;
    }

    public async Task<AttemptSession?> TryGetActiveSessionAsync(Guid profileId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireSessionsAsync(now, cancellationToken);
        await using var lifecycleLock = await sessionStore.AcquireLifecycleLockAsync(attemptId, cancellationToken);
        var attempt = await db.TypingAttempts.SingleOrDefaultAsync(item => item.Id == attemptId && item.UserProfileId == profileId, cancellationToken);
        if (attempt is null ||
            attempt.FinishedAt is not null ||
            attempt.Phase is AttemptPhase.Finished or AttemptPhase.Expired or AttemptPhase.Aborted)
        {
            return null;
        }

        if (!sessionStore.TryGet(attemptId, out var session) || session is null)
        {
            return null;
        }

        ValidateSession(profileId, attempt.Nonce, session);
        return session.Phase is AttemptPhase.Prepared or AttemptPhase.Started ? session : null;
    }

    public async Task<AttemptBeginResponse> BeginAsync(Guid profileId, BeginAttemptRequest request, CancellationToken cancellationToken = default)
    {
        await ExpireSessionsAsync(timeProvider.GetUtcNow(), cancellationToken);
        await using var lifecycleLock = await sessionStore.AcquireLifecycleLockAsync(request.AttemptId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var attempt = await db.TypingAttempts.SingleOrDefaultAsync(item => item.Id == request.AttemptId && item.UserProfileId == profileId, cancellationToken)
            ?? throw AttemptError(AttemptErrorCodes.NotFound, NotFoundStatus, "Dieser Versuch wurde nicht gefunden.");
        ValidateNonce(attempt.Nonce, request.Nonce);

        if (attempt.FinishedAt is not null)
        {
            return BuildBeginResponse(attempt.Id, attempt.StartedAt, attempt.Mode, now);
        }

        if (attempt.Phase is AttemptPhase.Expired or AttemptPhase.Aborted)
        {
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch ist abgelaufen.");
        }

        if (!sessionStore.TryGet(request.AttemptId, out var session) || session is null)
        {
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch ist nicht mehr aktiv.");
        }

        ValidateSession(profileId, request.Nonce, session);
        if (session.Phase == AttemptPhase.Started && session.StartedAt is { } existingStartedAt)
        {
            return BuildBeginResponse(session.Id, existingStartedAt, session.Mode, now);
        }

        if (session.Phase != AttemptPhase.Prepared)
        {
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch kann nicht mehr gestartet werden.");
        }

        var started = session with { Phase = AttemptPhase.Started, StartedAt = now };
        if (!sessionStore.TryUpdate(session, started))
        {
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch ist nicht mehr aktiv.");
        }

        var previousPhase = attempt.Phase;
        var previousStartedAt = attempt.StartedAt;
        attempt.Phase = AttemptPhase.Started;
        attempt.StartedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            attempt.Phase = previousPhase;
            attempt.StartedAt = previousStartedAt;
            sessionStore.TryUpdate(started, session);
            throw;
        }

        return BuildBeginResponse(started.Id, now, started.Mode, timeProvider.GetUtcNow());
    }

    public async Task<AttemptCompletion> FinishAsync(Guid profileId, FinishAttemptRequest request, CancellationToken cancellationToken = default)
    {
        await ExpireSessionsAsync(timeProvider.GetUtcNow(), cancellationToken);
        await using var lifecycleLock = await sessionStore.AcquireLifecycleLockAsync(request.AttemptId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var attempt = await db.TypingAttempts.SingleOrDefaultAsync(item => item.Id == request.AttemptId && item.UserProfileId == profileId, cancellationToken)
            ?? throw AttemptError(AttemptErrorCodes.NotFound, NotFoundStatus, "Dieser Versuch wurde nicht gefunden.");
        ValidateNonce(attempt.Nonce, request.Nonce);

        if (attempt.FinishedAt is not null && attempt.Phase == AttemptPhase.Finished)
        {
            var completion = await BuildPersistedCompletionAsync(profileId, attempt, cancellationToken);
            sessionStore.TryRemove(request.AttemptId, out _);
            return completion;
        }

        if (attempt.Phase is AttemptPhase.Expired or AttemptPhase.Aborted)
        {
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch ist abgelaufen.");
        }

        if (!sessionStore.TryGet(request.AttemptId, out var session) || session is null)
        {
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch ist nicht mehr aktiv.");
        }

        ValidateSession(profileId, request.Nonce, session);
        if (session.Phase != AttemptPhase.Started || session.StartedAt is not { } startedAt)
        {
            throw AttemptError(AttemptErrorCodes.NotStarted, ConflictStatus, "Dieser Versuch wurde noch nicht gestartet.");
        }

        var serverDuration = now - startedAt;
        if (serverDuration > SessionLifetime)
        {
            attempt.Phase = AttemptPhase.Expired;
            await db.SaveChangesAsync(cancellationToken);
            sessionStore.TryRemove(request.AttemptId, out _);
            throw AttemptError(AttemptErrorCodes.Expired, GoneStatus, "Dieser Versuch ist abgelaufen.");
        }

        var sprintDuration = GetSprintDuration(session.Mode);
        var timeMode = sprintDuration is not null;
        var duration = NormalizeServerDuration(serverDuration, session.Mode);
        var input = NormalizeBoundedInput(session.Text, request.Input);
        var wordDurations = NormalizeTimingSamples(request.WordDurationsMilliseconds);
        if (sprintDuration is { } limit && now < startedAt + limit && !IsExactNormalizedGraphemeSequence(session.Text, input))
        {
            var retryAfter = (int)Math.Clamp(Math.Ceiling((startedAt + limit - now).TotalMilliseconds), 1, int.MaxValue);
            throw AttemptError(
                AttemptErrorCodes.StillRunning,
                ConflictStatus,
                "Der Sprint läuft noch.",
                retryAfter);
        }

        var metrics = typingEngine.Analyze(session.Text, input, duration, request.Backspaces, request.FocusLosses, timeMode, wordDurations);
        if (!timeMode && !metrics.Completed)
        {
            throw AttemptError(AttemptErrorCodes.StillRunning, ConflictStatus, "Der Zieltext ist noch nicht fehlerfrei abgeschlossen.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            attempt.Phase = AttemptPhase.Finished;
            attempt.StartedAt = startedAt;
            attempt.FinishedAt = now;
            attempt.DurationMilliseconds = metrics.DurationMilliseconds;
            attempt.ClientDurationMilliseconds = NormalizeClientDuration(request.ClientDurationMilliseconds);
            attempt.CorrectCharacters = metrics.CorrectCharacters;
            attempt.IncorrectCharacters = metrics.IncorrectCharacters;
            attempt.Backspaces = metrics.Backspaces;
            attempt.FocusLosses = metrics.FocusLosses;
            attempt.TotalCharacters = metrics.TotalCharacters;
            attempt.Wpm = metrics.Wpm;
            attempt.RawWpm = metrics.RawWpm;
            attempt.CharactersPerMinute = metrics.CharactersPerMinute;
            attempt.Accuracy = metrics.Accuracy;
            attempt.Consistency = metrics.Consistency;
            attempt.ConsistencySampleCount = metrics.ConsistencySampleCount;
            attempt.MeanWordMilliseconds = metrics.MeanWordMilliseconds;
            attempt.WordTimingVariation = metrics.WordTimingVariation;
            attempt.Completed = metrics.Completed;

            PersistErrors(profileId, attempt.Id, metrics.Errors);
            await motivationService.ApplyAttemptAsync(profileId, attempt, metrics.Errors, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
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
                // Preserve the original persistence failure.
            }

            db.ChangeTracker.Clear();
            throw;
        }

        sessionStore.TryRemove(request.AttemptId, out _);
        return await BuildPersistedCompletionAsync(profileId, attempt, cancellationToken);
    }

    private async Task<ResolvedAttemptText> ResolveTextAsync(Guid profileId, StartAttemptRequest request, CancellationToken cancellationToken)
    {
        if (GetFixedWordCount(request.Mode) is { } fixedWordCount)
        {
            return new ResolvedAttemptText(TypingEngine.BuildWordTest(fixedWordCount), null);
        }

        if (GetSprintDuration(request.Mode) is not null)
        {
            return new ResolvedAttemptText(TypingEngine.BuildWordTest(SprintTargetWordCount), null);
        }

        if (IsStoredTextMode(request.Mode) && request.TrainingTextId is { } textId)
        {
            var text = await db.TrainingTexts.SingleOrDefaultAsync(item =>
                item.Id == textId && (item.IsStandard || item.Visibility == TrainingTextVisibility.Organization || item.OwnerProfileId == profileId), cancellationToken)
                ?? throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Der Trainingstext ist ungültig.");
            return new ResolvedAttemptText(text.Body, text);
        }

        if (request.Mode == TrainingMode.WeaknessFocus)
        {
            var observations = await db.WeaknessObservations
                .Where(item => item.UserProfileId == profileId)
                .ToListAsync(cancellationToken);
            return new ResolvedAttemptText(typingEngine.BuildWeaknessText(observations), null);
        }

        return new ResolvedAttemptText(TypingEngine.BuildWordTest(DefaultGeneratedWordCount), null);
    }

    private sealed record ResolvedAttemptText(string Body, TrainingText? Text);

    private static TimeSpan NormalizeServerDuration(TimeSpan serverDuration, TrainingMode mode)
    {
        var bounded = serverDuration < MinimumDuration ? MinimumDuration : serverDuration;
        var sprintLimit = GetSprintDuration(mode);

        return sprintLimit is { } limit && bounded > limit ? limit : bounded;
    }

    private async Task ExpireSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var id in sessionStore.GetExpiredIds(now, SessionLifetime))
        {
            await using var lifecycleLock = await sessionStore.AcquireLifecycleLockAsync(id, cancellationToken);
            if (!sessionStore.TryRemoveExpired(id, now, SessionLifetime, out var expiredSession) || expiredSession is null)
            {
                continue;
            }

            var attempt = await db.TypingAttempts.SingleOrDefaultAsync(
                item => item.Id == id && item.FinishedAt == null,
                cancellationToken);
            if (attempt is null || attempt.Phase is AttemptPhase.Expired or AttemptPhase.Aborted)
            {
                continue;
            }

            var previousPhase = attempt.Phase;
            attempt.Phase = AttemptPhase.Expired;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                attempt.Phase = previousPhase;
                sessionStore.Add(expiredSession);
                throw;
            }
        }
    }

    private static void ValidateStartRequest(StartAttemptRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Der Trainingsmodus ist ungültig.");
        }

        if (request.SprintSeconds is < 0 or > 120)
        {
            throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Die Sprintdauer ist ungültig.");
        }

        if (GetFixedWordCount(request.Mode) is { } fixedWordCount)
        {
            RejectTrainingText(request);
            RequireCompatibleWordCount(request, fixedWordCount);
            RejectNonzeroSprintSeconds(request);
            return;
        }

        if (GetSprintDuration(request.Mode) is not null)
        {
            RejectTrainingText(request);
            RequireCompatibleWordCount(request, SprintTargetWordCount);
            return;
        }

        if (IsStoredTextMode(request.Mode))
        {
            if (request.TrainingTextId is null)
            {
                throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Für diesen Trainingsmodus ist ein Trainingstext erforderlich.");
            }

            RequireCompatibleWordCount(request, DefaultGeneratedWordCount);
            RejectNonzeroSprintSeconds(request);
            return;
        }

        RejectTrainingText(request);
        RequireCompatibleWordCount(request, DefaultGeneratedWordCount);
        RejectNonzeroSprintSeconds(request);
    }

    private static void RejectTrainingText(StartAttemptRequest request)
    {
        if (request.TrainingTextId is not null)
        {
            throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Ein gespeicherter Trainingstext ist für diesen Modus nicht zulässig.");
        }
    }

    private static void RequireCompatibleWordCount(StartAttemptRequest request, int expectedWordCount)
    {
        if (request.WordCount is { } wordCount && wordCount != expectedWordCount)
        {
            throw AttemptError(
                AttemptErrorCodes.InvalidRequest,
                BadRequestStatus,
                $"Für diesen Modus ist ausschließlich die serverseitige Wortzahl {expectedWordCount} zulässig.");
        }
    }

    private static void RejectNonzeroSprintSeconds(StartAttemptRequest request)
    {
        if (request.SprintSeconds is not null and not 0)
        {
            throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Eine Sprintdauer ist für diesen Modus nicht zulässig.");
        }
    }

    private static string NormalizeBoundedInput(string targetText, string input)
    {
        var normalized = TypingEngine.NormalizeText(input);
        var targetLength = TypingEngine.SplitGraphemes(targetText).Count;
        var inputLength = TypingEngine.SplitGraphemes(normalized).Count;
        if (inputLength > targetLength + MaxInputOverrunCharacters)
        {
            throw AttemptError(AttemptErrorCodes.InvalidRequest, BadRequestStatus, "Die Eingabe ist zu lang.");
        }

        return normalized;
    }

    private static int NormalizeClientDuration(int clientDurationMilliseconds)
    {
        if (clientDurationMilliseconds <= 0)
        {
            return 0;
        }

        return Math.Min(clientDurationMilliseconds, (int)SessionLifetime.TotalMilliseconds);
    }

    private static IReadOnlyList<int> NormalizeTimingSamples(IReadOnlyList<int>? samples)
    {
        return (samples ?? [])
            .Where(value => value > 0)
            .Take(MaxTimingSamples)
            .Select(value => Math.Min(value, (int)SessionLifetime.TotalMilliseconds))
            .ToArray();
    }

    private void PersistErrors(Guid profileId, Guid attemptId, IReadOnlyList<TypingError> errors)
    {
        foreach (var error in errors.Take(MaxPersistedErrors))
        {
            db.TypingAttemptErrors.Add(new TypingAttemptError
            {
                TypingAttemptId = attemptId,
                UserProfileId = profileId,
                Position = error.Position,
                Kind = error.Kind,
                Expected = error.Expected,
                Actual = error.Actual,
                Pattern = error.Pattern,
                CreatedAt = timeProvider.GetUtcNow()
            });
        }
    }

    private static void ValidateSession(Guid profileId, string nonce, AttemptSession session)
    {
        if (session.UserProfileId != profileId)
        {
            throw AttemptError(AttemptErrorCodes.NotFound, NotFoundStatus, "Dieser Versuch wurde nicht gefunden.");
        }

        ValidateNonce(session.Nonce, nonce);
    }

    private static void ValidateNonce(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            throw AttemptError(AttemptErrorCodes.InvalidNonce, ConflictStatus, "Der Versuchsschlüssel ist ungültig.");
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw AttemptError(AttemptErrorCodes.InvalidNonce, ConflictStatus, "Der Versuchsschlüssel ist ungültig.");
        }
    }

    private async Task<AttemptCompletion> BuildPersistedCompletionAsync(
        Guid profileId,
        TypingAttempt attempt,
        CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.SingleAsync(item => item.Id == profileId, cancellationToken);
        var sourceId = attempt.Id.ToString("N");
        var anchor = await db.GamificationEvents
            .Where(item =>
                item.UserProfileId == profileId &&
                item.Source == "attempt" &&
                item.SourceId == sourceId &&
                item.EventKey == "xp-awarded")
            .SingleOrDefaultAsync(cancellationToken);
        if (anchor is null)
        {
            return new AttemptCompletion(attempt, MotivationOutcome.Empty(profile));
        }

        var candidates = await db.GamificationEvents
            .FromSqlInterpolated($"""
                SELECT *
                FROM GamificationEvents
                WHERE UserProfileId = {profileId.ToString().ToUpperInvariant()}
                  AND CreatedAt = {anchor.CreatedAt}
                ORDER BY rowid
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var anchorIndex = candidates.FindIndex(item => item.Id == anchor.Id);
        if (anchorIndex < 0)
        {
            return new AttemptCompletion(attempt, MotivationOutcome.Empty(profile));
        }

        var outcomeCandidates = candidates.Skip(anchorIndex).ToList();
        var nextOutcome = outcomeCandidates.FindIndex(1, item =>
            item.Type == GamificationEventType.XpAwarded &&
            item.EventKey == "xp-awarded");
        var events = nextOutcome < 0 ? outcomeCandidates : outcomeCandidates.Take(nextOutcome).ToList();
        var progress = MotivationService.GetLevelProgress(profile.ExperiencePoints);
        var motivation = new MotivationOutcome(
            events.Sum(item => item.XpDelta),
            anchor.LevelBefore,
            anchor.LevelAfter,
            progress.ProgressPercent,
            events);
        return new AttemptCompletion(attempt, motivation);
    }

    private static AttemptBeginResponse BuildBeginResponse(
        Guid attemptId,
        DateTimeOffset startedAt,
        TrainingMode mode,
        DateTimeOffset serverNow)
    {
        var sprintDuration = GetSprintDuration(mode);
        return new AttemptBeginResponse(attemptId, startedAt, sprintDuration is { } duration ? startedAt + duration : null, serverNow);
    }

    private static TimeSpan? GetSprintDuration(TrainingMode mode) => mode switch
    {
        TrainingMode.Sprint15 => TimeSpan.FromSeconds(15),
        TrainingMode.Sprint30 => TimeSpan.FromSeconds(30),
        TrainingMode.Sprint60 => TimeSpan.FromSeconds(60),
        TrainingMode.Sprint120 => TimeSpan.FromSeconds(120),
        _ => null
    };

    private static int? GetFixedWordCount(TrainingMode mode) => mode switch
    {
        TrainingMode.Words10 => 10,
        TrainingMode.Words25 => 25,
        TrainingMode.Words50 => 50,
        TrainingMode.Words100 => 100,
        _ => null
    };

    private static bool IsStoredTextMode(TrainingMode mode) =>
        mode is TrainingMode.Text or TrainingMode.Ghost or TrainingMode.RivalGhost;

    private static bool IsExactNormalizedGraphemeSequence(string target, string input)
    {
        var targetGraphemes = TypingEngine.SplitGraphemes(TypingEngine.NormalizeText(target));
        var inputGraphemes = TypingEngine.SplitGraphemes(TypingEngine.NormalizeText(input));
        return targetGraphemes.SequenceEqual(inputGraphemes, StringComparer.Ordinal);
    }

    private static AttemptLifecycleException AttemptError(
        string code,
        int statusCode,
        string message,
        int? retryAfterMs = null) =>
        new(code, statusCode, message, retryAfterMs);

    private static string ComputeTextHash(string text)
    {
        var normalized = TypingEngine.NormalizeText(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
