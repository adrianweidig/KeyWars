using System.Collections.Concurrent;
using System.Reflection;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisLiveStateShardingTests
{
    [Theory]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "a8")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "5a")]
    public void PresenceKeysUseTheCanonicalProfileBucket(string value, string bucket)
    {
        var profileId = Guid.Parse(value);
        var expectedTag = $"presence-b{bucket}";

        Assert.Equal(expectedTag, HashTag(RedisLivePresenceStateStore.ProfileKey(profileId)));
        Assert.Equal(expectedTag, HashTag(RedisLivePresenceStateStore.LockKey(profileId)));
        Assert.Equal(expectedTag, HashTag(RedisLivePresenceStateStore.ConnectionKey(profileId, "connection-1")));
        Assert.Equal(expectedTag, HashTag(RedisLivePresenceStateStore.ExpiryKey(profileId)));
    }

    [Theory]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "a8")]
    [InlineData("0198a1b2-c3d4-7e5f-8123-456789abcdef", "4e")]
    public void ProgressKeysUseTheCanonicalRoomBucket(string value, string bucket)
    {
        var roomId = Guid.Parse(value);
        var expectedTag = $"progress-b{bucket}";
        var keys = new[]
        {
            RedisLiveProgressRelay.PendingKey(roomId),
            RedisLiveProgressRelay.LatestKey(roomId),
            RedisLiveProgressRelay.SentKey(roomId),
            RedisLiveProgressRelay.BlockedKey(roomId),
            RedisLiveProgressRelay.LockKey(roomId),
            RedisLiveProgressRelay.DueRoomsKey(roomId)
        };

        Assert.All(keys, key => Assert.Equal(expectedTag, HashTag(key)));
        Assert.Equal($"keywars:{{progress-b{bucket}}}:due", RedisLiveProgressRelay.DueRoomsKey(roomId).ToString());
    }

    [Fact]
    public void PresenceDisconnectIsProfileTargetedAndScriptsStayBucketLocal()
    {
        var disconnect = typeof(KeyWars.Services.ILivePresenceStateStore)
            .GetMethod(nameof(KeyWars.Services.ILivePresenceStateStore.RemoveConnectionAsync));
        Assert.Equal([typeof(Guid), typeof(string), typeof(CancellationToken)], disconnect?.GetParameters().Select(item => item.ParameterType));

        var write = StaticScript(typeof(RedisLivePresenceStateStore), "WriteConnectionScript").OriginalScript;
        var delete = StaticScript(typeof(RedisLivePresenceStateStore), "DeleteConnectionScript").OriginalScript;
        Assert.Contains("get', @lockKey", write, StringComparison.Ordinal);
        Assert.Contains("set', @connectionKey", write, StringComparison.Ordinal);
        Assert.Contains("sadd', @profileKey", write, StringComparison.Ordinal);
        Assert.Contains("zadd', @expiryKey", write, StringComparison.Ordinal);
        Assert.Contains("del', @connectionKey", delete, StringComparison.Ordinal);
        Assert.Contains("srem', @profileKey", delete, StringComparison.Ordinal);
        Assert.Contains("zrem', @expiryKey", delete, StringComparison.Ordinal);
        Assert.DoesNotContain("directory", write, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("locator", delete, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgressScriptsPreserveSequenceAndAckFencesWithoutGlobalKeys()
    {
        var enqueue = StaticScript(typeof(RedisLiveProgressRelay), "EnqueueScript").OriginalScript;
        var acknowledge = StaticScript(typeof(RedisLiveProgressRelay), "AcknowledgeBatchScript").OriginalScript;
        var cleanup = StaticScript(typeof(RedisLiveProgressRelay), "CleanupBatchScript").OriginalScript;
        var purge = StaticScript(typeof(RedisLiveProgressRelay), "PurgeParticipantScript").OriginalScript;

        Assert.Contains("@roomVersion", enqueue, StringComparison.Ordinal);
        Assert.Contains("@sequence", enqueue, StringComparison.Ordinal);
        Assert.Contains("@dueKey", enqueue, StringComparison.Ordinal);
        Assert.Contains("update.RoomVersion", acknowledge, StringComparison.Ordinal);
        Assert.Contains("update.ParticipantSequence", acknowledge, StringComparison.Ordinal);
        Assert.Contains("ipairs(updates)", acknowledge, StringComparison.Ordinal);
        Assert.Contains("get', @lockKey", acknowledge, StringComparison.Ordinal);
        Assert.Contains("zrem', @dueKey", cleanup, StringComparison.Ordinal);
        Assert.Contains("get', @lockKey", purge, StringComparison.Ordinal);
        Assert.Contains("hdel', @pendingKey, @participant", purge, StringComparison.Ordinal);
        Assert.Contains("hdel', @latestKey, @participant", purge, StringComparison.Ordinal);
        Assert.Contains("hdel', @sentKey, @participant", purge, StringComparison.Ordinal);
        Assert.Contains("hset', @blockedKey, @participant", purge, StringComparison.Ordinal);
        Assert.Contains("hlen', @pendingKey", purge, StringComparison.Ordinal);
        Assert.Contains("zrem', @dueKey", purge, StringComparison.Ordinal);
        Assert.Contains("hexists', @blockedKey, @participant", enqueue, StringComparison.Ordinal);
        Assert.DoesNotContain("keywars:{progress}", enqueue, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressBroadcastBatchesSentWatermarksAndAcknowledgements()
    {
        var source = File.ReadAllText(FindSource("RedisLiveProgressRelay.cs"));
        var acknowledge = StaticScript(typeof(RedisLiveProgressRelay), "AcknowledgeBatchScript").OriginalScript;

        Assert.Contains("HashGetAsync(SentKey(roomId), participants)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HashGetAsync(SentKey(roomId), participant)", source, StringComparison.Ordinal);
        Assert.Contains("ipairs(updates)", acknowledge, StringComparison.Ordinal);
        Assert.Contains("redis.call('get', @lockKey)", acknowledge, StringComparison.Ordinal);
    }

    [Fact]
    public void PresenceListingSnapshotsBeforeBoundedCleanupWithoutLegacyKeys()
    {
        var source = File.ReadAllText(FindSource("RedisLivePresenceStateStore.cs"));

        Assert.Contains("MaximumProfileIndexSize = 1024", source, StringComparison.Ordinal);
        Assert.Contains("SetLengthAsync", source, StringComparison.Ordinal);
        Assert.Contains("SetMembersAsync", source, StringComparison.Ordinal);
        Assert.Contains("CleanupBatchSize = 64", source, StringComparison.Ordinal);
        Assert.Contains("SetScanAsync", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("var members = await SnapshotProfileMembersAsync", StringComparison.Ordinal) <
            source.IndexOf("foreach (var batch in stale.Chunk", StringComparison.Ordinal),
            "Der vollständige Snapshot muss vor der ersten Stale-Mutation abgeschlossen sein.");
        Assert.DoesNotContain("keywars:{presence}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("presence-directory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressPollingIsGloballyBoundedAndRecoveryCoversEveryBucket()
    {
        Assert.Equal(64, RedisLiveProgressRelay.MaxDueRoomsPerPoll);
        Assert.True(
            RedisLiveProgressRelay.MaxDueRoomsPerBucket * RedisLiveProgressRelay.MaxBucketVisitsPerPoll <=
            RedisLiveProgressRelay.MaxDueRoomsPerPoll);

        var cursor = 0;
        var firstCycle = Enumerable.Range(0, RedisClusterKeyspace.BucketCount)
            .Select(_ => RedisLiveProgressRelay.NextRecoveryBucket(ref cursor))
            .ToArray();

        Assert.Equal(Enumerable.Range(0, 256).Select(value => (byte)value), firstCycle);
        Assert.Equal((byte)0, RedisLiveProgressRelay.NextRecoveryBucket(ref cursor));
        var source = File.ReadAllText(FindSource("RedisLiveProgressRelay.cs"));
        Assert.DoesNotContain("keywars:{progress}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("for (var bucket = 0; bucket < RedisClusterKeyspace.BucketCount", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("foreach (var bucket in reactivate)", StringComparison.Ordinal) >
            source.IndexOf("while (processed < MaxDueRoomsPerPoll", StringComparison.Ordinal),
            "Ein noch fälliger Bucket darf innerhalb desselben Polls nicht erneut dieselben Räume claimen.");
    }

    [Fact]
    public async Task ActiveBucketSchedulerDeduplicatesConcurrentWakes()
    {
        var scheduler = new RedisDueBucketScheduler();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var bucket = 0; bucket < RedisClusterKeyspace.BucketCount; bucket++)
            {
                scheduler.Activate((byte)((bucket + worker) % RedisClusterKeyspace.BucketCount));
            }
        })));

        Assert.Equal(256, scheduler.Count);
        var drained = new HashSet<byte>();
        while (scheduler.TryTake(out var bucket))
        {
            Assert.True(drained.Add(bucket));
        }

        Assert.Equal(256, drained.Count);
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public void ActiveBucketSchedulerRequeuesBehindAlreadyWaitingBuckets()
    {
        var scheduler = new RedisDueBucketScheduler();
        scheduler.Activate(1);
        scheduler.Activate(2);

        Assert.True(scheduler.TryTake(out var first));
        scheduler.Activate(first);
        Assert.True(scheduler.TryTake(out var second));
        Assert.True(scheduler.TryTake(out var third));

        Assert.Equal((byte)1, first);
        Assert.Equal((byte)2, second);
        Assert.Equal((byte)1, third);
    }

    [Fact]
    public async Task ProfileCleanupRemovesARecoveredConnectionMemberInsideOneBucket()
    {
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var connectionKey = RedisLivePresenceStateStore.ConnectionKey(profileId, "partially-written-connection");
        var database = DispatchProxy.Create<IDatabase, PresenceDatabase>();
        var recording = (PresenceDatabase)(object)database;
        recording.ScanResponses.Enqueue([connectionKey.ToString()]);
        recording.ScanResponses.Enqueue([]);
        var store = new RedisLivePresenceStateStore(
            PresenceConnection(database),
            Options.Create(new LiveOptions()),
            TimeProvider.System);

        await store.RemoveProfileAsync(profileId);

        var deletion = Assert.Single(
            recording.Scripts,
            invocation => invocation.Script.OriginalScript.Contains(
                "srem', @profileKey, @connectionKey",
                StringComparison.Ordinal));
        var lockKey = (RedisKey)Parameter(deletion.Parameters, "lockKey");
        var profileKey = (RedisKey)Parameter(deletion.Parameters, "profileKey");
        var storedConnectionKey = (RedisKey)Parameter(deletion.Parameters, "connectionKey");
        Assert.Equal("presence-ba8", HashTag(lockKey));
        Assert.Equal(HashTag(lockKey), HashTag(profileKey));
        Assert.Equal(HashTag(lockKey), HashTag(storedConnectionKey));
        Assert.Equal(2, recording.ScanCalls);
    }

    [Fact]
    public async Task ProfileCleanupRemovesExactAndOrphanedExpiryMembersWithoutTouchingAnotherProfile()
    {
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var otherProfileId = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff");
        var roomId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var connectionId = "private-connection";
        var orphanedConnectionId = "orphaned-private-connection";
        var connectionKey = RedisLivePresenceStateStore.ConnectionKey(profileId, connectionId);
        var database = DispatchProxy.Create<IDatabase, PresenceDatabase>();
        var recording = (PresenceDatabase)(object)database;
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web);
        recording.ScanResponses.Enqueue([connectionKey.ToString()]);
        recording.ScanResponses.Enqueue([]);
        recording.Strings[connectionKey.ToString()] = System.Text.Json.JsonSerializer.Serialize(new
        {
            connectionId,
            profileId,
            roomId,
            lastSeenAt = DateTimeOffset.UtcNow
        }, jsonOptions);
        var exactExpiryMember = System.Text.Json.JsonSerializer.Serialize(
            new ExpiredPresenceClaim(connectionId, profileId, roomId),
            jsonOptions);
        var orphanedExpiryMember = System.Text.Json.JsonSerializer.Serialize(
            new ExpiredPresenceClaim(orphanedConnectionId, profileId, roomId),
            jsonOptions);
        var foreignExpiryMember = System.Text.Json.JsonSerializer.Serialize(
            new ExpiredPresenceClaim("foreign-connection", otherProfileId, roomId),
            jsonOptions);
        recording.SortedSetScanResponses.Enqueue(
        [
            new SortedSetEntry(orphanedExpiryMember, 1),
            new SortedSetEntry(foreignExpiryMember, 2)
        ]);
        recording.SortedSetScanResponses.Enqueue([new SortedSetEntry(foreignExpiryMember, 2)]);
        var store = new RedisLivePresenceStateStore(
            PresenceConnection(database),
            Options.Create(new LiveOptions()),
            TimeProvider.System);

        await store.RemoveProfileAsync(profileId);

        var connectionDeletion = Assert.Single(
            recording.Scripts,
            invocation => invocation.Script.OriginalScript.Contains(
                "del', @connectionKey",
                StringComparison.Ordinal));
        Assert.Equal(exactExpiryMember, Parameter(connectionDeletion.Parameters, "connectionMember")?.ToString());
        var orphanCleanup = Assert.Single(
            recording.Scripts,
            invocation => invocation.Script.OriginalScript.Contains(
                "cjson.decode(@members)",
                StringComparison.Ordinal));
        var removedMembers = System.Text.Json.JsonSerializer.Deserialize<string[]>(
            Parameter(orphanCleanup.Parameters, "members").ToString()!);
        Assert.NotNull(removedMembers);
        Assert.Equal([orphanedExpiryMember], removedMembers);
        Assert.Equal(2, recording.SortedSetScanCalls);
    }

    [Fact]
    public async Task ConnectionLimitCannotBeHiddenByRehashLikeSnapshotChangeAndSixtyFourStaleMembers()
    {
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var roomId = Guid.CreateVersion7();
        var database = DispatchProxy.Create<IDatabase, PresenceDatabase>();
        var recording = (PresenceDatabase)(object)database;
        var stale = Enumerable.Range(0, 64)
            .Select(index => (RedisValue)RedisLivePresenceStateStore.ConnectionKey(profileId, $"stale-{index}").ToString())
            .ToArray();
        var valid = Enumerable.Range(0, 20)
            .Select(index => (RedisValue)RedisLivePresenceStateStore.ConnectionKey(profileId, $"valid-{index}").ToString())
            .ToArray();
        recording.SetMembers(stale.Concat(valid));
        recording.SetLengthResponses.Enqueue(84);
        recording.SetLengthResponses.Enqueue(83);
        recording.SetLengthResponses.Enqueue(84);
        recording.SetLengthResponses.Enqueue(84);
        recording.SetMemberResponses.Enqueue(stale);
        recording.SetMemberResponses.Enqueue(stale.Concat(valid).ToArray());
        for (var index = 0; index < valid.Length; index++)
        {
            recording.Strings[valid[index].ToString()] = System.Text.Json.JsonSerializer.Serialize(new
            {
                ConnectionId = $"valid-{index}",
                ProfileId = profileId,
                RoomId = roomId,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }

        var store = new RedisLivePresenceStateStore(
            PresenceConnection(database),
            Options.Create(new LiveOptions { MaxConnectionsPerUser = 20 }),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.EnsureCanConnectAsync(profileId, "new-connection"));

        Assert.Contains("maximal 20", exception.Message, StringComparison.Ordinal);
        Assert.Equal(85, recording.StringReads);
        Assert.Equal(2, recording.SetMemberCalls);
        Assert.Equal(0, recording.ScanCalls);
        Assert.Equal(64, recording.Scripts.Count(invocation =>
            invocation.Script.OriginalScript.Contains("srem', @profileKey, @connectionKey", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RepeatedProfileIndexMutationFailsClosedBeforeCleanup()
    {
        var profileId = Guid.CreateVersion7();
        var database = DispatchProxy.Create<IDatabase, PresenceDatabase>();
        var recording = (PresenceDatabase)(object)database;
        var stale = Enumerable.Range(0, 64).Select(index => (RedisValue)$"stale-{index}").ToArray();
        recording.SetMembers(stale);
        foreach (var count in new long[] { 64, 63, 64, 63 })
        {
            recording.SetLengthResponses.Enqueue(count);
        }
        recording.SetMemberResponses.Enqueue(stale);
        recording.SetMemberResponses.Enqueue(stale);
        var store = new RedisLivePresenceStateStore(
            PresenceConnection(database),
            Options.Create(new LiveOptions { MaxConnectionsPerUser = 20 }),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.EnsureCanConnectAsync(profileId, "new-connection"));

        Assert.Contains("verändert", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            recording.Scripts,
            invocation => invocation.Script.OriginalScript.Contains(
                "srem', @profileKey",
                StringComparison.Ordinal));
        Assert.Equal(1, recording.StringReads);
    }

    [Fact]
    public async Task AbnormallyLargeProfileIndexFailsClosedWithoutAnUnboundedScan()
    {
        var profileId = Guid.CreateVersion7();
        var database = DispatchProxy.Create<IDatabase, PresenceDatabase>();
        var recording = (PresenceDatabase)(object)database;
        recording.SetMembers(Enumerable.Range(0, 1_025).Select(index => (RedisValue)$"corrupt-{index}"));
        var store = new RedisLivePresenceStateStore(
            PresenceConnection(database),
            Options.Create(new LiveOptions { MaxConnectionsPerUser = 20 }),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.EnsureCanConnectAsync(profileId, "new-connection"));

        Assert.Contains("ungewöhnlich groß", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, recording.ScanCalls);
    }

    [Fact]
    public async Task ExpiryClaimRemovesMalformedHeadAndReturnsValidDueConnection()
    {
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var database = DispatchProxy.Create<IDatabase, PresenceDatabase>();
        var recording = (PresenceDatabase)(object)database;
        var valid = System.Text.Json.JsonSerializer.Serialize(
            new ExpiredPresenceClaim("connection", profileId, roomId),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        recording.ScriptResults.Enqueue(RedisResult.Create(
            [(RedisValue)"not-json", (RedisValue)valid]));
        var store = new RedisLivePresenceStateStore(
            PresenceConnection(database),
            Options.Create(new LiveOptions()),
            TimeProvider.System);

        var batch = await ((ILivePresenceExpiryStore)store).ClaimExpiredAsync(
            RedisClusterKeyspace.GetBucket(profileId),
            16,
            CancellationToken.None);

        var claim = Assert.Single(batch.Claims);
        Assert.Equal(profileId, claim.ProfileId);
        Assert.Equal(roomId, claim.RoomId);
        Assert.Equal(1, batch.InvalidMembers);
        Assert.Equal("not-json", Assert.Single(recording.SortedSetRemovals));
    }

    [Fact]
    public async Task BlockingSenderDoesNotPreventAnotherBucketFromSending()
    {
        var blockedRoom = GuidForDifferentBucket(Guid.Empty, RedisClusterKeyspace.GetBucket(Guid.Empty));
        var fastRoom = GuidForDifferentBucket(blockedRoom, RedisClusterKeyspace.GetBucket(blockedRoom));
        var sender = new BlockingProgressSender(blockedRoom);
        var now = DateTimeOffset.UtcNow;
        var batches = new[]
        {
            new LiveProgressBatch(blockedRoom, 1, now, []),
            new LiveProgressBatch(fastRoom, 1, now, [])
        };

        var processing = RedisLiveProgressRelay.RunConcurrentlyAsync(
            batches,
            (batch, cancellationToken) => RedisLiveProgressRelay.SendWithDeadlineAsync(
                sender,
                batch.RoomId,
                batch,
                TimeSpan.FromMilliseconds(100),
                TimeProvider.System,
                cancellationToken),
            CancellationToken.None);

        await sender.FastSend.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => processing);
        Assert.Contains(fastRoom, sender.SentRooms);
    }

    private static LuaScript StaticScript(Type type, string name) =>
        (LuaScript)(type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException($"Redis-Skript {name} fehlt."));

    private static string HashTag(RedisKey key)
    {
        var value = key.ToString();
        var start = value.IndexOf('{');
        var end = value.IndexOf('}', start + 1);
        return start >= 0 && end > start + 1 ? value[(start + 1)..end] : value;
    }

    private static string FindSource(string fileName)
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
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(fileName);
    }

    private static object Parameter(object parameters, string name) =>
        parameters.GetType().GetProperty(name)?.GetValue(parameters)
        ?? throw new InvalidOperationException($"Redis-Parameter {name} fehlt.");

    private static IConnectionMultiplexer PresenceConnection(IDatabase database)
    {
        var connection = DispatchProxy.Create<IConnectionMultiplexer, PresenceMultiplexer>();
        ((PresenceMultiplexer)(object)connection).Database = database;
        return connection;
    }

    public sealed record ScriptInvocation(LuaScript Script, object Parameters);

    public class PresenceMultiplexer : DispatchProxy
    {
        public IDatabase Database { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase)
                ? Database
                : throw new NotSupportedException(targetMethod?.Name);
    }

    public class PresenceDatabase : DispatchProxy
    {
        public Queue<RedisValue[]> ScanResponses { get; } = [];
        public Queue<RedisValue[]> SetMemberResponses { get; } = [];
        public Queue<long> SetLengthResponses { get; } = [];
        public Queue<SortedSetEntry[]> SortedSetScanResponses { get; } = [];
        public List<ScriptInvocation> Scripts { get; } = [];
        public Queue<RedisResult> ScriptResults { get; } = [];
        public List<RedisValue> SortedSetRemovals { get; } = [];
        public int ScanCalls { get; private set; }
        public int SetMemberCalls { get; private set; }
        public int StringReads { get; private set; }
        public int SortedSetScanCalls { get; private set; }
        public Dictionary<string, RedisValue> Strings { get; } = [];
        private RedisValue[] members = [];

        public void SetMembers(IEnumerable<RedisValue> values) => members = values.ToArray();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("Redis-Methode fehlt.");
            var arguments = args ?? [];
            if (method.Name == nameof(IDatabaseAsync.StringSetAsync))
            {
                return Task.FromResult(true);
            }

            if (method.Name == nameof(IDatabaseAsync.StringGetAsync))
            {
                var key = ((RedisKey)arguments[0]!).ToString();
                if (!key.Contains(":lock:", StringComparison.Ordinal))
                {
                    StringReads++;
                }

                return Task.FromResult(Strings.GetValueOrDefault(key, RedisValue.Null));
            }

            if (method.Name == nameof(IDatabaseAsync.SetLengthAsync))
            {
                return Task.FromResult(SetLengthResponses.Count > 0
                    ? SetLengthResponses.Dequeue()
                    : (long)members.Length);
            }

            if (method.Name == nameof(IDatabaseAsync.SetMembersAsync))
            {
                SetMemberCalls++;
                return Task.FromResult(SetMemberResponses.Count > 0
                    ? SetMemberResponses.Dequeue()
                    : members);
            }

            if (method.Name == nameof(IDatabaseAsync.SetScanAsync))
            {
                ScanCalls++;
                return ScanResponses.Count == 0
                    ? Scan(members)
                    : Scan(ScanResponses.Dequeue());
            }

            if (method.Name == nameof(IDatabaseAsync.SortedSetScanAsync))
            {
                SortedSetScanCalls++;
                return ScanSortedSet(SortedSetScanResponses.Count == 0
                    ? []
                    : SortedSetScanResponses.Dequeue());
            }

            if (method.Name == nameof(IDatabaseAsync.ScriptEvaluateAsync) && arguments[0] is LuaScript script)
            {
                Scripts.Add(new ScriptInvocation(script, arguments[1]!));
                return Task.FromResult(ScriptResults.Count > 0
                    ? ScriptResults.Dequeue()
                    : RedisResult.Create((RedisValue)1));
            }

            if (method.Name == nameof(IDatabaseAsync.SortedSetRemoveAsync))
            {
                if (arguments[1] is RedisValue[] values)
                {
                    SortedSetRemovals.AddRange(values);
                    return Task.FromResult((long)values.Length);
                }
                else if (arguments[1] is RedisValue value)
                {
                    SortedSetRemovals.Add(value);
                }

                return Task.FromResult(true);
            }

            throw new NotSupportedException(method.Name);
        }

        private static async IAsyncEnumerable<RedisValue> Scan(IEnumerable<RedisValue> values)
        {
            foreach (var value in values)
            {
                yield return value;
            }

            await Task.CompletedTask;
        }

        private static async IAsyncEnumerable<SortedSetEntry> ScanSortedSet(
            IEnumerable<SortedSetEntry> values)
        {
            foreach (var value in values)
            {
                yield return value;
            }

            await Task.CompletedTask;
        }
    }

    private static Guid GuidForDifferentBucket(Guid excluded, byte excludedBucket)
    {
        for (var value = 1; value < 10_000; value++)
        {
            var candidate = new Guid(value, 0, 0, new byte[8]);
            if (candidate != excluded && RedisClusterKeyspace.GetBucket(candidate) != excludedBucket)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Kein zweiter Redis-Bucket gefunden.");
    }

    private sealed class BlockingProgressSender(Guid blockedRoom) : ILiveProgressSender
    {
        private readonly TaskCompletionSource fastSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<Guid> SentRooms { get; } = [];
        public Task FastSend => fastSend.Task;

        public async Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken)
        {
            if (roomId == blockedRoom)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            SentRooms.Add(roomId);
            fastSend.TrySetResult();
        }
    }
}
