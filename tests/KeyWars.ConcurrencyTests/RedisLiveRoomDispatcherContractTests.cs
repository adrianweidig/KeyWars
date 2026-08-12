using System.Reflection;
using System.Text.Json;
using KeyWars.Domain;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.ConcurrencyTests;

public sealed class RedisLiveRoomDispatcherContractTests
{
    [Fact]
    public void RoomStateAndLocksShardWhileDirectoryKeysShareOnlyTheirMetadataSlot()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var roomKey = InvokeKey(dispatcher, "RoomKey", first);
        var roomLock = InvokeKey(dispatcher, "LockKey", first);
        var otherRoomKey = InvokeKey(dispatcher, "RoomKey", second);

        Assert.Equal(HashTag(roomKey), HashTag(roomLock));
        Assert.NotEqual(HashTag(roomKey), HashTag(otherRoomKey));
        Assert.NotEqual(HashTag(roomKey), HashTag(StaticKey(dispatcher, "RoomIndexKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "MetricsKey")));
    }

    [Fact]
    public void RoomScriptsNeverReferenceDirectoryKeysAndDirectoryScriptsNeverReferenceRoomKeys()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        foreach (var name in new[] { "InitializeRoomScript", "CompareExchangeScript", "TouchRoomTtlScript", "DeleteRoomStateScript" })
        {
            var script = StaticScript(dispatcher, name).OriginalScript;
            Assert.DoesNotContain("@capacityKey", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@roomIndexKey", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@metricsKey", script, StringComparison.Ordinal);
        }

        foreach (var name in new[] { "ReserveDirectoryScript", "CommitDirectoryScript", "RemoveDirectoryScript" })
        {
            var script = StaticScript(dispatcher, name).OriginalScript;
            Assert.DoesNotContain("@roomKey", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@roomLockKey", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClusterOperationsUseRoomTargetedManagerApis()
    {
        var source = File.ReadAllText(FindDispatcherSource());

        Assert.Contains("enforceLocalRoomCapacity: false", source, StringComparison.Ordinal);
        Assert.Contains("manager.JoinByResolvedCode(roomId, code, profileId, displayName)", source, StringComparison.Ordinal);
        Assert.Contains("manager.RemoveProfileFromRoom(id, profileId)", source, StringComparison.Ordinal);
        Assert.Contains("manager.AbortActiveRoom(id)", source, StringComparison.Ordinal);
        Assert.Contains("rooms.UnloadRoomState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rooms.RemoveRoomState", source, StringComparison.Ordinal);
        Assert.Contains("await using (var roomLock", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("rooms.UnloadRoomState(snapshot.RoomId);", StringComparison.Ordinal) <
            source.IndexOf("if (!stateUnloaded)", StringComparison.Ordinal),
            "Der Create-Zustand muss im expliziten Room-Lock-Scope entladen werden.");
        Assert.DoesNotContain("manager.JoinByCode(code, profileId, displayName)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manager.RemoveProfile(profileId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manager.AbortActiveRooms()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservationRecoveryAndViewerFanoutStayInBoundedAtomicDirectoryScripts()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var reserve = StaticScript(dispatcher, "ReserveDirectoryScript").OriginalScript;
        var commit = StaticScript(dispatcher, "CommitDirectoryScript").OriginalScript;
        var remove = StaticScript(dispatcher, "RemoveDirectoryScript").OriginalScript;

        Assert.Contains("zrangebyscore', @reservationExpiryKey", reserve, StringComparison.Ordinal);
        Assert.DoesNotContain("zrangebyscore', @capacityKey", reserve, StringComparison.Ordinal);
        Assert.Contains("@cleanupBefore", reserve, StringComparison.Ordinal);
        Assert.Contains("'LIMIT', 0, @cleanupLimit", reserve, StringComparison.Ordinal);
        Assert.Contains("zrem', @reservationExpiryKey", commit, StringComparison.Ordinal);
        Assert.Contains("zadd', @privateLobbyKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @privateLobbyKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @privateLobbyKey", remove, StringComparison.Ordinal);
    }

    [Fact]
    public void SweepRefreshesRoomTtlWithLockAndRevisionFencing()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var touch = StaticScript(dispatcher, "TouchRoomTtlScript").OriginalScript;
        var source = File.ReadAllText(FindDispatcherSource());

        Assert.Contains("get', @lockKey", touch, StringComparison.Ordinal);
        Assert.Contains("@lockToken", touch, StringComparison.Ordinal);
        Assert.Contains("hget', @roomKey, 'revision'", touch, StringComparison.Ordinal);
        Assert.Contains("@expectedRevision", touch, StringComparison.Ordinal);
        Assert.Contains("expire', @roomKey, @ttlSeconds", touch, StringComparison.Ordinal);
        Assert.Contains("persist', @roomKey", touch, StringComparison.Ordinal);
        Assert.Contains("@persistent", touch, StringComparison.Ordinal);
        Assert.Contains("RequiresPersistentRoomState(updated) ? 1 : 0", source, StringComparison.Ordinal);
        Assert.Contains("RequiresPersistentRoomState(record.Memento) ? 1 : 0", source, StringComparison.Ordinal);
        Assert.Contains("RequiresPersistentRoomState(recovered) ? 1 : 0", source, StringComparison.Ordinal);
        Assert.True(
            source.Split("await TouchRoomTtlAsync(record, roomLock, operationToken);", StringSplitOptions.None).Length >= 3,
            "Pending-Recovery und normaler Auditpfad müssen die Room-TTL verlängern.");
    }

    [Fact]
    public async Task ClusterCreateSerializesQueueHeadroomReservationsPerObservedActiveCount()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var reserve = StaticScript(typeof(RedisLiveRoomDispatcher), "ReserveDirectoryScript").OriginalScript;
        Assert.Contains("SortedSetLengthAsync(CapacityRoomsKey)", source, StringComparison.Ordinal);
        Assert.Contains("completionSink.CanAcceptNewRoom(activeRoomCount)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(maxConcurrentRooms, checked(activeRoomCount + 1))", source, StringComparison.Ordinal);
        Assert.Contains("currentCapacity = redis.call('zcard', @capacityKey)", reserve, StringComparison.Ordinal);
        Assert.Contains("currentCapacity ~= tonumber(@expectedCapacity)", reserve, StringComparison.Ordinal);
        Assert.Contains("expectedCapacity = activeRoomCount", source, StringComparison.Ordinal);

        const int completionCapacity = 2;
        const int pending = 1;
        const int observedActive = 0;
        var reservationLimit = Math.Min(2, observedActive + 1);
        var active = observedActive;
        var accepted = 0;
        var gate = new object();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            lock (gate)
            {
                if (active >= reservationLimit)
                {
                    return;
                }

                active++;
                accepted++;
            }
        })));

