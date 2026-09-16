using KeyWars.Domain;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Reflection;
using System.Text.Json;

if (args is not [var connectionString])
{
    Console.Error.WriteLine("Usage: KeyWars.ClusterProbe <redis-connection-string>");
    return 1;
}

await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
var database = redis.GetDatabase();
var vectors = new[]
{
    Guid.Parse("00000000-0000-0000-0000-000000000001"),
    Guid.Parse("11111111-1111-1111-1111-111111111111"),
    Guid.Parse("22222222-2222-2222-2222-222222222222"),
    Guid.Parse("33333333-3333-3333-3333-333333333333"),
    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
    Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")
};

await ProbeAttemptStoreAsync(redis, vectors);
await ProbePresenceStoreAsync(redis, vectors);
await ProbeProfileAccessGateAsync(redis, vectors);
await ProbeCreatorRoomQuotaAsync(redis, database);
await ProbeProgressRelayAsync(redis, database, vectors);
await ProbeCompletionQueueAsync(redis, database, vectors);

Console.WriteLine("CLUSTER_PROBE_OK");
return 0;

static async Task ProbeAttemptStoreAsync(IConnectionMultiplexer redis, IReadOnlyList<Guid> vectors)
{
    var store = new RedisAttemptSessionStateStore(redis);
    var now = DateTimeOffset.UtcNow;
    var profile = Guid.Parse("99999999-9999-9999-9999-999999999999");
    var sessions = vectors.Select((id, index) => new AttemptSession(
        id,
        profile,
        $"Cluster-Versuch {index}",
        TrainingMode.Text,
        now.AddMinutes(-30 - index),
        null,
        $"nonce-{index}",
        AttemptPhase.Prepared)).ToArray();

    foreach (var session in sessions)
    {
        await using (await store.AcquireLifecycleLockAsync(session.Id))
        {
        }
        await store.AddAsync(session, TimeSpan.FromHours(1));
        var stored = await store.GetAsync(session.Id)
            ?? throw new InvalidOperationException($"Versuch {session.Id:N} fehlt nach AddAsync.");
        var updated = stored with { StartedAt = now, Phase = AttemptPhase.Started };
        if (!await store.TryUpdateAsync(stored, updated, TimeSpan.FromHours(1)))
        {
            throw new InvalidOperationException($"Versuch {session.Id:N} konnte nicht aktualisiert werden.");
        }
    }

    var removed = await store.RemoveAsync(sessions[0].Id);
    if (removed?.Id != sessions[0].Id)
    {
        throw new InvalidOperationException("RemoveAsync hat den erwarteten Versuch nicht entfernt.");
    }

    var profileRemoved = await store.RemoveProfileAsync(profile);
    if (profileRemoved.Count != sessions.Length - 1)
    {
        throw new InvalidOperationException("RemoveProfileAsync hat nicht alle Buckets bereinigt.");
    }

    var expired = new AttemptSession(
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        profile,
        "Abgelaufener Cluster-Versuch",
        TrainingMode.Text,
        now.AddHours(-2),
        null,
        "expired",
        AttemptPhase.Prepared);
    await store.AddAsync(expired, TimeSpan.FromMinutes(1));
    var expirySeen = false;
    for (var pass = 0; pass < 32; pass++)
    {
        var expiredIds = await store.GetExpiredIdsAsync(now, TimeSpan.FromMinutes(1));
        if (expiredIds.Count > 100)
        {
            throw new InvalidOperationException("GetExpiredIdsAsync überschritt das Batchlimit.");
        }
        expirySeen |= expiredIds.Contains(expired.Id);
    }
    if (!expirySeen)
    {
        throw new InvalidOperationException("Der rotierende Expiry-Sweep erreichte den Zielbucket nicht.");
    }
    var expiredRemoval = await store.TryRemoveExpiredAsync(expired.Id, now, TimeSpan.FromMinutes(1));
    if (expiredRemoval?.Id != expired.Id)
    {
        throw new InvalidOperationException("TryRemoveExpiredAsync hat die abgelaufene Sitzung nicht entfernt.");
    }
    if (await store.GetAsync(expired.Id) is not null)
    {
        throw new InvalidOperationException("Die abgelaufene Sitzung blieb nach TryRemoveExpiredAsync lesbar.");
    }
}

