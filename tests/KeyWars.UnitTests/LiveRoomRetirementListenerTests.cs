using System.Text.Json;
using System.Text.Json.Serialization;
using KeyWars.Domain;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class LiveRoomRetirementListenerTests
{
    [Fact]
    public void ClusterRoomUpdateStateHasHardCapacityAndRetirementSubscription()
    {
        var senderSource = File.ReadAllText(FindSource("Services/LiveRoomUpdateSender.cs"));
        var listenerSource = File.ReadAllText(FindSource("Infrastructure/Cluster/RedisLiveRoomRetirementListener.cs"));

        Assert.Contains("roomCapacity = Math.Clamp(options.Value.MaxConcurrentRooms", senderSource, StringComparison.Ordinal);
        Assert.Contains("while (rooms.Count >= roomCapacity", senderSource, StringComparison.Ordinal);
        Assert.Contains("RedisLiveRoomDispatcher.RetirementChannel", listenerSource, StringComparison.Ordinal);
        Assert.Contains("RedisLiveProgressRelay.ProgressInputPurgeChannel", listenerSource, StringComparison.Ordinal);
        Assert.Contains("rooms.RemoveRoomState(roomId)", listenerSource, StringComparison.Ordinal);
        Assert.Contains("SnapshotLocalRoomCache(RoomsPerReconciliation)", listenerSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissedProfilePurgeAndRetirementAreRecoveredOnTheOfflinePod()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var options = Options.Create(new LiveOptions
        {
            CountdownSeconds = 1,
            MaxConcurrentRooms = 8,
            MaxParticipantsPerRoom = 8
        });
        var podA = CreateManager(options, time);
        var podB = CreateManager(options, time);
        var profileId = Guid.CreateVersion7();
        var peerId = Guid.CreateVersion7();
        var room = podA.CreateRoom(new CreateLiveRoomRequest(
            profileId,
            "Privat",
            "Raum",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8), enforceLocalRoomCapacity: false);
        podA.Join(room.RoomId, peerId, "Peer");
        podA.SetReady(room.RoomId, profileId, true);
        podA.SetReady(room.RoomId, peerId, true);
        podA.Start(room.RoomId, profileId);
        var initial = podA.ExportRoomState(room.RoomId);
        Assert.True(podB.ImportRoomState(initial));
        time.Advance(TimeSpan.FromSeconds(1));
        foreach (var manager in new[] { podA, podB })
        {
            manager.SubmitProgressDelta(
                room.RoomId,
                profileId,
                new LiveProgressInputDelta(0, 1, 0, "T", "T"));
            manager.SubmitProgressDelta(
                room.RoomId,
                peerId,
                new LiveProgressInputDelta(0, 1, 0, "T", "T"));
            Assert.Equal(2, manager.ProgressInputCount);
        }

        var authoritative = podA.ExportRoomState(room.RoomId);

        var database = System.Reflection.DispatchProxy.Create<IDatabase, RoomDatabaseProxy>();
        var redisState = (RoomDatabaseProxy)(object)database;
        redisState.SetRoom(authoritative);
        var purgeGeneration = Guid.NewGuid().ToString("N");
        redisState.SetPurgeGeneration(room.RoomId, profileId, purgeGeneration);
        var listenerA = new RedisLiveRoomRetirementListener(
            Multiplexer(database),
            podA,
            NullLogger<RedisLiveRoomRetirementListener>.Instance);
        var listenerB = new RedisLiveRoomRetirementListener(
            Multiplexer(database),
            podB,
            NullLogger<RedisLiveRoomRetirementListener>.Instance);
        var purgeMessage = $"{room.RoomId:N}:{profileId:N}:{purgeGeneration}";

        listenerB.ApplyProgressPurge(purgeMessage);
        listenerB.ApplyProgressPurge(purgeMessage);

        Assert.Equal(2, podA.ProgressInputCount);
        Assert.Equal(1, podB.ProgressInputCount);
        Assert.Throws<InvalidOperationException>(() => podB.Snapshot(room.RoomId));
        Assert.True(podB.ImportRoomState(authoritative));

        await listenerA.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, podA.ProgressInputCount);
        Assert.Throws<InvalidOperationException>(() => podA.Snapshot(room.RoomId));
        Assert.True(podA.ImportRoomState(authoritative));
        Assert.Equal(
            LiveProgressInputStatus.Applied,
            podA.SubmitProgressDelta(
                room.RoomId,
                peerId,
                new LiveProgressInputDelta(1, 2, 0, "e", null)).Delta?.InputStatus);
        Assert.Equal(
            LiveProgressInputStatus.ResyncRequired,
            podA.SubmitProgressDelta(
                room.RoomId,
                profileId,
                new LiveProgressInputDelta(1, 2, 0, "e", null)).Delta?.InputStatus);

        redisState.RemoveRoom(room.RoomId);
        await listenerA.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, podA.ProgressInputCount);
        Assert.Throws<InvalidOperationException>(() => podA.Snapshot(room.RoomId));
    }

    [Fact]
    public async Task MissedRetirementAuditsMaterializedLobbyRoomsWithoutProgressInBoundedPages()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var options = Options.Create(new LiveOptions
        {
            MaxConcurrentRooms = 80,
            MaxParticipantsPerRoom = 8
        });
        var manager = CreateManager(options, time);
        var database = System.Reflection.DispatchProxy.Create<IDatabase, RoomDatabaseProxy>();
        var redisState = (RoomDatabaseProxy)(object)database;
        var roomIds = new List<Guid>();
        for (var index = 0; index < 65; index++)
        {
            var room = manager.CreateRoom(new CreateLiveRoomRequest(
                Guid.CreateVersion7(),
                $"Person {index}",
                $"Raum {index}",
                "Text",
                LiveRoomMode.Classic,
                LiveRoomVisibility.InternalOpen,
                1,
                8), enforceLocalRoomCapacity: false);
            roomIds.Add(room.RoomId);
            redisState.SetRoom(manager.ExportRoomState(room.RoomId));
        }

        var missedRetirement = roomIds[^1];
        redisState.RemoveRoom(missedRetirement);
        var listener = new RedisLiveRoomRetirementListener(
            Multiplexer(database),
            manager,
            NullLogger<RedisLiveRoomRetirementListener>.Instance);

        await listener.ReconcileAsync(CancellationToken.None);
        _ = manager.Snapshot(missedRetirement);

        await listener.ReconcileAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => manager.Snapshot(missedRetirement));
    }

    [Fact]
    public async Task MissedRetirementAlsoClearsProgressOnlyCacheLeftAfterProfilePurge()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var options = Options.Create(new LiveOptions
        {
            CountdownSeconds = 1,
            MaxConcurrentRooms = 8,
            MaxParticipantsPerRoom = 8
        });
        var manager = CreateManager(options, time);
        var profileId = Guid.CreateVersion7();
        var peerId = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            profileId,
            "Privat",
            "Raum",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8), enforceLocalRoomCapacity: false);
        manager.Join(room.RoomId, peerId, "Peer");
        manager.SetReady(room.RoomId, profileId, true);
        manager.SetReady(room.RoomId, peerId, true);
        manager.Start(room.RoomId, profileId);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.SubmitProgressDelta(
            room.RoomId,
            profileId,
            new LiveProgressInputDelta(0, 1, 0, "T", "T"));
        manager.SubmitProgressDelta(
            room.RoomId,
            peerId,
            new LiveProgressInputDelta(0, 1, 0, "T", "T"));

        var database = System.Reflection.DispatchProxy.Create<IDatabase, RoomDatabaseProxy>();
        var redisState = (RoomDatabaseProxy)(object)database;
        redisState.SetRoom(manager.ExportRoomState(room.RoomId));
        var listener = new RedisLiveRoomRetirementListener(
            Multiplexer(database),
            manager,
            NullLogger<RedisLiveRoomRetirementListener>.Instance);

        listener.ApplyProgressPurge(
            $"{room.RoomId:N}:{profileId:N}:{Guid.NewGuid():N}");
        Assert.Equal(1, manager.ProgressInputCount);
        Assert.Throws<InvalidOperationException>(() => manager.Snapshot(room.RoomId));

        redisState.RemoveRoom(room.RoomId);
        await listener.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, manager.ProgressInputCount);
    }

    [Fact]
    public async Task RetirementInvalidatesLocalRoomProgressAndUpdateStateAfterTheRoomWasUnloaded()
    {
        var broadcaster = new LiveProgressBroadcaster(
            new NoOpProgressSender(),
            Options.Create(new LiveOptions()),
            TimeProvider.System,
            NullLogger<LiveProgressBroadcaster>.Instance);
        var updates = new RecordingRoomUpdateSender();
        var manager = new LiveRoomManager(
            Options.Create(new LiveOptions()),
            TimeProvider.System,
            new TypingEngine(TimeProvider.System),
            NullLogger<LiveRoomManager>.Instance,
            progressBroadcaster: broadcaster,
            updateSender: updates);
        var creator = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            creator,
            "A",
            "Raum",
            "Text",
            Domain.LiveRoomMode.Classic,
            Domain.LiveRoomVisibility.InternalOpen,
            1,
            8));
        var listener = new RedisLiveRoomRetirementListener(
            Multiplexer(),
            manager,
            NullLogger<RedisLiveRoomRetirementListener>.Instance);
        await broadcaster.PublishAsync(
            new LiveProgressDelta(
                room.RoomId,
                1,
                1,
                creator,
                1,
                1,
                1,
                "AQ==",
                1,
                100,
                1),
            CancellationToken.None);
        Assert.Equal(1, broadcaster.Snapshot().ActiveRooms);
        Assert.True(manager.UnloadRoomState(room.RoomId));

        listener.ApplyRetirement(room.RoomId.ToString("N"));

        Assert.Throws<InvalidOperationException>(() => manager.Snapshot(room.RoomId));
        Assert.Contains(room.RoomId, updates.RemovedRooms);
        Assert.Equal(0, broadcaster.Snapshot().ActiveRooms);
    }

    private static LiveRoomManager CreateManager(
        IOptions<LiveOptions> options,
        TimeProvider time) => new(
            options,
            time,
            new TypingEngine(time),
            NullLogger<LiveRoomManager>.Instance);

    private static IConnectionMultiplexer Multiplexer(IDatabase? database = null)
    {
        var multiplexer = System.Reflection.DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
        var proxy = (MultiplexerProxy)(object)multiplexer;
        proxy.Subscriber = System.Reflection.DispatchProxy.Create<ISubscriber, SubscriberProxy>();
        proxy.Database = database;
        return multiplexer;
    }

    private static string FindSource(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "KeyWars",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }

    public class MultiplexerProxy : System.Reflection.DispatchProxy
    {
        public ISubscriber Subscriber { get; set; } = null!;
        public IDatabase? Database { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IConnectionMultiplexer.GetSubscriber) => Subscriber,
                nameof(IConnectionMultiplexer.GetDatabase) => Database
                    ?? throw new InvalidOperationException("Keine Testdatenbank konfiguriert."),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    public class RoomDatabaseProxy : System.Reflection.DispatchProxy
    {
        private static readonly JsonSerializerOptions MementoOptions = CreateMementoOptions();
        private readonly Dictionary<string, RedisValue> rooms = [];
        private readonly Dictionary<string, Dictionary<string, RedisValue>> purgeGenerations = [];

        public void SetRoom(LiveRoomMemento memento) =>
            rooms[RedisLiveRoomDispatcher.RoomKey(memento.Id).ToString()] =
                JsonSerializer.Serialize(memento, MementoOptions);

        public void RemoveRoom(Guid roomId) =>
            rooms.Remove(RedisLiveRoomDispatcher.RoomKey(roomId).ToString());

        public void SetPurgeGeneration(Guid roomId, Guid profileId, string generation)
        {
            var key = RedisLiveProgressRelay.PurgeGenerationKey(roomId).ToString();
            if (!purgeGenerations.TryGetValue(key, out var values))
            {
                values = [];
                purgeGenerations[key] = values;
            }

            values[profileId.ToString("N")] = generation;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDatabaseAsync.HashGetAsync))
            {
                var key = ((RedisKey)args![0]!).ToString();
                if (args[1] is RedisValue[] fields)
                {
                    purgeGenerations.TryGetValue(key, out var values);
                    return Task.FromResult(fields
                        .Select(field => values?.GetValueOrDefault(field.ToString(), RedisValue.Null)
                            ?? RedisValue.Null)
                        .ToArray());
                }
                return Task.FromResult(rooms.GetValueOrDefault(key, RedisValue.Null));
            }

            throw new NotSupportedException(targetMethod?.Name);
        }

        private static JsonSerializerOptions CreateMementoOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }

    public class SubscriberProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class NoOpProgressSender : ILiveProgressSender
    {
        public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRoomUpdateSender : ILiveRoomUpdateSender
    {
        public List<Guid> RemovedRooms { get; } = [];

        public Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void RemoveRoom(Guid roomId) => RemovedRooms.Add(roomId);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
