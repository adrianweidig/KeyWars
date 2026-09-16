using System.Reflection;
using KeyWars.Domain;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisAttemptSessionStateStoreTests
{
    [Fact]
    public async Task AddAndUpdateBoundTheProfileAndExpiryIndexes()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));
        var current = Session(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "current");
        var updated = current with { Phase = AttemptPhase.Started, StartedAt = DateTimeOffset.UtcNow };

        await store.AddAsync(current, TimeSpan.FromHours(2));
        Assert.True(await store.TryUpdateAsync(current, updated, TimeSpan.FromHours(2)));

        Assert.Collection(
            recording.Scripts,
            invocation => AssertBoundedIndexWrite(invocation, current, repairsProfileMembership: false),
            invocation => AssertBoundedIndexWrite(invocation, current, repairsProfileMembership: true));
    }

    [Fact]
    public async Task SameProfileSessionsAreDistributedByAttemptBucketWithoutLegacyKeys()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));
        var profileId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var first = Session(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            profileId,
            DateTimeOffset.UtcNow,
            "first");
        var second = Session(
            Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef"),
            profileId,
            DateTimeOffset.UtcNow,
            "second");

        await store.AddAsync(first, TimeSpan.FromHours(2));
        await store.AddAsync(second, TimeSpan.FromHours(2));

        Assert.Equal(2, recording.Scripts.Count);
        Assert.Contains(recording.Scripts, invocation =>
            Parameter(invocation.Parameters, "profileKey").ToString() ==
            $"keywars:{{attempt-ba8}}:profile:{profileId:N}");
        Assert.Contains(recording.Scripts, invocation =>
            Parameter(invocation.Parameters, "profileKey").ToString() ==
            $"keywars:{{attempt-b4e}}:profile:{profileId:N}");
        Assert.DoesNotContain(
            recording.Scripts.SelectMany(invocation => RedisKeys(invocation.Parameters)),
            key => key.Contains("{attempt}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveUsesOneBucketLocalCompareAndDeleteScript()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));
        var session = Session(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            "remove");
        await store.AddAsync(session, TimeSpan.FromHours(2));
        recording.StringGetResponses.Enqueue(Parameter(recording.Scripts[0].Parameters, "value").ToString());
        recording.Scripts.Clear();

        var removed = await store.RemoveAsync(session.Id);

        Assert.Equal(session, removed);
        var invocation = Assert.Single(recording.Scripts);
        Assert.Contains("redis.call('del', @sessionKey)", invocation.Script.OriginalScript);
        Assert.Contains("redis.call('srem', @profileKey, @id)", invocation.Script.OriginalScript);
        Assert.Contains("redis.call('zrem', @expiryKey, @id)", invocation.Script.OriginalScript);
        AssertLuaKeysShareHashTag(invocation.Parameters, "{attempt-ba8}");
    }

    [Fact]
    public async Task RemoveProfileCleansMembersWhoseSessionValueHasExpired()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var profileId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var bucket = RedisClusterKeyspace.GetBucket(sessionId);
        var profileKey = RedisAttemptSessionStateStore.ProfileKey(profileId, bucket).ToString();
        recording.SetScanResponses[profileKey] = new Queue<RedisValue[]>([
            [(RedisValue)sessionId.ToString("N"), "kein-guid-wert"],
            []
        ]);
        var store = new RedisAttemptSessionStateStore(Connection(database));

        var removed = await store.RemoveProfileAsync(profileId);

        Assert.Empty(removed);
        Assert.Contains(recording.Scripts, invocation =>
            Parameter(invocation.Parameters, "id").ToString() == "kein-guid-wert" &&
            invocation.Script.OriginalScript.Contains("srem", StringComparison.Ordinal));
        var cleanup = Assert.Single(
            recording.Scripts,
            invocation => invocation.Script.OriginalScript.Contains("exists", StringComparison.Ordinal));
        Assert.Contains("redis.call('exists', @sessionKey)", cleanup.Script.OriginalScript);
        Assert.Contains("redis.call('srem', @profileKey, @id)", cleanup.Script.OriginalScript);
        Assert.Contains("redis.call('zrem', @expiryKey, @id)", cleanup.Script.OriginalScript);
        Assert.Equal(profileKey, Parameter(cleanup.Parameters, "profileKey").ToString());
        Assert.Equal(sessionId.ToString("N"), Parameter(cleanup.Parameters, "id").ToString());
        Assert.Equal(RedisClusterKeyspace.BucketCount, recording.ScannedSetKeys.Distinct().Count());
        Assert.All(recording.ScanPageSizes, pageSize => Assert.Equal(100, pageSize));
    }

    [Fact]
    public async Task CorruptProfileIndexCannotDeleteAnotherProfilesSession()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));
        var ownerProfileId = Guid.CreateVersion7();
        var requestedProfileId = Guid.CreateVersion7();
        var session = Session(ownerProfileId, DateTimeOffset.UtcNow, "owner");
        await store.AddAsync(session, TimeSpan.FromHours(2));
        RedisValue serialized = Parameter(recording.Scripts[0].Parameters, "value").ToString();
        recording.Scripts.Clear();
        var bucket = RedisClusterKeyspace.GetBucket(session.Id);
        var corruptProfileKey = RedisAttemptSessionStateStore.ProfileKey(requestedProfileId, bucket).ToString();
        recording.SetScanResponses[corruptProfileKey] = new Queue<RedisValue[]>([
            [session.Id.ToString("N")],
            []
        ]);
        recording.StringGetResponses.Enqueue(serialized);

        var removed = await store.RemoveProfileAsync(requestedProfileId);

        Assert.Empty(removed);
        var repair = Assert.Single(recording.Scripts);
        Assert.Contains("srem", repair.Script.OriginalScript);
        Assert.Equal(corruptProfileKey, Parameter(repair.Parameters, "profileKey").ToString());
        Assert.Equal(session.Id.ToString("N"), Parameter(repair.Parameters, "id").ToString());
    }

    [Fact]
    public async Task ProfileCleanupConvergesAcrossBoundedPagesLargerThanOneHundred()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));
        var profileId = Guid.CreateVersion7();
        const byte bucket = 42;
        var ids = FindGuids(bucket, 125)
            .Select(id => (RedisValue)id.ToString("N"))
            .ToArray();
        Assert.Equal(125, ids.Length);
        var profileKey = RedisAttemptSessionStateStore.ProfileKey(profileId, bucket).ToString();
        recording.SetScanResponses[profileKey] = new Queue<RedisValue[]>([
            ids[..100],
            ids[100..],
            []
        ]);

        var removed = await store.RemoveProfileAsync(profileId);

        Assert.Empty(removed);
        Assert.Equal(125, recording.Scripts.Count);
        Assert.Equal(3, recording.ScannedSetKeys.Count(key => key == profileKey));
        Assert.All(recording.Scripts, invocation => AssertLuaKeysShareHashTag(invocation.Parameters, "{attempt-b2a}"));
    }

    [Fact]
    public async Task MissingExpiredSessionUsesRaceSafeExpiryIndexCleanup()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var sessionId = Guid.CreateVersion7();
        var store = new RedisAttemptSessionStateStore(Connection(database));

        var removed = await store.TryRemoveExpiredAsync(
            sessionId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(2));

        Assert.Null(removed);
        var cleanup = Assert.Single(recording.Scripts);
        Assert.Contains("redis.call('exists', @sessionKey)", cleanup.Script.OriginalScript);
        Assert.Contains("redis.call('zrem', @expiryKey, @id)", cleanup.Script.OriginalScript);
        Assert.Equal(sessionId.ToString("N"), Parameter(cleanup.Parameters, "id").ToString());
        AssertLuaKeysShareHashTag(cleanup.Parameters, RedisClusterKeyspace.GetHashTag("attempt", sessionId));
    }

    [Fact]
    public async Task ExpiryReadsEightBucketsPerCallAndCoversAllBucketsFairly()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));

        for (var call = 0; call < 32; call++)
        {
            Assert.Empty(await store.GetExpiredIdsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(2)));
        }

        Assert.Equal(256, recording.ExpiryReads.Count);
        Assert.Equal(256, recording.ExpiryReads.Select(read => read.Key).Distinct().Count());
        Assert.All(recording.ExpiryReads, read => Assert.Equal(13, read.Take));
    }

    [Fact]
    public async Task ExpiryMergeIsGloballyOrderedLimitedAndRejectsWrongBucketMembers()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var expected = new List<(Guid Id, double Score)>();
        for (byte bucket = 0; bucket < 8; bucket++)
        {
            var entries = new List<SortedSetEntry>();
            for (var offset = 0; offset < 13; offset++)
            {
                var id = FindGuid(bucket, offset);
                var score = 1_000 - (bucket * 13 + offset);
                entries.Add(new SortedSetEntry(id.ToString("N"), score));
                expected.Add((id, score));
            }

            if (bucket == 0)
            {
                entries[0] = new SortedSetEntry(FindGuid(250, 0).ToString("N"), -1);
                expected.RemoveAt(0);
            }

            recording.ExpiryResponses[RedisAttemptSessionStateStore.ExpiryKey(bucket).ToString()] = entries.ToArray();
        }
        var store = new RedisAttemptSessionStateStore(Connection(database));

        var result = await store.GetExpiredIdsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(2));

        Assert.Equal(100, result.Count);
        Assert.Equal(
            expected.OrderBy(item => item.Score).ThenBy(item => item.Id).Take(100).Select(item => item.Id),
            result);
    }

    [Fact]
    public async Task ExpiryRepairsCorruptHeadBeforeReturningDueMemberWithoutDeletingForeignSessions()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        const byte bucket = 0;
        var corruptEntries = Enumerable.Range(0, 7)
            .Select(index => new SortedSetEntry($"not-a-guid-{index}", index))
            .Concat(FindGuids(250, 6).Select((id, index) =>
                new SortedSetEntry(id.ToString("N"), index + 7)))
            .ToArray();
        var dueId = FindGuid(bucket, 10_000);
        var expiryKey = RedisAttemptSessionStateStore.ExpiryKey(bucket).ToString();
        recording.ExpiryResponseSequences[expiryKey] = new Queue<SortedSetEntry[]>(
        [
            corruptEntries,
            [new SortedSetEntry(dueId.ToString("N"), 13)]
        ]);
        var store = new RedisAttemptSessionStateStore(Connection(database));

        var result = await store.GetExpiredIdsAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(2));

        Assert.Equal([dueId], result);
        var cleanup = Assert.Single(recording.ExpiryRemovals);
        Assert.Equal(expiryKey, cleanup.Key);
        Assert.Equal(corruptEntries.Select(entry => entry.Element), cleanup.Members);
        Assert.Equal(2, recording.ExpiryReads.Count(read => read.Key == expiryKey));
        Assert.Empty(recording.StringGetKeys);
        Assert.Empty(recording.Scripts);
    }

    [Fact]
    public async Task LifecycleLeaseUsesTheAttemptBucketAndReleasesAtomically()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var store = new RedisAttemptSessionStateStore(Connection(database));
        var attemptId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        await using (var lease = await store.AcquireLifecycleLockAsync(attemptId))
        {
            lease.ThrowIfLost();
        }

        Assert.Equal(
            "keywars:{attempt-ba8}:lock:00112233445566778899aabbccddeeff",
            Assert.Single(recording.StringSetKeys));
        var release = Assert.Single(recording.Scripts);
        Assert.Contains("redis.call('del', @key)", release.Script.OriginalScript);
        AssertLuaKeysShareHashTag(release.Parameters, "{attempt-ba8}");
    }

    private static void AssertBoundedIndexWrite(
        ScriptInvocation invocation,
        AttemptSession session,
        bool repairsProfileMembership)
    {
        var source = invocation.Script.OriginalScript;
        Assert.Contains("redis.call('pttl', @profileKey)", source);
        Assert.Contains("if profileTtl < tonumber(@indexTtlMilliseconds)", source);
        Assert.Contains("redis.call('pexpire', @profileKey, @indexTtlMilliseconds)", source);
        Assert.Contains("redis.call('pttl', @expiryKey)", source);
        Assert.Contains("if expiryTtl < tonumber(@indexTtlMilliseconds)", source);
        Assert.Contains("redis.call('pexpire', @expiryKey, @indexTtlMilliseconds)", source);
        if (repairsProfileMembership)
        {
            Assert.Contains("redis.call('sadd', @profileKey, @id)", source);
        }

        var sessionTtl = Convert.ToInt64(Parameter(invocation.Parameters, "ttlMilliseconds"));
        var indexTtl = Convert.ToInt64(Parameter(invocation.Parameters, "indexTtlMilliseconds"));
        Assert.True(indexTtl > sessionTtl);
        AssertLuaKeysShareHashTag(invocation.Parameters, RedisClusterKeyspace.GetHashTag("attempt", session.Id));
    }

    private static AttemptSession Session(Guid profileId, DateTimeOffset preparedAt, string nonce) =>
        Session(Guid.CreateVersion7(), profileId, preparedAt, nonce);

    private static AttemptSession Session(Guid id, Guid profileId, DateTimeOffset preparedAt, string nonce) =>
        new(
            id,
            profileId,
            "alpha beta",
            TrainingMode.Text,
            preparedAt,
            null,
            nonce,
            AttemptPhase.Prepared);

    private static Guid FindGuid(byte bucket, int offset)
    {
        for (var candidate = offset; ; candidate += 13)
        {
            var id = new Guid(candidate, 0, 0, new byte[8]);
            if (RedisClusterKeyspace.GetBucket(id) == bucket)
            {
                return id;
            }
        }
    }

    private static IReadOnlyList<Guid> FindGuids(byte bucket, int count)
    {
        var result = new List<Guid>(count);
        for (var candidate = 0; result.Count < count; candidate++)
        {
            var id = new Guid(candidate, 0, 0, new byte[8]);
            if (RedisClusterKeyspace.GetBucket(id) == bucket)
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static void AssertLuaKeysShareHashTag(object parameters, string expectedTag)
    {
        var keys = RedisKeys(parameters).ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Contains(expectedTag, key, StringComparison.Ordinal));
    }

    private static IEnumerable<string> RedisKeys(object parameters) =>
        parameters.GetType().GetProperties()
            .Where(property => property.PropertyType == typeof(RedisKey))
            .Select(property => ((RedisKey)property.GetValue(parameters)!).ToString());

    private static object Parameter(object parameters, string name) =>
        parameters.GetType().GetProperty(name)?.GetValue(parameters)
        ?? throw new InvalidOperationException($"Redis-Parameter {name} fehlt.");

    private static IConnectionMultiplexer Connection(IDatabase database)
    {
        var connection = DispatchProxy.Create<IConnectionMultiplexer, RecordingConnection>();
        ((RecordingConnection)(object)connection).Database = database;
        return connection;
    }

    public sealed record ScriptInvocation(LuaScript Script, object Parameters);

    public class RecordingConnection : DispatchProxy
    {
        public IDatabase Database { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase)
                ? Database
                : throw new NotSupportedException(
                    $"Redis-Verbindungs-Testdouble unterstützt {targetMethod?.Name} nicht.");
    }

    public class RecordingDatabase : DispatchProxy
    {
        public Dictionary<string, Queue<RedisValue[]>> SetScanResponses { get; } = [];
        public Dictionary<string, SortedSetEntry[]> ExpiryResponses { get; } = [];
        public Dictionary<string, Queue<SortedSetEntry[]>> ExpiryResponseSequences { get; } = [];
        public List<ScriptInvocation> Scripts { get; } = [];
        public List<string> ScannedSetKeys { get; } = [];
        public List<int> ScanPageSizes { get; } = [];
        public List<(string Key, long Take)> ExpiryReads { get; } = [];
        public List<(string Key, RedisValue[] Members)> ExpiryRemovals { get; } = [];
        public List<string> StringGetKeys { get; } = [];
        public List<string> StringSetKeys { get; } = [];
        public Queue<RedisValue> StringGetResponses { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("Redis-Methode fehlt.");
            var arguments = args ?? [];
            if (method.Name == nameof(IDatabaseAsync.ScriptEvaluateAsync) && arguments[0] is LuaScript script)
            {
                Scripts.Add(new ScriptInvocation(script, arguments[1]!));
                return Task.FromResult(RedisResult.Create((RedisValue)1));
            }

            if (method.Name == nameof(IDatabaseAsync.StringGetAsync))
            {
                StringGetKeys.Add(((RedisKey)arguments[0]!).ToString());
                return Task.FromResult(
                    StringGetResponses.Count == 0
                        ? RedisValue.Null
                        : StringGetResponses.Dequeue());
            }

            if (method.Name == nameof(IDatabaseAsync.StringSetAsync))
            {
                StringSetKeys.Add(((RedisKey)arguments[0]!).ToString());
                return Task.FromResult(true);
            }

            if (method.Name == nameof(IDatabaseAsync.SetScanAsync))
            {
                var key = ((RedisKey)arguments[0]!).ToString();
                ScannedSetKeys.Add(key);
                ScanPageSizes.Add((int)arguments[2]!);
                var values = SetScanResponses.TryGetValue(key, out var responses) && responses.Count > 0
                    ? responses.Dequeue()
                    : [];
                return Enumerate(values);
            }

            if (method.Name == nameof(IDatabaseAsync.SortedSetRangeByScoreWithScoresAsync))
            {
                var key = ((RedisKey)arguments[0]!).ToString();
                var take = (long)arguments[6]!;
                ExpiryReads.Add((key, take));
                return Task.FromResult(
                    ExpiryResponseSequences.TryGetValue(key, out var responses) && responses.Count > 0
                        ? responses.Dequeue()
                        : ExpiryResponses.TryGetValue(key, out var response)
                        ? response
                        : []);
            }

            if (method.Name == nameof(IDatabaseAsync.SortedSetRemoveAsync) && arguments[1] is RedisValue[] members)
            {
                var key = ((RedisKey)arguments[0]!).ToString();
                ExpiryRemovals.Add((key, members));
                return Task.FromResult((long)members.Length);
            }

            throw new NotSupportedException($"Redis-Testdouble unterstützt {method.Name} nicht.");
        }

        private static async IAsyncEnumerable<RedisValue> Enumerate(IEnumerable<RedisValue> values)
        {
            foreach (var value in values)
            {
                yield return value;
            }

            await Task.CompletedTask;
        }
    }
}
