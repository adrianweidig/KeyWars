using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record StartAttemptRequest(TrainingMode Mode, Guid? TrainingTextId, int? SprintSeconds, int? WordCount);
public sealed record BeginAttemptRequest(Guid AttemptId, string Nonce);
public sealed record AttemptBeginResponse(Guid AttemptId, DateTimeOffset StartedAt);
public sealed record FinishAttemptRequest(Guid AttemptId, string Input, int Backspaces, int FocusLosses, int ClientDurationMilliseconds)
{
    public string Nonce { get; init; } = "";
    public IReadOnlyList<int>? WordDurationsMilliseconds { get; init; } = [];
}

public sealed record AttemptSession(
    Guid Id,
    Guid UserProfileId,
    string Text,
    TrainingMode Mode,
    DateTimeOffset PreparedAt,
    DateTimeOffset? StartedAt,
    string Nonce,
    AttemptPhase Phase);

public sealed class AttemptSessionStore
{
    private readonly ConcurrentDictionary<Guid, AttemptSession> sessions = new();

    public void Add(AttemptSession session) => sessions[session.Id] = session;

    public bool TryGet(Guid id, out AttemptSession? session) => sessions.TryGetValue(id, out session);

    public bool TryUpdate(AttemptSession current, AttemptSession updated) => sessions.TryUpdate(current.Id, updated, current);

    public bool TryRemove(Guid id, out AttemptSession? session) => sessions.TryRemove(id, out session);

    public IReadOnlyList<AttemptSession> RemoveExpired(DateTimeOffset now, TimeSpan lifetime)
    {
        var expired = new List<AttemptSession>();
        foreach (var item in sessions)
        {
            var reference = item.Value.StartedAt ?? item.Value.PreparedAt;
            if (now - reference > lifetime && sessions.TryRemove(item.Key, out var session))
            {
                expired.Add(session);
            }
        }

        return expired;
    }
}

