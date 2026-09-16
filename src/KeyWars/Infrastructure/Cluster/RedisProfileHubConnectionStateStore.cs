using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisProfileHubConnectionStateStore(
    IConnectionMultiplexer redis) : IProfileHubConnectionStateStore
{
    internal const string RevocationChannel = "keywars:profile-connections-revoked";
    private const string Subsystem = "profile-connections";
    private const int MaximumConnectionsPerProfile = 64;
    private const int MaximumDirectoryEntries = 256;
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly LuaScript RegisterScript = LuaScript.Prepare(
        "local generation = tonumber(redis.call('get', @generationKey) or '0'); " +
        "if generation ~= tonumber(@expectedGeneration) then return {2, generation, 0} end; " +
        "local rawCount = redis.call('scard', @indexKey); " +
        "if rawCount > tonumber(@maximumDirectoryEntries) then return {-2, generation, rawCount} end; " +
        "local members = redis.call('smembers', @indexKey); local activeCount = 0; local exists = 0; " +
        "for _, member in ipairs(members) do " +
        "if redis.call('exists', member) == 0 then redis.call('srem', @indexKey, member) " +
        "else activeCount = activeCount + 1; if member == @connectionKey then exists = 1 end end end; " +
        "if exists == 0 and activeCount >= tonumber(@maximumConnections) then return {-1, generation, activeCount} end; " +
        "redis.call('set', @generationKey, generation); " +
        "redis.call('set', @connectionKey, @payload, 'PX', @connectionTtl); " +
        "redis.call('sadd', @indexKey, @connectionKey); " +
        "redis.call('pexpire', @indexKey, @indexTtl); return {1, generation, activeCount + 1 - exists}");
    private static readonly LuaScript UpdateScript = LuaScript.Prepare(
        "local generation = tonumber(redis.call('get', @generationKey) or '-1'); " +
        "if generation ~= tonumber(@expectedGeneration) then return 0 end; " +
        "if redis.call('exists', @connectionKey) == 0 then return 0 end; " +
        "redis.call('set', @connectionKey, @payload, 'PX', @connectionTtl); " +
        "redis.call('sadd', @indexKey, @connectionKey); " +
        "redis.call('pexpire', @indexKey, @indexTtl); return 1");
    private static readonly LuaScript UnregisterScript = LuaScript.Prepare(
        "redis.call('del', @connectionKey); redis.call('srem', @indexKey, @connectionKey); " +
        "if redis.call('scard', @indexKey) == 0 then redis.call('del', @indexKey) end; return 1");
    private static readonly LuaScript RevokeScript = LuaScript.Prepare(
        "local generation = redis.call('incr', @generationKey); " +
        "local rawCount = redis.call('scard', @indexKey); " +
        "if rawCount > tonumber(@maximumDirectoryEntries) then return {-1, generation, rawCount} end; " +
        "local members = redis.call('smembers', @indexKey); " +
        "local result = {1, generation, 0}; local activeCount = 0; " +
        "for _, member in ipairs(members) do " +
        "if redis.call('exists', member) == 0 then redis.call('srem', @indexKey, member) " +
        "else activeCount = activeCount + 1; table.insert(result, member) end end; " +
        "result[3] = activeCount; return result");
    private readonly IDatabase database = redis.GetDatabase();

    public async ValueTask<ProfileHubConnectionRegistration> RegisterAsync(
        Guid profileId,
        string connectionId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var epoch = await EnsureEpochAsync(cancellationToken);
            var generationValue = await database.StringGetAsync(GenerationKey(profileId));
            var generation = generationValue.IsNull ? 0 : (long)generationValue;
            var registration = new ProfileHubConnectionRegistration(
                profileId,
                connectionId,
                ownerId,
                epoch,
                generation,
                null);
            var result = (RedisResult[]?)await database.ScriptEvaluateAsync(
                    RegisterScript,
                    new
                    {
                        generationKey = GenerationKey(profileId),
                        connectionKey = ConnectionKey(profileId, connectionId),
                        indexKey = IndexKey(profileId),
                        expectedGeneration = generation,
                        payload = JsonSerializer.Serialize(registration, SerializerOptions),
                        connectionTtl = (long)ConnectionLifetime.TotalMilliseconds,
                        indexTtl = (long)IndexLifetime.TotalMilliseconds,
                        maximumConnections = MaximumConnectionsPerProfile,
                        maximumDirectoryEntries = MaximumDirectoryEntries
                    })
                ?? throw new InvalidOperationException("Redis lieferte keine Profil-Registrierungsantwort.");
            var status = (long)result[0];
            if (status == -1)
            {
                throw new InvalidOperationException(
                    $"Es sind maximal {MaximumConnectionsPerProfile} aktive Arena-Verbindungen pro Person erlaubt.");
            }

            if (status == -2)
            {
                throw new InvalidOperationException("Das Profil-Verbindungsverzeichnis überschreitet die sichere Obergrenze.");
            }

            if (status == 1 &&
                StringComparer.Ordinal.Equals(epoch, await EnsureEpochAsync(cancellationToken)))
            {
                return registration;
            }
        }

        throw new InvalidOperationException("Die Profil-Widerrufsgeneration änderte sich während der Verbindungsaufnahme.");
    }

    public ValueTask<bool> RefreshAsync(
        ProfileHubConnectionRegistration registration,
        CancellationToken cancellationToken = default) =>
        WriteAsync(registration, registration.RoomId, cancellationToken);

    public ValueTask<bool> SetRoomAsync(
        ProfileHubConnectionRegistration registration,
        Guid? roomId,
        CancellationToken cancellationToken = default) =>
        WriteAsync(registration, roomId, cancellationToken);

    public async ValueTask UnregisterAsync(
        ProfileHubConnectionRegistration registration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await database.ScriptEvaluateAsync(
            UnregisterScript,
            new
            {
                connectionKey = ConnectionKey(registration.ProfileId, registration.ConnectionId),
                indexKey = IndexKey(registration.ProfileId)
            });
    }

    public async ValueTask<ProfileHubRevocation> RevokeAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var epoch = await EnsureEpochAsync(cancellationToken);
        var result = (RedisResult[]?)await database.ScriptEvaluateAsync(
                RevokeScript,
                new
                {
                    generationKey = GenerationKey(profileId),
                    indexKey = IndexKey(profileId),
                    maximumDirectoryEntries = MaximumDirectoryEntries
                })
            ?? throw new InvalidOperationException("Redis lieferte keine Profil-Widerrufsantwort.");
        var status = (long)result[0];
        var generation = (long)result[1];
        if (status != 1)
        {
            throw new InvalidOperationException(
                $"Das Profil-Verbindungsverzeichnis enthält {(long)result[2]} Einträge und wurde sicher gesperrt.");
        }

        var connectionKeys = result.Skip(3)
            .Select(item => (RedisKey)(item.ToString()
                ?? throw new InvalidOperationException("Das Profil-Verbindungsverzeichnis enthält einen ungültigen Schlüssel.")))
            .ToArray();
        var registrations = new List<ProfileHubConnectionRegistration>(connectionKeys.Length);
        if (connectionKeys.Length > 0)
        {
            var payloads = await database.StringGetAsync(connectionKeys);
            for (var index = 0; index < payloads.Length; index++)
            {
                var payload = payloads[index];
                if (payload.IsNull)
                {
                    await database.SetRemoveAsync(IndexKey(profileId), connectionKeys[index].ToString());
                    continue;
                }

                try
                {
                    var registration = JsonSerializer.Deserialize<ProfileHubConnectionRegistration>(
                        payload.ToString(),
                        SerializerOptions);
                    if (registration is not null && registration.ProfileId == profileId)
                    {
                        registrations.Add(registration);
                    }
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("Das Profil-Verbindungsverzeichnis enthält einen ungültigen Eintrag.");
                }
            }
        }

        var confirmedEpoch = await EnsureEpochAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(epoch, confirmedEpoch))
        {
            throw new InvalidOperationException("Redis wurde während des Profil-Widerrufs neu initialisiert.");
        }

        return new ProfileHubRevocation(epoch, generation, registrations);
    }

    public async ValueTask<(string Epoch, long Generation)> ReadVersionAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var epoch = await EnsureEpochAsync(cancellationToken);
            var generation = await database.StringGetAsync(GenerationKey(profileId));
            var confirmedEpoch = await EnsureEpochAsync(cancellationToken);
            if (StringComparer.Ordinal.Equals(epoch, confirmedEpoch))
            {
                return (epoch, generation.IsNull ? -1 : (long)generation);
            }
        }

        throw new InvalidOperationException("Redis wurde während der Profil-Versionsprüfung wiederholt neu initialisiert.");
    }

    public async ValueTask PublishRevocationAsync(
        Guid profileId,
        string epoch,
        long generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetSubscriber().PublishAsync(
            RedisChannel.Literal(RevocationChannel),
            $"{profileId:N}:{epoch}:{generation}");
    }

    private async ValueTask<bool> WriteAsync(
        ProfileHubConnectionRegistration registration,
        Guid? roomId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var epoch = await EnsureEpochAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(epoch, registration.Epoch))
        {
            return false;
        }

        var updated = registration with { RoomId = roomId };
        var result = (int)await database.ScriptEvaluateAsync(
            UpdateScript,
            new
            {
                generationKey = GenerationKey(registration.ProfileId),
                connectionKey = ConnectionKey(registration.ProfileId, registration.ConnectionId),
                indexKey = IndexKey(registration.ProfileId),
                expectedGeneration = registration.Generation,
                payload = JsonSerializer.Serialize(updated, SerializerOptions),
                connectionTtl = (long)ConnectionLifetime.TotalMilliseconds,
                indexTtl = (long)IndexLifetime.TotalMilliseconds
            });
        return result == 1 &&
            StringComparer.Ordinal.Equals(epoch, await EnsureEpochAsync(cancellationToken));
    }

    private async ValueTask<string> EnsureEpochAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await database.StringSetAsync(
            EpochKey,
            Guid.NewGuid().ToString("N"),
            when: When.NotExists);
        var epoch = await database.StringGetAsync(EpochKey);
        if (epoch.IsNull)
        {
            throw new InvalidOperationException("Die Redis-Epoch des Arena-Verbindungsverzeichnisses fehlt.");
        }

        return epoch.ToString();
    }

    internal static RedisKey EpochKey => "keywars:profile-connections:epoch";

    internal static RedisKey GenerationKey(Guid profileId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, profileId)}:generation:{profileId:N}";

    internal static RedisKey IndexKey(Guid profileId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, profileId)}:index:{profileId:N}";

    internal static RedisKey ConnectionKey(Guid profileId, string connectionId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, profileId)}:connection:{profileId:N}:{ConnectionHash(connectionId)}";

    private static string ConnectionHash(string connectionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionId))).ToLowerInvariant();
}

