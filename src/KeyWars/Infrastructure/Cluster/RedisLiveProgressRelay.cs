using System.Collections.Concurrent;
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
    internal const string ProgressInputPurgeChannel = "keywars:progress-input-purged";
    internal const int MaxDueRoomsPerPoll = 64;
    internal const int MaxDueRoomsPerBucket = 4;
    internal const int MaxBucketVisitsPerPoll = 16;
    private static readonly TimeSpan RecoveryBucketInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan BroadcastSendTimeout = TimeSpan.FromSeconds(5);
    private static readonly LuaScript EnqueueScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('hexists', @blockedKey, @participant) == 1 then return 3 end; " +
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
    private static readonly LuaScript AcknowledgeBatchScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local updates = cjson.decode(@updates); " +
        "for _, update in ipairs(updates) do " +
        "local currentPayload = redis.call('hget', @sentKey, update.Participant); " +
        "local shouldWrite = 1; if currentPayload then " +
        "local decoded = cjson.decode(currentPayload); local currentRoomVersion = tonumber(decoded.RoomVersion or -1); " +
        "if currentRoomVersion > tonumber(update.RoomVersion) " +
        "or (currentRoomVersion == tonumber(update.RoomVersion) and tonumber(decoded.ParticipantSequence or -1) >= tonumber(update.ParticipantSequence)) " +
        "then shouldWrite = 0 end end; " +
        "if shouldWrite == 1 then redis.call('hset', @sentKey, update.Participant, update.Watermark) end end; return 1");
    private static readonly LuaScript CleanupBatchScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('del', @pendingKey); redis.call('zrem', @dueKey, @roomId); " +
        "redis.call('pexpire', @sentKey, @sentTtlMilliseconds); return 1");
    private static readonly LuaScript PurgeParticipantScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('hdel', @pendingKey, @participant); " +
        "redis.call('hdel', @latestKey, @participant); " +
        "redis.call('hdel', @sentKey, @participant); " +
        "redis.call('hset', @blockedKey, @participant, @blockGeneration); " +
        "redis.call('hset', @purgeGenerationKey, @participant, @blockGeneration); " +
        "redis.call('pexpire', @blockedKey, @blockedTtlMilliseconds); " +
        "redis.call('pexpire', @purgeGenerationKey, @purgeGenerationTtlMilliseconds); " +
        "if redis.call('hlen', @pendingKey) == 0 then redis.call('zrem', @dueKey, @roomId); end; return 1");
    private static readonly LuaScript UnblockParticipantScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('hget', @blockedKey, @participant) ~= @expectedGeneration then return 2 end; " +
        "redis.call('hdel', @blockedKey, @participant); return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private readonly RedisDueBucketScheduler dueBuckets = new();
    private readonly TimeSpan broadcastInterval = TimeSpan.FromSeconds(
        1d / Math.Clamp(options.Value.ProgressBroadcastHz, 1, 60));
    private readonly TimeSpan purgeGenerationLifetime = TimeSpan.FromMinutes(
        Math.Clamp(
            Math.Max(options.Value.LobbyRoomRetentionMinutes, options.Value.CompletedRoomRetentionMinutes) + 60,
            65,
            8 * 24 * 60));
    private int recoveryBucketCursor = Random.Shared.Next(RedisClusterKeyspace.BucketCount);
    private DateTimeOffset nextRecoveryScanAt = DateTimeOffset.MinValue;

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
                blockedKey = BlockedKey(delta.RoomId),
                dueKey = DueRoomsKey(delta.RoomId),
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

        if (saved == 1)
        {
            dueBuckets.Activate(RedisClusterKeyspace.GetBucket(delta.RoomId));
        }
    }

    public async ValueTask PurgeParticipantAsync(
        Guid roomId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(roomId),
            cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roomLock.LeaseLost);
        operationCancellation.Token.ThrowIfCancellationRequested();
        var blockGeneration = Guid.NewGuid().ToString("N");
        var purged = (int)await database.ScriptEvaluateAsync(
            PurgeParticipantScript,
            new
            {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                pendingKey = PendingKey(roomId),
                latestKey = LatestKey(roomId),
                sentKey = SentKey(roomId),
                blockedKey = BlockedKey(roomId),
                purgeGenerationKey = PurgeGenerationKey(roomId),
                dueKey = DueRoomsKey(roomId),
                participant = participantId.ToString("N"),
                blockGeneration,
                roomId = roomId.ToString("N"),
                blockedTtlMilliseconds = (long)TimeSpan.FromHours(2).TotalMilliseconds,
                purgeGenerationTtlMilliseconds = (long)purgeGenerationLifetime.TotalMilliseconds
            });
        if (purged == 0)
        {
            roomLock.ThrowFenceLost("Profilfortschritt entfernen");
        }

        roomLock.ThrowIfLost();
        await redis.GetSubscriber().PublishAsync(
            RedisChannel.Literal(ProgressInputPurgeChannel),
            $"{roomId:N}:{participantId:N}:{blockGeneration}");
    }

    internal async ValueTask<string?> ReadParticipantBlockGenerationAsync(
        Guid roomId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(roomId),
            cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roomLock.LeaseLost);
        operationCancellation.Token.ThrowIfCancellationRequested();
        var generation = await database.HashGetAsync(
            BlockedKey(roomId),
            participantId.ToString("N"));
        roomLock.ThrowIfLost();
        return generation.IsNull ? null : generation.ToString();
    }

    internal async ValueTask<string?> ReadParticipantPurgeGenerationAsync(
        Guid roomId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = await database.HashGetAsync(
            PurgeGenerationKey(roomId),
            participantId.ToString("N"));
        return generation.IsNull ? null : generation.ToString();
    }

    internal async ValueTask UnblockParticipantAsync(
        Guid roomId,
        Guid participantId,
        string expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(roomId),
            cancellationToken);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roomLock.LeaseLost);
        operationCancellation.Token.ThrowIfCancellationRequested();
        var unblocked = (int)await database.ScriptEvaluateAsync(
            UnblockParticipantScript,
            new
            {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                blockedKey = BlockedKey(roomId),
                participant = participantId.ToString("N"),
                expectedGeneration
            });
        if (unblocked == 0)
        {
            roomLock.ThrowFenceLost("Profilfortschritt wieder freigeben");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                await PollActiveBucketsAsync(now, stoppingToken);
                await RecoverNextBucketIfDueAsync(now);
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

    private async Task PollActiveBucketsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var processed = 0;
        var bucketVisits = 0;
        var broadcasts = new List<Guid>(MaxDueRoomsPerPoll);
        var reactivate = new List<byte>(MaxBucketVisitsPerPoll);
        while (processed < MaxDueRoomsPerPoll &&
            bucketVisits < MaxBucketVisitsPerPoll &&
            dueBuckets.TryTake(out var bucket))
        {
            bucketVisits++;
            var take = Math.Min(MaxDueRoomsPerBucket, MaxDueRoomsPerPoll - processed);
            RedisValue[] due;
            try
            {
                due = await database.SortedSetRangeByScoreAsync(
                    DueRoomsKey(bucket),
                    stop: now.ToUnixTimeMilliseconds(),
                    take: take);
                processed += due.Length;
                foreach (var value in due)
                {
                    if (Guid.TryParseExact(value.ToString(), "N", out var roomId) &&
                        RedisClusterKeyspace.GetBucket(roomId) == bucket)
                    {
                        broadcasts.Add(roomId);
                    }
                    else
                    {
                        await database.SortedSetRemoveAsync(DueRoomsKey(bucket), value);
                    }
                }

                if (due.Length == take || await BucketHasPendingRoomsAsync(bucket))
                {
                    reactivate.Add(bucket);
                }
            }
            catch
            {
                dueBuckets.Activate(bucket);
                foreach (var pendingBucket in reactivate)
                {
                    dueBuckets.Activate(pendingBucket);
                }

                throw;
            }
        }

        foreach (var bucket in reactivate)
        {
            dueBuckets.Activate(bucket);
        }

        await RunConcurrentlyAsync(
            broadcasts,
            async (roomId, operationToken) =>
            {
                try
                {
                    await TryBroadcastAsync(roomId, operationToken);
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    dueBuckets.Activate(RedisClusterKeyspace.GetBucket(roomId));
                    logger.LogError(exception, "Der Fortschritt für Arena {RoomId} konnte nicht gesendet werden.", roomId);
                }
            },
            cancellationToken);
    }

    private async Task<bool> BucketHasPendingRoomsAsync(byte bucket) =>
        (await database.SortedSetRangeByRankAsync(DueRoomsKey(bucket), 0, 0)).Length > 0;

    private async Task RecoverNextBucketIfDueAsync(DateTimeOffset now)
    {
        if (now < nextRecoveryScanAt)
        {
            return;
        }

        var bucket = NextRecoveryBucket(ref recoveryBucketCursor);
        nextRecoveryScanAt = now.Add(RecoveryBucketInterval);
        if (await BucketHasPendingRoomsAsync(bucket))
        {
            dueBuckets.Activate(bucket);
        }
    }

    private async Task TryBroadcastAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var roomLock = await RedisDistributedLease.TryAcquireAsync(
            database,
            LockKey(roomId),
            cancellationToken);
        if (roomLock is null)
        {
            return;
        }

        await using var acquiredRoomLock = roomLock;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roomLock.LeaseLost);
        cancellationToken = operationCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var dueAt = await database.SortedSetScoreAsync(DueRoomsKey(roomId), roomId.ToString("N"));
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
        var participants = pendingDeltas
            .Select(delta => (RedisValue)delta.ParticipantId.ToString("N"))
            .ToArray();
        var sentWatermarks = await database.HashGetAsync(SentKey(roomId), participants);
        for (var index = 0; index < pendingDeltas.Length; index++)
        {
            var delta = pendingDeltas[index];
            var sentSequence = sentWatermarks[index];
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
        await SendWithDeadlineAsync(
            sender,
            roomId,
            batch,
            BroadcastSendTimeout,
            timeProvider,
            cancellationToken);
        roomLock.ThrowIfLost();
        var acknowledged = (int)await database.ScriptEvaluateAsync(
            AcknowledgeBatchScript,
            new
        {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                sentKey = SentKey(roomId),
                updates = JsonSerializer.Serialize(ordered.Select(delta => new ProgressAcknowledgement(
                    delta.ParticipantId.ToString("N"),
                    delta.RoomVersion,
                    delta.ParticipantSequence,
                    JsonSerializer.Serialize(new ProgressWatermark(
                        delta.RoomVersion,
                        delta.ParticipantSequence)))))
            });
        if (acknowledged == 0)
        {
            roomLock.ThrowFenceLost("Fortschritt bestätigen");
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

    internal static byte NextRecoveryBucket(ref int cursor)
    {
        var bucket = (byte)cursor;
        cursor = (cursor + 1) % RedisClusterKeyspace.BucketCount;
        return bucket;
    }

    internal static Task RunConcurrentlyAsync<T>(
        IReadOnlyList<T> items,
        Func<T, CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        Task.WhenAll(items.Select(item => action(item, cancellationToken)));

    internal static async Task SendWithDeadlineAsync(
        ILiveProgressSender progressSender,
        Guid roomId,
        LiveProgressBatch batch,
        TimeSpan timeout,
        TimeProvider timeoutProvider,
        CancellationToken cancellationToken)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutTimer = timeoutProvider.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            sendCancellation,
            timeout,
            Timeout.InfiniteTimeSpan);
        try
        {
            await progressSender.SendAsync(roomId, batch, sendCancellation.Token)
                .WaitAsync(timeout, timeoutProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            await sendCancellation.CancelAsync();
            throw;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && sendCancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Der Fortschrittsversand für Arena {roomId:N} hat das Zeitlimit überschritten.");
        }
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
                dueKey = DueRoomsKey(roomId),
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

    internal static RedisKey PendingKey(Guid roomId) => $"{ProgressPrefix(roomId)}:pending:{roomId:N}";
    internal static RedisKey LatestKey(Guid roomId) => $"{ProgressPrefix(roomId)}:latest:{roomId:N}";
    internal static RedisKey SentKey(Guid roomId) => $"{ProgressPrefix(roomId)}:sent:{roomId:N}";
    public static RedisKey BlockedKey(Guid roomId) => $"{ProgressPrefix(roomId)}:blocked:{roomId:N}";
    public static RedisKey PurgeGenerationKey(Guid roomId) =>
        $"{ProgressPrefix(roomId)}:purge-generation:{roomId:N}";
    internal static RedisKey LockKey(Guid roomId) => $"{ProgressPrefix(roomId)}:lock:{roomId:N}";
    internal static RedisKey DueRoomsKey(Guid roomId) => DueRoomsKey(RedisClusterKeyspace.GetBucket(roomId));
    internal static RedisKey DueRoomsKey(byte bucket) => $"keywars:{{progress-b{bucket:x2}}}:due";

    private static string ProgressPrefix(Guid roomId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag("progress", roomId)}";

    private sealed record ProgressWatermark(int RoomVersion, int ParticipantSequence);
    private sealed record ProgressAcknowledgement(
        string Participant,
        int RoomVersion,
        int ParticipantSequence,
        string Watermark);
}

internal sealed class RedisDueBucketScheduler
{
    private readonly ConcurrentDictionary<byte, byte> active = new();
    private readonly ConcurrentQueue<byte> queue = new();

    internal int Count => active.Count;

    internal void Activate(byte bucket)
    {
        if (active.TryAdd(bucket, 0))
        {
            queue.Enqueue(bucket);
        }
    }

    internal bool TryTake(out byte bucket)
    {
        while (queue.TryDequeue(out bucket))
        {
            if (active.TryRemove(bucket, out _))
            {
                return true;
            }
        }

        bucket = default;
        return false;
    }
}
