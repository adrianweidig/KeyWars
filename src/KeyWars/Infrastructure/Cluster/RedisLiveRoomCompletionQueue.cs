using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisLiveRoomCompletionQueue(
    IConnectionMultiplexer redis,
    ILiveRoomCompletionWriter writer,
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisLiveRoomCompletionQueue> logger) : BackgroundService,
    ILiveRoomCompletionSink,
    ILiveRoomCompletionDrain,
    ILiveRoomCompletionMonitor
{
    private const string Prefix = "keywars:completion";
    private const int MaxAttempts = 5;
    private static readonly RedisKey PendingKey = $"{Prefix}:pending";
    private static readonly RedisKey FailedKey = $"{Prefix}:failed";
    private static readonly LuaScript EnqueueScript = LuaScript.Prepare(
        "if redis.call('exists', @recordKey) == 1 then return 0 end; " +
        "redis.call('set', @recordKey, @payload); redis.call('set', @statusKey, @status); " +
        "redis.call('zadd', @pendingKey, @dueAt, @roomId); return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TimeSpan drainTimeout = TimeSpan.FromSeconds(options.Value.CompletionDrainTimeoutSeconds);
    private long persisted;
    private long failed;
    private long retries;
    private long durationTicks;
    private long durationCount;

    public int Capacity { get; } = options.Value.CompletionQueueCapacity;
    public int PendingCount => checked((int)database.SortedSetLength(PendingKey));
    public int FailedRecordCount => checked((int)database.SortedSetLength(FailedKey));
    public long FailedAttempts => Volatile.Read(ref failed);

    public CompletionReceipt Enqueue(CompletedRoomRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.IdempotencyKey))
        {
            throw new InvalidOperationException("Arena-Abschlussdaten enthalten keinen Idempotenzschlüssel.");
        }

        var existing = database.StringGet(RecordKey(record.Id));
        if (!existing.IsNull)
        {
            var stored = DeserializeRecord(existing!);
            if (!StringComparer.Ordinal.Equals(stored.IdempotencyKey, record.IdempotencyKey))
            {
                throw new InvalidOperationException("Für diesen Arena-Raum existiert bereits ein anderer Persistenzauftrag.");
            }

            return new CompletionReceipt(record.Id, record.IdempotencyKey, GetStatus(record.Id).State);
        }

        if (!CanAcceptNewRoom(0))
        {
            return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Failed);
        }

        var status = new CompletionStatusRecord(record.IdempotencyKey, CompletionState.Pending);
        var enqueued = (int)database.ScriptEvaluate(
            EnqueueScript,
            new
            {
                recordKey = RecordKey(record.Id),
                statusKey = StatusKey(record.Id),
                pendingKey = PendingKey,
                roomId = record.Id.ToString("N"),
                payload = JsonSerializer.Serialize(record),
                status = JsonSerializer.Serialize(status),
                dueAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            });
        if (enqueued == 0)
        {
            return new CompletionReceipt(record.Id, record.IdempotencyKey, GetStatus(record.Id).State);
        }

        return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Pending);
    }

    public CompletionStatusSnapshot GetStatus(Guid roomId)
    {
        var value = database.StringGet(StatusKey(roomId));
        if (value.IsNull)
        {
            return new CompletionStatusSnapshot(CompletionState.AbortedUnconfirmed);
        }

        return new CompletionStatusSnapshot(DeserializeStatus(value!).State);
    }

    public bool CanAcceptNewRoom(int currentRoomCount) =>
        currentRoomCount >= 0 && currentRoomCount + PendingCount + FailedRecordCount < Capacity;

    public LiveRoomCompletionMetrics GetMetrics()
    {
        var count = Volatile.Read(ref durationCount);
        var ticks = Volatile.Read(ref durationTicks);
        return new LiveRoomCompletionMetrics(
            PendingCount,
            FailedRecordCount,
            Volatile.Read(ref retries),
            Volatile.Read(ref persisted),
            Volatile.Read(ref failed),
            0,
            count == 0 ? 0 : Math.Round(ticks * 1000d / Stopwatch.Frequency / count, 2));
    }

    public async Task<CompletionDrainResult> DrainProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var started = timeProvider.GetUtcNow();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var related = await ReadRelatedAsync(profileId);
            var failedCount = related.Count(item => item.State == CompletionState.Failed);
            if (failedCount > 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Failed, 0, failedCount);
            }

            var pendingCount = related.Count(item => item.State == CompletionState.Pending);
            if (pendingCount == 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Success, 0, 0);
            }

            if (timeProvider.GetUtcNow() - started >= drainTimeout)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Timeout, pendingCount, 0);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await database.SortedSetRangeByScoreAsync(
                    PendingKey,
                    stop: timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    take: 16);
                foreach (var value in due)
                {
                    if (Guid.TryParseExact(value.ToString(), "N", out var roomId))
                    {
                        await TryPersistAsync(roomId, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Die verteilte Arena-Ergebnisqueue konnte nicht verarbeitet werden.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, stoppingToken);
        }
    }

    private async Task TryPersistAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var lease = await RedisDistributedLease.TryAcquireAsync(database, LockKey(roomId), cancellationToken);
        if (lease is null)
        {
            return;
        }

        await using (lease)
        {
            var value = await database.StringGetAsync(RecordKey(roomId));
            if (value.IsNull)
            {
                await database.SortedSetRemoveAsync(PendingKey, roomId.ToString("N"));
                return;
            }

            var record = DeserializeRecord(value!);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await writer.PersistAsync(record, cancellationToken);
                await database.KeyDeleteAsync(RecordKey(roomId));
                await database.KeyDeleteAsync(AttemptsKey(roomId));
                await database.SortedSetRemoveAsync(PendingKey, roomId.ToString("N"));
                await SetStatusAsync(roomId, record.IdempotencyKey, CompletionState.Persisted);
                Interlocked.Increment(ref persisted);
                Interlocked.Increment(ref durationCount);
                Interlocked.Add(ref durationTicks, stopwatch.ElapsedTicks);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var attempts = await database.StringIncrementAsync(AttemptsKey(roomId));
                if (attempts < MaxAttempts)
                {
                    Interlocked.Increment(ref retries);
                    var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 200 * Math.Pow(2, attempts - 1)));
                    await database.SortedSetAddAsync(
                        PendingKey,
                        roomId.ToString("N"),
                        timeProvider.GetUtcNow().Add(delay).ToUnixTimeMilliseconds());
                    logger.LogWarning(exception, "Arena-Ergebnis {RoomId} wird erneut persistiert (Versuch {Attempt}).", roomId, attempts + 1);
                    return;
                }

                await database.SortedSetRemoveAsync(PendingKey, roomId.ToString("N"));
                await database.SortedSetAddAsync(FailedKey, roomId.ToString("N"), timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                await SetStatusAsync(roomId, record.IdempotencyKey, CompletionState.Failed);
                Interlocked.Increment(ref failed);
                logger.LogError(exception, "Arena-Ergebnis {RoomId} ist nach {AttemptCount} Versuchen fehlgeschlagen.", roomId, attempts);
            }
        }
    }

    private async Task<IReadOnlyList<CompletionRecordState>> ReadRelatedAsync(Guid profileId)
    {
        var ids = (await database.SortedSetRangeByRankAsync(PendingKey))
            .Concat(await database.SortedSetRangeByRankAsync(FailedKey));
        var related = new List<CompletionRecordState>();
        foreach (var value in ids)
        {
            if (!Guid.TryParseExact(value.ToString(), "N", out var id))
            {
                continue;
            }

            var payload = await database.StringGetAsync(RecordKey(id));
            if (payload.IsNull)
            {
                continue;
            }

            var record = DeserializeRecord(payload!);
            if (record.Participants.Any(item => item.UserProfileId == profileId))
            {
                related.Add(new CompletionRecordState(id, GetStatus(id).State));
            }
        }

        return related;
    }

    private async Task SetStatusAsync(Guid roomId, string idempotencyKey, CompletionState state)
    {
        await database.StringSetAsync(
            StatusKey(roomId),
            JsonSerializer.Serialize(new CompletionStatusRecord(idempotencyKey, state)),
            TimeSpan.FromDays(7));
    }

    private static CompletedRoomRecord DeserializeRecord(RedisValue value) =>
        JsonSerializer.Deserialize<CompletedRoomRecord>(value.ToString())
        ?? throw new InvalidOperationException("Ein Arena-Ergebnisauftrag in Redis ist ungültig.");

    private static CompletionStatusRecord DeserializeStatus(RedisValue value) =>
        JsonSerializer.Deserialize<CompletionStatusRecord>(value.ToString())
        ?? throw new InvalidOperationException("Ein Arena-Ergebnisstatus in Redis ist ungültig.");

    private static RedisKey RecordKey(Guid roomId) => $"{Prefix}:record:{roomId:N}";
    private static RedisKey StatusKey(Guid roomId) => $"{Prefix}:status:{roomId:N}";
    private static RedisKey AttemptsKey(Guid roomId) => $"{Prefix}:attempts:{roomId:N}";
    private static RedisKey LockKey(Guid roomId) => $"{Prefix}:lock:{roomId:N}";

    private sealed record CompletionStatusRecord(string IdempotencyKey, CompletionState State);
    private sealed record CompletionRecordState(Guid RoomId, CompletionState State);
}