internal sealed class RedisProfileHubRevocationListener(
    IConnectionMultiplexer redis,
    ProfileHubConnectionRegistry registry,
    ILogger<RedisProfileHubRevocationListener> logger) : BackgroundService
{
    private readonly ISubscriber subscriber = redis.GetSubscriber();

    internal async Task ApplyAsync(RedisValue value, CancellationToken cancellationToken = default)
    {
        var parts = value.ToString().Split(':');
        if (parts.Length != 3 ||
            !Guid.TryParseExact(parts[0], "N", out var profileId) ||
            parts[1].Length != 32 ||
            !long.TryParse(parts[2], out var generation) ||
            generation < 0)
        {
            logger.LogWarning("Ungültige Profil-Verbindungswiderrufsnachricht wurde verworfen.");
            return;
        }

        await registry.ApplyPublishedRevocationAsync(
            profileId,
            parts[1],
            generation,
            "Deine Arena-Sitzung wurde widerrufen.",
            cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(RedisProfileHubConnectionStateStore.RevocationChannel),
            (_channel, value) =>
            {
                _ = ApplySafelyAsync(value, stoppingToken);
            });
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await subscriber.UnsubscribeAsync(
                RedisChannel.Literal(RedisProfileHubConnectionStateStore.RevocationChannel));
        }
    }

    private async Task ApplySafelyAsync(RedisValue value, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyAsync(value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Profil-Verbindungswiderruf konnte nicht sofort angewendet werden.");
        }
    }
}
