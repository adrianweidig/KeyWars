using KeyWars.Services;
using KeyWars.Domain;
using KeyWars.Infrastructure.Cluster;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class LivePresenceHeartbeatTests
{
    [Fact]
    public async Task RefreshConnectionKeepsOnlyTheCurrentRoomConnectionAlive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var presence = new LivePresenceTracker(Options.Create(new LiveOptions()), time);
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();

        await presence.EnterRoomAsync(profileId, "connection", roomId);
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await presence.RefreshConnectionAsync(profileId, "connection", roomId));
        Assert.False(await presence.RefreshConnectionAsync(profileId, "connection", Guid.CreateVersion7()));
        Assert.False(await presence.RefreshConnectionAsync(profileId, "other", roomId));
        Assert.Equal(1, await presence.CountRoomConnectionsAsync(profileId, roomId));
    }

    [Fact]
    public void ClusterPresenceHasAHeartbeatLeaseAndBoundedExpiryWorker()
    {
        var storeSource = File.ReadAllText(FindSource("Infrastructure/Cluster/RedisLivePresenceStateStore.cs"));
        var workerSource = File.ReadAllText(FindSource("Infrastructure/Cluster/RedisLivePresenceExpiryService.cs"));
        var browserSource = File.ReadAllText(FindSource("wwwroot/js/arena.js"));

        Assert.Contains("ConnectionLifetime = TimeSpan.FromMinutes(3)", storeSource, StringComparison.Ordinal);
        Assert.Contains("ClaimExpiredConnectionsScript", storeSource, StringComparison.Ordinal);
        Assert.Contains("CompleteExpiryClaimScript", storeSource, StringComparison.Ordinal);
        Assert.Contains("ClaimsPerBucket = 16", workerSource, StringComparison.Ordinal);
        Assert.Contains("accessGate.AcquireAsync(claim.ProfileId", workerSource, StringComparison.Ordinal);
        Assert.Contains("ExecuteIfRoomEmptyAsync", workerSource, StringComparison.Ordinal);
        Assert.True(
            workerSource.IndexOf("ExecuteIfRoomEmptyAsync", StringComparison.Ordinal) <
            workerSource.IndexOf("CompleteExpiryClaimAsync(claim, operationToken)", StringComparison.Ordinal));
        Assert.Contains("AcquireProfileLockAsync(claim.ProfileId", storeSource, StringComparison.Ordinal);
        Assert.Contains("DisconnectAsync", workerSource, StringComparison.Ordinal);
        Assert.Contains("presenceHeartbeatIntervalMilliseconds = 15000", browserSource, StringComparison.Ordinal);
        Assert.Contains("connection.invoke(\"Heartbeat\"", browserSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredConnectionDisconnectsOnlyWhenNoRefreshedOrReplacementConnectionExists()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));

        var expired = CreateRunningRoom(time);
        var expiry = new FakeExpiryStore { CompleteResult = true };
        var presence = new LivePresenceTracker(Options.Create(new LiveOptions()), time);
        var worker = CreateExpiryWorker(expired.Manager, expiry, presence, time);
        await worker.ProcessClaimAsync(
            new ExpiredPresenceClaim("old", expired.ProfileId, expired.RoomId),
            CancellationToken.None);
        var disconnected = expired.Manager.Snapshot(expired.RoomId);
        Assert.Equal(
            ParticipantStatus.Dnf,
            disconnected.Participants.Single(item => item.ProfileId == expired.ProfileId).Status);

        var refreshed = CreateRunningRoom(time);
        expiry.CompleteResult = false;
        await presence.EnterRoomAsync(refreshed.ProfileId, "refreshed", refreshed.RoomId);
        worker = CreateExpiryWorker(refreshed.Manager, expiry, presence, time);
        await worker.ProcessClaimAsync(
            new ExpiredPresenceClaim("refreshed", refreshed.ProfileId, refreshed.RoomId),
            CancellationToken.None);
        Assert.Equal(
            ParticipantStatus.Running,
            refreshed.Manager.Snapshot(refreshed.RoomId)
                .Participants.Single(item => item.ProfileId == refreshed.ProfileId).Status);

        var replaced = CreateRunningRoom(time);
        expiry.CompleteResult = true;
        await presence.EnterRoomAsync(replaced.ProfileId, "replacement", replaced.RoomId);
        worker = CreateExpiryWorker(replaced.Manager, expiry, presence, time);
        await worker.ProcessClaimAsync(
            new ExpiredPresenceClaim("old", replaced.ProfileId, replaced.RoomId),
            CancellationToken.None);
        Assert.Equal(
            ParticipantStatus.Running,
            replaced.Manager.Snapshot(replaced.RoomId)
                .Participants.Single(item => item.ProfileId == replaced.ProfileId).Status);
    }

    [Fact]
    public async Task CrashBeforeDisconnectFinalizationLeavesTheClaimForAnIdempotentRetry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var running = CreateRunningRoom(time);
        var expiry = new FakeExpiryStore
        {
            CompleteResult = true,
            ExecuteFailure = new InvalidOperationException("simulierter Pod-Abbruch")
        };
        var presence = new LivePresenceTracker(Options.Create(new LiveOptions()), time);
        var worker = CreateExpiryWorker(running.Manager, expiry, presence, time);
        var claim = new ExpiredPresenceClaim("expired", running.ProfileId, running.RoomId);

        await worker.ProcessClaimAsync(claim, CancellationToken.None);

        Assert.Equal(0, expiry.CompleteCalls);
        Assert.Equal(0, expiry.ReleaseCalls);
        Assert.Equal(
            ParticipantStatus.Running,
            running.Manager.Snapshot(running.RoomId)
                .Participants.Single(item => item.ProfileId == running.ProfileId).Status);

        expiry.ExecuteFailure = null;
        await worker.ProcessClaimAsync(claim, CancellationToken.None);

        Assert.Equal(1, expiry.CompleteCalls);
        Assert.Equal(
            ParticipantStatus.Dnf,
            running.Manager.Snapshot(running.RoomId)
                .Participants.Single(item => item.ProfileId == running.ProfileId).Status);
    }

    [Fact]
    public async Task ReconnectWaitsForTheFinalEmptyCheckAndRestoresTheParticipantAfterDisconnect()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var running = CreateRunningRoom(time, reconnectGraceSeconds: 30);
        var expiry = new BarrierExpiryStore();
        var presence = new LivePresenceTracker(Options.Create(new LiveOptions()), time);
        var worker = CreateExpiryWorker(running.Manager, expiry, presence, time);
        var processing = worker.ProcessClaimAsync(
            new ExpiredPresenceClaim("old", running.ProfileId, running.RoomId),
            CancellationToken.None);
        await expiry.EmptyChecked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var reconnect = expiry.EnterAfterLockAsync(() =>
        {
            running.Manager.Join(running.RoomId, running.ProfileId, "A");
            presence.EnterRoom(running.ProfileId, "replacement", running.RoomId);
        });
        Assert.False(reconnect.IsCompleted);

        expiry.AllowDisconnect.TrySetResult();
        await processing;
        await reconnect;

        Assert.Equal(
            ParticipantStatus.Running,
            running.Manager.Snapshot(running.RoomId)
                .Participants.Single(item => item.ProfileId == running.ProfileId).Status);
    }

    [Fact]
    public async Task PrivacyOperationCompletesClaimWithoutReaddingPersonalExpiryData()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var running = CreateRunningRoom(time);
        var expiry = new FakeExpiryStore { CompleteResult = true };
        var worker = new RedisLivePresenceExpiryService(
            expiry,
            new LocalLiveRoomDispatcher(running.Manager),
            new RejectingProfileAccessGate("profile_operation_in_progress"),
            new NoOpRoomUpdateSender(),
            time,
            NullLogger<RedisLivePresenceExpiryService>.Instance);

        await worker.ProcessClaimAsync(
            new ExpiredPresenceClaim("expired", running.ProfileId, running.RoomId),
            CancellationToken.None);

        Assert.Equal(1, expiry.CompleteCalls);
        Assert.Equal(0, expiry.ReleaseCalls);
    }

    private static RedisLivePresenceExpiryService CreateExpiryWorker(
        LiveRoomManager manager,
        ILivePresenceExpiryStore expiry,
        ILivePresenceStateStore presence,
        TimeProvider time)
    {
        if (expiry is FakeExpiryStore fake)
        {
            fake.Presence = presence;
        }

        return new RedisLivePresenceExpiryService(
            expiry,
            new LocalLiveRoomDispatcher(manager),
            new ProfileAccessGate(),
            new NoOpRoomUpdateSender(),
            time,
            NullLogger<RedisLivePresenceExpiryService>.Instance);
    }

    private static (LiveRoomManager Manager, Guid RoomId, Guid ProfileId) CreateRunningRoom(
        ManualTimeProvider time,
        int reconnectGraceSeconds = 0)
    {
        var manager = new LiveRoomManager(
            Options.Create(new LiveOptions
            {
                CountdownSeconds = 1,
                ReconnectGraceSeconds = reconnectGraceSeconds
            }),
            time,
            new TypingEngine(time),
            NullLogger<LiveRoomManager>.Instance);
        var profileId = Guid.CreateVersion7();
        var peerId = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            profileId,
            "A",
            "Raum",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8));
        manager.Join(room.RoomId, peerId, "B");
        manager.SetReady(room.RoomId, profileId, true);
        manager.SetReady(room.RoomId, peerId, true);
        manager.Start(room.RoomId, profileId);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Snapshot(room.RoomId);
        return (manager, room.RoomId, profileId);
    }

    private static string FindSource(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "KeyWars", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class FakeExpiryStore : ILivePresenceExpiryStore
    {
        public bool CompleteResult { get; set; }
        public ILivePresenceStateStore Presence { get; set; } = null!;
        public int CompleteCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public Exception? ExecuteFailure { get; set; }

        public Task<PresenceExpiryClaimBatch> ClaimExpiredAsync(
            byte bucket,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PresenceExpiryClaimBatch([], 0));

        public Task<bool> CompleteExpiryClaimAsync(
            ExpiredPresenceClaim claim,
            CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.FromResult(CompleteResult);
        }

        public async Task<bool> ExecuteIfRoomEmptyAsync(
            ExpiredPresenceClaim claim,
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            if (ExecuteFailure is { } exception)
            {
                throw exception;
            }

            if (await Presence.CountRoomConnectionsAsync(
                claim.ProfileId,
                claim.RoomId,
                cancellationToken) != 0)
            {
                return false;
            }

            await action(cancellationToken);
            return true;
        }

        public Task ReleaseExpiryClaimAsync(ExpiredPresenceClaim claim)
        {
            ReleaseCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingProfileAccessGate(string code) : IProfileAccessGate
    {
        public ValueTask<ProfileAccessState> GetStateAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ProfileAccessState.OperationInProgress);

        public ValueTask<IOperationLease> AcquireAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IOperationLease>(new ProfileOperationException(code, code));

        public ValueTask<IOperationLease> AcquireManyAsync(
            IEnumerable<Guid> profileIds,
            CancellationToken cancellationToken = default) =>
            AcquireAsync(profileIds.First(), cancellationToken);

        public ValueTask<IOperationLease?> TryBeginOperationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IOperationLease?>(null);

        public Task WaitForIdleAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask CompleteOperationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask MarkDeletedAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class BarrierExpiryStore : ILivePresenceExpiryStore
    {
        private readonly SemaphoreSlim profileLock = new(1, 1);
        public TaskCompletionSource EmptyChecked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDisconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PresenceExpiryClaimBatch> ClaimExpiredAsync(
            byte bucket,
            int take,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PresenceExpiryClaimBatch([], 0));

        public Task<bool> CompleteExpiryClaimAsync(
            ExpiredPresenceClaim claim,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<bool> ExecuteIfRoomEmptyAsync(
            ExpiredPresenceClaim claim,
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken)
        {
            await profileLock.WaitAsync(cancellationToken);
            try
            {
                EmptyChecked.TrySetResult();
                await AllowDisconnect.Task.WaitAsync(cancellationToken);
                await action(cancellationToken);
                return true;
            }
            finally
            {
                profileLock.Release();
            }
        }

        public async Task EnterAfterLockAsync(Action reconnect)
        {
            await profileLock.WaitAsync();
            try
            {
                reconnect();
            }
            finally
            {
                profileLock.Release();
            }
        }

        public Task ReleaseExpiryClaimAsync(ExpiredPresenceClaim claim) => Task.CompletedTask;
    }

    private sealed class NoOpRoomUpdateSender : ILiveRoomUpdateSender
    {
        public Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
