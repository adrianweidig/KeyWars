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
}

public sealed class AttemptService(
    KeyWarsDbContext db,
    TypingEngine typingEngine,
    MotivationService motivationService,
    TimeProvider timeProvider,
    AttemptSessionStore sessionStore)
{
    public async Task<AttemptSession> StartAsync(Guid profileId, StartAttemptRequest request, CancellationToken cancellationToken = default)
    {
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

        var now = timeProvider.GetUtcNow();
        var serverDuration = now - session.StartedAt;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(request.ClientDurationMilliseconds, 1, (int)Math.Max(1, serverDuration.TotalMilliseconds + 2_000)));
        var timeMode = session.Mode is TrainingMode.Sprint15 or TrainingMode.Sprint30 or TrainingMode.Sprint60 or TrainingMode.Sprint120;
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
}
