using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

internal sealed class ChallengeAttemptTerminalizer(
    KeyWarsDbContext db,
    IAttemptSessionStateStore attemptSessions)
{
    public async Task<ChallengeAttemptTerminalization> PrepareAbortAsync(
        Guid challengeId,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        var bindings = await db.ChallengeAttemptBindings
            .Where(item => item.ChallengeId == challengeId && !item.Consumed)
            .OrderBy(item => item.TypingAttemptId)
            .ToListAsync(cancellationToken);
        if (bindings.Count == 0)
        {
            return new ChallengeAttemptTerminalization(attemptSessions, [], []);
        }

        var attemptIds = bindings.Select(item => item.TypingAttemptId).Distinct().Order().ToArray();
        var lifecycleLeases = new List<IOperationLease>(attemptIds.Length);
        try
        {
            foreach (var attemptId in attemptIds)
            {
                lifecycleLeases.Add(await attemptSessions.AcquireLifecycleLockAsync(attemptId, cancellationToken));
            }

            var leaseTokens = lifecycleLeases
                .Select(item => item.LeaseLost)
                .Prepend(cancellationToken)
                .ToArray();
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(leaseTokens);
            var operationToken = operationCancellation.Token;
            foreach (var attemptId in attemptIds)
            {
                await AttemptWriteFence.AcquireAsync(db, attemptId, operationToken);
            }

            var attempts = await db.TypingAttempts
                .Where(item => attemptIds.Contains(item.Id))
                .ToListAsync(operationToken);
            foreach (var attempt in attempts.Where(item => item.Phase is AttemptPhase.Prepared or AttemptPhase.Started))
            {
                attempt.Phase = AttemptPhase.Aborted;
                attempt.FinishedAt = finishedAt;
            }

            db.ChallengeAttemptBindings.RemoveRange(bindings);
            await db.SaveChangesAsync(operationToken);
            foreach (var lease in lifecycleLeases)
            {
                lease.ThrowIfLost();
            }

            return new ChallengeAttemptTerminalization(attemptSessions, attemptIds, lifecycleLeases);
        }
        catch
        {
            for (var index = lifecycleLeases.Count - 1; index >= 0; index--)
            {
                await lifecycleLeases[index].DisposeAsync();
            }

            throw;
        }
    }
}

internal sealed class ChallengeAttemptTerminalization(
    IAttemptSessionStateStore attemptSessions,
    IReadOnlyList<Guid> attemptIds,
    IReadOnlyList<IOperationLease> lifecycleLeases) : IAsyncDisposable
{
    public IReadOnlyList<Guid> AttemptIds { get; } = attemptIds;

    public void ThrowIfLost()
    {
        foreach (var lease in lifecycleLeases)
        {
            lease.ThrowIfLost();
        }
    }

    public async Task RemoveSessionsBestEffortAsync()
    {
        foreach (var attemptId in AttemptIds)
        {
            try
            {
                await attemptSessions.RemoveAsync(attemptId, CancellationToken.None);
            }
            catch
            {
                // The terminal database phase is authoritative; Redis state expires independently.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        for (var index = lifecycleLeases.Count - 1; index >= 0; index--)
        {
            await lifecycleLeases[index].DisposeAsync();
        }
    }
}

internal sealed class ChallengeTransactionContext(ChallengeAttemptTerminalizer terminalizer) : IAsyncDisposable
{
    private readonly List<ChallengeAttemptTerminalization> terminalizations = [];

    public async Task AbortBoundAttemptsAsync(
        Guid challengeId,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        terminalizations.Add(await terminalizer.PrepareAbortAsync(challengeId, finishedAt, cancellationToken));
    }

    public void ThrowIfLost()
    {
        foreach (var terminalization in terminalizations)
        {
            terminalization.ThrowIfLost();
        }
    }

    public async Task CompleteAsync()
    {
        foreach (var terminalization in terminalizations)
        {
            await terminalization.RemoveSessionsBestEffortAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        for (var index = terminalizations.Count - 1; index >= 0; index--)
        {
            await terminalizations[index].DisposeAsync();
        }
    }
}
