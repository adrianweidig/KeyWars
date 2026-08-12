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
            invocation => AssertBoundedIndexWrite(invocation, repairsProfileMembership: false),
            invocation => AssertBoundedIndexWrite(invocation, repairsProfileMembership: true));
    }

    [Fact]
    public async Task RemoveProfileCleansMembersWhoseSessionValueHasExpired()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var profileId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        recording.SetMemberResponses.Enqueue([(RedisValue)sessionId.ToString("N"), "kein-guid-wert"]);
        recording.SetMemberResponses.Enqueue([]);
        var store = new RedisAttemptSessionStateStore(Connection(database));

        var removed = await store.RemoveProfileAsync(profileId);

        Assert.Empty(removed);
        Assert.Equal("kein-guid-wert", Assert.Single(recording.RemovedSetMembers).Member);
        var cleanup = Assert.Single(recording.Scripts);
        Assert.Contains("redis.call('exists', @sessionKey)", cleanup.Script.OriginalScript);
        Assert.Contains("redis.call('srem', @profileKey, @id)", cleanup.Script.OriginalScript);
        Assert.Contains("redis.call('zrem', @expiryKey, @id)", cleanup.Script.OriginalScript);
        Assert.Equal($"keywars:{{attempt}}:profile:{profileId:N}", Parameter(cleanup.Parameters, "profileKey").ToString());
        Assert.Equal(sessionId.ToString("N"), Parameter(cleanup.Parameters, "id").ToString());
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
    }

    private static void AssertBoundedIndexWrite(ScriptInvocation invocation, bool repairsProfileMembership)
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
        public Queue<RedisValue[]> SetMemberResponses { get; } = [];
        public List<ScriptInvocation> Scripts { get; } = [];
        public List<(string Key, string Member)> RemovedSetMembers { get; } = [];

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
                return Task.FromResult(RedisValue.Null);
            }

            if (method.Name == nameof(IDatabaseAsync.SetMembersAsync))
            {
                return Task.FromResult(SetMemberResponses.Dequeue());
            }

            if (method.Name == nameof(IDatabaseAsync.SetRemoveAsync))
            {
                RemovedSetMembers.Add((((RedisKey)arguments[0]!).ToString(), ((RedisValue)arguments[1]!).ToString()));
                return Task.FromResult(true);
            }

            throw new NotSupportedException($"Redis-Testdouble unterstützt {method.Name} nicht.");
        }
    }
}
