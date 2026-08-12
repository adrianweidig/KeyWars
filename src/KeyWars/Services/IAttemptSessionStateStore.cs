namespace KeyWars.Services;

public interface IAttemptSessionStateStore
{
    ValueTask AddAsync(AttemptSession session, TimeSpan lifetime, CancellationToken cancellationToken = default);
    ValueTask<AttemptSession?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask<bool> TryUpdateAsync(
        AttemptSession current,
        AttemptSession updated,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
    ValueTask<AttemptSession?> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AttemptSession>> RemoveProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireLifecycleLockAsync(
        Guid id,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<Guid>> GetExpiredIdsAsync(
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
    ValueTask<AttemptSession?> TryRemoveExpiredAsync(
        Guid id,
        DateTimeOffset now,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
}