static async Task ProbePresenceStoreAsync(IConnectionMultiplexer redis, IReadOnlyList<Guid> vectors)
{
    var options = Options.Create(new LiveOptions { MaxConnectionsPerUser = 8 });
    var store = new RedisLivePresenceStateStore(redis, options, TimeProvider.System);
    var roomA = Guid.Parse("10101010-1010-1010-1010-101010101010");
    var roomB = Guid.Parse("20202020-2020-2020-2020-202020202020");

    for (var index = 0; index < vectors.Count; index++)
    {
        var profile = vectors[index];
        var connection = $"cluster-probe-{index}";
        await store.EnsureCanConnectAsync(profile, connection);
        await store.EnterRoomAsync(profile, connection, roomA);
        if (await store.CountRoomConnectionsAsync(profile, roomA) != 1)
        {
            throw new InvalidOperationException("EnterRoomAsync wurde nicht sichtbar.");
        }
        var transition = await store.EnterRoomAsync(profile, connection, roomB);
        if (!transition.Changed || transition.PreviousRoomId != roomA)
        {
            throw new InvalidOperationException("Der Presence-Raumwechsel wurde nicht erkannt.");
        }
        var leave = await store.RemoveConnectionAsync(profile, connection);
        if (leave?.RoomId != roomB)
        {
            throw new InvalidOperationException("RemoveConnectionAsync meldete nicht den aktiven Raum.");
        }
    }

    var cleanupProfile = Guid.Parse("abababab-abab-abab-abab-abababababab");
    await store.EnterRoomAsync(cleanupProfile, "cleanup-a", roomA);
    await store.EnterRoomAsync(cleanupProfile, "cleanup-b", roomB);
    var cleanupExpiryKey = RedisLivePresenceStateStore.ExpiryKey(cleanupProfile);
    await store.RemoveProfileAsync(cleanupProfile);
    if (await store.CountRoomConnectionsAsync(cleanupProfile, roomA) != 0 ||
        await store.CountRoomConnectionsAsync(cleanupProfile, roomB) != 0)
    {
        throw new InvalidOperationException("RemoveProfileAsync ließ Presence-Zustand zurück.");
    }
    var cleanupExpiryMembers = await redis.GetDatabase().SortedSetRangeByRankAsync(cleanupExpiryKey);
    if (cleanupExpiryMembers.Any(member =>
        member.ToString().Contains(cleanupProfile.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
        member.ToString().Contains("cleanup-a", StringComparison.Ordinal) ||
        member.ToString().Contains("cleanup-b", StringComparison.Ordinal) ||
        member.ToString().Contains(roomA.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
        member.ToString().Contains(roomB.ToString("D"), StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("RemoveProfileAsync ließ personenbezogene Presence-Ablaufdaten zurück.");
    }

    await ProbePresenceExpiryAsync(redis, store, roomA);
}

static async Task ProbePresenceExpiryAsync(
    IConnectionMultiplexer redis,
    RedisLivePresenceStateStore store,
    Guid roomId)
{
    var database = redis.GetDatabase();
    var expiryStore = (ILivePresenceExpiryStore)store;
    var profileId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
    var expiredConnection = "cluster-probe-expired";
    var refreshedConnection = "cluster-probe-refreshed";
    var expiryKey = RedisLivePresenceStateStore.ExpiryKey(profileId);
    var bucket = RedisClusterKeyspace.GetBucket(profileId);

    await store.RemoveProfileAsync(profileId);
    await store.EnterRoomAsync(profileId, expiredConnection, roomId);
    var expiredMember = JsonSerializer.Serialize(
        new ExpiredPresenceClaim(expiredConnection, profileId, roomId),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await database.KeyDeleteAsync(RedisLivePresenceStateStore.ConnectionKey(profileId, expiredConnection));
    await database.SortedSetAddAsync(expiryKey, "malformed-presence-probe", DateTimeOffset.UtcNow.AddSeconds(-2).ToUnixTimeMilliseconds());
    await database.SortedSetAddAsync(expiryKey, expiredMember, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());

    var expiredBatch = await expiryStore.ClaimExpiredAsync(bucket, 16, CancellationToken.None);
    var expiredClaim = expiredBatch.Claims.SingleOrDefault(item => item.ConnectionId == expiredConnection)
        ?? throw new InvalidOperationException("Der Presence-Expiry-Claim enthielt die fällige Verbindung nicht.");
    if (expiredBatch.InvalidMembers == 0 ||
        await database.SortedSetScoreAsync(expiryKey, "malformed-presence-probe") is not null)
    {
        throw new InvalidOperationException("Der Presence-Expiry-Claim entfernte einen ungültigen Head-Eintrag nicht.");
    }
    if (!await expiryStore.CompleteExpiryClaimAsync(expiredClaim, CancellationToken.None) ||
        await store.CountRoomConnectionsAsync(profileId, roomId) != 0)
    {
        throw new InvalidOperationException("Eine abgelaufene Presence-Verbindung wurde nicht vollständig entfernt.");
    }

    await store.EnterRoomAsync(profileId, refreshedConnection, roomId);
    var refreshedMember = JsonSerializer.Serialize(
        new ExpiredPresenceClaim(refreshedConnection, profileId, roomId),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await database.SortedSetAddAsync(expiryKey, refreshedMember, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
    var refreshBatch = await expiryStore.ClaimExpiredAsync(bucket, 16, CancellationToken.None);
    var refreshClaim = refreshBatch.Claims.SingleOrDefault(item => item.ConnectionId == refreshedConnection)
        ?? throw new InvalidOperationException("Der Presence-Expiry-Claim enthielt die Refresh-Verbindung nicht.");
    if (!await store.RefreshConnectionAsync(profileId, refreshedConnection, roomId) ||
        await expiryStore.CompleteExpiryClaimAsync(refreshClaim, CancellationToken.None) ||
        await store.CountRoomConnectionsAsync(profileId, roomId) != 1)
    {
        throw new InvalidOperationException("Ein Heartbeat schützte die Verbindung nicht vor einem alten Expiry-Claim.");
    }
    var refreshedScore = await database.SortedSetScoreAsync(expiryKey, refreshedMember);
    if (refreshedScore is null || refreshedScore <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    {
        throw new InvalidOperationException("Der Heartbeat setzte keine zukünftige Presence-Expiry-Frist.");
    }

    await store.RemoveProfileAsync(profileId);
}

static async Task ProbeProfileAccessGateAsync(IConnectionMultiplexer redis, IReadOnlyList<Guid> vectors)
{
    var gate = new RedisProfileAccessGate(redis);
    foreach (var profile in vectors)
    {
        await using (await gate.AcquireAsync(profile))
        {
            if (await gate.GetStateAsync(profile) != ProfileAccessState.Available)
            {
                throw new InvalidOperationException("Ein normaler Profil-Lease änderte den sichtbaren Zustand.");
            }
        }

        await using var operation = await gate.TryBeginOperationAsync(profile)
            ?? throw new InvalidOperationException("Die exklusive Profiloperation konnte nicht begonnen werden.");
        if (await gate.GetStateAsync(profile) != ProfileAccessState.OperationInProgress)
        {
            throw new InvalidOperationException("Die exklusive Profiloperation ist nicht sichtbar.");
        }
        await gate.WaitForIdleAsync(profile);
        await gate.MarkDeletedAsync(profile);
        if (await gate.GetStateAsync(profile) != ProfileAccessState.Deleted)
        {
            throw new InvalidOperationException("MarkDeletedAsync setzte den Profilzustand nicht.");
        }
        await gate.CompleteOperationAsync(profile);
    }
}

static async Task ProbeCreatorRoomQuotaAsync(IConnectionMultiplexer redis, IDatabase database)
{
    var dispatcher = typeof(RedisLiveRoomDispatcher);
    var reserve = (LuaScript)(dispatcher.GetField(
        "ReserveDirectoryScript",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException("ReserveDirectoryScript fehlt."));
    var remove = (LuaScript)(dispatcher.GetField(
        "RemoveDirectoryScript",
        BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException("RemoveDirectoryScript fehlt."));
    const string prefix = "keywars:{room-directory}";
    var creator = Guid.Parse("41414141-4141-4141-4141-414141414141").ToString("N");
    var rooms = new[]
    {
        Guid.Parse("51515151-5151-5151-5151-515151515151").ToString("N"),
        Guid.Parse("61616161-6161-6161-6161-616161616161").ToString("N")
    };
    var codes = new[] { "QTA234", "QTB234" };
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    async Task<int> ReserveAsync(int index, int expectedCapacity, int capacity) =>
        (int)await database.ScriptEvaluateAsync(
            reserve,
            new
            {
                codeDirectoryKey = (RedisKey)$"{prefix}:codes",
                roomCodesKey = (RedisKey)$"{prefix}:room-codes",
                revisionKey = (RedisKey)$"{prefix}:revisions",
                reservationKey = (RedisKey)$"{prefix}:reservations",
                reservationExpiryKey = (RedisKey)$"{prefix}:reservation-expiry",
                roomIndexKey = (RedisKey)$"{prefix}:active",
                directoryEpochKey = (RedisKey)$"{prefix}:epoch",
                capacityKey = (RedisKey)$"{prefix}:capacity",
                quotaOwnersKey = (RedisKey)$"{prefix}:quota-owners",
                creatorCountsKey = (RedisKey)$"{prefix}:creator-room-counts",
                creatorInitializedKey = (RedisKey)$"{prefix}:creator-quota-initialized",
                dueKey = (RedisKey)$"{prefix}:sweep-due",
                dirtyKey = (RedisKey)$"{prefix}:reconcile-dirty",
                roomId = rooms[index],
                code = codes[index],
                reservationToken = $"quota-probe-{index}",
                now,
                cleanupBefore = now - TimeSpan.FromHours(2).TotalMilliseconds,
                cleanupLimit = 8,
                reservationUntil = now + TimeSpan.FromMinutes(2).TotalMilliseconds,
                expectedCapacity,
                capacity,
                creatorProfileId = creator,
                creatorQuota = 1
            });

    async Task RemoveAsync(string roomId)
    {
        await database.ScriptEvaluateAsync(
            remove,
            new
            {
                entryKey = (RedisKey)$"{prefix}:entries",
                revisionKey = (RedisKey)$"{prefix}:revisions",
                sortMembersKey = (RedisKey)$"{prefix}:sort-members",
                contributionKey = (RedisKey)$"{prefix}:contributions",
                audienceKey = (RedisKey)$"{prefix}:audiences",
                profileKey = (RedisKey)$"{prefix}:profiles",
                roomCodesKey = (RedisKey)$"{prefix}:room-codes",
                reservationKey = (RedisKey)$"{prefix}:reservations",
                reservationExpiryKey = (RedisKey)$"{prefix}:reservation-expiry",
                codeDirectoryKey = (RedisKey)$"{prefix}:codes",
                metricsKey = (RedisKey)$"{prefix}:metrics",
                publicLobbyKey = (RedisKey)$"{prefix}:lobby:public",
                privateLobbyKey = (RedisKey)$"{prefix}:lobby:private",
                profileRoomsKey = (RedisKey)$"{prefix}:profile-rooms",
                runningRoomsKey = (RedisKey)$"{prefix}:running",
                roomIndexKey = (RedisKey)$"{prefix}:active",
                capacityKey = (RedisKey)$"{prefix}:capacity",
                quotaOwnersKey = (RedisKey)$"{prefix}:quota-owners",
                creatorCountsKey = (RedisKey)$"{prefix}:creator-room-counts",
                creatorInitializedKey = (RedisKey)$"{prefix}:creator-quota-initialized",
                dueKey = (RedisKey)$"{prefix}:sweep-due",
                dirtyKey = (RedisKey)$"{prefix}:reconcile-dirty",
                roomId,
                expectedRevision = 0
            });
    }

    try
    {
        var initialCapacity = checked((int)await database.SortedSetLengthAsync($"{prefix}:capacity"));
        var raced = await Task.WhenAll(
            ReserveAsync(0, initialCapacity, initialCapacity + 1),
            ReserveAsync(1, initialCapacity, initialCapacity + 1));
        if (raced.Count(result => result == 1) != 1 ||
            raced.Count(result => result == -2) != 1)
        {
            throw new InvalidOperationException(
                $"Die atomare Creator-Reservierung lieferte unerwartet [{string.Join(',', raced)}].");
        }

        var rejectedIndex = raced[0] == -2 ? 0 : 1;
        var quotaResult = await ReserveAsync(rejectedIndex, initialCapacity + 1, initialCapacity + 2);
        if (quotaResult != -3)
        {
            throw new InvalidOperationException(
                $"Die clusterweite Creator-Quote blockierte die zweite Reservierung nicht ({quotaResult}).");
        }
    }
    finally
    {
        await RemoveAsync(rooms[0]);
        await RemoveAsync(rooms[1]);
    }

    if (await database.HashExistsAsync($"{prefix}:creator-room-counts", creator) ||
        await database.HashExistsAsync($"{prefix}:quota-owners", rooms[0]) ||
        await database.HashExistsAsync($"{prefix}:quota-owners", rooms[1]))
    {
        throw new InvalidOperationException("Die Creator-Quote wurde nach dem Rollback nicht freigegeben.");
    }
}

static async Task ProbeProgressRelayAsync(
    IConnectionMultiplexer redis,
    IDatabase database,
    IReadOnlyList<Guid> vectors)
{
    var sender = new RecordingProgressSender();
    using var telemetry = new KeyWarsTelemetry();
    using var relay = new RedisLiveProgressRelay(
        redis,
        sender,
        Options.Create(new LiveOptions { ProgressBroadcastHz = 60 }),
        TimeProvider.System,
        telemetry,
        NullLogger<RedisLiveProgressRelay>.Instance);

    var relayTask = relay.StartAsync(CancellationToken.None);
    try
    {
        foreach (var room in vectors)
        {
            await relay.EnqueueAsync(new LiveProgressDelta(
                room,
                1,
                1,
                Guid.NewGuid(),
                1,
                12,
                12,
                "AQ==",
                42,
                100,
                null), CancellationToken.None);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sender.WaitForRoomsAsync(vectors.Count, timeout.Token);
        foreach (var room in vectors)
        {
            var bucket = BucketHex(room);
            var pending = $"keywars:{{progress-b{bucket}}}:pending:{room:N}";
            var due = $"keywars:{{progress-b{bucket}}}:due";
            if (await database.KeyExistsAsync(pending) ||
                await database.SortedSetScoreAsync(due, room.ToString("N")) is not null)
            {
                throw new InvalidOperationException("Der Progress-Relay ließ einen Pending-/Due-Eintrag zurück.");
            }
        }

        var privacyRoom = Guid.Parse("fefefefe-fefe-fefe-fefe-fefefefefefe");
        var privacyProfile = Guid.Parse("edededed-eded-eded-eded-edededededed");
        await relay.EnqueueAsync(new LiveProgressDelta(
            privacyRoom,
            1,
            1,
            privacyProfile,
            1,
            12,
            12,
            "AQ==",
            42,
            100,
            null), CancellationToken.None);
        await relay.PurgeParticipantAsync(privacyRoom, privacyProfile, CancellationToken.None);
        var broadcastsAfterPurge = sender.ParticipantBroadcastCount(privacyProfile);
        await relay.EnqueueAsync(new LiveProgressDelta(
            privacyRoom,
            1,
            2,
            privacyProfile,
            2,
            13,
            13,
            "Ag==",
            43,
            100,
            null), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        if (sender.ParticipantBroadcastCount(privacyProfile) != broadcastsAfterPurge)
        {
            throw new InvalidOperationException("Nach der Profilbereinigung wurde erneut Fortschritt gesendet.");
        }

        var blockGeneration = await relay.ReadParticipantBlockGenerationAsync(
            privacyRoom,
            privacyProfile,
            CancellationToken.None) ?? throw new InvalidOperationException(
                "Die Progress-Sperrgeneration der Profilbereinigung fehlte.");
        await relay.UnblockParticipantAsync(
            privacyRoom,
            privacyProfile,
            blockGeneration,
            CancellationToken.None);
        await relay.EnqueueAsync(new LiveProgressDelta(
            privacyRoom,
            1,
            3,
            privacyProfile,
            3,
            14,
            14,
            "Aw==",
            44,
            100,
            null), CancellationToken.None);
        using var rejoinTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (sender.ParticipantBroadcastCount(privacyProfile) == broadcastsAfterPurge)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), rejoinTimeout.Token);
        }

        var privacyBucket = BucketHex(privacyRoom);
        var participant = privacyProfile.ToString("N");
        foreach (var key in new[] { "pending", "latest", "sent" })
        {
            if (await database.HashExistsAsync(
                $"keywars:{{progress-b{privacyBucket}}}:{key}:{privacyRoom:N}",
                participant))
            {
                throw new InvalidOperationException($"Die Profilbereinigung ließ den Progress-Hash '{key}' zurück.");
            }
        }
    }
    finally
    {
        await relay.StopAsync(CancellationToken.None);
        await relayTask;
    }
}

static async Task ProbeCompletionQueueAsync(
    IConnectionMultiplexer redis,
    IDatabase database,
    IReadOnlyList<Guid> vectors)
{
    var writer = new FlakyCompletionWriter();
    var options = Options.Create(new LiveOptions
    {
        CompletionQueueCapacity = 256,
        CompletionDrainTimeoutSeconds = 10
    });
    using var queue = new RedisLiveRoomCompletionQueue(
        redis,
        writer,
        options,
        TimeProvider.System,
        NullLogger<RedisLiveRoomCompletionQueue>.Instance);
    await queue.StartAsync(CancellationToken.None);
    try
    {
        var profile = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
        var now = DateTimeOffset.UtcNow;
        var records = vectors.Take(3).Select((room, index) => new CompletedRoomRecord(
            room,
            1,
            1,
            $"cluster-probe-{room:N}",
            profile,
            $"P{index:00000}",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            now.AddMinutes(-1),
            now.AddSeconds(-30),
            now,
            [new CompletedParticipantRecord(
                profile,
                ParticipantStatus.Finished,
                1,
                30_000,
                60,
                100,
                CorrectCharacters: 150,
                CompetitionEligible: true)]))
            .ToArray();

        writer.FailNext(records[0].Id);
        foreach (var record in records)
        {
            var receipt = queue.Enqueue(record);
            if (receipt.State != CompletionState.Pending)
            {
                throw new InvalidOperationException("Completion Enqueue lieferte nicht Pending.");
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (records.Any(record => queue.GetStatus(record.Id).State != CompletionState.Persisted))
        {
            await Task.Delay(50, timeout.Token);
        }

        var drain = await queue.DrainProfileAsync(profile, timeout.Token);
        var (failuresObserved, successfulWrites) = writer.Snapshot();
        if (drain.Status != CompletionDrainStatus.Success || failuresObserved < 1 ||
            successfulWrites != records.Length)
        {
            throw new InvalidOperationException("Completion Retry/Redrive/Complete/Drain wurde nicht vollständig beobachtet.");
        }

        foreach (var record in records)
        {
            var bucket = BucketHex(record.Id);
            var statusKey = $"keywars:{{completion-b{bucket}}}:status:{record.Id:N}";
            var ttl = await database.KeyTimeToLiveAsync(statusKey);
            if (ttl is null || ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(7))
            {
                throw new InvalidOperationException("Completion Status hat keine gültige TTL.");
            }
        }
    }
    finally
    {
        await queue.StopAsync(CancellationToken.None);
    }
}

static string BucketHex(Guid id)
{
    Span<byte> bytes = stackalloc byte[16];
    if (!id.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
    {
        throw new InvalidOperationException("Guid konnte nicht serialisiert werden.");
    }
    Span<byte> digest = stackalloc byte[32];
    System.Security.Cryptography.SHA256.HashData(bytes, digest);
    return digest[0].ToString("x2");
}

file sealed class RecordingProgressSender : ILiveProgressSender
{
    private readonly HashSet<Guid> rooms = [];
    private readonly Dictionary<Guid, int> participantBroadcasts = [];
    private readonly SemaphoreSlim changed = new(0);

    public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken)
    {
        lock (rooms)
        {
            foreach (var delta in batch.Deltas)
            {
                participantBroadcasts[delta.ParticipantId] =
                    participantBroadcasts.GetValueOrDefault(delta.ParticipantId) + 1;
            }
            if (rooms.Add(roomId))
            {
                changed.Release();
            }
        }
        return Task.CompletedTask;
    }

    public int ParticipantBroadcastCount(Guid participantId)
    {
        lock (rooms)
        {
            return participantBroadcasts.GetValueOrDefault(participantId);
        }
    }

    public async Task WaitForRoomsAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (rooms)
            {
                if (rooms.Count >= count)
                {
                    return;
                }
            }
            await changed.WaitAsync(cancellationToken);
        }
    }
}

file sealed class FlakyCompletionWriter : ILiveRoomCompletionWriter
{
    private readonly object gate = new();
    private readonly HashSet<Guid> failNext = [];
    public HashSet<Guid> SuccessfulWrites { get; } = [];
    public int FailuresObserved { get; private set; }

    public (int Failures, int Successes) Snapshot()
    {
        lock (gate)
        {
            return (FailuresObserved, SuccessfulWrites.Count);
        }
    }

    public void FailNext(Guid roomId)
    {
        lock (gate)
        {
            failNext.Add(roomId);
        }
    }

    public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failNext.Remove(record.Id))
            {
                FailuresObserved++;
                throw new InvalidOperationException("Absichtlicher erster Completion-Fehler.");
            }
            SuccessfulWrites.Add(record.Id);
        }
        return Task.CompletedTask;
    }
}
