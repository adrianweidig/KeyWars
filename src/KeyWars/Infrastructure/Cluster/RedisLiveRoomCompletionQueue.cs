using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed record CompletionRoomSlotReservation(Guid RoomId, string Token);

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
    private const string Subsystem = "completion";
    private const int MaxAttempts = 5;
    private const int WorkerBatchSize = 16;
    private const int WorkerConcurrency = 4;
    private const int ProfilePageSize = 128;
    private const int ProfileIndexMemberLength = 65;
    private const int RoomSlotCleanupBatchSize = 128;
    private const string RetryMetric = "retries";
    private const string PersistedMetric = "persisted";
    private const string FailedMetric = "failed";
    private const string DurationMicrosecondsMetric = "duration-microseconds";
    private const string DurationCountMetric = "duration-count";
    private static readonly TimeSpan PersistedStatusLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan PersistenceAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnconfirmedRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AdmissionReconcileDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EnqueueIntentLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AdmissionRepairInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan AdmissionHealthLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AdmissionRebuildLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AdmissionRebuildInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RoomSlotReservationLifetime = TimeSpan.FromMinutes(2);
    private static readonly RedisValue[] MetricFields =
    [
        RetryMetric,
        PersistedMetric,
        FailedMetric,
        DurationMicrosecondsMetric,
        DurationCountMetric
    ];
    private static readonly CompletionBucket[] Buckets = CreateBuckets();
    private static readonly RedisKey AdmissionActiveKey = "keywars:{completion-admission}:active";
    private static readonly RedisKey AdmissionPendingKey = "keywars:{completion-admission}:pending";
    private static readonly RedisKey AdmissionFailedKey = "keywars:{completion-admission}:failed";
    private static readonly RedisKey AdmissionEnqueuedKey = "keywars:{completion-admission}:enqueued";
    private static readonly RedisKey AdmissionReconcileKey = "keywars:{completion-admission}:reconcile";
    private static readonly RedisKey AdmissionMetricsKey = "keywars:{completion-admission}:metrics";
    private static readonly RedisKey AdmissionHealthKey = "keywars:{completion-admission}:health";
    private static readonly RedisKey AdmissionRoomSlotsKey = "keywars:{completion-admission}:room-slots";
    private static readonly RedisKey AdmissionRoomSlotTokensKey = "keywars:{completion-admission}:room-slot-tokens";
    private static readonly RedisKey AdmissionRebuildTokenKey = "keywars:{completion-admission}:rebuild-token";
    private static readonly RedisKey AdmissionRebuildActiveKey = "keywars:{completion-admission}:rebuild-active";
    private static readonly RedisKey AdmissionRebuildPendingKey = "keywars:{completion-admission}:rebuild-pending";
    private static readonly RedisKey AdmissionRebuildFailedKey = "keywars:{completion-admission}:rebuild-failed";
    private static readonly RedisKey AdmissionRebuildEnqueuedKey = "keywars:{completion-admission}:rebuild-enqueued";
    private static readonly RedisKey AdmissionRebuildReconcileKey = "keywars:{completion-admission}:rebuild-reconcile";
    private static readonly RedisKey AdmissionRebuildLockKey = "keywars:{completion-admission}:rebuild-lock";
    private static readonly RedisKey AdmissionLockPrefix = "keywars:{completion-admission}:lock:";
    private static readonly LuaScript ReadAdmissionCountScript = LuaScript.Prepare(
        "if redis.call('exists', @healthKey) == 0 then return -1 end; return redis.call('scard', @setKey)");
    private static readonly LuaScript ReadAdmissionOccupancyScript = LuaScript.Prepare(
        "if redis.call('exists', @healthKey) == 0 then return -1 end; " +
        "local expired = redis.call('zrangebyscore', @roomSlotsKey, '-inf', @now, 'LIMIT', 0, @cleanupLimit); " +
        "for _, roomId in ipairs(expired) do redis.call('zrem', @roomSlotsKey, roomId); redis.call('hdel', @roomSlotTokensKey, roomId); end; " +
        "return tonumber(@currentRoomCount) + redis.call('scard', @activeKey) + redis.call('zcard', @roomSlotsKey)");
    private static readonly LuaScript BeginAdmissionRebuildScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('del', @rebuildActiveKey, @rebuildPendingKey, @rebuildFailedKey, @rebuildEnqueuedKey, @rebuildReconcileKey); " +
        "redis.call('set', @tokenKey, @rebuildToken, 'PX', @rebuildTtlMilliseconds); return 1");
    private static readonly LuaScript RepairAdmissionBucketScript = LuaScript.Prepare(
        "if redis.call('get', @tokenKey) ~= @rebuildToken then return 0 end; " +
        "local pending = cjson.decode(@pendingJson); local failed = cjson.decode(@failedJson); " +
        "for _, item in ipairs(pending) do local roomId = item[1]; " +
        "redis.call('sadd', @rebuildActiveKey, roomId); redis.call('sadd', @rebuildPendingKey, roomId); " +
        "redis.call('srem', @rebuildFailedKey, roomId); redis.call('zadd', @rebuildEnqueuedKey, 'NX', item[2], roomId); " +
        "redis.call('zadd', @rebuildReconcileKey, @reconcileAt, roomId); end; " +
        "for _, item in ipairs(failed) do local roomId = item[1]; " +
        "redis.call('sadd', @rebuildActiveKey, roomId); redis.call('sadd', @rebuildFailedKey, roomId); " +
        "redis.call('srem', @rebuildPendingKey, roomId); redis.call('zadd', @rebuildEnqueuedKey, 'NX', item[2], roomId); " +
        "redis.call('zadd', @rebuildReconcileKey, @reconcileAt, roomId); end; return 1");
    private static readonly LuaScript CompleteAdmissionRebuildScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('get', @tokenKey) ~= @rebuildToken then return 0 end; " +
        "local expired = redis.call('zrangebyscore', @roomSlotsKey, '-inf', @now, 'LIMIT', 0, @cleanupLimit); " +
        "for _, expiredRoomId in ipairs(expired) do redis.call('zrem', @roomSlotsKey, expiredRoomId); redis.call('hdel', @roomSlotTokensKey, expiredRoomId); end; " +
        "if redis.call('scard', @rebuildActiveKey) + redis.call('zcard', @roomSlotsKey) > tonumber(@capacity) then return -2 end; " +
        "redis.call('sunionstore', @activeKey, 1, @rebuildActiveKey); " +
        "redis.call('sunionstore', @pendingKey, 1, @rebuildPendingKey); " +
        "redis.call('sunionstore', @failedKey, 1, @rebuildFailedKey); " +
        "redis.call('zunionstore', @enqueuedKey, 1, @rebuildEnqueuedKey); " +
        "redis.call('zunionstore', @reconcileKey, 1, @rebuildReconcileKey); " +
        "redis.call('del', @rebuildActiveKey, @rebuildPendingKey, @rebuildFailedKey, @rebuildEnqueuedKey, @rebuildReconcileKey); " +
        "redis.call('set', @healthKey, @rebuildToken, 'PX', @healthTtlMilliseconds); " +
        "redis.call('del', @tokenKey); return 1");
    private static readonly LuaScript ReserveAdmissionScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -1 end; " +
        "redis.call('zrem', @roomSlotsKey, @roomId); redis.call('hdel', @roomSlotTokensKey, @roomId); " +
        "local function reserve(activeKey, pendingKey, enqueuedKey, reconcileKey) " +
        "local added = redis.call('sadd', activeKey, @roomId); redis.call('sadd', pendingKey, @roomId); " +
        "redis.call('zadd', enqueuedKey, 'NX', @enqueuedAt, @roomId); " +
        "redis.call('zadd', reconcileKey, 'NX', @reconcileAt, @roomId); return added end; " +
        "local added = reserve(@activeKey, @pendingKey, @enqueuedKey, @reconcileKey); " +
        "if redis.call('exists', @rebuildTokenKey) == 1 then " +
        "reserve(@rebuildActiveKey, @rebuildPendingKey, @rebuildEnqueuedKey, @rebuildReconcileKey); end; return added");
    private static readonly LuaScript MarkAdmissionPendingScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local function mark(activeKey, pendingKey, failedKey, reconcileKey) " +
        "redis.call('sadd', activeKey, @roomId); redis.call('sadd', pendingKey, @roomId); redis.call('srem', failedKey, @roomId); " +
        "redis.call('zadd', reconcileKey, @reconcileAt, @roomId); end; " +
        "mark(@activeKey, @pendingKey, @failedKey, @reconcileKey); " +
        "if redis.call('exists', @rebuildTokenKey) == 1 then " +
        "mark(@rebuildActiveKey, @rebuildPendingKey, @rebuildFailedKey, @rebuildReconcileKey); end; return 1");
    private static readonly LuaScript MarkAdmissionFailedScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local function mark(activeKey, pendingKey, failedKey, reconcileKey) " +
        "redis.call('sadd', activeKey, @roomId); redis.call('sadd', failedKey, @roomId); redis.call('srem', pendingKey, @roomId); " +
        "redis.call('zadd', reconcileKey, @reconcileAt, @roomId); end; " +
        "mark(@activeKey, @pendingKey, @failedKey, @reconcileKey); " +
        "if redis.call('exists', @rebuildTokenKey) == 1 then " +
        "mark(@rebuildActiveKey, @rebuildPendingKey, @rebuildFailedKey, @rebuildReconcileKey); end; return 1");
    private static readonly LuaScript CompleteAdmissionScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local function complete(activeKey, pendingKey, failedKey, enqueuedKey, reconcileKey) " +
        "redis.call('srem', activeKey, @roomId); redis.call('srem', pendingKey, @roomId); " +
        "redis.call('srem', failedKey, @roomId); redis.call('zrem', enqueuedKey, @roomId); " +
        "redis.call('zrem', reconcileKey, @roomId); end; " +
        "complete(@activeKey, @pendingKey, @failedKey, @enqueuedKey, @reconcileKey); " +
        "if redis.call('exists', @rebuildTokenKey) == 1 then " +
        "complete(@rebuildActiveKey, @rebuildPendingKey, @rebuildFailedKey, @rebuildEnqueuedKey, @rebuildReconcileKey); end; return 1");
    private static readonly LuaScript ReserveRoomSlotScript = LuaScript.Prepare(
        "if redis.call('exists', @healthKey) == 0 then return -2 end; " +
        "local expired = redis.call('zrangebyscore', @roomSlotsKey, '-inf', @now, 'LIMIT', 0, @cleanupLimit); " +
        "for _, roomId in ipairs(expired) do redis.call('zrem', @roomSlotsKey, roomId); redis.call('hdel', @roomSlotTokensKey, roomId); end; " +
        "local existing = redis.call('zscore', @roomSlotsKey, @roomId); " +
        "if existing then return -3 end; " +
        "if redis.call('sismember', @activeKey, @roomId) == 1 then return -3 end; " +
        "if tonumber(@currentRoomCount) + redis.call('scard', @activeKey) + redis.call('zcard', @roomSlotsKey) >= tonumber(@capacity) then return -1 end; " +
        "redis.call('zadd', @roomSlotsKey, @expiresAt, @roomId); redis.call('hset', @roomSlotTokensKey, @roomId, 'reserved:' .. @reservationToken); return 1");
    private static readonly LuaScript CommitRoomSlotScript = LuaScript.Prepare(
        "local reserved = 'reserved:' .. @reservationToken; " +
        "local existing = redis.call('hget', @roomSlotTokensKey, @roomId); " +
        "if existing ~= reserved then return 0 end; " +
        "redis.call('hdel', @roomSlotTokensKey, @roomId); redis.call('zrem', @roomSlotsKey, @roomId); return 1");
    private static readonly LuaScript ReleaseRoomSlotScript = LuaScript.Prepare(
        "local existing = redis.call('hget', @roomSlotTokensKey, @roomId); if not existing then redis.call('zrem', @roomSlotsKey, @roomId); return 0 end; " +
        "if existing == 'reserved:' .. @reservationToken then " +
        "redis.call('hdel', @roomSlotTokensKey, @roomId); return redis.call('zrem', @roomSlotsKey, @roomId) end; return -1");
    private static readonly LuaScript PublishMetricsScript = LuaScript.Prepare(
        "local fields = {'retries', 'persisted', 'failed', 'duration-microseconds', 'duration-count'}; " +
        "local values = {@retries, @persisted, @failed, @durationMicroseconds, @durationCount}; " +
        "for index, field in ipairs(fields) do " +
        "local current = tonumber(redis.call('hget', @metricsKey, field) or '0'); " +
        "local candidate = tonumber(values[index]); " +
        "if candidate > current then redis.call('hset', @metricsKey, field, candidate); end; end; return 1");
    private static readonly LuaScript EnqueueScript = LuaScript.Prepare(
        "if redis.call('get', @intentKey) ~= @intentToken then return 3 end; " +
        "if redis.call('exists', @recordKey) == 1 then redis.call('del', @intentKey); return 0 end; " +
        "if redis.call('exists', @statusKey) == 1 then redis.call('del', @intentKey); return 2 end; " +
        "redis.call('set', @recordKey, @payload); redis.call('set', @statusKey, @status); " +
        "local profileIds = @profileIds; " +
        "for offset = 1, string.len(profileIds), 32 do " +
        "local profileId = string.sub(profileIds, offset, offset + 31); " +
        "local profileField = profileId .. ':' .. @roomId; " +
        "redis.call('sadd', @profilesKey, profileId); " +
        "if redis.call('zadd', @profileIndexKey, 0, profileField) == 1 then " +
        "redis.call('hincrby', @profileCountsKey, profileId, 1); " +
        "local epoch = redis.call('incr', @profileEpochKey); " +
        "redis.call('hset', @profileRevisionsKey, profileId, epoch); end; end; " +
        "redis.call('zadd', @pendingKey, @dueAt, @roomId); " +
        "redis.call('zadd', @enqueuedKey, @enqueuedAt, @roomId); " +
        "redis.call('del', @intentKey); return 1");
    private static readonly LuaScript ActivateRedriveScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('zscore', @failedKey, @roomId) then " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zadd', @pendingKey, @now, @roomId); " +
        "redis.call('zadd', @enqueuedKey, 'NX', @now, @roomId); " +
        "redis.call('del', @attemptsKey); redis.call('set', @statusKey, @status) end; " +
        "redis.call('persist', @recordKey); redis.call('persist', @profilesKey); " +
        "redis.call('persist', @pendingKey); redis.call('persist', @failedKey); " +
        "redis.call('persist', @enqueuedKey); redis.call('persist', @profileIndexKey); return 1");
    private static readonly LuaScript FailureScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -1 end; " +
        "redis.call('persist', @recordKey); redis.call('persist', @profilesKey); " +
        "redis.call('persist', @pendingKey); redis.call('persist', @failedKey); " +
        "redis.call('persist', @enqueuedKey); redis.call('persist', @profileIndexKey); " +
        "local attempts = redis.call('incr', @attemptsKey); " +
        "if attempts < tonumber(@maxAttempts) then " +
        "local retryDelay = math.min(10000, 200 * (2 ^ (attempts - 1))); " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zadd', @pendingKey, tonumber(@now) + retryDelay, @roomId); " +
        "redis.call('set', @statusKey, @pendingStatus); " +
        "redis.call('hincrby', @metricsKey, 'retries', 1); return attempts end; " +
        "local redrive = redis.call('incr', @redriveKey); " +
        "local exponent = math.min(redrive - 1, 8); " +
        "local redriveDelay = math.min(900000, 30000 * (2 ^ exponent)); " +
        "redis.call('zrem', @pendingKey, @roomId); " +
        "redis.call('zadd', @failedKey, tonumber(@now) + redriveDelay, @roomId); " +
        "redis.call('del', @attemptsKey); redis.call('set', @statusKey, @failedStatus); " +
        "redis.call('hincrby', @metricsKey, 'failed', 1); " +
        "return tonumber(@maxAttempts) + redrive");
    private static readonly LuaScript RescheduleUnconfirmedScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "redis.call('persist', @recordKey); redis.call('persist', @profilesKey); " +
        "redis.call('persist', @pendingKey); redis.call('persist', @failedKey); " +
        "redis.call('persist', @enqueuedKey); redis.call('persist', @profileIndexKey); " +
        "redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zadd', @pendingKey, @dueAt, @roomId); " +
        "redis.call('set', @statusKey, @status); return 1");
    private static readonly LuaScript CompleteScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "local profileIds = redis.call('smembers', @profilesKey); " +
        "for _, profileId in ipairs(profileIds) do " +
        "local profileField = profileId .. ':' .. @roomId; " +
        "if redis.call('zrem', @profileIndexKey, profileField) == 1 then " +
        "local count = redis.call('hincrby', @profileCountsKey, profileId, -1); " +
        "local epoch = redis.call('incr', @profileEpochKey); " +
        "if count <= 0 then redis.call('hdel', @profileCountsKey, profileId); " +
        "redis.call('hdel', @profileRevisionsKey, profileId); " +
        "else redis.call('hset', @profileRevisionsKey, profileId, epoch); end; end; end; " +
        "redis.call('del', @recordKey, @attemptsKey, @redriveKey, @profilesKey); " +
        "redis.call('zrem', @pendingKey, @roomId); redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zrem', @enqueuedKey, @roomId); " +
        "redis.call('set', @statusKey, @status, 'PX', @statusTtlMilliseconds); " +
        "redis.call('hincrby', @metricsKey, 'persisted', 1); " +
        "redis.call('hincrby', @metricsKey, 'duration-microseconds', @durationMicroseconds); " +
        "redis.call('hincrby', @metricsKey, 'duration-count', 1); return 1");
    private static readonly LuaScript CleanupMissingScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return 0 end; " +
        "if redis.call('exists', @recordKey) == 1 then return 2 end; " +
        "local profileIds = redis.call('smembers', @profilesKey); " +
        "for _, profileId in ipairs(profileIds) do " +
        "local profileField = profileId .. ':' .. @roomId; " +
        "if redis.call('zrem', @profileIndexKey, profileField) == 1 then " +
        "local count = redis.call('hincrby', @profileCountsKey, profileId, -1); " +
        "local epoch = redis.call('incr', @profileEpochKey); " +
        "if count <= 0 then redis.call('hdel', @profileCountsKey, profileId); " +
        "redis.call('hdel', @profileRevisionsKey, profileId); " +
        "else redis.call('hset', @profileRevisionsKey, profileId, epoch); end; end; end; " +
        "redis.call('del', @attemptsKey, @redriveKey, @profilesKey); " +
        "redis.call('zrem', @pendingKey, @roomId); redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zrem', @enqueuedKey, @roomId); return 1");
    private static readonly LuaScript CleanupStaleProfileMemberScript = LuaScript.Prepare(
        "if redis.call('exists', @recordKey) == 1 then return 0 end; " +
        "if redis.call('zrem', @profileIndexKey, @profileField) == 0 then return 0 end; " +
        "local count = redis.call('hincrby', @profileCountsKey, @profileId, -1); " +
        "local epoch = redis.call('incr', @profileEpochKey); " +
        "if count <= 0 then redis.call('hdel', @profileCountsKey, @profileId); " +
        "redis.call('hdel', @profileRevisionsKey, @profileId); " +
        "else redis.call('hset', @profileRevisionsKey, @profileId, epoch); end; return 1");
    private static readonly LuaScript RemoveInvalidProfileMemberScript = LuaScript.Prepare(
        "if redis.call('zrem', @profileIndexKey, @profileField) == 0 then return 0 end; " +
        "local count = redis.call('hincrby', @profileCountsKey, @profileId, -1); " +
        "local epoch = redis.call('incr', @profileEpochKey); " +
        "if count <= 0 then redis.call('hdel', @profileCountsKey, @profileId); " +
        "redis.call('hdel', @profileRevisionsKey, @profileId); " +
        "else redis.call('hset', @profileRevisionsKey, @profileId, epoch); end; return 1");
    private static readonly LuaScript CleanupInvalidQueueMemberScript = LuaScript.Prepare(
        "redis.call('zrem', @pendingKey, @roomId); redis.call('zrem', @failedKey, @roomId); " +
        "redis.call('zrem', @enqueuedKey, @roomId); return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TimeSpan drainTimeout = TimeSpan.FromSeconds(options.Value.CompletionDrainTimeoutSeconds);
    private readonly object metricCacheGate = new();
    private readonly long[,] metricCache = new long[RedisClusterKeyspace.BucketCount, MetricFields.Length];
    private readonly bool[] metricCacheReady = new bool[RedisClusterKeyspace.BucketCount];
    private int nextBucket;
    private int readyMetricBuckets;

    public int Capacity { get; } = options.Value.CompletionQueueCapacity;
    public int PendingCount => ReadAdmissionCount(AdmissionPendingKey);
    public int FailedRecordCount => ReadAdmissionCount(AdmissionFailedKey);
    public long FailedAttempts => ReadPublishedMetric(FailedMetric);
    public TimeSpan OldestPendingAge
    {
        get
        {
            var oldest = database.SortedSetRangeByRankWithScores(AdmissionEnqueuedKey, 0, 0);
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

        var bucket = GetBucket(record.Id);
        var roomId = record.Id.ToString("N");
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var admissionLease = RedisDistributedLease
            .AcquireAsync(database, AdmissionLockKey(record.Id), CancellationToken.None)
            .AsTask()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        int enqueued;
        try
        {
            if (!database.StringSet(
                    EnqueueIntentKey(record.Id),
                    admissionLease.Token,
                    EnqueueIntentLifetime))
            {
                throw new RedisException("Der Completion-Enqueue-Intent konnte nicht gespeichert werden.");
            }
            admissionLease.ThrowIfLost();
            ReserveAdmission(roomId, now, admissionLease);
            var status = new CompletionStatusRecord(record.IdempotencyKey, CompletionState.Pending);
            enqueued = (int)database.ScriptEvaluate(
                EnqueueScript,
                new
                {
                    recordKey = RecordKey(record.Id),
                    statusKey = StatusKey(record.Id),
                    intentKey = EnqueueIntentKey(record.Id),
                    intentToken = admissionLease.Token,
                    profilesKey = ProfilesKey(record.Id),
                    profileIndexKey = bucket.ProfileIndexKey,
                    profileCountsKey = bucket.ProfileCountsKey,
                    profileRevisionsKey = bucket.ProfileRevisionsKey,
                    profileEpochKey = bucket.ProfileEpochKey,
                    pendingKey = bucket.PendingKey,
                    enqueuedKey = bucket.EnqueuedKey,
                    roomId,
                    profileIds = EncodeProfileIds(record),
                    payload = JsonSerializer.Serialize(record),
                    status = JsonSerializer.Serialize(status),
                    dueAt = now,
                    enqueuedAt = now
                });
            if (enqueued == 1)
            {
                MarkAdmissionPendingDirect(record.Id, now, admissionLease);
            }
            admissionLease.ThrowIfLost();
        }
        finally
        {
            admissionLease.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        }
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

        if (enqueued != 1)
        {
            if (enqueued == 3)
            {
                throw new InvalidOperationException("Der Arena-Persistenzauftrag hat sein Enqueue-Fencing verloren.");
            }

            var terminalStatus = GetStatus(record.Id);
            if (terminalStatus.State == CompletionState.Persisted)
            {
                CompleteAdmission(record.Id);
                return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Persisted);
            }

            throw new InvalidOperationException("Der Arena-Persistenzauftrag kollidiert mit einem bestehenden Abschlussstatus.");
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

    public bool CanAcceptNewRoom(int currentRoomCount)
    {
        if (currentRoomCount < 0)
        {
            return false;
        }

        try
        {
            var occupied = (long)database.ScriptEvaluate(
                ReadAdmissionOccupancyScript,
                new
                {
                    healthKey = AdmissionHealthKey,
                    activeKey = AdmissionActiveKey,
                    roomSlotsKey = AdmissionRoomSlotsKey,
                    roomSlotTokensKey = AdmissionRoomSlotTokensKey,
                    currentRoomCount,
                    now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    cleanupLimit = RoomSlotCleanupBatchSize
                });
            return occupied >= 0 && occupied < Capacity;
        }
        catch (RedisException exception)
        {
            logger.LogError(exception, "Completion-Admission ist nicht erreichbar; neue Arenen werden fail-closed abgelehnt.");
            return false;
        }
    }

    public CompletionRoomSlotReservation? TryReserveRoomSlot(Guid roomId, int currentRoomCount)
    {
        if (currentRoomCount < 0)
        {
            return null;
        }

        var reservation = new CompletionRoomSlotReservation(roomId, Guid.NewGuid().ToString("N"));
        try
        {
            var result = (int)database.ScriptEvaluate(
                ReserveRoomSlotScript,
                new
                {
                    healthKey = AdmissionHealthKey,
                    activeKey = AdmissionActiveKey,
                    roomSlotsKey = AdmissionRoomSlotsKey,
                    roomSlotTokensKey = AdmissionRoomSlotTokensKey,
                    roomId = roomId.ToString("N"),
                    reservationToken = reservation.Token,
                    capacity = Capacity,
                    currentRoomCount,
                    now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    expiresAt = timeProvider.GetUtcNow().Add(RoomSlotReservationLifetime).ToUnixTimeMilliseconds(),
                    cleanupLimit = RoomSlotCleanupBatchSize
                });
            return result switch
            {
                1 or 0 => reservation,
                -1 or -2 => null,
                -3 => throw new InvalidOperationException(
                    "Für den Live-Raum existiert bereits eine andere Completion-Admission-Reservierung."),
                _ => throw new InvalidOperationException(
                    $"Redis lieferte für die Completion-Admission-Reservierung das unbekannte Ergebnis {result}.")
            };
        }
        catch (RedisException exception)
        {
            logger.LogError(exception, "Completion-Admission ist nicht erreichbar; der Raumslot wird fail-closed abgelehnt.");
            return null;
        }
    }

    public void CommitRoomSlot(CompletionRoomSlotReservation reservation)
    {
        try
        {
            var result = (int)database.ScriptEvaluate(
                CommitRoomSlotScript,
                new
                {
                    roomSlotsKey = AdmissionRoomSlotsKey,
                    roomSlotTokensKey = AdmissionRoomSlotTokensKey,
                    roomId = reservation.RoomId.ToString("N"),
                    reservationToken = reservation.Token
                });
            if (result is not (0 or 1))
            {
                throw new InvalidOperationException(
                    $"Redis lieferte beim Übernehmen des Completion-Admission-Raumslots das unbekannte Ergebnis {result}.");
            }
        }
        catch (RedisException exception)
        {
            logger.LogWarning(
                exception,
                "Der bestätigte Live-Raum {RoomId} behält seine Admission-Reservierung bis zum Ablauf; die Kapazität bleibt konservativ.",
                reservation.RoomId);
        }
    }

    public void ReleaseRoomSlot(CompletionRoomSlotReservation reservation)
    {
        try
        {
            database.ScriptEvaluate(
                ReleaseRoomSlotScript,
                new
                {
                    roomSlotsKey = AdmissionRoomSlotsKey,
                    roomSlotTokensKey = AdmissionRoomSlotTokensKey,
                    roomId = reservation.RoomId.ToString("N"),
                    reservationToken = reservation.Token
                });
        }
        catch (RedisException exception)
        {
            logger.LogError(
                exception,
                "Completion-Admission-Reservierung für Live-Raum {RoomId} konnte nicht kompensiert werden.",
                reservation.RoomId);
        }
    }

    public LiveRoomCompletionMetrics GetMetrics()
    {
        var values = ReadPublishedMetrics();
        return new LiveRoomCompletionMetrics(
            PendingCount,
            FailedRecordCount,
            values.Retries,
            values.Persisted,
            values.Failed,
            0,
            values.DurationCount == 0
                ? 0
                : Math.Round(values.DurationMicroseconds / 1000d / values.DurationCount, 2));
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
            if (!related.Stable)
            {
                if (timeProvider.GetUtcNow() - started >= drainTimeout)
                {
                    return new CompletionDrainResult(CompletionDrainStatus.Timeout, 1, 0);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
                continue;
            }

            var failedCount = related.Records.Count(item => item.State == CompletionState.Failed);
            if (failedCount > 0)
            {
                return new CompletionDrainResult(CompletionDrainStatus.Failed, 0, failedCount);
            }

            var pendingCount = related.Records.Count(item => item.State == CompletionState.Pending);
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
        await Task.WhenAll(
            ProcessQueueAsync(stoppingToken),
            RepairAdmissionAuthorityAsync(stoppingToken));
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var window = TakeNextBucketWindow();
                var dueReads = window.Select(bucket => ReadDueAsync(bucket, now)).ToArray();
                var due = (await Task.WhenAll(dueReads)).Where(item => item is not null).Select(item => item!).ToArray();
                await ReconcileAdmissionAsync(now, stoppingToken);
                await RunConcurrentlyAsync(
                    due,
                    WorkerConcurrency,
                    ProcessDueAsync,
                    stoppingToken);
                await RefreshMetricsAsync(window);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Die verteilte Arena-Ergebnisqueue konnte nicht verarbeitet werden.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async ValueTask ProcessDueAsync(DueCompletion entry, CancellationToken cancellationToken)
    {
        if (Guid.TryParseExact(entry.Element.ToString(), "N", out var roomId) &&
            RedisClusterKeyspace.GetBucket(roomId) == entry.Bucket.Id)
        {
            try
            {
                await TryPersistAsync(roomId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Arena-Ergebnis {RoomId} konnte in diesem Durchlauf nicht verarbeitet werden.", roomId);
            }
            return;
        }

        await database.ScriptEvaluateAsync(
            CleanupInvalidQueueMemberScript,
            new
            {
                pendingKey = entry.Bucket.PendingKey,
                failedKey = entry.Bucket.FailedKey,
                enqueuedKey = entry.Bucket.EnqueuedKey,
                roomId = entry.Element
            });
        logger.LogError(
            "Ungültige oder falsch partitionierte Arena-Ergebnis-ID {RoomId} wurde aus Bucket {Bucket} entfernt.",
            entry.Element,
            entry.Bucket.Id.ToString("x2"));
    }

    internal static Task RunConcurrentlyAsync<T>(
        IReadOnlyCollection<T> items,
        int maxDegreeOfParallelism,
        Func<T, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        return Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            action);
    }

    private async Task<DueCompletion?> ReadDueAsync(CompletionBucket bucket, long now)
    {
        var pendingRead = database.SortedSetRangeByScoreWithScoresAsync(
            bucket.PendingKey,
            stop: now,
            take: 1);
        var failedRead = database.SortedSetRangeByScoreWithScoresAsync(
            bucket.FailedKey,
            stop: now,
            take: 1);
        await Task.WhenAll(pendingRead, failedRead);
        return pendingRead.Result
            .Concat(failedRead.Result)
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Element)
            .Select(item => new DueCompletion(bucket, item.Element))
            .FirstOrDefault();
    }

    private async Task TryPersistAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var bucket = GetBucket(roomId);
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
                        profilesKey = ProfilesKey(roomId),
                        profileIndexKey = bucket.ProfileIndexKey,
                        profileCountsKey = bucket.ProfileCountsKey,
                        profileRevisionsKey = bucket.ProfileRevisionsKey,
                        profileEpochKey = bucket.ProfileEpochKey,
                        attemptsKey = AttemptsKey(roomId),
                        redriveKey = RedriveKey(roomId),
                        pendingKey = bucket.PendingKey,
                        failedKey = bucket.FailedKey,
                        enqueuedKey = bucket.EnqueuedKey,
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
                if (cleaned == 1)
                {
                    await SynchronizeAdmissionAsync(roomId, CancellationToken.None);
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
                await ParkCorruptRecordAsync(roomId, bucket, lease);
                return;
            }

            var activated = (int)await database.ScriptEvaluateAsync(
                ActivateRedriveScript,
                new
                {
                    lockKey = lease.Key,
                    lockToken = lease.Token,
                    recordKey = RecordKey(roomId),
                    profilesKey = ProfilesKey(roomId),
                    profileIndexKey = bucket.ProfileIndexKey,
                    pendingKey = bucket.PendingKey,
                    failedKey = bucket.FailedKey,
                    enqueuedKey = bucket.EnqueuedKey,
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
            await SynchronizeAdmissionAsync(roomId, CancellationToken.None);

            var stopwatch = Stopwatch.StartNew();
            Task persistenceTask;
            try
            {
                persistenceTask = writer.PersistAsync(record, operationToken);
                await AwaitPersistenceWithDeadlineAsync(
                    persistenceTask,
                    operationCancellation,
                    PersistenceAttemptTimeout,
                    timeProvider,
                    operationToken);
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
            catch (TimeoutException exception)
            {
                await RescheduleUnconfirmedAsync(roomId, bucket, record, lease);
                logger.LogError(
                    exception,
                    "Arena-Ergebnis {RoomId} überschritt das Persistenzzeitlimit und wird gefencet erneut eingeplant.",
                    roomId);
                return;
            }
            catch (Exception exception)
            {
                await HandlePersistenceFailureAsync(roomId, bucket, record, lease, exception);
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
                    profilesKey = ProfilesKey(roomId),
                    profileIndexKey = bucket.ProfileIndexKey,
                    profileCountsKey = bucket.ProfileCountsKey,
                    profileRevisionsKey = bucket.ProfileRevisionsKey,
                    profileEpochKey = bucket.ProfileEpochKey,
                    attemptsKey = AttemptsKey(roomId),
                    redriveKey = RedriveKey(roomId),
                    pendingKey = bucket.PendingKey,
                    failedKey = bucket.FailedKey,
                    enqueuedKey = bucket.EnqueuedKey,
                    metricsKey = bucket.MetricsKey,
                    roomId = roomId.ToString("N"),
                    status = JsonSerializer.Serialize(new CompletionStatusRecord(
                        record.IdempotencyKey,
                        CompletionState.Persisted)),
                    statusTtlMilliseconds = (long)PersistedStatusLifetime.TotalMilliseconds,
                    durationMicroseconds = Math.Max(0L, (long)(stopwatch.Elapsed.TotalMilliseconds * 1000d))
                });
            if (completed == 0)
            {
                lease.ThrowFenceLost("Abschlussauftrag bestätigen");
            }
            await SynchronizeAdmissionAsync(roomId, CancellationToken.None);
        }
    }

    private async Task RescheduleUnconfirmedAsync(
        Guid roomId,
        CompletionBucket bucket,
        CompletedRoomRecord record,
        RedisDistributedLease lease)
    {
        var scheduled = (int)await database.ScriptEvaluateAsync(
            RescheduleUnconfirmedScript,
            new
            {
                lockKey = lease.Key,
                lockToken = lease.Token,
                recordKey = RecordKey(roomId),
                profilesKey = ProfilesKey(roomId),
                profileIndexKey = bucket.ProfileIndexKey,
                pendingKey = bucket.PendingKey,
                failedKey = bucket.FailedKey,
                enqueuedKey = bucket.EnqueuedKey,
                statusKey = StatusKey(roomId),
                roomId = roomId.ToString("N"),
                dueAt = timeProvider.GetUtcNow().Add(UnconfirmedRetryDelay).ToUnixTimeMilliseconds(),
                status = JsonSerializer.Serialize(new CompletionStatusRecord(
                    record.IdempotencyKey,
                    CompletionState.Pending))
            });
        if (scheduled == 0)
        {
            lease.ThrowFenceLost("unbestätigten Abschlussauftrag erneut einplanen");
        }

        await SynchronizeAdmissionAsync(roomId, CancellationToken.None);
    }

    internal static async Task AwaitPersistenceWithDeadlineAsync(
        Task persistenceTask,
        CancellationTokenSource operationCancellation,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            await persistenceTask.WaitAsync(timeout, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            operationCancellation.Cancel();
            _ = persistenceTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private async Task HandlePersistenceFailureAsync(
        Guid roomId,
        CompletionBucket bucket,
        CompletedRoomRecord record,
        RedisDistributedLease lease,
        Exception exception)
    {
        var outcome = (long)await database.ScriptEvaluateAsync(
            FailureScript,
            new
            {
                lockKey = lease.Key,
                lockToken = lease.Token,
                recordKey = RecordKey(roomId),
                profilesKey = ProfilesKey(roomId),
                profileIndexKey = bucket.ProfileIndexKey,
                pendingKey = bucket.PendingKey,
                failedKey = bucket.FailedKey,
                enqueuedKey = bucket.EnqueuedKey,
                statusKey = StatusKey(roomId),
                attemptsKey = AttemptsKey(roomId),
                redriveKey = RedriveKey(roomId),
                metricsKey = bucket.MetricsKey,
                roomId = roomId.ToString("N"),
                now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                maxAttempts = MaxAttempts,
                pendingStatus = JsonSerializer.Serialize(new CompletionStatusRecord(
                    record.IdempotencyKey,
                    CompletionState.Pending)),
                failedStatus = JsonSerializer.Serialize(new CompletionStatusRecord(
                    record.IdempotencyKey,
                    CompletionState.Failed))
            });
        if (outcome == -1)
        {
            lease.ThrowFenceLost("fehlgeschlagenen Abschlussauftrag planen");
        }

        if (outcome < MaxAttempts)
        {
            await SynchronizeAdmissionAsync(roomId, CancellationToken.None);
            logger.LogWarning(
                exception,
                "Arena-Ergebnis {RoomId} wird erneut persistiert (Versuch {Attempt}).",
                roomId,
                outcome + 1);
            return;
        }

        await SynchronizeAdmissionAsync(roomId, CancellationToken.None);
        var redriveCycle = outcome - MaxAttempts;
        logger.LogError(
            exception,
            "Arena-Ergebnis {RoomId} wird nach {AttemptCount} Versuchen in {Delay} erneut aktiviert (Redrive {RedriveCycle}).",
            roomId,
            MaxAttempts,
            CalculateRedriveDelay(redriveCycle),
            redriveCycle);
    }

    private async Task ParkCorruptRecordAsync(
        Guid roomId,
        CompletionBucket bucket,
        RedisDistributedLease lease)
    {
        var parked = (long)await database.ScriptEvaluateAsync(
            FailureScript,
            new
            {
                lockKey = lease.Key,
                lockToken = lease.Token,
                recordKey = RecordKey(roomId),
                profilesKey = ProfilesKey(roomId),
                profileIndexKey = bucket.ProfileIndexKey,
                pendingKey = bucket.PendingKey,
                failedKey = bucket.FailedKey,
                enqueuedKey = bucket.EnqueuedKey,
                statusKey = StatusKey(roomId),
                attemptsKey = AttemptsKey(roomId),
                redriveKey = RedriveKey(roomId),
                metricsKey = bucket.MetricsKey,
                roomId = roomId.ToString("N"),
                now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                maxAttempts = 1,
                pendingStatus = JsonSerializer.Serialize(new CompletionStatusRecord("corrupt", CompletionState.Pending)),
                failedStatus = JsonSerializer.Serialize(new CompletionStatusRecord("corrupt", CompletionState.Failed))
            });
        if (parked == -1)
        {
            lease.ThrowFenceLost("beschädigten Abschlussauftrag isolieren");
        }
        await SynchronizeAdmissionAsync(roomId, CancellationToken.None);
    }

    private void ReserveAdmission(
        string roomId,
        long enqueuedAt,
        RedisDistributedLease admissionLease)
    {
        var reserved = (int)database.ScriptEvaluate(
            ReserveAdmissionScript,
            new
            {
                lockKey = admissionLease.Key,
                lockToken = admissionLease.Token,
                activeKey = AdmissionActiveKey,
                pendingKey = AdmissionPendingKey,
                enqueuedKey = AdmissionEnqueuedKey,
                reconcileKey = AdmissionReconcileKey,
                roomSlotsKey = AdmissionRoomSlotsKey,
                roomSlotTokensKey = AdmissionRoomSlotTokensKey,
                rebuildTokenKey = AdmissionRebuildTokenKey,
                rebuildActiveKey = AdmissionRebuildActiveKey,
                rebuildPendingKey = AdmissionRebuildPendingKey,
                rebuildEnqueuedKey = AdmissionRebuildEnqueuedKey,
                rebuildReconcileKey = AdmissionRebuildReconcileKey,
                roomId,
                enqueuedAt,
                reconcileAt = enqueuedAt + (long)AdmissionReconcileDelay.TotalMilliseconds
            });
        if (reserved == -1)
        {
            admissionLease.ThrowFenceLost("Completion-Admission reservieren");
        }
        if (reserved is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"Redis lieferte beim Reservieren der Completion-Admission das unbekannte Ergebnis {reserved}.");
        }
    }

    private async Task MarkAdmissionPendingDirectAsync(
        Guid roomId,
        RedisDistributedLease admissionLease)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var marked = (int)await database.ScriptEvaluateAsync(
            MarkAdmissionPendingScript,
            new
            {
                lockKey = admissionLease.Key,
                lockToken = admissionLease.Token,
                activeKey = AdmissionActiveKey,
                pendingKey = AdmissionPendingKey,
                failedKey = AdmissionFailedKey,
                reconcileKey = AdmissionReconcileKey,
                rebuildTokenKey = AdmissionRebuildTokenKey,
                rebuildActiveKey = AdmissionRebuildActiveKey,
                rebuildPendingKey = AdmissionRebuildPendingKey,
                rebuildFailedKey = AdmissionRebuildFailedKey,
                rebuildReconcileKey = AdmissionRebuildReconcileKey,
                roomId = roomId.ToString("N"),
                reconcileAt = now + (long)AdmissionReconcileDelay.TotalMilliseconds
            });
        EnsureAdmissionMutationFenced(marked, admissionLease, "Completion-Admission als ausstehend markieren");
    }

    private void MarkAdmissionPendingDirect(
        Guid roomId,
        long now,
        RedisDistributedLease admissionLease)
    {
        var marked = (int)database.ScriptEvaluate(
            MarkAdmissionPendingScript,
            new
            {
                lockKey = admissionLease.Key,
                lockToken = admissionLease.Token,
                activeKey = AdmissionActiveKey,
                pendingKey = AdmissionPendingKey,
                failedKey = AdmissionFailedKey,
                reconcileKey = AdmissionReconcileKey,
                rebuildTokenKey = AdmissionRebuildTokenKey,
                rebuildActiveKey = AdmissionRebuildActiveKey,
                rebuildPendingKey = AdmissionRebuildPendingKey,
                rebuildFailedKey = AdmissionRebuildFailedKey,
                rebuildReconcileKey = AdmissionRebuildReconcileKey,
                roomId = roomId.ToString("N"),
                reconcileAt = now + (long)AdmissionReconcileDelay.TotalMilliseconds
            });
        EnsureAdmissionMutationFenced(marked, admissionLease, "Completion-Admission als ausstehend markieren");
    }

    private async Task MarkAdmissionFailedDirectAsync(
        Guid roomId,
        RedisDistributedLease admissionLease)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var marked = (int)await database.ScriptEvaluateAsync(
            MarkAdmissionFailedScript,
            new
            {
                lockKey = admissionLease.Key,
                lockToken = admissionLease.Token,
                activeKey = AdmissionActiveKey,
                pendingKey = AdmissionPendingKey,
                failedKey = AdmissionFailedKey,
                reconcileKey = AdmissionReconcileKey,
                rebuildTokenKey = AdmissionRebuildTokenKey,
                rebuildActiveKey = AdmissionRebuildActiveKey,
                rebuildPendingKey = AdmissionRebuildPendingKey,
                rebuildFailedKey = AdmissionRebuildFailedKey,
                rebuildReconcileKey = AdmissionRebuildReconcileKey,
                roomId = roomId.ToString("N"),
                reconcileAt = now + (long)AdmissionReconcileDelay.TotalMilliseconds
            });
        EnsureAdmissionMutationFenced(marked, admissionLease, "Completion-Admission als fehlgeschlagen markieren");
    }

    private void CompleteAdmission(Guid roomId)
    {
        var admissionLease = RedisDistributedLease
            .AcquireAsync(database, AdmissionLockKey(roomId), CancellationToken.None)
            .AsTask()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        try
        {
            CompleteAdmissionDirectAsync(roomId, admissionLease)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            admissionLease.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }

    private async Task CompleteAdmissionDirectAsync(
        Guid roomId,
        RedisDistributedLease admissionLease)
    {
        var completed = (int)await database.ScriptEvaluateAsync(
            CompleteAdmissionScript,
            new
            {
                lockKey = admissionLease.Key,
                lockToken = admissionLease.Token,
                activeKey = AdmissionActiveKey,
                pendingKey = AdmissionPendingKey,
                failedKey = AdmissionFailedKey,
                enqueuedKey = AdmissionEnqueuedKey,
                reconcileKey = AdmissionReconcileKey,
                rebuildTokenKey = AdmissionRebuildTokenKey,
                rebuildActiveKey = AdmissionRebuildActiveKey,
                rebuildPendingKey = AdmissionRebuildPendingKey,
                rebuildFailedKey = AdmissionRebuildFailedKey,
                rebuildEnqueuedKey = AdmissionRebuildEnqueuedKey,
                rebuildReconcileKey = AdmissionRebuildReconcileKey,
                roomId = roomId.ToString("N")
            });
        EnsureAdmissionMutationFenced(completed, admissionLease, "Completion-Admission abschließen");
    }

    private static void EnsureAdmissionMutationFenced(
        int result,
        RedisDistributedLease admissionLease,
        string operation)
    {
        if (result == 0)
        {
            admissionLease.ThrowFenceLost(operation);
        }
        if (result != 1)
        {
            throw new InvalidOperationException(
                $"Redis lieferte beim Vorgang '{operation}' das unbekannte Ergebnis {result}.");
        }
    }

    private async Task ReconcileAdmissionAsync(long now, CancellationToken cancellationToken)
    {
        var due = await database.SortedSetRangeByScoreAsync(
            AdmissionReconcileKey,
            stop: now,
            take: 1);
        if (due.Length == 0)
        {
            return;
        }

        if (!Guid.TryParseExact(due[0].ToString(), "N", out var roomId))
        {
            await CompleteMalformedAdmissionAsync(due[0], cancellationToken);
            logger.LogError("Ungültige Completion-Admission-ID {RoomId} wurde aus dem Reconcile-Index entfernt.", due[0]);
            return;
        }

        var admissionLease = await RedisDistributedLease.TryAcquireAsync(
            database,
            AdmissionLockKey(roomId),
            cancellationToken);
        if (admissionLease is null)
        {
            return;
        }

        await using (admissionLease)
        {
            await SynchronizeAdmissionUnderLockAsync(roomId, admissionLease);
        }
    }

    private async Task RepairAdmissionAuthorityAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var completedRebuild = false;
            try
            {
                var rebuildLease = await RedisDistributedLease.TryAcquireAsync(
                    database,
                    AdmissionRebuildLockKey,
                    stoppingToken);
                if (rebuildLease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
                    continue;
                }

                await using (rebuildLease)
                {
                    var rebuildToken = Guid.NewGuid().ToString("N");
                    var began = (int)await database.ScriptEvaluateAsync(
                        BeginAdmissionRebuildScript,
                        new
                        {
                            lockKey = rebuildLease.Key,
                            lockToken = rebuildLease.Token,
                            tokenKey = AdmissionRebuildTokenKey,
                            rebuildActiveKey = AdmissionRebuildActiveKey,
                            rebuildPendingKey = AdmissionRebuildPendingKey,
                            rebuildFailedKey = AdmissionRebuildFailedKey,
                            rebuildEnqueuedKey = AdmissionRebuildEnqueuedKey,
                            rebuildReconcileKey = AdmissionRebuildReconcileKey,
                            rebuildToken,
                            rebuildTtlMilliseconds = (long)AdmissionRebuildLifetime.TotalMilliseconds
                        });
                    if (began == 0)
                    {
                        rebuildLease.ThrowFenceLost("Completion-Admission-Rebuild beginnen");
                    }

                    foreach (var bucket in Buckets)
                    {
                        await RepairAdmissionBucketAsync(bucket, rebuildToken, rebuildLease);
                        await Task.Delay(AdmissionRepairInterval, timeProvider, stoppingToken);
                    }

                    var completed = (int)await database.ScriptEvaluateAsync(
                        CompleteAdmissionRebuildScript,
                        new
                        {
                            lockKey = rebuildLease.Key,
                            lockToken = rebuildLease.Token,
                            tokenKey = AdmissionRebuildTokenKey,
                            healthKey = AdmissionHealthKey,
                            activeKey = AdmissionActiveKey,
                            pendingKey = AdmissionPendingKey,
                            failedKey = AdmissionFailedKey,
                            enqueuedKey = AdmissionEnqueuedKey,
                            reconcileKey = AdmissionReconcileKey,
                            rebuildActiveKey = AdmissionRebuildActiveKey,
                            rebuildPendingKey = AdmissionRebuildPendingKey,
                            rebuildFailedKey = AdmissionRebuildFailedKey,
                            rebuildEnqueuedKey = AdmissionRebuildEnqueuedKey,
                            rebuildReconcileKey = AdmissionRebuildReconcileKey,
                            roomSlotsKey = AdmissionRoomSlotsKey,
                            roomSlotTokensKey = AdmissionRoomSlotTokensKey,
                            capacity = Capacity,
                            now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                            cleanupLimit = RoomSlotCleanupBatchSize,
                            rebuildToken,
                            healthTtlMilliseconds = (long)AdmissionHealthLifetime.TotalMilliseconds
                        });
                    if (completed == 0)
                    {
                        rebuildLease.ThrowFenceLost("Completion-Admission-Rebuild bestätigen");
                        throw new InvalidOperationException("Der Completion-Admission-Rebuild verlor seinen Generationstoken.");
                    }
                    if (completed == -2)
                    {
                        throw new InvalidOperationException(
                            "Completion-Admission überschreitet nach dem vollständigen Rebuild die konfigurierte Kapazität.");
                    }
                    if (completed != 1)
                    {
                        throw new InvalidOperationException(
                            $"Redis lieferte beim Completion-Admission-Rebuild das unbekannte Ergebnis {completed}.");
                    }
                    completedRebuild = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Completion-Admission konnte nicht aus den autoritativen Buckets rekonstruiert werden; Admission bleibt fail-closed.");
            }

            try
            {
                await Task.Delay(
                    completedRebuild ? AdmissionRebuildInterval : TimeSpan.FromSeconds(1),
                    timeProvider,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RepairAdmissionBucketAsync(
        CompletionBucket bucket,
        string rebuildToken,
        RedisDistributedLease rebuildLease)
    {
        var pendingRead = database.SortedSetRangeByRankWithScoresAsync(
            bucket.PendingKey,
            0,
            Capacity);
        var failedRead = database.SortedSetRangeByRankWithScoresAsync(
            bucket.FailedKey,
            0,
            Capacity);
        var enqueuedRead = database.SortedSetRangeByRankWithScoresAsync(
            bucket.EnqueuedKey,
            0,
            Capacity);
        await Task.WhenAll(pendingRead, failedRead, enqueuedRead);
        var pending = pendingRead.Result;
        var failed = failedRead.Result;
        var enqueued = enqueuedRead.Result;
        if (pending.Length > Capacity || failed.Length > Capacity || enqueued.Length > Capacity)
        {
            throw new InvalidOperationException(
                $"Completion-Bucket {bucket.Id:x2} überschreitet beim Admission-Rebuild die konfigurierte Kapazität {Capacity}.");
        }

        var enqueuedScores = enqueued.ToDictionary(item => item.Element.ToString(), item => (long)item.Score);
        var pendingEntries = await ValidateAdmissionRepairEntriesAsync(bucket, pending, enqueuedScores);
        var failedEntries = await ValidateAdmissionRepairEntriesAsync(bucket, failed, enqueuedScores);
        if (pendingEntries.Select(item => item.RoomId).Intersect(failedEntries.Select(item => item.RoomId)).Any())
        {
            throw new InvalidOperationException(
                $"Completion-Bucket {bucket.Id:x2} enthält denselben Auftrag gleichzeitig in Pending und Failed.");
        }

        if (pendingEntries.Count + failedEntries.Count > Capacity)
        {
            throw new InvalidOperationException(
                $"Completion-Bucket {bucket.Id:x2} überschreitet die konfigurierte Queuekapazität {Capacity}.");
        }

        rebuildLease.ThrowIfLost();
        var repaired = (int)await database.ScriptEvaluateAsync(
            RepairAdmissionBucketScript,
            new
            {
                tokenKey = AdmissionRebuildTokenKey,
                rebuildToken,
                rebuildActiveKey = AdmissionRebuildActiveKey,
                rebuildPendingKey = AdmissionRebuildPendingKey,
                rebuildFailedKey = AdmissionRebuildFailedKey,
                rebuildEnqueuedKey = AdmissionRebuildEnqueuedKey,
                rebuildReconcileKey = AdmissionRebuildReconcileKey,
                pendingJson = SerializeAdmissionRepairEntries(pendingEntries),
                failedJson = SerializeAdmissionRepairEntries(failedEntries),
                reconcileAt = timeProvider.GetUtcNow().Add(AdmissionReconcileDelay).ToUnixTimeMilliseconds()
            });
        if (repaired == 0)
        {
            rebuildLease.ThrowFenceLost("Completion-Admission-Bucket reparieren");
            throw new InvalidOperationException("Der Completion-Admission-Rebuild verlor seinen Generationstoken.");
        }

        foreach (var roomId in pendingEntries
                     .Concat(failedEntries)
                     .Select(item => Guid.ParseExact(item.RoomId, "N")))
        {
            await SynchronizeAdmissionForRebuildAsync(roomId, rebuildLease);
        }
    }

    private async Task<IReadOnlyList<AdmissionRepairEntry>> ValidateAdmissionRepairEntriesAsync(
        CompletionBucket bucket,
        IReadOnlyList<SortedSetEntry> entries,
        IReadOnlyDictionary<string, long> enqueuedScores)
    {
        var valid = new List<AdmissionRepairEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var roomValue = entry.Element.ToString();
            if (Guid.TryParseExact(roomValue, "N", out var roomId) &&
                RedisClusterKeyspace.GetBucket(roomId) == bucket.Id)
            {
                valid.Add(new AdmissionRepairEntry(
                    roomValue,
                    enqueuedScores.GetValueOrDefault(roomValue, 0)));
                continue;
            }

            await database.ScriptEvaluateAsync(
                CleanupInvalidQueueMemberScript,
                new
                {
                    pendingKey = bucket.PendingKey,
                    failedKey = bucket.FailedKey,
                    enqueuedKey = bucket.EnqueuedKey,
                    roomId = entry.Element
                });
            logger.LogError(
                "Ungültige oder falsch partitionierte Arena-Ergebnis-ID {RoomId} wurde beim Admission-Rebuild aus Bucket {Bucket} entfernt.",
                entry.Element,
                bucket.Id.ToString("x2"));
        }

        return valid;
    }

    private static string SerializeAdmissionRepairEntries(IReadOnlyList<AdmissionRepairEntry> entries) =>
        JsonSerializer.Serialize(entries.Select(item => new object[] { item.RoomId, item.EnqueuedAt }).ToArray());

    private async Task SynchronizeAdmissionAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var admissionLease = await RedisDistributedLease.TryAcquireAsync(
            database,
            AdmissionLockKey(roomId),
            cancellationToken);
        if (admissionLease is null)
        {
            return;
        }

        await using (admissionLease)
        {
            await SynchronizeAdmissionUnderLockAsync(roomId, admissionLease);
        }
    }

    private async Task SynchronizeAdmissionForRebuildAsync(
        Guid roomId,
        RedisDistributedLease rebuildLease)
    {
        await using var admissionLease = await RedisDistributedLease.AcquireAsync(
            database,
            AdmissionLockKey(roomId),
            rebuildLease.LeaseLost);
        rebuildLease.ThrowIfLost();
        await SynchronizeAdmissionUnderLockAsync(roomId, admissionLease);
        rebuildLease.ThrowIfLost();
    }

    private async Task SynchronizeAdmissionUnderLockAsync(
        Guid roomId,
        RedisDistributedLease admissionLease)
    {
        var values = await database.StringGetAsync(
            [RecordKey(roomId), StatusKey(roomId), EnqueueIntentKey(roomId)]);
        admissionLease.ThrowIfLost();
        if (!values[0].IsNull)
        {
            CompletionState state;
            try
            {
                state = values[1].IsNull ? CompletionState.Pending : DeserializeStatus(values[1]).State;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Admission-Metadaten für Abschlussauftrag {RoomId} bleiben fail-closed.", roomId);
                await MarkAdmissionFailedDirectAsync(roomId, admissionLease);
                return;
            }

            if (state == CompletionState.Failed)
            {
                await MarkAdmissionFailedDirectAsync(roomId, admissionLease);
            }
            else
            {
                await MarkAdmissionPendingDirectAsync(roomId, admissionLease);
            }
            return;
        }

        if (!values[2].IsNull)
        {
            await MarkAdmissionPendingDirectAsync(roomId, admissionLease);
            return;
        }

        if (values[1].IsNull)
        {
            await CompleteAdmissionDirectAsync(roomId, admissionLease);
            return;
        }

        try
        {
            if (DeserializeStatus(values[1]).State == CompletionState.Persisted)
            {
                await CompleteAdmissionDirectAsync(roomId, admissionLease);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Admission-Metadaten für Abschlussauftrag {RoomId} bleiben fail-closed.", roomId);
        }

        await MarkAdmissionFailedDirectAsync(roomId, admissionLease);
    }

    private async Task CompleteMalformedAdmissionAsync(
        RedisValue roomId,
        CancellationToken cancellationToken)
    {
        var value = roomId.ToString();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var lease = await RedisDistributedLease.TryAcquireAsync(
            database,
            $"{AdmissionLockPrefix}invalid:{digest}",
            cancellationToken);
        if (lease is null)
        {
            return;
        }

        await using (lease)
        {
            var completed = (int)await database.ScriptEvaluateAsync(
                CompleteAdmissionScript,
                new
                {
                    lockKey = lease.Key,
                    lockToken = lease.Token,
                    activeKey = AdmissionActiveKey,
                    pendingKey = AdmissionPendingKey,
                    failedKey = AdmissionFailedKey,
                    enqueuedKey = AdmissionEnqueuedKey,
                    reconcileKey = AdmissionReconcileKey,
                    rebuildTokenKey = AdmissionRebuildTokenKey,
                    rebuildActiveKey = AdmissionRebuildActiveKey,
                    rebuildPendingKey = AdmissionRebuildPendingKey,
                    rebuildFailedKey = AdmissionRebuildFailedKey,
                    rebuildEnqueuedKey = AdmissionRebuildEnqueuedKey,
                    rebuildReconcileKey = AdmissionRebuildReconcileKey,
                    roomId
                });
            EnsureAdmissionMutationFenced(completed, lease, "ungültige Completion-Admission bereinigen");
        }
    }

    private async Task RefreshMetricsAsync(IReadOnlyList<CompletionBucket> buckets)
    {
        var reads = buckets
            .Select(bucket => database.HashGetAsync(bucket.MetricsKey, MetricFields))
            .ToArray();
        var values = await Task.WhenAll(reads);
        CompletionMetricSnapshot snapshot;
        bool allBucketsReady;
        lock (metricCacheGate)
        {
            for (var bucketIndex = 0; bucketIndex < buckets.Count; bucketIndex++)
            {
                var id = buckets[bucketIndex].Id;
                for (var metricIndex = 0; metricIndex < MetricFields.Length; metricIndex++)
                {
                    metricCache[id, metricIndex] = ParseLong(values[bucketIndex][metricIndex]);
                }

                if (!metricCacheReady[id])
                {
                    metricCacheReady[id] = true;
                    readyMetricBuckets++;
                }
            }

            snapshot = SummarizeMetricCache();
            allBucketsReady = readyMetricBuckets == RedisClusterKeyspace.BucketCount;
        }

        if (!allBucketsReady)
        {
            return;
        }

        await database.ScriptEvaluateAsync(
            PublishMetricsScript,
            new
            {
                metricsKey = AdmissionMetricsKey,
                retries = snapshot.Retries,
                persisted = snapshot.Persisted,
                failed = snapshot.Failed,
                durationMicroseconds = snapshot.DurationMicroseconds,
                durationCount = snapshot.DurationCount
            });
    }

    private async Task<RelatedCompletionSnapshot> ReadRelatedAsync(Guid profileId)
    {
        var profileKey = profileId.ToString("N");
        var revisionsBefore = await ReadProfileRevisionsAsync(profileKey);
        var reads = Buckets
            .Select(bucket => ReadBucketRelatedAsync(bucket, profileId, profileKey))
            .ToArray();
        var related = (await Task.WhenAll(reads)).SelectMany(items => items).ToArray();
        var revisionsAfter = await ReadProfileRevisionsAsync(profileKey);
        return new RelatedCompletionSnapshot(
            related,
            revisionsBefore.SequenceEqual(revisionsAfter, StringComparer.Ordinal));
    }

    private async Task<IReadOnlyList<CompletionRecordState>> ReadBucketRelatedAsync(
        CompletionBucket bucket,
        Guid profileId,
        string profileKey)
    {
        var minimum = (RedisValue)$"{profileKey}:{new string('0', 32)}";
        var maximum = (RedisValue)$"{profileKey}:{new string('f', 32)}";
        var cursor = minimum;
        var exclude = Exclude.None;
        var related = new List<CompletionRecordState>();
        while (true)
        {
            var members = await database.SortedSetRangeByValueAsync(
                bucket.ProfileIndexKey,
                cursor,
                maximum,
                exclude,
                take: ProfilePageSize);
            if (members.Length == 0)
            {
                break;
            }

            var valid = new List<(RedisValue Member, Guid RoomId)>();
            foreach (var member in members)
            {
                var text = member.ToString();
                if (text.Length == ProfileIndexMemberLength &&
                    text[32] == ':' &&
                    Guid.TryParseExact(text.AsSpan(33), "N", out var roomId) &&
                    RedisClusterKeyspace.GetBucket(roomId) == bucket.Id)
                {
                    valid.Add((member, roomId));
                    continue;
                }

                await RemoveInvalidProfileMemberAsync(bucket, profileKey, member);
                logger.LogError(
                    "Ungültiger Profilindexeintrag {ProfileField} wurde aus Completion-Bucket {Bucket} entfernt.",
                    member,
                    bucket.Id.ToString("x2"));
            }

            if (valid.Count > 0)
            {
                var keys = new RedisKey[valid.Count * 2];
                for (var index = 0; index < valid.Count; index++)
                {
                    keys[index * 2] = RecordKey(valid[index].RoomId);
                    keys[index * 2 + 1] = StatusKey(valid[index].RoomId);
                }

                var values = await database.StringGetAsync(keys);
                for (var index = 0; index < valid.Count; index++)
                {
                    var candidate = valid[index];
                    var recordValue = values[index * 2];
                    if (recordValue.IsNull)
                    {
                        await CleanupStaleProfileMemberAsync(bucket, profileKey, candidate.RoomId, candidate.Member);
                        continue;
                    }

                    CompletedRoomRecord record;
                    try
                    {
                        record = DeserializeRecord(recordValue);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Der Abschlussauftrag {RoomId} ist beim Profildrain beschädigt.",
                            candidate.RoomId);
                        related.Add(new CompletionRecordState(candidate.RoomId, CompletionState.Failed));
                        continue;
                    }

                    if (!record.Participants.Any(item => item.UserProfileId == profileId))
                    {
                        logger.LogError(
                            "Der Profilindex für {ProfileId} widerspricht Abschlussauftrag {RoomId}.",
                            profileId,
                            candidate.RoomId);
                        related.Add(new CompletionRecordState(candidate.RoomId, CompletionState.Failed));
                        continue;
                    }

                    var statusValue = values[index * 2 + 1];
                    CompletionState state;
                    try
                    {
                        state = statusValue.IsNull
                            ? CompletionState.Pending
                            : DeserializeStatus(statusValue).State;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Der Abschlussstatus {RoomId} ist beim Profildrain beschädigt.",
                            candidate.RoomId);
                        state = CompletionState.Failed;
                    }

                    related.Add(new CompletionRecordState(
                        candidate.RoomId,
                        state == CompletionState.Pending ? CompletionState.Pending : CompletionState.Failed));
                }
            }

            cursor = members[^1];
            exclude = Exclude.Start;
            if (members.Length < ProfilePageSize)
            {
                break;
            }
        }

        return related;
    }

    private async Task CleanupStaleProfileMemberAsync(
        CompletionBucket bucket,
        string profileId,
        Guid roomId,
        RedisValue profileField)
    {
        await database.ScriptEvaluateAsync(
            CleanupStaleProfileMemberScript,
            new
            {
                recordKey = RecordKey(roomId),
                profileIndexKey = bucket.ProfileIndexKey,
                profileCountsKey = bucket.ProfileCountsKey,
                profileRevisionsKey = bucket.ProfileRevisionsKey,
                profileEpochKey = bucket.ProfileEpochKey,
                profileId,
                profileField
            });
    }

    private async Task RemoveInvalidProfileMemberAsync(
        CompletionBucket bucket,
        string profileId,
        RedisValue profileField)
    {
        await database.ScriptEvaluateAsync(
            RemoveInvalidProfileMemberScript,
            new
            {
                profileIndexKey = bucket.ProfileIndexKey,
                profileCountsKey = bucket.ProfileCountsKey,
                profileRevisionsKey = bucket.ProfileRevisionsKey,
                profileEpochKey = bucket.ProfileEpochKey,
                profileId,
                profileField
            });
    }

    private async Task<string[]> ReadProfileRevisionsAsync(string profileId)
    {
        var reads = Buckets
            .Select(bucket => database.HashGetAsync(bucket.ProfileRevisionsKey, profileId))
            .ToArray();
        var values = await Task.WhenAll(reads);
        return values.Select(value => value.IsNull ? string.Empty : value.ToString()).ToArray();
    }

    private CompletionBucket[] TakeNextBucketWindow()
    {
        var ids = SelectBucketWindow(nextBucket, WorkerBatchSize);
        nextBucket = (nextBucket + WorkerBatchSize) % RedisClusterKeyspace.BucketCount;
        return ids.Select(id => Buckets[id]).ToArray();
    }

    private static int[] SelectBucketWindow(int start, int count)
    {
        if (start < 0 || start >= RedisClusterKeyspace.BucketCount)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (count < 1 || count > RedisClusterKeyspace.BucketCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return Enumerable.Range(0, count)
            .Select(offset => (start + offset) % RedisClusterKeyspace.BucketCount)
            .ToArray();
    }

    private long ReadPublishedMetric(string field) => ParseLong(database.HashGet(AdmissionMetricsKey, field));

    private int ReadAdmissionCount(RedisKey setKey)
    {
        try
        {
            var value = ReadAdmissionCountValue(setKey);
            return value < 0 ? Capacity : ToPublicCount(value);
        }
        catch (RedisException exception)
        {
            logger.LogError(exception, "Completion-Admission-Metadaten sind nicht erreichbar; der Zähler bleibt konservativ.");
            return Capacity;
        }
    }

    private long ReadAdmissionCountValue(RedisKey setKey) => (long)database.ScriptEvaluate(
        ReadAdmissionCountScript,
        new
        {
            healthKey = AdmissionHealthKey,
            setKey
        });

    private CompletionMetricSnapshot ReadPublishedMetrics()
    {
        var values = database.HashGet(AdmissionMetricsKey, MetricFields);
        return new CompletionMetricSnapshot(
            ParseLong(values[0]),
            ParseLong(values[1]),
            ParseLong(values[2]),
            ParseLong(values[3]),
            ParseLong(values[4]));
    }

    private CompletionMetricSnapshot SummarizeMetricCache()
    {
        long retries = 0;
        long persisted = 0;
        long failed = 0;
        long durationMicroseconds = 0;
        long durationCount = 0;
        for (var bucket = 0; bucket < RedisClusterKeyspace.BucketCount; bucket++)
        {
            retries += metricCache[bucket, 0];
            persisted += metricCache[bucket, 1];
            failed += metricCache[bucket, 2];
            durationMicroseconds += metricCache[bucket, 3];
            durationCount += metricCache[bucket, 4];
        }

        return new CompletionMetricSnapshot(
            retries,
            persisted,
            failed,
            durationMicroseconds,
            durationCount);
    }

    private static long ParseLong(RedisValue value) => value.IsNull ? 0 : (long)value;

    private static int ToPublicCount(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));

    private static string EncodeProfileIds(CompletedRoomRecord record) => string.Concat(
        record.Participants
            .Select(item => item.UserProfileId)
            .Distinct()
            .Order()
            .Select(item => item.ToString("N")));

    private static CompletedRoomRecord DeserializeRecord(RedisValue value) =>
        JsonSerializer.Deserialize<CompletedRoomRecord>(value.ToString())
        ?? throw new InvalidOperationException("Ein Arena-Ergebnisauftrag in Redis ist ungültig.");

    private static CompletionStatusRecord DeserializeStatus(RedisValue value) =>
        JsonSerializer.Deserialize<CompletionStatusRecord>(value.ToString())
        ?? throw new InvalidOperationException("Ein Arena-Ergebnisstatus in Redis ist ungültig.");

    private static CompletionBucket GetBucket(Guid roomId) => Buckets[RedisClusterKeyspace.GetBucket(roomId)];

    private static string RoomPrefix(Guid roomId) =>
        $"keywars:{RedisClusterKeyspace.GetHashTag(Subsystem, roomId)}";

    private static RedisKey RecordKey(Guid roomId) => $"{RoomPrefix(roomId)}:record:{roomId:N}";
    private static RedisKey StatusKey(Guid roomId) => $"{RoomPrefix(roomId)}:status:{roomId:N}";
    private static RedisKey ProfilesKey(Guid roomId) => $"{RoomPrefix(roomId)}:profiles:{roomId:N}";
    private static RedisKey AttemptsKey(Guid roomId) => $"{RoomPrefix(roomId)}:attempts:{roomId:N}";
    private static RedisKey RedriveKey(Guid roomId) => $"{RoomPrefix(roomId)}:redrive:{roomId:N}";
    private static RedisKey LockKey(Guid roomId) => $"{RoomPrefix(roomId)}:lock:{roomId:N}";
    private static RedisKey EnqueueIntentKey(Guid roomId) => $"{RoomPrefix(roomId)}:enqueue-intent:{roomId:N}";
    private static RedisKey AdmissionLockKey(Guid roomId) => $"{AdmissionLockPrefix}{roomId:N}";
    private static RedisKey PendingKey(Guid roomId) => GetBucket(roomId).PendingKey;
    private static RedisKey FailedKey(Guid roomId) => GetBucket(roomId).FailedKey;
    private static RedisKey EnqueuedKey(Guid roomId) => GetBucket(roomId).EnqueuedKey;
    private static RedisKey ProfileIndexKey(Guid roomId) => GetBucket(roomId).ProfileIndexKey;
    private static RedisKey MetricsKey(Guid roomId) => GetBucket(roomId).MetricsKey;

    private static CompletionBucket[] CreateBuckets() => Enumerable
        .Range(0, RedisClusterKeyspace.BucketCount)
        .Select(index =>
        {
            var prefix = $"keywars:{{{Subsystem}-b{index:x2}}}";
            return new CompletionBucket(
                (byte)index,
                $"{prefix}:pending",
                $"{prefix}:failed",
                $"{prefix}:enqueued",
                $"{prefix}:profile-index",
                $"{prefix}:profile-counts",
                $"{prefix}:profile-revisions",
                $"{prefix}:profile-epoch",
                $"{prefix}:metrics");
        })
        .ToArray();

    private static TimeSpan CalculateRedriveDelay(long cycle)
    {
        var exponent = (int)Math.Clamp(cycle - 1, 0, 8);
        return TimeSpan.FromSeconds(Math.Min(15 * 60, 30 * Math.Pow(2, exponent)));
    }

    private sealed record CompletionStatusRecord(string IdempotencyKey, CompletionState State);
    private sealed record CompletionRecordState(Guid RoomId, CompletionState State);
    private sealed record AdmissionRepairEntry(string RoomId, long EnqueuedAt);
    private sealed record RelatedCompletionSnapshot(
        IReadOnlyList<CompletionRecordState> Records,
        bool Stable);
    private sealed record CompletionMetricSnapshot(
        long Retries,
        long Persisted,
        long Failed,
        long DurationMicroseconds,
        long DurationCount);
    private sealed record DueCompletion(CompletionBucket Bucket, RedisValue Element);
    private sealed record CompletionBucket(
        byte Id,
        RedisKey PendingKey,
        RedisKey FailedKey,
        RedisKey EnqueuedKey,
        RedisKey ProfileIndexKey,
        RedisKey ProfileCountsKey,
        RedisKey ProfileRevisionsKey,
        RedisKey ProfileEpochKey,
        RedisKey MetricsKey);
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
    public CompletionRoomSlotReservation? TryReserveRoomSlot(Guid roomId, int currentRoomCount) =>
        durable.TryReserveRoomSlot(roomId, currentRoomCount);
    public void CommitRoomSlot(CompletionRoomSlotReservation reservation) => durable.CommitRoomSlot(reservation);
    public void ReleaseRoomSlot(CompletionRoomSlotReservation reservation) => durable.ReleaseRoomSlot(reservation);

    public sealed class CompletionBatch(
        ClusterLiveRoomCompletionSink owner,
        RedisLiveRoomCompletionQueue durable) : IDisposable
    {
        private readonly Dictionary<Guid, CompletedRoomRecord> records = [];
        private int completed;

        internal CompletionReceipt Add(CompletedRoomRecord record)
        {
            records[record.Id] = record;
            return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Pending);
        }

        public void Commit()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            foreach (var record in records.Values.OrderBy(item => item.Id))
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
