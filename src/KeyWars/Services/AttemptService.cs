using System.Collections.Concurrent;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record StartAttemptRequest(TrainingMode Mode, Guid? TrainingTextId, int? SprintSeconds, int? WordCount);
public sealed record FinishAttemptRequest(Guid AttemptId, string Input, int Backspaces, int FocusLosses, int ClientDurationMilliseconds);
public sealed record AttemptSession(Guid Id, Guid UserProfileId, string Text, TrainingMode Mode, DateTimeOffset StartedAt, string Nonce);

public sealed class AttemptSessionStore
{
    private readonly ConcurrentDictionary<Guid, AttemptSession> sessions = new();

    public void Add(AttemptSession session) => sessions[session.Id] = session;

    public bool TryRemove(Guid id, out AttemptSession? session) => sessions.TryRemove(id, out session);

    public void RemoveExpired(DateTimeOffset now, TimeSpan lifetime)
    {
        foreach (var item in sessions)
        {
            if (now - item.Value.StartedAt > lifetime)
            {
                sessions.TryRemove(item.Key, out _);
            }
        }
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

    public async Task<AttemptSession> StartAsync(Guid profileId, StartAttemptRequest request, CancellationToken cancellationToken = default)
    {
        sessionStore.RemoveExpired(timeProvider.GetUtcNow(), SessionLifetime);
        var text = await ResolveTextAsync(profileId, request, cancellationToken);
        var start = typingEngine.Start(text);
        var session = new AttemptSession(start.AttemptId, profileId, start.Text, request.Mode, start.StartedAt, start.Nonce);
        sessionStore.Add(session);

        db.TypingAttempts.Add(new TypingAttempt
        {
            Id = session.Id,
            UserProfileId = profileId,
            TrainingTextId = request.TrainingTextId,
            Mode = request.Mode,
            Nonce = session.Nonce,
            StartedAt = session.StartedAt,
            Official = request.Mode != TrainingMode.Endless,
            LeaderboardEligible = request.Mode is not TrainingMode.Endless && request.TrainingTextId is not null
        });
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<TypingAttempt> FinishAsync(Guid profileId, FinishAttemptRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        sessionStore.RemoveExpired(now, SessionLifetime);
        sessionStore.TryRemove(request.AttemptId, out var session);
        var attempt = await db.TypingAttempts.SingleAsync(item => item.Id == request.AttemptId && item.UserProfileId == profileId, cancellationToken);
        if (session is null)
        {
            if (attempt.FinishedAt is not null)
            {
                return attempt;
            }

            throw new InvalidOperationException("Dieser Versuch ist nicht mehr aktiv.");
        }

        var serverDuration = now - session.StartedAt;
        if (serverDuration > SessionLifetime)
        {
            throw new InvalidOperationException("Dieser Versuch ist abgelaufen.");
        }

        var timeMode = session.Mode is TrainingMode.Sprint15 or TrainingMode.Sprint30 or TrainingMode.Sprint60 or TrainingMode.Sprint120;
        var duration = NormalizeServerDuration(serverDuration, session.Mode);
        var metrics = typingEngine.Analyze(session.Text, request.Input, duration, request.Backspaces, request.FocusLosses, timeMode);

        attempt.FinishedAt = now;
        attempt.DurationMilliseconds = metrics.DurationMilliseconds;
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
        attempt.Completed = metrics.Completed;

        await motivationService.ApplyAttemptAsync(profileId, attempt, session.Text, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
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
        var minimum = TimeSpan.FromSeconds(1);
        var bounded = serverDuration < minimum ? minimum : serverDuration;
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
}