public sealed class AttemptService(
    KeyWarsDbContext db,
    TypingEngine typingEngine,
    MotivationService motivationService,
    TimeProvider timeProvider,
    AttemptSessionStore sessionStore)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(1);
    private const int MaxInputOverrunCharacters = 20;
    private const int MaxTimingSamples = 200;
    private const int MaxPersistedErrors = 200;

    public async Task<AttemptSession> StartAsync(Guid profileId, StartAttemptRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireSessionsAsync(now, cancellationToken);
        ValidateStartRequest(request);
        var text = await ResolveTextAsync(profileId, request, cancellationToken);
        var start = typingEngine.Start(text);
        var session = new AttemptSession(start.AttemptId, profileId, start.Text, request.Mode, start.StartedAt, null, start.Nonce, AttemptPhase.Prepared);
        db.TypingAttempts.Add(new TypingAttempt
        {
            Id = session.Id,
            UserProfileId = profileId,
            TrainingTextId = request.TrainingTextId,
            Mode = request.Mode,
            Phase = AttemptPhase.Prepared,
            Nonce = session.Nonce,
            TextHash = ComputeTextHash(session.Text),
            PreparedAt = session.PreparedAt,
            StartedAt = session.PreparedAt,
            Official = request.Mode != TrainingMode.Endless,
            LeaderboardEligible = request.Mode is not TrainingMode.Endless && request.TrainingTextId is not null
        });
        await db.SaveChangesAsync(cancellationToken);
        sessionStore.Add(session);
        return session;
    }

    public async Task<AttemptBeginResponse> BeginAsync(Guid profileId, BeginAttemptRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireSessionsAsync(now, cancellationToken);
        var attempt = await db.TypingAttempts.SingleAsync(item => item.Id == request.AttemptId && item.UserProfileId == profileId, cancellationToken);
        ValidateNonce(attempt.Nonce, request.Nonce);

        if (attempt.FinishedAt is not null)
        {
            return new AttemptBeginResponse(attempt.Id, attempt.StartedAt);
        }

        while (true)
        {
            if (!sessionStore.TryGet(request.AttemptId, out var session) || session is null)
            {
                throw new InvalidOperationException("Dieser Versuch ist nicht mehr aktiv.");
            }

            ValidateSession(profileId, request.Nonce, session);
            if (session.Phase == AttemptPhase.Started && session.StartedAt is { } existingStartedAt)
            {
                return new AttemptBeginResponse(session.Id, existingStartedAt);
            }

            if (session.Phase != AttemptPhase.Prepared)
            {
                throw new InvalidOperationException("Dieser Versuch kann nicht mehr gestartet werden.");
            }

            var started = session with { Phase = AttemptPhase.Started, StartedAt = now };
            if (!sessionStore.TryUpdate(session, started))
            {
                continue;
            }

            attempt.Phase = AttemptPhase.Started;
            attempt.StartedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new AttemptBeginResponse(started.Id, now);
        }
    }

    public async Task<TypingAttempt> FinishAsync(Guid profileId, FinishAttemptRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireSessionsAsync(now, cancellationToken);
        var attempt = await db.TypingAttempts.SingleAsync(item => item.Id == request.AttemptId && item.UserProfileId == profileId, cancellationToken);
        ValidateNonce(attempt.Nonce, request.Nonce);

        if (!sessionStore.TryGet(request.AttemptId, out var session) || session is null)
        {
            if (attempt.FinishedAt is not null)
            {
                return attempt;
            }

            throw new InvalidOperationException("Dieser Versuch ist nicht mehr aktiv.");
        }

        ValidateSession(profileId, request.Nonce, session);
        if (session.Phase != AttemptPhase.Started || session.StartedAt is not { } startedAt)
        {
            throw new InvalidOperationException("Dieser Versuch wurde noch nicht gestartet.");
        }

        var serverDuration = now - startedAt;
        if (serverDuration > SessionLifetime)
        {
            attempt.Phase = AttemptPhase.Expired;
            await db.SaveChangesAsync(cancellationToken);
            sessionStore.TryRemove(request.AttemptId, out _);
            throw new InvalidOperationException("Dieser Versuch ist abgelaufen.");
        }

        var timeMode = session.Mode is TrainingMode.Sprint15 or TrainingMode.Sprint30 or TrainingMode.Sprint60 or TrainingMode.Sprint120;
        var duration = NormalizeServerDuration(serverDuration, session.Mode);
        var input = NormalizeBoundedInput(session.Text, request.Input);
        var wordDurations = NormalizeTimingSamples(request.WordDurationsMilliseconds);
        var metrics = typingEngine.Analyze(session.Text, input, duration, request.Backspaces, request.FocusLosses, timeMode, wordDurations);
        if (!timeMode && !metrics.Completed)
        {
            throw new InvalidOperationException("Der Zieltext ist noch nicht fehlerfrei abgeschlossen.");
        }

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
        sessionStore.TryRemove(request.AttemptId, out _);
        return attempt;
    }

    private async Task<string> ResolveTextAsync(Guid profileId, StartAttemptRequest request, CancellationToken cancellationToken)
    {
        if (request.TrainingTextId is { } textId)
        {
            var text = await db.TrainingTexts.SingleAsync(item =>
                item.Id == textId && (item.IsStandard || item.Visibility == TrainingTextVisibility.Organization || item.OwnerProfileId == profileId), cancellationToken);
            return text.Body;
        }

        if (request.WordCount is { } wordCount)
        {
            if (wordCount is < 1 or > 200)
            {
                throw new InvalidOperationException("Die Wortzahl muss zwischen 1 und 200 liegen.");
            }

            return TypingEngine.BuildWordTest(wordCount);
        }

        if (request.Mode == TrainingMode.WeaknessFocus)
        {
            var observations = await db.WeaknessObservations
                .Where(item => item.UserProfileId == profileId)
                .ToListAsync(cancellationToken);
            return typingEngine.BuildWeaknessText(observations);
        }

        return TypingEngine.BuildWordTest(80);
    }

    private static TimeSpan NormalizeServerDuration(TimeSpan serverDuration, TrainingMode mode)
    {
        var bounded = serverDuration < MinimumDuration ? MinimumDuration : serverDuration;
        var sprintLimit = mode switch
        {
            TrainingMode.Sprint15 => TimeSpan.FromSeconds(15),
            TrainingMode.Sprint30 => TimeSpan.FromSeconds(30),
            TrainingMode.Sprint60 => TimeSpan.FromSeconds(60),
            TrainingMode.Sprint120 => TimeSpan.FromSeconds(120),
            _ => (TimeSpan?)null
        };

        return sprintLimit is { } limit && bounded > limit ? limit : bounded;
    }

    private async Task ExpireSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = sessionStore.RemoveExpired(now, SessionLifetime);
        if (expired.Count == 0)
        {
            return;
        }

        var ids = expired.Select(session => session.Id).ToArray();
        var attempts = await db.TypingAttempts
            .Where(attempt => ids.Contains(attempt.Id) && attempt.FinishedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var attempt in attempts)
        {
            attempt.Phase = AttemptPhase.Expired;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateStartRequest(StartAttemptRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new InvalidOperationException("Der Trainingsmodus ist ungültig.");
        }

        if (request.SprintSeconds is < 0 or > 120)
        {
            throw new InvalidOperationException("Die Sprintdauer ist ungültig.");
        }

        var expectedSprintSeconds = request.Mode switch
        {
            TrainingMode.Sprint15 => 15,
            TrainingMode.Sprint30 => 30,
            TrainingMode.Sprint60 => 60,
            TrainingMode.Sprint120 => 120,
            _ => 0
        };
        if (expectedSprintSeconds > 0 && request.SprintSeconds is { } sprintSeconds && sprintSeconds != expectedSprintSeconds)
        {
            throw new InvalidOperationException("Die Sprintdauer passt nicht zum Trainingsmodus.");
        }

        if (request.WordCount is < 1 or > 200)
        {
            throw new InvalidOperationException("Die Wortzahl muss zwischen 1 und 200 liegen.");
        }
    }

    private static string NormalizeBoundedInput(string targetText, string input)
    {
        var normalized = TypingEngine.NormalizeText(input);
        var targetLength = TypingEngine.SplitGraphemes(targetText).Count;
        var inputLength = TypingEngine.SplitGraphemes(normalized).Count;
        if (inputLength > targetLength + MaxInputOverrunCharacters)
        {
            throw new InvalidOperationException("Die Eingabe ist zu lang.");
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
            throw new InvalidOperationException("Dieser Versuch ist nicht mehr aktiv.");
        }

        ValidateNonce(session.Nonce, nonce);
    }

    private static void ValidateNonce(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            throw new InvalidOperationException("Der Versuchsschlüssel ist ungültig.");
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new InvalidOperationException("Der Versuchsschlüssel ist ungültig.");
        }
    }

    private static string ComputeTextHash(string text)
    {
        var normalized = TypingEngine.NormalizeText(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