        Assert.Equal(1, accepted);
        Assert.Equal(completionCapacity, active + pending);
        Assert.False(active + pending < completionCapacity);
    }

    [Fact]
    public void LobbyAndMetricsContractsAreBoundedWithoutMementoFullScans()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var lobby = ExtractMethod(source, "ListLobbySummariesAsync", "MetricsSnapshotAsync");
        var metrics = ExtractMethod(source, "MetricsSnapshotAsync", "ResolveRoomIdByCodeAsync");

        Assert.Contains("limit + LobbyRepairAllowance", lobby, StringComparison.Ordinal);
        Assert.Contains("SortedSetRangeByRankAsync", lobby, StringComparison.Ordinal);
        Assert.Contains("DirectoryEntriesKey", lobby, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadRoomAsync", lobby, StringComparison.Ordinal);
        Assert.Contains("HashGetAsync", metrics, StringComparison.Ordinal);
        Assert.Contains("MetricsKey", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadRoomAsync", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("SortedSetRangeByRankAsync", metrics, StringComparison.Ordinal);
        Assert.Contains("room.Finished ? 0 : room.Participants.Count", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SweepClaimRemainsBoundedAndFairAcrossDirtyDueAndAuditQueues()
    {
        var script = StaticScript(typeof(RedisLiveRoomDispatcher), "ClaimSweepWorkScript").OriginalScript;

        Assert.Contains("zrangebyscore", script, StringComparison.Ordinal);
        Assert.Contains("'LIMIT', 0, limit", script, StringComparison.Ordinal);
        Assert.Contains("claim(@dirtyKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@reservationExpiryKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@dueKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@roomIndexKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@capacityKey", script, StringComparison.Ordinal);
        Assert.Contains("for roomId in string.gmatch(@roomIds", StaticScript(
            typeof(RedisLiveRoomDispatcher),
            "RetrySweepClaimScript").OriginalScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LobbyPageLoadsOnlyLimitPlusBoundedRepairEntries()
    {
        var fake = new DirectoryReadRedis();
        for (var index = 0; index < 50; index++)
        {
            fake.AddPublicLobby(Guid.CreateVersion7(), $"Raum {index:D2}");
        }
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var page = await dispatcher.ListLobbySummariesAsync(Guid.CreateVersion7(), limit: 5);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(50, page.Total);
        Assert.InRange(fake.MaximumHashFieldCount, 1, 21);
        Assert.InRange(fake.MaximumSortedSetResultCount, 1, 21);
        Assert.Equal(0, fake.RoomStateHashReads);
    }

    [Fact]
    public async Task LobbyPageRefillsPastMoreThanOneRepairAllowance()
    {
        var fake = new DirectoryReadRedis();
        for (var index = 0; index < 40; index++)
        {
            fake.AddStalePublicLobby($"0000:{Guid.CreateVersion7():N}:{index:D20}");
        }
        for (var index = 0; index < 10; index++)
        {
            fake.AddPublicLobby(Guid.CreateVersion7(), $"Raum {index:D2}");
        }
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var page = await dispatcher.ListLobbySummariesAsync(Guid.CreateVersion7(), limit: 5);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(10, page.Total);
        Assert.InRange(fake.MaximumHashFieldCount, 1, 21);
        Assert.InRange(fake.MaximumSortedSetResultCount, 1, 21);
    }

    [Fact]
    public async Task MetricsReadOneCompactHashWithoutIndexesOrRoomStates()
    {
        var fake = new DirectoryReadRedis();
        fake.SetMetrics(active: 7, open: 3, running: 4, participants: 42);
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var snapshot = await dispatcher.MetricsSnapshotAsync();

        Assert.Equal(new LiveRoomMetricsSnapshot(7, 3, 4, 42), snapshot);
        Assert.Equal(0, fake.SortedSetReads);
        Assert.Equal(4, fake.MaximumHashFieldCount);
        Assert.Equal(0, fake.RoomStateHashReads);
    }

    private static string StaticKey(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)?.ToString()
        ?? throw new InvalidOperationException($"Redis-Schlüssel {name} fehlt.");

    private static LuaScript StaticScript(Type type, string name) =>
        (LuaScript)(type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException($"Redis-Skript {name} fehlt."));

    private static string InvokeKey(Type type, string name, Guid roomId) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [roomId])?.ToString()
        ?? throw new InvalidOperationException($"Redis-Schlüsselfunktion {name} fehlt.");

    private static string HashTag(string key)
    {
        var start = key.IndexOf('{');
        var end = key.IndexOf('}', start + 1);
        return start >= 0 && end > start + 1 ? key[(start + 1)..end] : key;
    }

    private static string FindDispatcherSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "KeyWars",
                "Infrastructure",
                "Cluster",
                "RedisLiveRoomDispatcher.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("RedisLiveRoomDispatcher.cs wurde nicht gefunden.");
    }

    private static string ExtractMethod(string source, string startName, string endName)
    {
        var start = source.IndexOf(startName, StringComparison.Ordinal);
        var end = source.IndexOf(endName, start + startName.Length, StringComparison.Ordinal);
        return source[start..end];
    }

    private static TestResources CreateDispatcher(
        IConnectionMultiplexer redis,
        out RedisLiveRoomDispatcher dispatcher)
    {
        var options = Options.Create(new LiveOptions
        {
            MaxConcurrentRooms = 100,
            CompletionQueueCapacity = 100
        });
        var time = TimeProvider.System;
        var queue = new RedisLiveRoomCompletionQueue(
            redis,
            new NoopCompletionWriter(),
            options,
            time,
            NullLogger<RedisLiveRoomCompletionQueue>.Instance);
        var sink = new ClusterLiveRoomCompletionSink(queue);
        var rooms = new LiveRoomManager(
            options,
            time,
            new TypingEngine(time),
            NullLogger<LiveRoomManager>.Instance,
            sink);
        var telemetry = new KeyWarsTelemetry();
        var relay = new RedisLiveProgressRelay(
            redis,
            new NoopProgressSender(),
            options,
            time,
            telemetry,
            NullLogger<RedisLiveProgressRelay>.Instance);
        dispatcher = new RedisLiveRoomDispatcher(redis, rooms, relay, sink, options, time);
        return new TestResources(queue, relay, telemetry);
    }

    private sealed class NoopCompletionWriter : ILiveRoomCompletionWriter
    {
        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoopProgressSender : ILiveProgressSender
    {
        public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestResources(
        RedisLiveRoomCompletionQueue queue,
        RedisLiveProgressRelay relay,
        KeyWarsTelemetry telemetry) : IDisposable
    {
        public void Dispose()
        {
            relay.Dispose();
            queue.Dispose();
            telemetry.Dispose();
        }
    }

    private sealed class DirectoryReadRedis
    {
        private readonly Dictionary<string, Dictionary<string, RedisValue>> hashes = [];
        private readonly Dictionary<string, Dictionary<string, double>> sortedSets = [];
        private readonly IDatabase database;
        private readonly string entriesKey = StaticKey(typeof(RedisLiveRoomDispatcher), "DirectoryEntriesKey");
        private readonly string metricsKey = StaticKey(typeof(RedisLiveRoomDispatcher), "MetricsKey");
        private readonly string publicLobbyKey = StaticKey(typeof(RedisLiveRoomDispatcher), "PublicLobbyIndexKey");
        private readonly string privateLobbyKey = StaticKey(typeof(RedisLiveRoomDispatcher), "PrivateLobbyIndexKey");

        public DirectoryReadRedis()
        {
            database = CreateProxy<IDatabase>(InvokeDatabase);
            Connection = CreateProxy<IConnectionMultiplexer>((method, _) => method.Name switch
            {
                nameof(IConnectionMultiplexer.GetDatabase) => database,
                _ => throw new NotSupportedException(method.Name)
            });
        }

        public IConnectionMultiplexer Connection { get; }
        public int MaximumHashFieldCount { get; private set; }
        public int RoomStateHashReads { get; private set; }
        public int SortedSetReads { get; private set; }
        public int MaximumSortedSetResultCount { get; private set; }

        public void AddPublicLobby(Guid roomId, string title)
        {
            var revision = 1L;
            var sortMember = $"{title}:{roomId:N}:{revision:D20}";
            GetSortedSet(publicLobbyKey)[sortMember] = 0;
            var summary = new LiveRoomLobbySummary(
                roomId,
                Guid.CreateVersion7(),
                "Ersteller",
                "ABC123",
                title,
                LiveRoomMode.Classic,
                LiveRoomVisibility.InternalOpen,
                LiveRoomPhase.Lobby,
                1,
                1,
                1,
                8,
                false,
                1);
            GetHash(entriesKey)[roomId.ToString("N")] = JsonSerializer.Serialize(new
            {
                revision,
                summary,
                isLobby = true,
                isPublicLobby = true,
                consumesCapacity = true,
                sortMember,
                audienceProfileIds = Array.Empty<Guid>(),
                audienceKey = "",
                contribution = new { active = 1, open = 1, running = 0, participants = 1 },
                sweepScheduleKey = "Lobby",
                nextDueAt = DateTimeOffset.UtcNow.AddMinutes(30)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        public void AddStalePublicLobby(string sortMember) =>
            GetSortedSet(publicLobbyKey)[sortMember] = 0;

        public void SetMetrics(int active, int open, int running, int participants)
        {
            var hash = GetHash(metricsKey);
            hash["active"] = active;
            hash["open"] = open;
            hash["running"] = running;
            hash["participants"] = participants;
        }

        private object? InvokeDatabase(MethodInfo method, object?[] arguments)
        {
            return method.Name switch
            {
                nameof(IDatabase.SortedSetRangeByRankAsync) => RangeAsync(arguments),
                nameof(IDatabase.SortedSetLengthAsync) => Task.FromResult((long)GetSortedSet(Key(arguments[0])).Count),
                nameof(IDatabase.SortedSetLengthByValueAsync) => Task.FromResult(LexValues(arguments).LongCount()),
                nameof(IDatabase.SortedSetRangeByValueAsync) => LexRangeAsync(arguments),
                nameof(IDatabase.SortedSetRemoveAsync) => Task.FromResult(GetSortedSet(Key(arguments[0])).Remove(arguments[1]!.ToString()!)),
                nameof(IDatabase.HashGetAsync) => HashGetAsync(arguments),
                nameof(IDatabase.SortedSetLength) => (long)GetSortedSet(Key(arguments[0])).Count,
                _ => throw new NotSupportedException(method.Name)
            };
        }

        private Task<RedisValue[]> LexRangeAsync(object?[] arguments)
        {
            SortedSetReads++;
            var skip = Convert.ToInt64(arguments[4]);
            var take = Convert.ToInt64(arguments[5]);
            var values = LexValues(arguments)
                .Skip(checked((int)skip))
                .Take(checked((int)take))
                .Select(value => (RedisValue)value)
                .ToArray();
            MaximumSortedSetResultCount = Math.Max(MaximumSortedSetResultCount, values.Length);
            return Task.FromResult(values);
        }

        private IEnumerable<string> LexValues(object?[] arguments)
        {
            var minimum = arguments[1]!.ToString()!;
            var maximum = arguments[2]!.ToString()!;
            return GetSortedSet(Key(arguments[0])).Keys
                .Where(value => StringComparer.Ordinal.Compare(value, minimum) >= 0 &&
                    StringComparer.Ordinal.Compare(value, maximum) <= 0)
                .Order(StringComparer.Ordinal);
        }

        private Task<RedisValue[]> RangeAsync(object?[] arguments)
        {
            SortedSetReads++;
            var start = Convert.ToInt64(arguments[1]);
            var stop = Convert.ToInt64(arguments[2]);
            var values = GetSortedSet(Key(arguments[0]))
                .OrderBy(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (RedisValue)item.Key)
                .Skip(checked((int)start))
                .Take(checked((int)Math.Max(0, stop - start + 1)))
                .ToArray();
            MaximumSortedSetResultCount = Math.Max(MaximumSortedSetResultCount, values.Length);
            return Task.FromResult(values);
        }

        private object HashGetAsync(object?[] arguments)
        {
            var key = Key(arguments[0]);
            if (key.Contains("{room:", StringComparison.Ordinal))
            {
                RoomStateHashReads++;
            }
            var hash = GetHash(key);
            if (arguments[1] is RedisValue[] fields)
            {
                MaximumHashFieldCount = Math.Max(MaximumHashFieldCount, fields.Length);
                return Task.FromResult(fields
                    .Select(field => hash.TryGetValue(field.ToString(), out var value) ? value : RedisValue.Null)
                    .ToArray());
            }

            var field = arguments[1]!.ToString()!;
            return Task.FromResult(hash.TryGetValue(field, out var result) ? result : RedisValue.Null);
        }

        private Dictionary<string, RedisValue> GetHash(string key)
        {
            if (!hashes.TryGetValue(key, out var hash))
            {
                hash = [];
                hashes[key] = hash;
            }
            return hash;
        }

        private Dictionary<string, double> GetSortedSet(string key)
        {
            if (!sortedSets.TryGetValue(key, out var set))
            {
                set = [];
                sortedSets[key] = set;
            }
            return set;
        }

        private static string Key(object? value) => value?.ToString()
            ?? throw new InvalidOperationException("Redis-Schlüssel fehlt.");

        private static T CreateProxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, RedisProxy>();
            ((RedisProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        public class RedisProxy : DispatchProxy
        {
            public Func<MethodInfo, object?[], object?> Handler { private get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                Handler(targetMethod ?? throw new InvalidOperationException(), args ?? []);
        }
    }
}
