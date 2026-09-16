using System.Reflection;
using System.Security.Claims;
using KeyWars.Hubs;
using KeyWars.Infrastructure;
using KeyWars.Infrastructure.Cluster;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class ProfileHubConnectionRegistryTests
{
    [Fact]
    public async Task PassiveConnectionIsRemovedNotifiedAndAbortedWithoutAnotherInvocation()
    {
        var state = new TestConnectionStateStore();
        var hub = new RecordingHubContext();
        var registry = CreateRegistry(state, hub);
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var caller = new TestHubCallerContext("passive-local");
        await registry.RegisterAsync(profileId, caller, CancellationToken.None);
        await registry.SetRoomAsync(caller.ConnectionId, roomId, CancellationToken.None);

        await registry.RevokeProfileAsync(profileId, "Profil gelöscht.");

        Assert.True(caller.Aborted);
        Assert.Contains((caller.ConnectionId, roomId.ToString("N")), hub.Groups.Removals);
        var notification = Assert.Single(hub.Clients.Messages);
        Assert.Equal(caller.ConnectionId, notification.ConnectionId);
        Assert.Equal("profileRevoked", notification.Method);
        Assert.Equal("Profil gelöscht.", Assert.Single(notification.Arguments));
    }

    [Fact]
    public async Task MissedPubSubIsRecoveredFromDurableGeneration()
    {
        var state = new TestConnectionStateStore();
        var hub = new RecordingHubContext();
        var registry = CreateRegistry(state, hub);
        var profileId = Guid.CreateVersion7();
        var caller = new TestHubCallerContext("missed-event");
        await registry.RegisterAsync(profileId, caller, CancellationToken.None);

        await state.RevokeAsync(profileId);
        Assert.False(caller.Aborted);

        await registry.ReconcileAsync(CancellationToken.None);

        Assert.True(caller.Aborted);
        Assert.Single(hub.Clients.Messages);
    }

    [Fact]
    public async Task RedisRestartEpochInvalidatesAConnectionEvenWhenGenerationWasLost()
    {
        var state = new TestConnectionStateStore();
        var hub = new RecordingHubContext();
        var registry = CreateRegistry(state, hub);
        var profileId = Guid.CreateVersion7();
        var caller = new TestHubCallerContext("before-redis-restart");
        await registry.RegisterAsync(profileId, caller, CancellationToken.None);

        state.SimulateRedisRestart();
        await registry.ReconcileAsync(CancellationToken.None);

        Assert.True(caller.Aborted);
        Assert.Single(hub.Clients.Messages);
    }

    [Fact]
    public async Task GroupRemovalFailureStillAbortsAndUnregistersPassiveConnection()
    {
        var state = new TestConnectionStateStore();
        var hub = new RecordingHubContext { ThrowOnGroupRemoval = true };
        var registry = CreateRegistry(state, hub);
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var caller = new TestHubCallerContext("failing-backplane");
        await registry.RegisterAsync(profileId, caller, CancellationToken.None);
        await registry.SetRoomAsync(caller.ConnectionId, roomId, CancellationToken.None);
        var revocation = await state.RevokeAsync(profileId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ApplyRevocationAsync(
            profileId,
            revocation.Epoch,
            revocation.Generation,
            "Profil gelöscht."));

        Assert.True(caller.Aborted);
        Assert.False(await state.ContainsConnectionAsync(caller.ConnectionId));
        hub.ThrowOnGroupRemoval = false;
        await registry.ReconcileAsync(CancellationToken.None);
        Assert.True(caller.Aborted);
    }

    [Fact]
    public async Task TwoNodesRevokePassiveConnectionsAndIgnoreDelayedOldEventForNewGeneration()
    {
        var state = new TestConnectionStateStore();
        var firstHub = new RecordingHubContext();
        var secondHub = new RecordingHubContext();
        var firstNode = CreateRegistry(state, firstHub);
        var secondNode = CreateRegistry(state, secondHub);
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var firstCaller = new TestHubCallerContext("node-a");
        var secondCaller = new TestHubCallerContext("node-b");
        await firstNode.RegisterAsync(profileId, firstCaller, CancellationToken.None);
        await secondNode.RegisterAsync(profileId, secondCaller, CancellationToken.None);
        await firstNode.SetRoomAsync(firstCaller.ConnectionId, roomId, CancellationToken.None);
        await secondNode.SetRoomAsync(secondCaller.ConnectionId, roomId, CancellationToken.None);

        await firstNode.RevokeProfileAsync(profileId, "Profil gelöscht.");
        var published = Assert.Single(state.Published);
        var listener = new RedisProfileHubRevocationListener(
            RedisConnection(),
            secondNode,
            NullLogger<RedisProfileHubRevocationListener>.Instance);
        await listener.ApplyAsync($"{profileId:N}:{published.Epoch}:{published.Generation}");

        Assert.True(firstCaller.Aborted);
        Assert.True(secondCaller.Aborted);
        Assert.Contains((firstCaller.ConnectionId, roomId.ToString("N")), firstHub.Groups.Removals);
        Assert.Contains((secondCaller.ConnectionId, roomId.ToString("N")), firstHub.Groups.Removals);
        Assert.Contains((secondCaller.ConnectionId, roomId.ToString("N")), secondHub.Groups.Removals);

        var newCaller = new TestHubCallerContext("node-b-new-generation");
        await secondNode.RegisterAsync(profileId, newCaller, CancellationToken.None);
        await listener.ApplyAsync($"{profileId:N}:{published.Epoch}:{published.Generation}");

        Assert.False(newCaller.Aborted);
        Assert.DoesNotContain(
            secondHub.Clients.Messages,
            message => message.ConnectionId == newCaller.ConnectionId);
    }

    private static ProfileHubConnectionRegistry CreateRegistry(
        IProfileHubConnectionStateStore state,
        RecordingHubContext hub) => new(
            state,
            hub,
            NullLogger<ProfileHubConnectionRegistry>.Instance);

    private static IConnectionMultiplexer RedisConnection()
    {
        var subscriber = DispatchProxy.Create<ISubscriber, NoopRedisSubscriber>();
        var connection = DispatchProxy.Create<IConnectionMultiplexer, SubscriberRedisConnection>();
        ((SubscriberRedisConnection)(object)connection).Subscriber = subscriber;
        return connection;
    }

    private sealed class TestConnectionStateStore : IProfileHubConnectionStateStore
    {
        private readonly object gate = new();
        private readonly Dictionary<Guid, long> generations = [];
        private readonly Dictionary<string, ProfileHubConnectionRegistration> connections =
            new(StringComparer.Ordinal);

        public string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
        public List<(Guid ProfileId, string Epoch, long Generation)> Published { get; } = [];

        public ValueTask<ProfileHubConnectionRegistration> RegisterAsync(
            Guid profileId,
            string connectionId,
            string ownerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var registration = new ProfileHubConnectionRegistration(
                    profileId,
                    connectionId,
                    ownerId,
                    Epoch,
                    generations.GetValueOrDefault(profileId),
                    null);
                connections[connectionId] = registration;
                return ValueTask.FromResult(registration);
            }
        }

        public ValueTask<bool> RefreshAsync(
            ProfileHubConnectionRegistration registration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult(
                    registration.Epoch == Epoch &&
                    registration.Generation == generations.GetValueOrDefault(registration.ProfileId) &&
                    connections.ContainsKey(registration.ConnectionId));
            }
        }

        public ValueTask<bool> SetRoomAsync(
            ProfileHubConnectionRegistration registration,
            Guid? roomId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (registration.Epoch != Epoch ||
                    registration.Generation != generations.GetValueOrDefault(registration.ProfileId) ||
                    !connections.ContainsKey(registration.ConnectionId))
                {
                    return ValueTask.FromResult(false);
                }

                connections[registration.ConnectionId] = registration with { RoomId = roomId };
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask UnregisterAsync(
            ProfileHubConnectionRegistration registration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                connections.Remove(registration.ConnectionId);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<ProfileHubRevocation> RevokeAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var generation = checked(generations.GetValueOrDefault(profileId) + 1);
                generations[profileId] = generation;
                var snapshot = connections.Values
                    .Where(item => item.ProfileId == profileId && item.Generation < generation)
                    .ToArray();
                return ValueTask.FromResult(new ProfileHubRevocation(Epoch, generation, snapshot));
            }
        }

        public ValueTask<(string Epoch, long Generation)> ReadVersionAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return ValueTask.FromResult((Epoch, generations.GetValueOrDefault(profileId)));
            }
        }

        public ValueTask PublishRevocationAsync(
            Guid profileId,
            string epoch,
            long generation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Published.Add((profileId, epoch, generation));
            return ValueTask.CompletedTask;
        }

        public void SimulateRedisRestart()
        {
            lock (gate)
            {
                Epoch = Guid.NewGuid().ToString("N");
                generations.Clear();
                connections.Clear();
            }
        }

        public ValueTask<bool> ContainsConnectionAsync(string connectionId)
        {
            lock (gate)
            {
                return ValueTask.FromResult(connections.ContainsKey(connectionId));
            }
        }
    }

    private sealed class TestHubCallerContext(string connectionId) : HubCallerContext
    {
        private readonly CancellationTokenSource aborted = new();

        public bool Aborted { get; private set; }
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity());
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => aborted.Token;

        public override void Abort()
        {
            Aborted = true;
            aborted.Cancel();
        }
    }

    private sealed class RecordingHubContext : IHubContext<ArenaHub>
    {
        public RecordingHubClients Clients { get; } = new();
        public RecordingGroupManager Groups { get; } = new();
        public bool ThrowOnGroupRemoval
        {
            get => Groups.ThrowOnRemoval;
            set => Groups.ThrowOnRemoval = value;
        }
        IHubClients IHubContext<ArenaHub>.Clients => Clients;
        IGroupManager IHubContext<ArenaHub>.Groups => Groups;
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> Removals { get; } = [];
        public bool ThrowOnRemoval { get; set; }

        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRemoval)
            {
                throw new InvalidOperationException("Simulierter Backplane-Fehler.");
            }

            Removals.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);

    private sealed class RecordingHubClients : IHubClients
    {
        public List<SentMessage> Messages { get; } = [];
        public IClientProxy All => new RecordingClientProxy("*", Messages);
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Client(string connectionId) => new RecordingClientProxy(connectionId, Messages);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
        public IClientProxy Group(string groupName) => All;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
        public IClientProxy User(string userId) => All;
        public IClientProxy Users(IReadOnlyList<string> userIds) => All;
    }

    private sealed class RecordingClientProxy(
        string connectionId,
        List<SentMessage> messages) : IClientProxy
    {
        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            messages.Add(new SentMessage(connectionId, method, args));
            return Task.CompletedTask;
        }
    }

    public class SubscriberRedisConnection : DispatchProxy
    {
        public ISubscriber Subscriber { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IConnectionMultiplexer.GetSubscriber)
                ? Subscriber
                : throw new NotSupportedException(targetMethod?.Name);
    }

    public class NoopRedisSubscriber : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
