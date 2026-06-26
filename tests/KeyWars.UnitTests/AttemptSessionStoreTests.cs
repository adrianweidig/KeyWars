using KeyWars.Domain;
using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class AttemptSessionStoreTests
{
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
}
