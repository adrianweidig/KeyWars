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
    private const string Prefix = "keywars:{completion}";
    private const int MaxAttempts = 5;
    private static readonly RedisKey PendingKey = $"{Prefix}:pending";
    private static readonly RedisKey FailedKey = $"{Prefix}:failed";
    private static readonly RedisKey EnqueuedKey = $"{Prefix}:enqueued";
    private static readonly LuaScript EnqueueScript = LuaScript.Prepare(
        "if redis.call('exists', @recordKey) == 1 then return 0 end; " +
        "redis.call('set', @recordKey, @payload); redis.call('set', @statusKey, @status); " +
        "redis.call('zadd', @pendingKey, @dueAt, @roomId); " +
        "redis.call('zadd', @enqueuedKey, @enqueuedAt, @roomId); return 1");
    private static readonly LuaScript ActivateRedriveScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('zscore', @failedKey, @roomId) then " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zadd', @pendingKey, @now, @roomId); " +
        "redis.call('zadd', @enqueuedKey, 'NX', @now, @roomId); " +
        "redis.call('del', @attemptsKey); redis.call('set', @statusKey, @status) end; return 1");
    private static readonly LuaScript FailureScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -1 end; " +
        "local attempts = redis.call('incr', @attemptsKey); " +
        "if attempts < tonumber(@maxAttempts) then " +
        "local retryDelay = math.min(10000, 200 * (2 ^ (attempts - 1))); " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zadd', @pendingKey, tonumber(@now) + retryDelay, @roomId); " +
        "redis.call('set', @statusKey, @status); return attempts end; " +
        "local redrive = redis.call('incr', @redriveKey); " +
        "local exponent = math.min(redrive - 1, 8); " +
        "local redriveDelay = math.min(900000, 30000 * (2 ^ exponent)); " +
        "redis.call('zrem', @pendingKey, @roomId); " +
        "redis.call('zadd', @failedKey, tonumber(@now) + redriveDelay, @roomId); " +
        "redis.call('del', @attemptsKey); " +
        "redis.call('set', @statusKey, @status); return tonumber(@maxAttempts) + redrive");
    private static readonly LuaScript CompleteScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('del', @recordKey, @attemptsKey, @redriveKey); " +
        "redis.call('zrem', @pendingKey, @roomId); " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zrem', @enqueuedKey, @roomId); " +
        "redis.call('set', @statusKey, @status, 'PX', @statusTtlMilliseconds); return 1");
    private static readonly LuaScript CleanupMissingScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('exists', @recordKey) == 1 then return 2 end; " +
        "redis.call('del', @attemptsKey, @redriveKey); " +
        "redis.call('zrem', @pendingKey, @roomId); " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zrem', @enqueuedKey, @roomId); return 1");
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
    public TimeSpan OldestPendingAge
    {
        get
        {
            var oldest = database.SortedSetRangeByRankWithScores(EnqueuedKey, 0, 0);
            if (oldest.Length == 0)
            {
                return TimeSpan.Zero;
            }

            var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds((long)oldest[0].Score);
            return age > TimeSpan.Zero ? age : TimeSpan.Zero;
        }
    }

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

        var statusValue = database.StringGet(StatusKey(record.Id));
        if (!statusValue.IsNull)
        {
            var currentStatus = DeserializeStatus(statusValue!);
            if (currentStatus.State == CompletionState.Persisted)
            {
                if (!StringComparer.Ordinal.Equals(currentStatus.IdempotencyKey, record.IdempotencyKey))
                {
                    throw new InvalidOperationException("Für diesen Arena-Raum wurde bereits ein anderer Persistenzauftrag abgeschlossen.");
                }

                return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Persisted);
            }
        }

        var status = new CompletionStatusRecord(record.IdempotencyKey, CompletionState.Pending);
        var enqueued = (int)database.ScriptEvaluate(
            EnqueueScript,
            new
            {
                recordKey = RecordKey(record.Id),
                statusKey = StatusKey(record.Id),
                pendingKey = PendingKey,
                enqueuedKey = EnqueuedKey,
                roomId = record.Id.ToString("N"),
                payload = JsonSerializer.Serialize(record),
                status = JsonSerializer.Serialize(status),
                dueAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                enqueuedAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            });
        if (enqueued == 0)
        {
            var storedValue = database.StringGet(RecordKey(record.Id));
            if (storedValue.IsNull)
            {
                throw new InvalidOperationException("Der Arena-Persistenzauftrag wurde parallel verändert.");
            }

            var stored = DeserializeRecord(storedValue!);
            if (!StringComparer.Ordinal.Equals(stored.IdempotencyKey, record.IdempotencyKey))
            {
                throw new InvalidOperationException("Für diesen Arena-Raum existiert bereits ein anderer Persistenzauftrag.");
            }

            return new CompletionReceipt(record.Id, stored.IdempotencyKey, GetStatus(record.Id).State);
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
                var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var pendingDue = await database.SortedSetRangeByScoreWithScoresAsync(
                    PendingKey,
                    stop: now,
                    take: 16);
                var failedDue = await database.SortedSetRangeByScoreWithScoresAsync(
                    FailedKey,
                    stop: now,
                    take: 16);
                var due = pendingDue
                    .Concat(failedDue)
                    .GroupBy(item => item.Element)
                    .Select(group => group.OrderBy(item => item.Score).First())
                    .OrderBy(item => item.Score)
                    .Take(16);
                foreach (var entry in due)
                {
                    if (Guid.TryParseExact(entry.Element.ToString(), "N", out var roomId))
                    {
                        try
                        {
                            await TryPersistAsync(roomId, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            logger.LogError(exception, "Arena-Ergebnis {RoomId} konnte in diesem Durchlauf nicht verarbeitet werden.", roomId);
                        }
                    }
                    else
                    {
                        await database.SortedSetRemoveAsync(PendingKey, entry.Element);
                        await database.SortedSetRemoveAsync(FailedKey, entry.Element);
                        await database.SortedSetRemoveAsync(EnqueuedKey, entry.Element);
                        logger.LogError("Ungültige Arena-Ergebnis-ID {RoomId} wurde aus den Redis-Indizes entfernt.", entry.Element);
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
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.LeaseLost);
            var operationToken = operationCancellation.Token;
            var value = await database.StringGetAsync(RecordKey(roomId));
            if (value.IsNull)
            {
                var cleaned = (int)await database.ScriptEvaluateAsync(
                    CleanupMissingScript,
                    new
                    {
                        lockKey = lease.Key,
                        lockToken = lease.Token,
                        recordKey = RecordKey(roomId),
                        attemptsKey = AttemptsKey(roomId),
                        redriveKey = RedriveKey(roomId),
                        pendingKey = PendingKey,
                        failedKey = FailedKey,
                        enqueuedKey = EnqueuedKey,
                        roomId = roomId.ToString("N")
                    });
                if (cleaned == 0)
                {
                    lease.ThrowFenceLost("fehlenden Abschlussauftrag bereinigen");
                }

                if (cleaned is not (1 or 2))
                {
                    throw new InvalidOperationException(
                        $"Redis lieferte beim Bereinigen eines fehlenden Abschlussauftrags das unbekannte Ergebnis {cleaned}.");
                }
                return;
            }

            CompletedRoomRecord record;
            try
            {
                record = DeserializeRecord(value!);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Arena-Ergebnis {RoomId} ist beschädigt und bleibt für die manuelle Diagnose erhalten.", roomId);
                await ParkCorruptRecordAsync(roomId, lease);
                return;
            }
            var activated = (int)await database.ScriptEvaluateAsync(
                ActivateRedriveScript,
                new
                {
                    lockKey = lease.Key,
                    lockToken = lease.Token,
                    pendingKey = PendingKey,
                    failedKey = FailedKey,
                    enqueuedKey = EnqueuedKey,
                    statusKey = StatusKey(roomId),
                    attemptsKey = AttemptsKey(roomId),
                    roomId = roomId.ToString("N"),
                    now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    status = JsonSerializer.Serialize(new CompletionStatusRecord(
                        record.IdempotencyKey,
                        CompletionState.Pending))
                });
            if (activated == 0)
            {
                lease.ThrowFenceLost("Abschlussauftrag reaktivieren");
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await writer.PersistAsync(record, operationToken);
                lease.ThrowIfLost();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (lease.LeaseLost.IsCancellationRequested)
            {
                lease.ThrowIfLost();
                throw;
            }
            catch (Exception exception)
            {
                var outcome = (long)await database.ScriptEvaluateAsync(
                    FailureScript,
                    new
                    {
                        lockKey = lease.Key,
                        lockToken = lease.Token,
                        pendingKey = PendingKey,
                        failedKey = FailedKey,
                        statusKey = StatusKey(roomId),
                        attemptsKey = AttemptsKey(roomId),
                        redriveKey = RedriveKey(roomId),
                        roomId = roomId.ToString("N"),
                        now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        maxAttempts = MaxAttempts,
                        status = JsonSerializer.Serialize(new CompletionStatusRecord(
                            record.IdempotencyKey,
                            CompletionState.Pending))
                    });
                if (outcome == -1)
                {
                    lease.ThrowFenceLost("fehlgeschlagenen Abschlussauftrag planen");
                }

                if (outcome < MaxAttempts)
                {
                    var attempts = outcome;
                    Interlocked.Increment(ref retries);
                    var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 200 * Math.Pow(2, attempts - 1)));
                    logger.LogWarning(exception, "Arena-Ergebnis {RoomId} wird erneut persistiert (Versuch {Attempt}).", roomId, attempts + 1);
                    return;
                }

                var redriveCycle = outcome - MaxAttempts;
                var redriveDelay = CalculateRedriveDelay(redriveCycle);
                Interlocked.Increment(ref failed);
                logger.LogError(
                    exception,
                    "Arena-Ergebnis {RoomId} wird nach {AttemptCount} Versuchen in {Delay} erneut aktiviert (Redrive {RedriveCycle}).",
                    roomId,
                    MaxAttempts,
                    redriveDelay,
                    redriveCycle);
                return;
            }

            var completed = (int)await database.ScriptEvaluateAsync(
                CompleteScript,
                new
                {
                    lockKey = lease.Key,
                    lockToken = lease.Token,
                    recordKey = RecordKey(roomId),
                    statusKey = StatusKey(roomId),
                    attemptsKey = AttemptsKey(roomId),
                    redriveKey = RedriveKey(roomId),
                    pendingKey = PendingKey,
                    failedKey = FailedKey,
                    enqueuedKey = EnqueuedKey,
                    roomId = roomId.ToString("N"),
                    status = JsonSerializer.Serialize(new CompletionStatusRecord(
                        record.IdempotencyKey,
                        CompletionState.Persisted)),
                    statusTtlMilliseconds = (long)TimeSpan.FromDays(7).TotalMilliseconds
                });
            if (completed == 0)
            {
                lease.ThrowFenceLost("Abschlussauftrag bestätigen");
            }
            Interlocked.Increment(ref persisted);
            Interlocked.Increment(ref durationCount);
            Interlocked.Add(ref durationTicks, stopwatch.ElapsedTicks);
        }
    }

    private async Task ParkCorruptRecordAsync(Guid roomId, RedisDistributedLease lease)
    {
        var parked = (long)await database.ScriptEvaluateAsync(
            FailureScript,
            new
            {
                lockKey = lease.Key,
                lockToken = lease.Token,
                pendingKey = PendingKey,
                failedKey = FailedKey,
                statusKey = StatusKey(roomId),
                attemptsKey = AttemptsKey(roomId),
                redriveKey = RedriveKey(roomId),
                roomId = roomId.ToString("N"),
                now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                maxAttempts = 1,
                status = JsonSerializer.Serialize(new CompletionStatusRecord("corrupt", CompletionState.Failed))
            });
        if (parked == -1)
        {
            lease.ThrowFenceLost("beschädigten Abschlussauftrag isolieren");
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

    private static CompletedRoomRecord DeserializeRecord(RedisValue value) =>
        JsonSerializer.Deserialize<CompletedRoomRecord>(value.ToString())
        ?? throw new InvalidOperationException("Ein Arena-Ergebnisauftrag in Redis ist ungültig.");

    private static CompletionStatusRecord DeserializeStatus(RedisValue value) =>
        JsonSerializer.Deserialize<CompletionStatusRecord>(value.ToString())
        ?? throw new InvalidOperationException("Ein Arena-Ergebnisstatus in Redis ist ungültig.");

    private static RedisKey RecordKey(Guid roomId) => $"{Prefix}:record:{roomId:N}";
    private static RedisKey StatusKey(Guid roomId) => $"{Prefix}:status:{roomId:N}";
    private static RedisKey AttemptsKey(Guid roomId) => $"{Prefix}:attempts:{roomId:N}";
    private static RedisKey RedriveKey(Guid roomId) => $"{Prefix}:redrive:{roomId:N}";
    private static RedisKey LockKey(Guid roomId) => $"{Prefix}:lock:{roomId:N}";

    private sealed record CompletionStatusRecord(string IdempotencyKey, CompletionState State);
    private sealed record CompletionRecordState(Guid RoomId, CompletionState State);

    private static TimeSpan CalculateRedriveDelay(long cycle)
    {
        var exponent = (int)Math.Clamp(cycle - 1, 0, 8);
        return TimeSpan.FromSeconds(Math.Min(15 * 60, 30 * Math.Pow(2, exponent)));
    }
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
