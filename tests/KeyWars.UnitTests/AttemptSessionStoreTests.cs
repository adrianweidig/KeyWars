using KeyWars.Domain;
using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class AttemptSessionStoreTests
{
    [Fact]
    public async Task LifecycleLockSerializesSameAttemptButNotDifferentAttempts()
    {
        var store = new AttemptSessionStore();
        var attemptId = Guid.CreateVersion7();
        await using var first = await store.AcquireLifecycleLockAsync(attemptId);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            await using var lease = await store.AcquireLifecycleLockAsync(attemptId);
            secondEntered.SetResult();
        });

        await using (await store.AcquireLifecycleLockAsync(Guid.CreateVersion7()))
        {
        }

        Assert.False(secondEntered.Task.IsCompleted);
        await first.DisposeAsync();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await second;
    }

    [Fact]
    public void StoreUpdatesRemovesAndExpiresSessionsByReferenceTime()
    {
        var store = new AttemptSessionStore();
        var profileId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var prepared = new AttemptSession(
            Guid.CreateVersion7(),
            profileId,
            "alpha beta",
            TrainingMode.Text,
            now.AddHours(-3),
            null,
            "nonce-1",
            AttemptPhase.Prepared);
        var started = new AttemptSession(
            Guid.CreateVersion7(),
            profileId,
            "gamma delta",
            TrainingMode.Text,
            now.AddHours(-3),
            now.AddMinutes(-10),
            "nonce-2",
            AttemptPhase.Started);

        store.Add(prepared);
        store.Add(started);

        var updated = started with { Phase = AttemptPhase.Finished };
        Assert.True(store.TryUpdate(started, updated));
        Assert.True(store.TryGet(started.Id, out var currentStarted));
        Assert.Equal(AttemptPhase.Finished, currentStarted?.Phase);

        var expired = store.RemoveExpired(now, TimeSpan.FromHours(2));
        Assert.Single(expired);
        Assert.Equal(prepared.Id, expired[0].Id);
        Assert.False(store.TryGet(prepared.Id, out _));
        Assert.True(store.TryRemove(started.Id, out var removed));
        Assert.Equal(started.Id, removed?.Id);
    }

    [Fact]
    public void RemoveProfileAtomicallyReturnsOnlyRemovedProfileSessions()
    {
        var store = new AttemptSessionStore();
        var profileId = Guid.CreateVersion7();
        var otherProfileId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var first = Session(profileId, now, "nonce-1");
        var second = Session(profileId, now, "nonce-2");
        var other = Session(otherProfileId, now, "nonce-3");
        store.Add(first);
        store.Add(second);
        store.Add(other);

        var removed = store.RemoveProfile(profileId);

        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            removed.Select(item => item.Id).Order());
        Assert.False(store.TryGet(first.Id, out _));
        Assert.False(store.TryGet(second.Id, out _));
        Assert.True(store.TryGet(other.Id, out _));
        Assert.Empty(store.RemoveProfile(profileId));
    }

    private static AttemptSession Session(Guid profileId, DateTimeOffset preparedAt, string nonce) =>
        new(
            Guid.CreateVersion7(),
            profileId,
            "alpha beta",
            TrainingMode.Text,
            preparedAt,
            null,
            nonce,
            AttemptPhase.Prepared);
}
