using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisLiveRoomDispatcher(
    IConnectionMultiplexer redis,
    LiveRoomManager rooms,
    RedisLiveProgressRelay progressRelay,
    ClusterLiveRoomCompletionSink completionSink,
    IOptions<LiveOptions> options,
    TimeProvider timeProvider) : ILiveRoomDispatcher, ILiveRoomStateCoordinator
{
    private const string DirectoryPrefix = "keywars:{room-directory}";
    private const int SweepDirtyBatchSize = 32;
    private const int SweepDirtyMaxBatchSize = 256;
    private const int SweepReservationBatchSize = 32;
    private const int SweepReservationMaxBatchSize = 128;
    private const int SweepDueBatchSize = 32;
    private const int SweepAuditBatchSize = 8;
    private const int LobbyRepairAllowance = 16;
    private const int LobbyRepairBudget = 128;
    private const int ParallelRoomReads = 8;
    private static readonly RedisKey RoomIndexKey = $"{DirectoryPrefix}:active";
    private static readonly RedisKey CapacityRoomsKey = $"{DirectoryPrefix}:capacity";
    private static readonly RedisKey SweepDueKey = $"{DirectoryPrefix}:sweep-due";
    private static readonly RedisKey ReconcileDirtyKey = $"{DirectoryPrefix}:reconcile-dirty";
    private static readonly RedisKey SweepLeaderKey = $"{DirectoryPrefix}:sweep-leader";
    private static readonly RedisKey CodeDirectoryKey = $"{DirectoryPrefix}:codes";
    private static readonly RedisKey DirectoryEntriesKey = $"{DirectoryPrefix}:entries";
    private static readonly RedisKey DirectoryRevisionsKey = $"{DirectoryPrefix}:revisions";
    private static readonly RedisKey DirectorySortMembersKey = $"{DirectoryPrefix}:sort-members";
    private static readonly RedisKey DirectoryContributionsKey = $"{DirectoryPrefix}:contributions";
    private static readonly RedisKey DirectoryCodesKey = $"{DirectoryPrefix}:room-codes";
    private static readonly RedisKey DirectoryReservationsKey = $"{DirectoryPrefix}:reservations";
    private static readonly RedisKey ReservationExpiryKey = $"{DirectoryPrefix}:reservation-expiry";
    private static readonly RedisKey DirectoryAudiencesKey = $"{DirectoryPrefix}:audiences";
    private static readonly RedisKey PublicLobbyIndexKey = $"{DirectoryPrefix}:lobby:public";
    private static readonly RedisKey PrivateLobbyIndexKey = $"{DirectoryPrefix}:lobby:private";
    private static readonly RedisKey MetricsKey = $"{DirectoryPrefix}:metrics";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly LuaScript ReserveDirectoryScript = LuaScript.Prepare(
        "local expired = redis.call('zrangebyscore', @reservationExpiryKey, '-inf', @cleanupBefore, 'LIMIT', 0, @cleanupLimit); " +
        "for _, staleId in ipairs(expired) do " +
        "if tonumber(redis.call('hget', @revisionKey, staleId) or '-1') == 0 then " +
        "local staleCode = redis.call('hget', @roomCodesKey, staleId); " +
        "if staleCode and redis.call('hget', @codeDirectoryKey, staleCode) == staleId then redis.call('hdel', @codeDirectoryKey, staleCode); end; " +
        "redis.call('hdel', @roomCodesKey, staleId); redis.call('hdel', @revisionKey, staleId); redis.call('hdel', @reservationKey, staleId); " +
        "redis.call('zrem', @reservationExpiryKey, staleId); " +
        "redis.call('zrem', @roomIndexKey, staleId); redis.call('zrem', @capacityKey, staleId); redis.call('zrem', @dueKey, staleId); redis.call('zrem', @dirtyKey, staleId); " +
        "end; end; " +
        "if redis.call('hexists', @codeDirectoryKey, @code) == 1 or " +
        "redis.call('hexists', @revisionKey, @roomId) == 1 then return 0 end; " +
        "local currentCapacity = redis.call('zcard', @capacityKey); " +
        "if currentCapacity ~= tonumber(@expectedCapacity) then return -2 end; " +
        "if currentCapacity >= tonumber(@capacity) then return -1 end; " +
        "redis.call('hset', @codeDirectoryKey, @code, @roomId); " +
        "redis.call('hset', @roomCodesKey, @roomId, @code); " +
        "redis.call('hset', @revisionKey, @roomId, 0); " +
        "redis.call('hset', @reservationKey, @roomId, @reservationToken); " +
        "redis.call('zadd', @reservationExpiryKey, @reservationUntil, @roomId); " +
        "redis.call('zadd', @roomIndexKey, @reservationUntil, @roomId); " +
        "redis.call('zadd', @capacityKey, @reservationUntil, @roomId); " +
        "redis.call('zadd', @dueKey, @reservationUntil, @roomId); " +
        "redis.call('zadd', @dirtyKey, @now, @roomId); return 1");
    private static readonly LuaScript InitializeRoomScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -2 end; " +
        "if redis.call('exists', @roomKey) == 1 then return 0 end; " +
        "redis.call('hset', @roomKey, 'revision', 1, 'memento', @memento, 'code', @code); " +
        "redis.call('expire', @roomKey, @ttlSeconds); return 1");
    private static readonly LuaScript CompareExchangeScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -2 end; " +
        "if tonumber(redis.call('hget', @roomKey, 'revision') or '-1') ~= tonumber(@expectedRevision) then return 0 end; " +
        "redis.call('hset', @roomKey, 'revision', tonumber(@expectedRevision) + 1, 'memento', @memento, 'code', @code); " +
        "if tonumber(@persistent) == 1 then redis.call('persist', @roomKey) else redis.call('expire', @roomKey, @ttlSeconds) end; return 1");
    private static readonly LuaScript TouchRoomTtlScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -2 end; " +
        "if tonumber(redis.call('hget', @roomKey, 'revision') or '-1') ~= tonumber(@expectedRevision) then return 0 end; " +
        "if tonumber(@persistent) == 1 then redis.call('persist', @roomKey) else redis.call('expire', @roomKey, @ttlSeconds) end; return 1");
    private static readonly LuaScript DeleteRoomStateScript = LuaScript.Prepare(
        "if redis.call('get', @lockKey) ~= @lockToken then return -2 end; " +
        "if tonumber(redis.call('hget', @roomKey, 'revision') or '-1') ~= tonumber(@expectedRevision) then return 0 end; " +
        "redis.call('del', @roomKey); return 1");
    private static readonly LuaScript CommitDirectoryScript = LuaScript.Prepare(
        "local current = tonumber(redis.call('hget', @revisionKey, @roomId) or '-1'); " +
        "if current > tonumber(@revision) then return 0 end; " +
        "if string.len(@reservationToken) > 0 and redis.call('hget', @reservationKey, @roomId) ~= @reservationToken then return -2 end; " +
        "if current < 0 then return -3 end; " +
        "local codeOwner = redis.call('hget', @codeDirectoryKey, @code); " +
        "if codeOwner and codeOwner ~= @roomId then return -4 end; " +
        "local oldContribution = redis.call('hget', @contributionKey, @roomId); " +
        "local oldA, oldO, oldR, oldP = 0, 0, 0, 0; " +
        "if oldContribution then oldA, oldO, oldR, oldP = string.match(oldContribution, '^(%-?%d+),(%-?%d+),(%-?%d+),(%-?%d+)$'); end; " +
        "oldA, oldO, oldR, oldP = tonumber(oldA) or 0, tonumber(oldO) or 0, tonumber(oldR) or 0, tonumber(oldP) or 0; " +
        "redis.call('hincrby', @metricsKey, 'active', tonumber(@metricActive) - oldA); " +
        "redis.call('hincrby', @metricsKey, 'open', tonumber(@metricOpen) - oldO); " +
        "redis.call('hincrby', @metricsKey, 'running', tonumber(@metricRunning) - oldR); " +
        "redis.call('hincrby', @metricsKey, 'participants', tonumber(@metricParticipants) - oldP); " +
        "local oldSort = redis.call('hget', @sortMembersKey, @roomId); " +
        "local oldAudience = redis.call('hget', @audienceKey, @roomId); " +
        "if oldSort then redis.call('zrem', @publicLobbyKey, oldSort); " +
        "if oldAudience then for profileId in string.gmatch(oldAudience, '[^,]+') do redis.call('zrem', @privateLobbyKey, profileId .. ':' .. oldSort); end; end; end; " +
        "local oldCode = redis.call('hget', @roomCodesKey, @roomId); " +
        "if oldCode and oldCode ~= @code and redis.call('hget', @codeDirectoryKey, oldCode) == @roomId then redis.call('hdel', @codeDirectoryKey, oldCode); end; " +
        "redis.call('hset', @entryKey, @roomId, @entry); redis.call('hset', @revisionKey, @roomId, @revision); " +
        "redis.call('hset', @sortMembersKey, @roomId, @sortMember); " +
        "redis.call('hset', @contributionKey, @roomId, @contribution); " +
        "redis.call('hset', @audienceKey, @roomId, @audience); " +
        "redis.call('hset', @roomCodesKey, @roomId, @code); redis.call('hset', @codeDirectoryKey, @code, @roomId); " +
        "redis.call('hdel', @reservationKey, @roomId); redis.call('zrem', @reservationExpiryKey, @roomId); redis.call('zadd', @roomIndexKey, @auditAt, @roomId); " +
        "if tonumber(@consumesCapacity) == 1 then redis.call('zadd', @capacityKey, @auditAt, @roomId) else redis.call('zrem', @capacityKey, @roomId) end; " +
        "if tonumber(@publicLobby) == 1 then redis.call('zadd', @publicLobbyKey, 0, @sortMember); end; " +
        "if tonumber(@privateLobby) == 1 then for profileId in string.gmatch(@audience, '[^,]+') do redis.call('zadd', @privateLobbyKey, 0, profileId .. ':' .. @sortMember); end; end; " +
        "redis.call('zadd', @dueKey, @nextDue, @roomId); redis.call('zrem', @dirtyKey, @roomId); return 1");
    private static readonly LuaScript RemoveDirectoryScript = LuaScript.Prepare(
        "local current = tonumber(redis.call('hget', @revisionKey, @roomId) or '-1'); if current ~= tonumber(@expectedRevision) then return 0 end; " +
        "local contribution = redis.call('hget', @contributionKey, @roomId); local a, o, r, p = 0, 0, 0, 0; " +
        "if contribution then a, o, r, p = string.match(contribution, '^(%-?%d+),(%-?%d+),(%-?%d+),(%-?%d+)$'); end; " +
        "a, o, r, p = tonumber(a) or 0, tonumber(o) or 0, tonumber(r) or 0, tonumber(p) or 0; " +
        "redis.call('hincrby', @metricsKey, 'active', -a); redis.call('hincrby', @metricsKey, 'open', -o); " +
        "redis.call('hincrby', @metricsKey, 'running', -r); redis.call('hincrby', @metricsKey, 'participants', -p); " +
        "local sortMember = redis.call('hget', @sortMembersKey, @roomId); local audience = redis.call('hget', @audienceKey, @roomId); " +
        "if sortMember then redis.call('zrem', @publicLobbyKey, sortMember); " +
        "if audience then for profileId in string.gmatch(audience, '[^,]+') do redis.call('zrem', @privateLobbyKey, profileId .. ':' .. sortMember); end; end; end; " +
        "local code = redis.call('hget', @roomCodesKey, @roomId); " +
        "if code and redis.call('hget', @codeDirectoryKey, code) == @roomId then redis.call('hdel', @codeDirectoryKey, code); end; " +
        "redis.call('hdel', @entryKey, @roomId); redis.call('hdel', @revisionKey, @roomId); " +
        "redis.call('hdel', @sortMembersKey, @roomId); redis.call('hdel', @contributionKey, @roomId); redis.call('hdel', @audienceKey, @roomId); " +
        "redis.call('hdel', @roomCodesKey, @roomId); redis.call('hdel', @reservationKey, @roomId); " +
        "redis.call('zrem', @reservationExpiryKey, @roomId); " +
        "redis.call('zrem', @roomIndexKey, @roomId); redis.call('zrem', @capacityKey, @roomId); " +
        "redis.call('zrem', @dueKey, @roomId); redis.call('zrem', @dirtyKey, @roomId); return 1");
    private static readonly LuaScript ClaimSweepWorkScript = LuaScript.Prepare(
        "if redis.call('get', @leaderKey) ~= @leaderToken then return {-2} end; " +
        "local selected = {}; local seen = {}; " +
        "local function claim(key, limit) " +
        "local members = redis.call('zrangebyscore', key, '-inf', @now, 'LIMIT', 0, limit); " +
        "for _, member in ipairs(members) do " +
        "redis.call('zadd', key, @claimUntil, member); " +
        "if not seen[member] then seen[member] = true; table.insert(selected, member); end; " +
        "end; end; " +
        "claim(@reservationExpiryKey, tonumber(@reservationLimit)); claim(@dirtyKey, tonumber(@dirtyLimit)); claim(@dueKey, tonumber(@dueLimit)); " +
        "claim(@roomIndexKey, tonumber(@auditLimit)); claim(@capacityKey, tonumber(@auditLimit)); " +
        "return selected");
    private static readonly LuaScript RetrySweepClaimScript = LuaScript.Prepare(
        "if redis.call('get', @leaderKey) ~= @leaderToken then return -2 end; " +
        "local function retry(key, roomId) " +
        "local score = redis.call('zscore', key, roomId); " +
        "if score and tonumber(score) == tonumber(@claimUntil) then redis.call('zadd', key, @retryAt, roomId); end; " +
        "end; for roomId in string.gmatch(@roomIds, '[^,]+') do " +
        "retry(@reservationExpiryKey, roomId); retry(@dirtyKey, roomId); retry(@dueKey, roomId); " +
        "retry(@roomIndexKey, roomId); retry(@capacityKey, roomId); end; return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TimeSpan lobbyRoomRetention = TimeSpan.FromMinutes(
        Math.Clamp(options.Value.LobbyRoomRetentionMinutes, 30, 7 * 24 * 60));
    private readonly TimeSpan completedRoomRetention = TimeSpan.FromMinutes(
        Math.Clamp(options.Value.CompletedRoomRetentionMinutes, 5, 24 * 60));
    private readonly TimeSpan roomStateLifetime = TimeSpan.FromMinutes(Math.Max(
        60,
        Math.Max(options.Value.LobbyRoomRetentionMinutes, options.Value.CompletedRoomRetentionMinutes) + 60));
    private static readonly TimeSpan SweepLeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SweepClaimVisibility = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SweepRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SweepTimeBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AuditInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CreateReservationLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReservationCleanupSafetyMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingCompletionPollInterval = TimeSpan.FromSeconds(15);

    public bool IsAuthoritative => true;
    public string InstanceId { get; } = ResolveInstanceId();

    public async ValueTask<LiveRoomSnapshot> CreateRoomAsync(
        CreateLiveRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maxConcurrentRooms = Math.Max(1, options.Value.MaxConcurrentRooms);
            var activeRoomCount = checked((int)Math.Min(
                int.MaxValue,
                await database.SortedSetLengthAsync(CapacityRoomsKey)));
            if (!completionSink.CanAcceptNewRoom(activeRoomCount))
            {
                throw new InvalidOperationException(
                    "Die Arena nimmt vorübergehend keine neuen Räume an, weil die Ergebnispersistenz ausgelastet ist.");
            }
            if (activeRoomCount >= maxConcurrentRooms)
            {
                throw new InvalidOperationException("Die maximale Anzahl gleichzeitiger Live-Räume ist erreicht.");
            }
            var reservationCapacity = Math.Min(maxConcurrentRooms, checked(activeRoomCount + 1));
            var snapshot = rooms.CreateRoom(request, enforceLocalRoomCapacity: false);
            var reservationToken = Guid.NewGuid().ToString("N");
            var reserved = false;
            var completed = false;
            var stateUnloaded = false;
            try
            {
                var memento = ScrubTransientInput(rooms.ExportRoomState(snapshot.RoomId));
                var now = timeProvider.GetUtcNow();
                await using (var roomLock = await RedisDistributedLease.AcquireAsync(
                    database,
                    LockKey(snapshot.RoomId),
                    cancellationToken))
                {
                    try
                    {
                        using var operationCancellation = LinkToLease(cancellationToken, roomLock);
                        var operationToken = operationCancellation.Token;
                        var reservationResult = (int)await database.ScriptEvaluateAsync(
                            ReserveDirectoryScript,
                            new
                            {
                                codeDirectoryKey = CodeDirectoryKey,
                                roomCodesKey = DirectoryCodesKey,
                                revisionKey = DirectoryRevisionsKey,
                                reservationKey = DirectoryReservationsKey,
                                reservationExpiryKey = ReservationExpiryKey,
                                roomIndexKey = RoomIndexKey,
                                capacityKey = CapacityRoomsKey,
                                dueKey = SweepDueKey,
                                dirtyKey = ReconcileDirtyKey,
                                roomId = snapshot.RoomId.ToString("N"),
                                code = snapshot.Code,
                                reservationToken,
                                now = now.ToUnixTimeMilliseconds(),
                                cleanupBefore = (now - roomStateLifetime - ReservationCleanupSafetyMargin).ToUnixTimeMilliseconds(),
                                cleanupLimit = SweepReservationBatchSize,
                                reservationUntil = (now + CreateReservationLifetime).ToUnixTimeMilliseconds(),
                                expectedCapacity = activeRoomCount,
                                capacity = reservationCapacity
                            });
                        if (reservationResult is -1 or -2)
                        {
                            continue;
                        }

                        if (reservationResult != 1)
                        {
                            continue;
                        }
                        reserved = true;

                        var initialized = (int)await database.ScriptEvaluateAsync(
                            InitializeRoomScript,
                            new
                            {
                                lockKey = roomLock.Key,
                                lockToken = roomLock.Token,
                                roomKey = RoomKey(snapshot.RoomId),
                                code = snapshot.Code,
                                memento = Serialize(memento),
                                ttlSeconds = (long)roomStateLifetime.TotalSeconds
                            });
                        if (initialized == -2)
                        {
                            roomLock.ThrowFenceLost("Raumzustand initialisieren");
                        }
                        if (initialized != 1)
                        {
                            throw new InvalidOperationException("Der neue Live-Raum konnte nicht initialisiert werden.");
                        }

                        await CommitDirectoryAsync(
                            new RoomRecord(1, memento),
                            reservationToken,
                            roomLock,
                            now,
                            operationToken);
                        completed = true;
                        return snapshot;
                    }
                    finally
                    {
                        rooms.UnloadRoomState(snapshot.RoomId);
                        stateUnloaded = true;
                    }
                }
            }
            finally
            {
                if (!stateUnloaded)
                {
                    rooms.UnloadRoomState(snapshot.RoomId);
                }
                if (reserved && !completed)
                {
                    await RollbackReservationAsync(snapshot.RoomId, reservationToken, CancellationToken.None);
                }
            }
        }

        throw new InvalidOperationException("Es konnte kein clusterweit eindeutiger Raumcode erzeugt werden.");
    }

    public async ValueTask<IReadOnlyList<LiveRoomSnapshot>> ListOpenRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await database.SortedSetRangeByRankAsync(PublicLobbyIndexKey);
        var mementos = await ReadMementosBoundedAsync(
            members.Select(ParseRoomId).Where(id => id != Guid.Empty).ToArray(),
            cancellationToken);
        return mementos
            .Where(room => room.Visibility == LiveRoomVisibility.InternalOpen &&
                room.Phase == LiveRoomPhase.Lobby && !room.Finished)
            .Select(rooms.ProjectSnapshot)
            .OrderBy(room => room.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(room => room.RoomId)
            .ToArray();
    }

    public async ValueTask<LiveRoomLobbyPage> ListLobbySummariesAsync(
        Guid viewerProfileId,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        cancellationToken.ThrowIfCancellationRequested();
        var privatePrefix = ViewerLobbyMemberPrefix(viewerProfileId);
        var privateUpperBound = privatePrefix + '\uffff';
        var publicCountTask = database.SortedSetLengthAsync(PublicLobbyIndexKey);
        var privateCountTask = database.SortedSetLengthByValueAsync(
            PrivateLobbyIndexKey,
            privatePrefix,
            privateUpperBound);
        await Task.WhenAll(publicCountTask, privateCountTask);
        var totalLong = publicCountTask.Result + privateCountTask.Result;
        var effectiveOffset = Math.Min((long)offset, totalLong);
        var partition = await FindMergePartitionAsync(
            PublicLobbyIndexKey,
            publicCountTask.Result,
            PrivateLobbyIndexKey,
            privateCountTask.Result,
            effectiveOffset,
            privatePrefix,
            privateUpperBound,
            cancellationToken);
        var publicOffset = partition.Left;
        var privateOffset = partition.Right;
        var items = new List<LiveRoomLobbySummary>(limit);
        var returnedIds = new HashSet<Guid>();
        var invalid = 0;
        var inspected = 0;
        var inspectionLimit = limit + LobbyRepairBudget;
        while (items.Count < limit && inspected < inspectionLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readLimit = Math.Min(limit + LobbyRepairAllowance, inspectionLimit - inspected);
            var publicMembersTask = database.SortedSetRangeByRankAsync(
                PublicLobbyIndexKey,
                publicOffset,
                publicOffset + readLimit - 1);
            var privateMembersTask = database.SortedSetRangeByValueAsync(
                PrivateLobbyIndexKey,
                privatePrefix,
                privateUpperBound,
                Exclude.None,
                privateOffset,
                readLimit);
            await Task.WhenAll(publicMembersTask, privateMembersTask);
            var candidates = MergeLobbyMembers(
                    publicMembersTask.Result,
                    privateMembersTask.Result,
                    privatePrefix)
                .Take(readLimit)
                .ToArray();
            if (candidates.Length == 0)
            {
                break;
            }

            var ids = candidates.Select(candidate => ParseRoomId(candidate.SortMember)).ToArray();
            var values = await database.HashGetAsync(
                DirectoryEntriesKey,
                ids.Select(id => (RedisValue)id.ToString("N")).ToArray());
            var consumedPublic = 0;
            var consumedPrivate = 0;
            var removedPublic = 0;
            var removedPrivate = 0;
            for (var index = 0; index < candidates.Length && items.Count < limit; index++)
            {
                var candidate = candidates[index];
                if (candidate.IsPrivate)
                {
                    consumedPrivate++;
                }
                else
                {
                    consumedPublic++;
                }

                inspected++;
                var entry = DeserializeDirectoryEntry(values[index]);
                var isValid = ids[index] != Guid.Empty && entry is not null &&
                    StringComparer.Ordinal.Equals(entry.SortMember, candidate.SortMember) &&
                    entry.IsLobby &&
                    (candidate.IsPrivate
                        ? !entry.IsPublicLobby && entry.AudienceProfileIds.Contains(viewerProfileId)
                        : entry.IsPublicLobby);
                if (!isValid)
                {
                    invalid++;
                    if (candidate.IsPrivate)
                    {
                        removedPrivate++;
                        await database.SortedSetRemoveAsync(PrivateLobbyIndexKey, candidate.StoredMember);
                    }
                    else
                    {
                        removedPublic++;
                        await database.SortedSetRemoveAsync(PublicLobbyIndexKey, candidate.StoredMember);
                    }
                    continue;
                }

                if (returnedIds.Add(ids[index]))
                {
                    items.Add(entry!.Summary);
                }
            }

            publicOffset += consumedPublic - removedPublic;
            privateOffset += consumedPrivate - removedPrivate;
        }

        var total = checked((int)Math.Min(int.MaxValue, totalLong));
        return new LiveRoomLobbyPage(items, offset, limit, Math.Max(0, total - invalid));
    }

    public async ValueTask<LiveRoomMetricsSnapshot> MetricsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await database.HashGetAsync(
            MetricsKey,
            ["active", "open", "running", "participants"]);
        return new LiveRoomMetricsSnapshot(
            ParseMetric(values, 0),
            ParseMetric(values, 1),
            ParseMetric(values, 2),
            ParseMetric(values, 3));
    }

    public async ValueTask<Guid> ResolveRoomIdByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = LiveRoomManager.NormalizeRoomCode(code);
        var value = await database.HashGetAsync(CodeDirectoryKey, normalized);
        if (value.IsNull || !Guid.TryParseExact(value.ToString(), "N", out var roomId))
        {
            throw new InvalidOperationException("Der Raumcode ist ungültig.");
        }

        return roomId;
    }

    public async ValueTask<LiveRoomSnapshot> JoinByCodeAsync(
        string code,
        Guid profileId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var roomId = await ResolveRoomIdByCodeAsync(code, cancellationToken);
        return await ExecuteAsync(
            roomId,
            manager => manager.JoinByResolvedCode(roomId, code, profileId, displayName),
            cancellationToken);
    }

    public ValueTask<LiveRoomSnapshot> JoinAsync(Guid roomId, Guid profileId, string displayName, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Join(roomId, profileId, displayName), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SetReadyAsync(Guid roomId, Guid profileId, bool ready, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.SetReady(roomId, profileId, ready), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SetLobbyLockedAsync(Guid roomId, Guid hostProfileId, bool locked, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.SetLobbyLocked(roomId, hostProfileId, locked), cancellationToken);

    public ValueTask<LiveRoomSnapshot> TransferHostAsync(Guid roomId, Guid hostProfileId, Guid nextHostProfileId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.TransferHost(roomId, hostProfileId, nextHostProfileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> KickAsync(Guid roomId, Guid hostProfileId, Guid targetProfileId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Kick(roomId, hostProfileId, targetProfileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> CloseAsync(Guid roomId, Guid hostProfileId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Close(roomId, hostProfileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> StartAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Start(roomId, profileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SubmitProgressAsync(Guid roomId, Guid profileId, int sequence, string input, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.SubmitProgress(roomId, profileId, sequence, input), cancellationToken);

    public async ValueTask<LiveProgressResult> SubmitProgressDeltaAsync(
        Guid roomId,
        Guid profileId,
        int sequence,
        string input,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
            roomId,
            manager => manager.SubmitProgressDelta(roomId, profileId, sequence, input),
            cancellationToken);
        if (result.Delta is not { } delta)
        {
            return result;
        }

        await progressRelay.EnqueueAsync(delta, cancellationToken);
        return result with { Delta = null };
    }

    public ValueTask<LiveRoomSnapshot> FinishAsync(Guid roomId, Guid profileId, string input, int backspaces, int focusLosses, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Finish(roomId, profileId, input, backspaces, focusLosses), cancellationToken);

    public ValueTask<LiveRoomSnapshot> GiveUpAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.GiveUp(roomId, profileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> DisconnectAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Disconnect(roomId, profileId), cancellationToken);

    public ValueTask<LiveRoomSnapshot> SnapshotAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(roomId, manager => manager.Snapshot(roomId), cancellationToken);

    public async ValueTask<IReadOnlyList<LiveRoomSnapshot>> SweepAsync(CancellationToken cancellationToken = default)
    {
        await using var leader = await RedisDistributedLease.TryAcquireAsync(
            database,
            SweepLeaderKey,
            SweepLeaseDuration,
            cancellationToken);
        if (leader is null)
        {
            return [];
        }

        using var operationCancellation = LinkToLease(cancellationToken, leader);
        var operationToken = operationCancellation.Token;
        var now = timeProvider.GetUtcNow();
        var claimUntil = now + SweepClaimVisibility;
        var ids = await ClaimSweepWorkAsync(leader, now, claimUntil, operationToken);
        var changed = new List<LiveRoomSnapshot>();
        var startedAt = Stopwatch.GetTimestamp();
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            if (Stopwatch.GetElapsedTime(startedAt) >= SweepTimeBudget)
            {
                await RetryClaimsAsync(ids.Skip(index), leader, claimUntil, now, operationToken);
                break;
            }

            try
            {
                var result = await SweepRoomAsync(id, leader, claimUntil, now, operationToken);
                if (result is not null)
                {
                    changed.Add(result);
                }
            }
            catch (RoomNotFoundException)
            {
                await RetryClaimAsync(id, leader, claimUntil, now + SweepRetryDelay, operationToken);
            }
        }

        return changed;
    }

    public async ValueTask RemoveProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        foreach (var id in await ReadActiveRoomIdsAsync(cancellationToken))
        {
            try
            {
                await ExecuteAsync(
                    id,
                    manager =>
                    {
                        manager.RemoveProfileFromRoom(id, profileId);
                        return true;
                    },
                    cancellationToken);
            }
            catch (RoomNotFoundException)
            {
            }
        }
    }

    public async ValueTask<int> AbortActiveRoomsAsync(CancellationToken cancellationToken = default)
    {
        var aborted = 0;
        foreach (var id in await ReadActiveRoomIdsAsync(cancellationToken))
        {
            try
            {
                aborted += await ExecuteAsync(id, manager => manager.AbortActiveRoom(id), cancellationToken);
            }
            catch (RoomNotFoundException)
            {
            }
        }

        return aborted;
    }

    private async ValueTask<T> ExecuteAsync<T>(
        Guid roomId,
        Func<LiveRoomManager, T> operation,
        CancellationToken cancellationToken)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(database, LockKey(roomId), cancellationToken);
        using var operationCancellation = LinkToLease(cancellationToken, roomLock);
        var operationToken = operationCancellation.Token;
        var record = await ReadRoomAsync(roomId, operationToken)
            ?? throw new RoomNotFoundException();
        if (IsExpiredLobby(record.Memento, timeProvider.GetUtcNow()))
        {
            await RetireRoomAsync(record, roomLock, operationToken);
            throw new RoomNotFoundException();
        }

        var execution = await ExecuteLockedAsync(roomId, operation, record, roomLock, operationToken);
        if (execution.Revision != record.Revision &&
            RequiresDirectoryUpdate(record.Memento, execution.Memento, timeProvider.GetUtcNow()))
        {
            await CommitDirectoryAsync(
                new RoomRecord(execution.Revision, execution.Memento),
                "",
                roomLock,
                timeProvider.GetUtcNow(),
                operationToken);
        }

        return execution.Result;
    }

    private async ValueTask<LockedExecution<T>> ExecuteLockedAsync<T>(
        Guid roomId,
        Func<LiveRoomManager, T> operation,
        RoomRecord record,
        RedisDistributedLease roomLock,
        CancellationToken operationToken)
    {
        try
        {
            rooms.UnloadRoomState(roomId);
            if (!rooms.ImportRoomState(record.Memento))
            {
                throw new InvalidOperationException("Der Live-Raum konnte nicht auf den aktuellen Clusterstand gebracht werden.");
            }

            rooms.EnsurePendingPersistenceQueued(roomId);

            using var completionBatch = completionSink.BeginBatch();
            var result = operation(rooms);
            var updated = ScrubTransientInput(rooms.ExportRoomState(roomId));
            if (updated.StateVersion == record.Memento.StateVersion)
            {
                roomLock.ThrowIfLost();
                completionBatch.Commit();
                return new LockedExecution<T>(result, updated, record.Revision);
            }

            if (RequiresDirectoryUpdate(record.Memento, updated, timeProvider.GetUtcNow()))
            {
                await MarkDirectoryDirtyAsync(roomId, operationToken);
            }

            var saved = (int)await database.ScriptEvaluateAsync(
                CompareExchangeScript,
                new
                {
                    lockKey = roomLock.Key,
                    lockToken = roomLock.Token,
                    roomKey = RoomKey(roomId),
                    code = updated.Code,
                    expectedRevision = record.Revision,
                    memento = Serialize(updated),
                    persistent = RequiresPersistentRoomState(updated) ? 1 : 0,
                    ttlSeconds = (long)roomStateLifetime.TotalSeconds
                });
            if (saved == -2)
            {
                roomLock.ThrowFenceLost("Raumzustand speichern");
            }

            if (saved != 1)
            {
                throw new InvalidOperationException(
                    "Der Live-Raum wurde parallel geändert. Lade den aktuellen Stand neu.");
            }

            completionBatch.Commit();
            return new LockedExecution<T>(result, updated, checked(record.Revision + 1));
        }
        finally
        {
            rooms.UnloadRoomState(roomId);
        }
    }

    private async Task<IReadOnlyList<LiveRoomMemento>> ReadMementosBoundedAsync(
        IReadOnlyList<Guid> roomIds,
        CancellationToken cancellationToken)
    {
        var result = new List<LiveRoomMemento>();
        for (var offset = 0; offset < roomIds.Count; offset += ParallelRoomReads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = await Task.WhenAll(roomIds
                .Skip(offset)
                .Take(ParallelRoomReads)
                .Select(id => ReadRoomAsync(id, cancellationToken)));
            foreach (var record in records)
            {
                if (record is not null)
                {
                    result.Add(record.Memento);
                }
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> ReadActiveRoomIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await database.SortedSetRangeByRankAsync(RoomIndexKey);
        return values
            .Select(value => Guid.TryParseExact(value.ToString(), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
    }

    private async Task<RoomRecord?> ReadRoomAsync(Guid roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await database.HashGetAsync(RoomKey(roomId), ["revision", "memento"]);
        if (values.Length != 2 || values[0].IsNull || values[1].IsNull || !long.TryParse(values[0].ToString(), out var revision))
        {
            return null;
        }

        var memento = JsonSerializer.Deserialize<LiveRoomMemento>(values[1].ToString(), SerializerOptions)
            ?? throw new InvalidOperationException("Der gespeicherte Live-Raum ist ungültig.");
        return new RoomRecord(revision, memento);
    }

    private async Task<IReadOnlyList<Guid>> ClaimSweepWorkAsync(
        RedisDistributedLease leader,
        DateTimeOffset now,
        DateTimeOffset claimUntil,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        var dirtyBacklogTask = database.SortedSetLengthAsync(
            ReconcileDirtyKey,
            double.NegativeInfinity,
            nowMilliseconds);
        var reservationBacklogTask = database.SortedSetLengthAsync(
            ReservationExpiryKey,
            double.NegativeInfinity,
            nowMilliseconds);
        await Task.WhenAll(dirtyBacklogTask, reservationBacklogTask);
        var dirtyLimit = AdaptiveBatchSize(
            dirtyBacklogTask.Result,
            SweepDirtyBatchSize,
            SweepDirtyMaxBatchSize);
        var reservationLimit = AdaptiveBatchSize(
            reservationBacklogTask.Result,
            SweepReservationBatchSize,
            SweepReservationMaxBatchSize);
        var result = await database.ScriptEvaluateAsync(
            ClaimSweepWorkScript,
            new
            {
                leaderKey = leader.Key,
                leaderToken = leader.Token,
                dueKey = SweepDueKey,
                dirtyKey = ReconcileDirtyKey,
                reservationExpiryKey = ReservationExpiryKey,
                roomIndexKey = RoomIndexKey,
                capacityKey = CapacityRoomsKey,
                now = nowMilliseconds,
                claimUntil = claimUntil.ToUnixTimeMilliseconds(),
                dirtyLimit,
                reservationLimit,
                dueLimit = SweepDueBatchSize,
                auditLimit = SweepAuditBatchSize
            });
        var claimed = (RedisResult[]?)result ?? [];
        if (claimed.Length == 1 && StringComparer.Ordinal.Equals(claimed[0].ToString(), "-2"))
        {
            leader.ThrowFenceLost("Arena-Sweep-Arbeit reservieren");
        }

        return claimed
            .Select(item => Guid.TryParseExact(item.ToString(), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
    }

    private async Task<LiveRoomSnapshot?> SweepRoomAsync(
        Guid id,
        RedisDistributedLease leader,
        DateTimeOffset claimUntil,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var roomLock = await RedisDistributedLease.TryAcquireAsync(database, LockKey(id), cancellationToken);
        if (roomLock is null)
        {
            await RetryClaimAsync(id, leader, claimUntil, now + SweepRetryDelay, cancellationToken);
            return null;
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            leader.LeaseLost,
            roomLock.LeaseLost);
        var operationToken = operationCancellation.Token;
        var record = await ReadRoomAsync(id, operationToken);
        if (record is null)
        {
            roomLock.ThrowIfLost();
            await RemoveDirectoryAsync(id, await ReadDirectoryRevisionAsync(id), operationToken);
            return null;
        }

        if (IsExpiredLobby(record.Memento, now))
        {
            await RetireRoomAsync(record, roomLock, operationToken);
            return null;
        }

        if (IsExpiredCompletedRoom(record.Memento, now))
        {
            if (record.Memento.PersistenceState is CompletionState.Pending or CompletionState.Failed)
            {
                await TouchRoomTtlAsync(record, roomLock, operationToken);
                record = await RecoverPendingCompletionAsync(record, roomLock);
                await CommitDirectoryAsync(record, "", roomLock, now, operationToken);
                return null;
            }

            await RetireRoomAsync(record, roomLock, operationToken);
            return null;
        }

        var beforeVersion = record.Memento.StateVersion;
        await TouchRoomTtlAsync(record, roomLock, operationToken);
        var execution = await ExecuteLockedAsync(id, manager => manager.Snapshot(id), record, roomLock, operationToken);
        var currentNow = timeProvider.GetUtcNow();
        await CommitDirectoryAsync(
            new RoomRecord(execution.Revision, execution.Memento),
            "",
            roomLock,
            currentNow,
            operationToken);
        return execution.Memento.StateVersion == beforeVersion ? null : execution.Result;
    }

    private async Task RetryClaimsAsync(
        IEnumerable<Guid> roomIds,
        RedisDistributedLease leader,
        DateTimeOffset claimUntil,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retryAt = now + SweepRetryDelay;
        var ids = roomIds.Distinct().ToArray();
        if (ids.Length > 0)
        {
            await RetryClaimsCoreAsync(ids, leader, claimUntil, retryAt, cancellationToken);
        }
    }

    private async Task RetryClaimAsync(
        Guid roomId,
        RedisDistributedLease leader,
        DateTimeOffset claimUntil,
        DateTimeOffset retryAt,
        CancellationToken cancellationToken)
    {
        await RetryClaimsCoreAsync([roomId], leader, claimUntil, retryAt, cancellationToken);
    }

    private async Task RetryClaimsCoreAsync(
        IReadOnlyList<Guid> roomIds,
        RedisDistributedLease leader,
        DateTimeOffset claimUntil,
        DateTimeOffset retryAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (int)await database.ScriptEvaluateAsync(
            RetrySweepClaimScript,
            new
            {
                leaderKey = leader.Key,
                leaderToken = leader.Token,
                reservationExpiryKey = ReservationExpiryKey,
                dirtyKey = ReconcileDirtyKey,
                dueKey = SweepDueKey,
                roomIndexKey = RoomIndexKey,
                capacityKey = CapacityRoomsKey,
                roomIds = string.Join(',', roomIds.Select(roomId => roomId.ToString("N"))),
                claimUntil = claimUntil.ToUnixTimeMilliseconds(),
                retryAt = retryAt.ToUnixTimeMilliseconds()
            });
        if (result == -2)
        {
            leader.ThrowFenceLost("Arena-Sweep-Arbeit freigeben");
        }
    }

    private async Task CommitDirectoryAsync(
        RoomRecord record,
        string reservationToken,
        RedisDistributedLease roomLock,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        roomLock.ThrowIfLost();
        var entry = CreateDirectoryEntry(record, now);
        var result = (int)await database.ScriptEvaluateAsync(
            CommitDirectoryScript,
            new
            {
                entryKey = DirectoryEntriesKey,
                revisionKey = DirectoryRevisionsKey,
                sortMembersKey = DirectorySortMembersKey,
                contributionKey = DirectoryContributionsKey,
                audienceKey = DirectoryAudiencesKey,
                roomCodesKey = DirectoryCodesKey,
                reservationKey = DirectoryReservationsKey,
                reservationExpiryKey = ReservationExpiryKey,
                codeDirectoryKey = CodeDirectoryKey,
                metricsKey = MetricsKey,
                publicLobbyKey = PublicLobbyIndexKey,
                privateLobbyKey = PrivateLobbyIndexKey,
                roomIndexKey = RoomIndexKey,
                capacityKey = CapacityRoomsKey,
                dueKey = SweepDueKey,
                dirtyKey = ReconcileDirtyKey,
                roomId = record.Memento.Id.ToString("N"),
                revision = record.Revision,
                reservationToken,
                code = record.Memento.Code,
                entry = JsonSerializer.Serialize(entry, SerializerOptions),
                sortMember = entry.SortMember,
                audience = entry.AudienceKey,
                contribution = entry.Contribution.Serialize(),
                metricActive = entry.Contribution.Active,
                metricOpen = entry.Contribution.Open,
                metricRunning = entry.Contribution.Running,
                metricParticipants = entry.Contribution.Participants,
                consumesCapacity = entry.ConsumesCapacity ? 1 : 0,
                publicLobby = entry.IsPublicLobby ? 1 : 0,
                privateLobby = entry.IsLobby && !entry.IsPublicLobby ? 1 : 0,
                auditAt = (now + AuditInterval).ToUnixTimeMilliseconds(),
                nextDue = entry.NextDueAt.ToUnixTimeMilliseconds()
            });
        if (result == -2)
        {
            throw new InvalidOperationException("Die Reservierung des Live-Raums ist abgelaufen.");
        }
        if (result == -3)
        {
            throw new InvalidOperationException("Der Live-Raum fehlt im globalen Raumverzeichnis.");
        }
        if (result == -4)
        {
            throw new InvalidOperationException("Der Raumcode wurde parallel einem anderen Raum zugeordnet.");
        }
        if (result != 1)
        {
            throw new InvalidOperationException("Ein neuerer Raumverzeichnisstand konnte nicht überschrieben werden.");
        }

        roomLock.ThrowIfLost();
    }

    private async Task RemoveDirectoryAsync(Guid roomId, long revision, CancellationToken cancellationToken)
    {
        if (revision < 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await database.ScriptEvaluateAsync(
            RemoveDirectoryScript,
            new
            {
                entryKey = DirectoryEntriesKey,
                revisionKey = DirectoryRevisionsKey,
                sortMembersKey = DirectorySortMembersKey,
                contributionKey = DirectoryContributionsKey,
                audienceKey = DirectoryAudiencesKey,
                roomCodesKey = DirectoryCodesKey,
                reservationKey = DirectoryReservationsKey,
                reservationExpiryKey = ReservationExpiryKey,
                codeDirectoryKey = CodeDirectoryKey,
                metricsKey = MetricsKey,
                publicLobbyKey = PublicLobbyIndexKey,
                privateLobbyKey = PrivateLobbyIndexKey,
                roomIndexKey = RoomIndexKey,
                capacityKey = CapacityRoomsKey,
                dueKey = SweepDueKey,
                dirtyKey = ReconcileDirtyKey,
                roomId = roomId.ToString("N"),
                expectedRevision = revision
            });
    }

    private async Task RollbackReservationAsync(
        Guid roomId,
        string reservationToken,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revisionValue = await database.HashGetAsync(DirectoryRevisionsKey, roomId.ToString("N"));
            if (!long.TryParse(revisionValue.ToString(), out var revision) || revision != 0)
            {
                return;
            }

            var currentToken = await database.HashGetAsync(DirectoryReservationsKey, roomId.ToString("N"));
            if (!StringComparer.Ordinal.Equals(currentToken.ToString(), reservationToken))
            {
                return;
            }

            await RemoveDirectoryAsync(roomId, 0, cancellationToken);
        }
        catch
        {
            // The bounded reservation deadline lets the sweep repair an interrupted create.
        }
    }

    private async Task MarkDirectoryDirtyAsync(Guid roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await database.SortedSetAddAsync(
            ReconcileDirtyKey,
            roomId.ToString("N"),
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private async Task TouchRoomTtlAsync(
        RoomRecord record,
        RedisDistributedLease roomLock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var touched = (int)await database.ScriptEvaluateAsync(
            TouchRoomTtlScript,
            new
            {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                roomKey = RoomKey(record.Memento.Id),
                expectedRevision = record.Revision,
                persistent = RequiresPersistentRoomState(record.Memento) ? 1 : 0,
                ttlSeconds = (long)roomStateLifetime.TotalSeconds
            });
        if (touched == -2)
        {
            roomLock.ThrowFenceLost("Raumzustands-Lebensdauer verlängern");
        }
        if (touched != 1)
        {
            throw new RoomNotFoundException();
        }
    }

    private static CancellationTokenSource LinkToLease(
        CancellationToken cancellationToken,
        IOperationLease lease) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LeaseLost);

    private async Task RetireRoomAsync(
        RoomRecord record,
        RedisDistributedLease roomLock,
        CancellationToken cancellationToken)
    {
        var roomId = record.Memento.Id;
        var retired = (int)await database.ScriptEvaluateAsync(
            DeleteRoomStateScript,
            new
            {
                lockKey = roomLock.Key,
                lockToken = roomLock.Token,
                roomKey = RoomKey(roomId),
                expectedRevision = record.Revision
            });
        if (retired == -2)
        {
            roomLock.ThrowFenceLost("Raumzustand entfernen");
        }
        if (retired == 1)
        {
            roomLock.ThrowIfLost();
            await RemoveDirectoryAsync(roomId, await ReadDirectoryRevisionAsync(roomId), cancellationToken);
        }
    }

    private async Task<RoomRecord> RecoverPendingCompletionAsync(RoomRecord record, RedisDistributedLease roomLock)
    {
        rooms.UnloadRoomState(record.Memento.Id);
        try
        {
            if (!rooms.ImportRoomState(record.Memento))
            {
                return record;
            }

            rooms.EnsurePendingPersistenceQueued(record.Memento.Id);
            var recovered = ScrubTransientInput(rooms.ExportRoomState(record.Memento.Id));
            if (recovered.StateVersion == record.Memento.StateVersion)
            {
                return record;
            }

            await MarkDirectoryDirtyAsync(recovered.Id, roomLock.LeaseLost);
            var saved = (int)await database.ScriptEvaluateAsync(
                CompareExchangeScript,
                new
                {
                    lockKey = roomLock.Key,
                    lockToken = roomLock.Token,
                    roomKey = RoomKey(recovered.Id),
                    code = recovered.Code,
                    expectedRevision = record.Revision,
                    memento = Serialize(recovered),
                    persistent = RequiresPersistentRoomState(recovered) ? 1 : 0,
                    ttlSeconds = (long)roomStateLifetime.TotalSeconds
                });
            if (saved == -2)
            {
                roomLock.ThrowFenceLost("Arena-Persistenzauftrag rekonstruieren");
            }

            return saved == 1
                ? new RoomRecord(checked(record.Revision + 1), recovered)
                : record;
        }
        finally
        {
            rooms.UnloadRoomState(record.Memento.Id);
        }
    }

    private static LiveRoomMemento ScrubTransientInput(LiveRoomMemento memento) =>
        memento with
        {
            Participants = memento.Participants
                .Select(participant => participant with { TypedTextPreview = "" })
                .ToArray()
        };

    private bool ConsumesCapacity(LiveRoomMemento room, DateTimeOffset now) =>
        !room.Finished &&
        (room.Phase != LiveRoomPhase.Lobby || now - room.CreatedAt < lobbyRoomRetention);

    private bool IsExpiredLobby(LiveRoomMemento room, DateTimeOffset now) =>
        !room.Finished && room.Phase == LiveRoomPhase.Lobby && now - room.CreatedAt >= lobbyRoomRetention;

    private bool IsExpiredCompletedRoom(LiveRoomMemento room, DateTimeOffset now) =>
        room.Finished && room.FinishedAt is { } finishedAt && now - finishedAt >= completedRoomRetention;

    private static bool RequiresPersistentRoomState(LiveRoomMemento room) =>
        room.PersistenceState is CompletionState.Pending or CompletionState.Failed;

    private DateTimeOffset NextSweepAt(LiveRoomMemento room, DateTimeOffset now)
    {
        if (room.Finished)
        {
            if (room.PersistenceState is CompletionState.Pending or CompletionState.Failed)
            {
                return now + PendingCompletionPollInterval;
            }

            return room.FinishedAt is { } finishedAt
                ? Max(now, finishedAt + completedRoomRetention)
                : now + AuditInterval;
        }

        var next = room.Phase switch
        {
            LiveRoomPhase.Lobby => room.CreatedAt + lobbyRoomRetention,
            LiveRoomPhase.Countdown when room.RaceStartsAt is { } raceStartsAt => raceStartsAt,
            _ => now + AuditInterval
        };
        var reconnectGrace = TimeSpan.FromSeconds(Math.Clamp(options.Value.ReconnectGraceSeconds, 0, 300));
        foreach (var disconnectedAt in room.Participants
                     .Where(participant => participant.DisconnectedAt is not null)
                     .Select(participant => participant.DisconnectedAt!.Value))
        {
            var disconnectDue = disconnectedAt + reconnectGrace;
            if (disconnectDue < next)
            {
                next = disconnectDue;
            }
        }

        return Max(now, next);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static int AdaptiveBatchSize(long backlog, int minimum, int maximum) =>
        checked((int)Math.Clamp(backlog, minimum, maximum));

    private bool RequiresDirectoryUpdate(
        LiveRoomMemento previous,
        LiveRoomMemento current,
        DateTimeOffset now)
    {
        var before = CreateDirectoryEntry(new RoomRecord(0, previous), now);
        var after = CreateDirectoryEntry(new RoomRecord(0, current), now);
        return before.Summary != after.Summary ||
            before.IsLobby != after.IsLobby ||
            before.IsPublicLobby != after.IsPublicLobby ||
            before.ConsumesCapacity != after.ConsumesCapacity ||
            before.SortMember != after.SortMember ||
            before.AudienceKey != after.AudienceKey ||
            before.Contribution != after.Contribution ||
            before.SweepScheduleKey != after.SweepScheduleKey;
    }

    private RoomDirectoryEntry CreateDirectoryEntry(RoomRecord record, DateTimeOffset now)
    {
        var room = record.Memento;
        var isLobby = !room.Finished && room.Phase == LiveRoomPhase.Lobby;
        var summary = ToLobbySummary(room) with
        {
            StateVersion = isLobby ? room.StateVersion : 0
        };
        var audience = room.Visibility == LiveRoomVisibility.InternalOpen
            ? []
            : room.Participants.Select(participant => participant.ProfileId)
                .Append(room.CreatorProfileId)
                .Concat(room.Visibility == LiveRoomVisibility.InvitationOnly ? room.InvitedProfileIds : [])
                .Distinct()
                .Order()
                .ToArray();
        var contribution = new DirectoryContribution(
            room.Finished ? 0 : 1,
            isLobby ? 1 : 0,
            !room.Finished && room.Phase is LiveRoomPhase.Countdown or LiveRoomPhase.Running ? 1 : 0,
            room.Finished ? 0 : room.Participants.Count(CountsTowardCapacity));
        return new RoomDirectoryEntry(
            record.Revision,
            summary,
            isLobby,
            isLobby && room.Visibility == LiveRoomVisibility.InternalOpen,
            ConsumesCapacity(room, now),
            BuildSortMember(room.Title, room.Id, record.Revision),
            audience,
            string.Join(',', audience.Select(id => id.ToString("N"))),
            contribution,
            BuildSweepScheduleKey(room),
            NextSweepAt(room, now));
    }

    private async Task<long> ReadDirectoryRevisionAsync(Guid roomId)
    {
        var value = await database.HashGetAsync(DirectoryRevisionsKey, roomId.ToString("N"));
        return value.IsNull || !long.TryParse(value.ToString(), out var revision) ? -1 : revision;
    }

    private static bool CountsTowardCapacity(LiveParticipantMemento participant) =>
        participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready or
            ParticipantStatus.Running or ParticipantStatus.Disconnected or
            ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.Invited;

    private static LiveRoomLobbySummary ToLobbySummary(LiveRoomMemento room) => new(
        room.Id,
        room.CreatorProfileId,
        room.Participants.FirstOrDefault(participant => participant.ProfileId == room.CreatorProfileId)?.DisplayName
            ?? "Raumleitung",
        room.Visibility == LiveRoomVisibility.Code ? "" : room.Code,
        room.Title,
        room.Mode,
        room.Visibility,
        room.Phase,
        room.RoundCount,
        room.CurrentRound,
        room.Participants.Count(participant => participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready),
        room.MaxParticipants,
        room.LobbyLocked,
        room.StateVersion);

    private static IEnumerable<LobbyIndexMember> MergeLobbyMembers(
        IReadOnlyList<RedisValue> left,
        IReadOnlyList<RedisValue> right,
        string rightPrefix)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Count || rightIndex < right.Count)
        {
            var rightSortMember = rightIndex < right.Count
                ? StripViewerPrefix(right[rightIndex], rightPrefix)
                : null;
            if (rightIndex >= right.Count ||
                leftIndex < left.Count && StringComparer.Ordinal.Compare(left[leftIndex].ToString(), rightSortMember) <= 0)
            {
                var member = left[leftIndex++];
                yield return new LobbyIndexMember(member, member.ToString(), false);
            }
            else
            {
                var member = right[rightIndex++];
                yield return new LobbyIndexMember(member, rightSortMember!, true);
            }
        }
    }

    private async Task<MergePartition> FindMergePartitionAsync(
        RedisKey leftKey,
        long leftCount,
        RedisKey rightKey,
        long rightCount,
        long offset,
        string rightPrefix,
        string rightUpperBound,
        CancellationToken cancellationToken)
    {
        var low = Math.Max(0, offset - rightCount);
        var high = Math.Min(offset, leftCount);
        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = low + ((high - low) / 2);
            var right = offset - left;
            var leftPreviousTask = ReadSortedMemberAsync(leftKey, left - 1, null, null);
            var leftCurrentTask = ReadSortedMemberAsync(leftKey, left, null, null);
            var rightPreviousTask = ReadSortedMemberAsync(rightKey, right - 1, rightPrefix, rightUpperBound);
            var rightCurrentTask = ReadSortedMemberAsync(rightKey, right, rightPrefix, rightUpperBound);
            await Task.WhenAll(leftPreviousTask, leftCurrentTask, rightPreviousTask, rightCurrentTask);
            var leftPrevious = leftPreviousTask.Result;
            var leftCurrent = leftCurrentTask.Result;
            var rightPrevious = rightPreviousTask.Result;
            var rightCurrent = rightCurrentTask.Result;
            if (leftPrevious is not null && rightCurrent is not null &&
                StringComparer.Ordinal.Compare(leftPrevious, rightCurrent) > 0)
            {
                high = left - 1;
                continue;
            }
            if (rightPrevious is not null && leftCurrent is not null &&
                StringComparer.Ordinal.Compare(rightPrevious, leftCurrent) > 0)
            {
                low = left + 1;
                continue;
            }

            return new MergePartition(left, right);
        }

        return new MergePartition(Math.Min(offset, leftCount), Math.Max(0, offset - leftCount));
    }

    private async Task<string?> ReadSortedMemberAsync(
        RedisKey key,
        long rank,
        string? prefix,
        string? upperBound)
    {
        if (rank < 0)
        {
            return null;
        }
        if (prefix is null)
        {
            var rankedValues = await database.SortedSetRangeByRankAsync(key, rank, rank);
            return rankedValues.Length == 0 ? null : rankedValues[0].ToString();
        }

        var values = await database.SortedSetRangeByValueAsync(
            key,
            prefix,
            upperBound!,
            Exclude.None,
            rank,
            1);
        return values.Length == 0 ? null : StripViewerPrefix(values[0], prefix);
    }

    private static string StripViewerPrefix(RedisValue member, string prefix)
    {
        var value = member.ToString();
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private static Guid ParseRoomId(RedisValue member)
    {
        var value = member.ToString();
        var lastSeparator = value.LastIndexOf(':');
        var previousSeparator = lastSeparator <= 0 ? -1 : value.LastIndexOf(':', lastSeparator - 1);
        if (previousSeparator < 0 || lastSeparator - previousSeparator != 33)
        {
            return Guid.Empty;
        }

        return Guid.TryParseExact(value.AsSpan(previousSeparator + 1, 32), "N", out var roomId)
            ? roomId
            : Guid.Empty;
    }

    private static RoomDirectoryEntry? DeserializeDirectoryEntry(RedisValue value) =>
        value.IsNull
            ? null
            : JsonSerializer.Deserialize<RoomDirectoryEntry>(value.ToString(), SerializerOptions);

    private static int ParseMetric(IReadOnlyList<RedisValue> values, int index) =>
        index < values.Count && int.TryParse(values[index].ToString(), out var metric) ? Math.Max(0, metric) : 0;

    private static string BuildSortMember(string title, Guid roomId, long revision)
    {
        var key = CultureInfo.GetCultureInfo("de-DE").CompareInfo.GetSortKey(title, CompareOptions.IgnoreCase).KeyData;
        return $"{Convert.ToHexString(key)}:{roomId:N}:{revision:D20}";
    }

    private static string BuildSweepScheduleKey(LiveRoomMemento room)
    {
        var earliestDisconnect = room.Participants
            .Where(participant => participant.DisconnectedAt is not null)
            .Select(participant => participant.DisconnectedAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        return string.Join(
            ':',
            room.Phase,
            room.Finished,
            room.PersistenceState,
            room.CreatedAt.UtcTicks,
            room.RaceStartsAt?.UtcTicks ?? 0,
            room.FinishedAt?.UtcTicks ?? 0,
            earliestDisconnect == DateTimeOffset.MaxValue ? 0 : earliestDisconnect.UtcTicks);
    }

    private static string Serialize(LiveRoomMemento memento) =>
        JsonSerializer.Serialize(memento, SerializerOptions);

    private static RedisKey RoomKey(Guid roomId) => $"keywars:{{room:{roomId:N}}}:state";
    private static RedisKey LockKey(Guid roomId) => $"keywars:{{room:{roomId:N}}}:lock";
    private static string ViewerLobbyMemberPrefix(Guid profileId) => $"{profileId:N}:";

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string ResolveInstanceId()
    {
        var hostName = Environment.GetEnvironmentVariable("HOSTNAME")?.Trim();
        return string.IsNullOrWhiteSpace(hostName) ? $"{Environment.MachineName}-{Environment.ProcessId}" : hostName;
    }

    private sealed record RoomRecord(long Revision, LiveRoomMemento Memento);
    private sealed record LockedExecution<T>(T Result, LiveRoomMemento Memento, long Revision);
    private sealed record RoomDirectoryEntry(
        long Revision,
        LiveRoomLobbySummary Summary,
        bool IsLobby,
        bool IsPublicLobby,
        bool ConsumesCapacity,
        string SortMember,
        IReadOnlyList<Guid> AudienceProfileIds,
        string AudienceKey,
        DirectoryContribution Contribution,
        string SweepScheduleKey,
        DateTimeOffset NextDueAt);
    private sealed record DirectoryContribution(int Active, int Open, int Running, int Participants)
    {
        public string Serialize() => $"{Active},{Open},{Running},{Participants}";
    }
    private sealed record LobbyIndexMember(RedisValue StoredMember, string SortMember, bool IsPrivate);
    private sealed record MergePartition(long Left, long Right);

    private sealed class RoomNotFoundException()
        : InvalidOperationException("Der Live-Raum wurde nicht gefunden.");
}
