using System.Reflection;
using KeyWars.Infrastructure.Cluster;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisProfileAccessGateKeyspaceTests
{
    [Theory]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "a8")]
    [InlineData("0198a1b2-c3d4-7e5f-8123-456789abcdef", "4e")]
    public void ProfileKeysUseTheStableProfileBucket(string profile, string bucket)
    {
        var profileId = Guid.Parse(profile);

        Assert.Equal(
            $"keywars:{{profile-access-b{bucket}}}:state:{profileId:N}",
            RedisProfileAccessGate.StateKey(profileId).ToString());
        Assert.Equal(
            $"keywars:{{profile-access-b{bucket}}}:active:{profileId:N}",
            RedisProfileAccessGate.ActiveKey(profileId).ToString());
        Assert.DoesNotContain("{profile-access}:", RedisProfileAccessGate.StateKey(profileId).ToString());
    }

    [Fact]
    public async Task GateLuaOperationsKeepEveryKeyInTheProfileSlot()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var gate = new RedisProfileAccessGate(Connection(database));
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        await using (var access = await gate.AcquireAsync(profileId))
        {
            access.ThrowIfLost();
        }
        await using (var operation = await gate.TryBeginOperationAsync(profileId))
        {
            Assert.NotNull(operation);
            await gate.MarkDeletedAsync(profileId);
        }

        Assert.NotEmpty(recording.Scripts);
        Assert.All(recording.Scripts, invocation =>
        {
            var keys = RedisKeys(invocation.Parameters).ToArray();
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.Contains("{profile-access-ba8}", key, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task StateReadUsesOnlyTheV2BucketKey()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var gate = new RedisProfileAccessGate(Connection(database));
        var profileId = Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef");

        var state = await gate.GetStateAsync(profileId);

        Assert.Equal(KeyWars.Services.ProfileAccessState.Available, state);
        Assert.Equal(
            "keywars:{profile-access-b4e}:state:0198a1b2c3d47e5f8123456789abcdef",
            Assert.Single(recording.StringGetKeys));
    }

    [Fact]
    public async Task PrivacyDeletionWritesAPersistentMarkerWithoutTtl()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var gate = new RedisProfileAccessGate(Connection(database));
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        await using (var operation = await gate.TryBeginOperationAsync(profileId))
        {
            Assert.NotNull(operation);
            await gate.MarkDeletedAsync(profileId);
        }

        var deletion = Assert.Single(recording.Scripts, invocation =>
            invocation.Script.ExecutableScript.Contains("zcard", StringComparison.Ordinal) &&
            invocation.Script.ExecutableScript.Contains("'deleted'", StringComparison.Ordinal));
        Assert.DoesNotContain("pexpire", deletion.Script.ExecutableScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'PX'", deletion.Script.ExecutableScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            deletion.Parameters.GetType().GetProperties(),
            property => property.Name == "deletedLifetime");
    }

    [Fact]
    public async Task AuthoritativeRecoveryOverwritesAMissingMarkerWithoutExpiry()
    {
        var database = DispatchProxy.Create<IDatabase, RecordingDatabase>();
        var recording = (RecordingDatabase)(object)database;
        var gate = new RedisProfileAccessGate(Connection(database));
        var profileId = Guid.Parse("0198a1b2-c3d4-7e5f-8123-456789abcdef");

        await ((IAuthoritativeProfileDeletionMarker)gate)
            .MarkDeletedAuthoritativelyAsync(profileId);

        var write = Assert.Single(recording.StringSets);
        Assert.Equal(
            "keywars:{profile-access-b4e}:state:0198a1b2c3d47e5f8123456789abcdef",
            write.Key);
        Assert.Equal("deleted", write.Value);
        Assert.False(write.HasExpiry);
    }

    [Fact]
    public void ActiveStateMachineRequiresDatabaseValidationOnlyAfterStateLoss()
    {
        var acquire = ScriptNamed("AcquireScript");
        var beginOperation = ScriptNamed("BeginOperationScript");
        var markValidated = ScriptNamed("MarkValidatedScript");

        Assert.Contains("state and state ~= 'active'", acquire, StringComparison.Ordinal);
        Assert.Contains("state == 'active' then return 1 else return 2", acquire, StringComparison.Ordinal);
        Assert.Contains("state and state ~= 'active'", beginOperation, StringComparison.Ordinal);
        Assert.Contains("@operationToken", beginOperation, StringComparison.Ordinal);
        Assert.Contains("'active'", markValidated, StringComparison.Ordinal);
        Assert.Contains("zscore", markValidated, StringComparison.Ordinal);
    }

    private static IEnumerable<string> RedisKeys(object parameters) =>
        parameters.GetType().GetProperties()
            .Where(property => property.PropertyType == typeof(RedisKey))
            .Select(property => ((RedisKey)property.GetValue(parameters)!).ToString());

    private static string ScriptNamed(string fieldName) =>
        Assert.IsType<LuaScript>(typeof(RedisProfileAccessGate)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)).OriginalScript;

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
                : throw new NotSupportedException(targetMethod?.Name);
    }

    public class RecordingDatabase : DispatchProxy
    {
        public List<ScriptInvocation> Scripts { get; } = [];
        public List<string> StringGetKeys { get; } = [];
        public List<(string Key, string Value, bool HasExpiry)> StringSets { get; } = [];

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
                return Task.FromResult(RedisValue.Null);
            }

            if (method.Name == nameof(IDatabaseAsync.StringSetAsync) &&
                arguments[0] is RedisKey key &&
                arguments[1] is RedisValue value)
            {
                var expiry = arguments.ElementAtOrDefault(2);
                StringSets.Add((
                    key.ToString(),
                    value.ToString(),
                    expiry is TimeSpan || expiry is DateTime));
                return Task.FromResult(true);
            }

            throw new NotSupportedException(method.Name);
        }
    }
}
