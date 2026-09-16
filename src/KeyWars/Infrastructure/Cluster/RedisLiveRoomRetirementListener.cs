using System.Text.Json;
using System.Text.Json.Serialization;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

internal sealed class RedisLiveRoomRetirementListener(
    IConnectionMultiplexer redis,
    LiveRoomManager rooms,
    ILogger<RedisLiveRoomRetirementListener> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(15);
    private const int RoomsPerReconciliation = 64;
    private static readonly JsonSerializerOptions MementoOptions = CreateMementoOptions();
    private readonly ISubscriber subscriber = redis.GetSubscriber();

    internal void ApplyRetirement(RedisValue value)
    {
        if (Guid.TryParseExact(value.ToString(), "N", out var roomId))
        {
            rooms.RemoveRoomState(roomId);
        }
        else
        {
            logger.LogWarning("Ungültige Arena-Retire-Nachricht wurde verworfen.");
        }
    }

    internal void ApplyProgressPurge(RedisValue value)
    {
        var parts = value.ToString().Split(':');
        if (parts.Length == 3 &&
            Guid.TryParseExact(parts[0], "N", out var roomId) &&
            Guid.TryParseExact(parts[1], "N", out var profileId) &&
            Guid.TryParseExact(parts[2], "N", out _))
        {
            rooms.SynchronizeProgressInputPurge(roomId, profileId, parts[2]);
            rooms.UnloadRoomState(roomId);
        }
        else
        {
            logger.LogWarning("Ungültige Arena-Fortschrittsbereinigung wurde verworfen.");
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        foreach (var cachedRoom in rooms.SnapshotLocalRoomCache(RoomsPerReconciliation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await database.HashGetAsync(
                RedisLiveRoomDispatcher.RoomKey(cachedRoom.RoomId),
                "memento");
            if (payload.IsNull)
            {
                rooms.RemoveRoomState(cachedRoom.RoomId);
                continue;
            }

            LiveRoomMemento? memento;
            try
            {
                memento = JsonSerializer.Deserialize<LiveRoomMemento>(payload.ToString(), MementoOptions);
            }
            catch (JsonException)
            {
                memento = null;
            }
            if (memento is null || memento.Id != cachedRoom.RoomId)
            {
                rooms.RemoveRoomState(cachedRoom.RoomId);
                logger.LogWarning("Ungültiger Arena-Zustand wurde bei der lokalen Cache-Bereinigung verworfen.");
                continue;
            }

            var unloadRoom = cachedRoom.StateVersion is { } localStateVersion &&
                memento.StateVersion != localStateVersion;
            var progressCache = rooms.SnapshotProgressInputCache(cachedRoom.RoomId);
            if (progressCache is null)
            {
                if (unloadRoom)
                {
                    rooms.UnloadRoomState(cachedRoom.RoomId);
                }
                continue;
            }

            var activeProfiles = memento.Participants
                .Select(participant => participant.ProfileId)
                .ToHashSet();
            if (progressCache.ProfileGenerations.Count == 0)
            {
                if (unloadRoom)
                {
                    rooms.UnloadRoomState(cachedRoom.RoomId);
                }
                continue;
            }
            var profileIds = progressCache.ProfileGenerations.Keys.ToArray();
            var generations = await database.HashGetAsync(
                RedisLiveProgressRelay.PurgeGenerationKey(cachedRoom.RoomId),
                profileIds.Select(profileId => (RedisValue)profileId.ToString("N")).ToArray());
            for (var index = 0; index < profileIds.Length; index++)
            {
                var profileId = profileIds[index];
                var generation = generations[index].IsNull ? null : generations[index].ToString();
                if (!StringComparer.Ordinal.Equals(
                    progressCache.ProfileGenerations[profileId],
                    generation))
                {
                    rooms.SynchronizeProgressInputPurge(
                        cachedRoom.RoomId,
                        profileId,
                        generation);
                    unloadRoom = true;
                }
                if (!activeProfiles.Contains(profileId))
                {
                    rooms.RemoveProgressInput(cachedRoom.RoomId, profileId);
                    unloadRoom = true;
                }
            }
            if (unloadRoom)
            {
                rooms.UnloadRoomState(cachedRoom.RoomId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(RedisLiveRoomDispatcher.RetirementChannel),
            (_, value) => ApplyRetirement(value));
        await subscriber.SubscribeAsync(
            RedisChannel.Literal(RedisLiveProgressRelay.ProgressInputPurgeChannel),
            (_, value) => ApplyProgressPurge(value));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Lokale Arena-Caches konnten nicht mit Redis abgeglichen werden.");
                }

                await Task.Delay(ReconciliationInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await subscriber.UnsubscribeAsync(
                RedisChannel.Literal(RedisLiveRoomDispatcher.RetirementChannel));
            await subscriber.UnsubscribeAsync(
                RedisChannel.Literal(RedisLiveProgressRelay.ProgressInputPurgeChannel));
        }
    }

    private static JsonSerializerOptions CreateMementoOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