public sealed class ClusterLiveRoomCompletionSink(RedisLiveRoomCompletionQueue durable) : ILiveRoomCompletionSink
{
    private readonly AsyncLocal<CompletionBatch?> currentBatch = new();

    public CompletionBatch BeginBatch()
    {
        if (currentBatch.Value is not null)
        {
            throw new InvalidOperationException("Ein Arena-Ergebnisbatch ist bereits aktiv.");
        }

        var batch = new CompletionBatch(this, durable);
        currentBatch.Value = batch;
        return batch;
    }

    public CompletionReceipt Enqueue(CompletedRoomRecord record)
    {
        if (currentBatch.Value is { } batch)
        {
            return batch.Add(record);
        }

        return durable.Enqueue(record);
    }

    public CompletionStatusSnapshot GetStatus(Guid roomId) => durable.GetStatus(roomId);
    public bool CanAcceptNewRoom(int currentRoomCount) => durable.CanAcceptNewRoom(currentRoomCount);

    public sealed class CompletionBatch(
        ClusterLiveRoomCompletionSink owner,
        RedisLiveRoomCompletionQueue durable) : IDisposable
    {
        private readonly List<CompletedRoomRecord> records = [];
        private int completed;

        internal CompletionReceipt Add(CompletedRoomRecord record)
        {
            records.Add(record);
            return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Pending);
        }

        public void Commit()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            foreach (var record in records)
            {
                var receipt = durable.Enqueue(record);
                if (receipt.State != CompletionState.Pending && receipt.State != CompletionState.Persisted)
                {
                    throw new InvalidOperationException("Der verteilte Arena-Ergebnisauftrag wurde abgelehnt.");
                }
            }

            owner.currentBatch.Value = null;
        }

        public void Dispose()
        {
            owner.currentBatch.Value = null;
        }
    }
}
