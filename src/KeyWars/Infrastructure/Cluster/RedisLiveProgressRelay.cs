using System.Diagnostics;
using System.Text.Json;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisLiveProgressRelay(
    IConnectionMultiplexer redis,
    ILiveProgressSender sender,
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    KeyWarsTelemetry telemetry,
    ILogger<RedisLiveProgressRelay> logger) : BackgroundService
{
    private const string Prefix = "keywars:{progress}";
    private static readonly RedisKey DueRoomsKey = $"{Prefix}:due";
    private static readonly LuaScript EnqueueScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local currentPayload = redis.call('hget', @latestKey, @participant); " +
        "if currentPayload then " +
        "local decoded = cjson.decode(currentPayload); " +
        "local currentRoomVersion = tonumber(decoded.RoomVersion or -1); " +
        "if currentRoomVersion > tonumber(@roomVersion) " +
        "or (currentRoomVersion == tonumber(@roomVersion) and tonumber(decoded.ParticipantSequence or -1) >= tonumber(@sequence)) " +
        "then return 2 end end; " +
        "redis.call('hset', @pendingKey, @participant, @payload); " +
        "redis.call('hset', @latestKey, @participant, @payload); " +
        "redis.call('pexpire', @pendingKey, @pendingTtlMilliseconds); " +
        "redis.call('pexpire', @latestKey, @latestTtlMilliseconds); " +
        "redis.call('zadd', @dueKey, 'NX', @dueAt, @roomId); return 1");
    private static readonly LuaScript AcknowledgeParticipantScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local currentPayload = redis.call('hget', @sentKey, @participant); " +
        "local shouldWrite = 1; if currentPayload then " +
        "local decoded = cjson.decode(currentPayload); local currentRoomVersion = tonumber(decoded.RoomVersion or -1); " +
        "if currentRoomVersion > tonumber(@roomVersion) " +
        "or (currentRoomVersion == tonumber(@roomVersion) and tonumber(decoded.ParticipantSequence or -1) >= tonumber(@sequence)) " +
        "then shouldWrite = 0 end end; " +
        "if shouldWrite == 1 then redis.call('hset', @sentKey, @participant, @watermark) end; return 1");
    private static readonly LuaScript CleanupBatchScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('del', @pendingKey); redis.call('zrem', @dueKey, @roomId); " +
        "redis.call('pexpire', @sentKey, @sentTtlMilliseconds); return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TimeSpan broadcastInterval = TimeSpan.FromSeconds(
        1d / Math.Clamp(options.Value.ProgressBroadcastHz, 1, 60));

    public async ValueTask EnqueueAsync(LiveProgressDelta delta, CancellationToken cancellationToken)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(delta.RoomId),
            cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roomLock.LeaseLost);
        operationCancellation.Token.ThrowIfCancellationRequested();
        var saved = (int)await database.ScriptEvaluateAsync(
            EnqueueScript,
            new
            {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                pendingKey = PendingKey(delta.RoomId),
                latestKey = LatestKey(delta.RoomId),
                dueKey = DueRoomsKey,
                participant = delta.ParticipantId.ToString("N"),
                payload = JsonSerializer.Serialize(delta),
                sequence = delta.ParticipantSequence,
                roomVersion = delta.RoomVersion,
                roomId = delta.RoomId.ToString("N"),
                dueAt = timeProvider.GetUtcNow().Add(broadcastInterval).ToUnixTimeMilliseconds(),
                pendingTtlMilliseconds = (long)TimeSpan.FromMinutes(10).TotalMilliseconds,
                latestTtlMilliseconds = (long)TimeSpan.FromHours(2).TotalMilliseconds
            });
        if (saved == 0)
        {
            roomLock.ThrowFenceLost("Fortschritt puffern");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await database.SortedSetRangeByScoreAsync(
                    DueRoomsKey,
                    stop: timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    take: 64);
                foreach (var value in due)
                {
                    if (Guid.TryParseExact(value.ToString(), "N", out var roomId))
                    {
                        await TryBroadcastAsync(roomId, stoppingToken);
                    }
                    else
                    {
                        await database.SortedSetRemoveAsync(DueRoomsKey, value);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Der verteilte Arena-Fortschritt konnte nicht gesendet werden.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeProvider, stoppingToken);
        }
    }

    private async Task TryBroadcastAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(roomId),
            cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roomLock.LeaseLost);
        cancellationToken = operationCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var dueAt = await database.SortedSetScoreAsync(DueRoomsKey, roomId.ToString("N"));
        if (dueAt is null || dueAt > timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            return;
        }

        var pending = await database.HashGetAllAsync(PendingKey(roomId));
        if (pending.Length == 0)
        {
            await CleanupBatchAsync(roomId, roomLock);
            return;
        }

        var pendingDeltas = SelectNewestRoomVersion(pending
            .Select(entry => Deserialize(entry.Value))
            .Where(delta => delta is not null)
            .Cast<LiveProgressDelta>());
        if (pendingDeltas.Length == 0)
        {
            await CleanupBatchAsync(roomId, roomLock);
            return;
        }

        var roomVersion = pendingDeltas[0].RoomVersion;
        var latest = (await database.HashGetAllAsync(LatestKey(roomId)))
            .Select(entry => Deserialize(entry.Value))
            .Where(delta => delta?.RoomVersion == roomVersion)
            .Cast<LiveProgressDelta>()
            .ToArray();
        var ranks = latest
            .OrderByDescending(delta => delta.CorrectCharacters)
            .ThenByDescending(delta => delta.Wpm)
            .ThenBy(delta => delta.ParticipantId)
            .Select((delta, index) => new { delta.ParticipantId, Rank = index + 1 })
            .ToDictionary(item => item.ParticipantId, item => item.Rank);
        var deltas = new List<LiveProgressDelta>();
        foreach (var delta in pendingDeltas)
        {
            var participant = delta.ParticipantId.ToString("N");
            var sentSequence = await database.HashGetAsync(SentKey(roomId), participant);
            if (!sentSequence.IsNull &&
                JsonSerializer.Deserialize<ProgressWatermark>(sentSequence.ToString()) is { } sent &&
                (delta.RoomVersion < sent.RoomVersion ||
                    delta.RoomVersion == sent.RoomVersion && delta.ParticipantSequence <= sent.ParticipantSequence))
            {
                continue;
            }

            deltas.Add(delta with { RankHint = ranks.GetValueOrDefault(delta.ParticipantId) });
        }

        if (deltas.Count == 0)
        {
            await CleanupBatchAsync(roomId, roomLock);
            return;
        }

        var ordered = deltas
            .OrderBy(delta => delta.RankHint)
            .ThenBy(delta => delta.ParticipantId)
            .ToArray();
        var batch = new LiveProgressBatch(
            roomId,
            roomVersion,
            timeProvider.GetUtcNow(),
            ordered);
        var stopwatch = Stopwatch.StartNew();
        await sender.SendAsync(roomId, batch, cancellationToken);
        roomLock.ThrowIfLost();
        foreach (var delta in ordered)
        {
            var acknowledged = (int)await database.ScriptEvaluateAsync(
                AcknowledgeParticipantScript,
                new
                {
                    lockKey = roomLock.Key,
                    lockToken = roomLock.Token,
                    sentKey = SentKey(roomId),
                    participant = delta.ParticipantId.ToString("N"),
                    sequence = delta.ParticipantSequence,
                    roomVersion = delta.RoomVersion,
                    watermark = JsonSerializer.Serialize(new ProgressWatermark(
                        delta.RoomVersion,
                        delta.ParticipantSequence))
                });
            if (acknowledged == 0)
            {
                roomLock.ThrowFenceLost("Fortschritt bestätigen");
            }
        }

        await CleanupBatchAsync(roomId, roomLock);
        telemetry.RecordProgress(
            "broadcast",
            ordered.Length,
            ordered.Sum(delta => delta.TypedStateBits.Length),
            stopwatch.Elapsed);
    }

    internal static LiveProgressDelta[] SelectNewestRoomVersion(IEnumerable<LiveProgressDelta> deltas)
    {
        var materialized = deltas.ToArray();
        if (materialized.Length == 0)
        {
            return [];
        }

        var newestRoomVersion = materialized.Max(delta => delta.RoomVersion);
        return materialized
            .Where(delta => delta.RoomVersion == newestRoomVersion)
            .ToArray();
    }

    private async Task CleanupBatchAsync(Guid roomId, RedisDistributedLease roomLock)
    {
        var cleaned = (int)await database.ScriptEvaluateAsync(
            CleanupBatchScript,
            new
            {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                pendingKey = PendingKey(roomId),
                dueKey = DueRoomsKey,
                sentKey = SentKey(roomId),
                roomId = roomId.ToString("N"),
                sentTtlMilliseconds = (long)TimeSpan.FromHours(2).TotalMilliseconds
            });
        if (cleaned == 0)
        {
            roomLock.ThrowFenceLost("Fortschrittsbatch abschließen");
        }
    }

    private static LiveProgressDelta? Deserialize(RedisValue value) =>
        JsonSerializer.Deserialize<LiveProgressDelta>(value.ToString());

    private static RedisKey PendingKey(Guid roomId) => $"{Prefix}:pending:{roomId:N}";
    private static RedisKey LatestKey(Guid roomId) => $"{Prefix}:latest:{roomId:N}";
    private static RedisKey SentKey(Guid roomId) => $"{Prefix}:sent:{roomId:N}";
    private static RedisKey LockKey(Guid roomId) => $"{Prefix}:lock:{roomId:N}";

    private sealed record ProgressWatermark(int RoomVersion, int ParticipantSequence);
}
