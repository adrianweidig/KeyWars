using System.Reflection;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyWars.Domain;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.ConcurrencyTests;

public sealed class RedisLiveRoomDispatcherContractTests
{
    [Fact]
    public void RoomStateAndLocksShardWhileDirectoryKeysShareOnlyTheirMetadataSlot()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var roomKey = InvokeKey(dispatcher, "RoomKey", first);
        var roomLock = InvokeKey(dispatcher, "LockKey", first);
        var rematchKey = InvokeKey(dispatcher, "RematchKey", first);
        var otherRoomKey = InvokeKey(dispatcher, "RoomKey", second);

        Assert.Equal(HashTag(roomKey), HashTag(roomLock));
        Assert.Equal(HashTag(roomKey), HashTag(rematchKey));
        Assert.NotEqual(HashTag(roomKey), HashTag(otherRoomKey));
        Assert.NotEqual(HashTag(roomKey), HashTag(StaticKey(dispatcher, "RoomIndexKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "MetricsKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "ProfileRoomsKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "RunningRoomsKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "DirectoryEpochKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "CreatorRoomCountsKey")));
        Assert.Equal(
            HashTag(StaticKey(dispatcher, "RoomIndexKey")),
            HashTag(StaticKey(dispatcher, "QuotaOwnersKey")));
    }

    [Fact]
    public void ClusterRematchUsesDurableSourceMappingAndRecoversInterruptedDirectoryCommit()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var rematch = ExtractMethod(source, "CreateRematchAsync", "CreateMappedRematchAsync");
        var mappedCreate = ExtractMethod(source, "CreateMappedRematchAsync", "ListOpenRoomsAsync");

        Assert.Contains("When.NotExists", rematch, StringComparison.Ordinal);
        Assert.Contains("RematchKey(roomId)", rematch, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact", rematch, StringComparison.Ordinal);
        Assert.Contains("ReadDirectoryRevisionAsync(roomId)", mappedCreate, StringComparison.Ordinal);
        Assert.Contains("DirectoryReservationsKey", mappedCreate, StringComparison.Ordinal);
        Assert.Contains("CommitDirectoryAsync", mappedCreate, StringComparison.Ordinal);
        Assert.Contains("existing ?? new RoomRecord(1, memento)", mappedCreate, StringComparison.Ordinal);
    }

    [Fact]
    public void RoomScriptsNeverReferenceDirectoryKeysAndDirectoryScriptsNeverReferenceRoomKeys()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        foreach (var name in new[] { "InitializeRoomScript", "CompareExchangeScript", "TouchRoomTtlScript", "DeleteRoomStateScript" })
        {
            var script = StaticScript(dispatcher, name).OriginalScript;
            Assert.DoesNotContain("@capacityKey", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@roomIndexKey", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@metricsKey", script, StringComparison.Ordinal);
        }

        foreach (var name in new[]
                 {
                     "ReserveDirectoryScript",
                     "CommitDirectoryScript",
                     "RemoveDirectoryScript",
                     "ClaimAbortRoomsScript",
                     "CompleteAbortRoomScript",
                     "RetryAbortRoomScript"
                 })
        {
            var script = StaticScript(dispatcher, name).OriginalScript;
            Assert.DoesNotContain("@roomKey", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@roomLockKey", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClusterOperationsUseRoomTargetedManagerApis()
    {
        var source = File.ReadAllText(FindDispatcherSource());

        Assert.Contains("enforceLocalRoomCapacity: false", source, StringComparison.Ordinal);
        Assert.Contains("manager.JoinByResolvedCode(roomId, code, profileId, displayName)", source, StringComparison.Ordinal);
        Assert.Contains("manager.RemoveProfileFromRoom(roomId, profileId)", source, StringComparison.Ordinal);
        Assert.Contains("manager.AbortActiveRoom(id)", source, StringComparison.Ordinal);
        Assert.Contains("rooms.UnloadRoomState", source, StringComparison.Ordinal);
        Assert.Contains("rooms.RemoveRoomState(roomId)", source, StringComparison.Ordinal);
        Assert.Contains("RetirementChannel", source, StringComparison.Ordinal);
        Assert.Contains("await using (var roomLock", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("rooms.UnloadRoomState(snapshot.RoomId);", StringComparison.Ordinal) <
            source.IndexOf("if (!stateUnloaded)", StringComparison.Ordinal),
            "Der Create-Zustand muss im expliziten Room-Lock-Scope entladen werden.");
        Assert.DoesNotContain("manager.JoinByCode(code, profileId, displayName)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manager.RemoveProfile(profileId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manager.AbortActiveRooms()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterProgressKeepsTheAppliedDeltaForTheAckAndMarksItAsAlreadyRelayed()
    {
        var dispatcherSource = File.ReadAllText(FindDispatcherSource());
        var progressMethod = ExtractMethod(
            dispatcherSource,
            "SubmitProgressDeltaAsync",
            "FinishAsync");
        var hubSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "KeyWars",
            "Hubs",
            "ArenaHub.cs"));

        Assert.Contains("await progressRelay.EnqueueAsync(delta, cancellationToken)", progressMethod, StringComparison.Ordinal);
        Assert.Contains("return result with { RelayedByDispatcher = true }", progressMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Delta = null", progressMethod, StringComparison.Ordinal);
        Assert.Contains("!result.RelayedByDispatcher", hubSource, StringComparison.Ordinal);
        Assert.Contains("result.Delta?.ParticipantSequence", hubSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservationRecoveryAndViewerFanoutStayInBoundedAtomicDirectoryScripts()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var reserve = StaticScript(dispatcher, "ReserveDirectoryScript").OriginalScript;
        var commit = StaticScript(dispatcher, "CommitDirectoryScript").OriginalScript;
        var remove = StaticScript(dispatcher, "RemoveDirectoryScript").OriginalScript;

        Assert.Contains("zrangebyscore', @reservationExpiryKey", reserve, StringComparison.Ordinal);
        Assert.DoesNotContain("zrangebyscore', @capacityKey", reserve, StringComparison.Ordinal);
        Assert.Contains("@cleanupBefore", reserve, StringComparison.Ordinal);
        Assert.Contains("'LIMIT', 0, @cleanupLimit", reserve, StringComparison.Ordinal);
        Assert.Contains("zrem', @reservationExpiryKey", commit, StringComparison.Ordinal);
        Assert.Contains("zadd', @privateLobbyKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @privateLobbyKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @privateLobbyKey", remove, StringComparison.Ordinal);
    }

    [Fact]
    public void SweepRefreshesRoomTtlWithLockAndRevisionFencing()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var touch = StaticScript(dispatcher, "TouchRoomTtlScript").OriginalScript;
        var source = File.ReadAllText(FindDispatcherSource());

        Assert.Contains("get', @lockKey", touch, StringComparison.Ordinal);
        Assert.Contains("@lockToken", touch, StringComparison.Ordinal);
        Assert.Contains("hget', @roomKey, 'revision'", touch, StringComparison.Ordinal);
        Assert.Contains("@expectedRevision", touch, StringComparison.Ordinal);
        Assert.Contains("expire', @roomKey, @ttlSeconds", touch, StringComparison.Ordinal);
        Assert.Contains("persist', @roomKey", touch, StringComparison.Ordinal);
        Assert.Contains("@persistent", touch, StringComparison.Ordinal);
        Assert.Contains("RequiresPersistentRoomState(updated) ? 1 : 0", source, StringComparison.Ordinal);
        Assert.Contains("RequiresPersistentRoomState(record.Memento) ? 1 : 0", source, StringComparison.Ordinal);
        Assert.Contains("RequiresPersistentRoomState(recovered) ? 1 : 0", source, StringComparison.Ordinal);
        Assert.True(
            source.Split("await TouchRoomTtlAsync(record, roomLock, operationToken);", StringSplitOptions.None).Length >= 3,
            "Pending-Recovery und normaler Auditpfad müssen die Room-TTL verlängern.");
    }

    [Fact]
    public async Task ClusterCreateReservesCompletionAdmissionBeforeDirectoryAndCompensatesFailures()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var create = ExtractMethod(source, "CreateRoomAsync", "CreateRematchAsync");
        var reserve = StaticScript(typeof(RedisLiveRoomDispatcher), "ReserveDirectoryScript").OriginalScript;
        Assert.Contains("SortedSetLengthAsync(CapacityRoomsKey)", source, StringComparison.Ordinal);
        Assert.Contains("completionSink.TryReserveRoomSlot", source, StringComparison.Ordinal);
        Assert.Contains("completionSink.CommitRoomSlot", source, StringComparison.Ordinal);
        Assert.Contains("completionSink.ReleaseRoomSlot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("completionSink.CanAcceptNewRoom(activeRoomCount)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(maxConcurrentRooms, checked(activeRoomCount + 1))", source, StringComparison.Ordinal);
        Assert.Contains("currentCapacity = redis.call('zcard', @capacityKey)", reserve, StringComparison.Ordinal);
        Assert.Contains("currentCapacity ~= tonumber(@expectedCapacity)", reserve, StringComparison.Ordinal);
        Assert.Contains("expectedCapacity = activeRoomCount", source, StringComparison.Ordinal);
        Assert.True(
            create.IndexOf("completionSink.TryReserveRoomSlot(snapshot.RoomId)", StringComparison.Ordinal) <
            create.IndexOf("ReserveDirectoryScript", StringComparison.Ordinal),
            "Der globale Completion-Slot muss vor der Directory-Reservierung belegt sein.");
        Assert.True(
            create.IndexOf("await CommitDirectoryAsync", StringComparison.Ordinal) <
            create.IndexOf("completionSink.CommitRoomSlot(completionReservation)", StringComparison.Ordinal),
            "Erst der bestätigte Directory-Create darf die Admission-Reservierung konsumieren.");
        Assert.True(
            create.IndexOf("await RollbackReservationAsync", StringComparison.Ordinal) <
            create.IndexOf("completionSink.ReleaseRoomSlot(completionReservation)", StringComparison.Ordinal),
            "Der Crash-/Fehlerpfad muss Directory und Admission in dieser Reihenfolge kompensieren.");

        const int completionCapacity = 2;
        var completionJobs = new HashSet<Guid> { Guid.CreateVersion7() };
        var roomSlots = new HashSet<Guid>();
        var candidates = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var gate = new object();
        await Task.WhenAll(candidates.Select(candidate => Task.Run(() =>
        {
            lock (gate)
            {
                if (completionJobs.Count + roomSlots.Count >= completionCapacity)
                {
                    return;
                }

                roomSlots.Add(candidate);
            }
        })));

        Assert.Single(roomSlots);
        Assert.Equal(completionCapacity, completionJobs.Count + roomSlots.Count);
    }

    [Fact]
    public void CreatorQuotaIsAtomicForCreateAndRematchAndReleasesEveryDirectoryExit()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var create = ExtractMethod(source, "CreateRoomAsync", "CreateRematchAsync");
        var rematch = ExtractMethod(source, "CreateMappedRematchAsync", "ListOpenRoomsAsync");
        var reserve = StaticScript(typeof(RedisLiveRoomDispatcher), "ReserveDirectoryScript").OriginalScript;
        var commit = StaticScript(typeof(RedisLiveRoomDispatcher), "CommitDirectoryScript").OriginalScript;
        var remove = StaticScript(typeof(RedisLiveRoomDispatcher), "RemoveDirectoryScript").OriginalScript;
        var backfill = StaticScript(typeof(RedisLiveRoomDispatcher), "BackfillCreatorQuotaScript").OriginalScript;

        Assert.Contains("hget', @creatorCountsKey, @creatorProfileId", reserve, StringComparison.Ordinal);
        Assert.Contains("return -3", reserve, StringComparison.Ordinal);
        Assert.Contains("hincrby', @creatorCountsKey, @creatorProfileId, 1", reserve, StringComparison.Ordinal);
        Assert.Contains("creatorQuota = Math.Max(1, options.Value.MaxActiveRoomsPerCreator)", create, StringComparison.Ordinal);
        Assert.Contains("creatorQuota = Math.Max(1, options.Value.MaxActiveRoomsPerCreator)", rematch, StringComparison.Ordinal);
        Assert.Contains("reservationResult == -3", create, StringComparison.Ordinal);
        Assert.Contains("reservationResult == -3", rematch, StringComparison.Ordinal);
        Assert.Contains("hincrby', @creatorCountsKey, quotaOwner, -1", commit, StringComparison.Ordinal);
        Assert.Contains("quotaOwner ~= nextQuotaOwner", commit, StringComparison.Ordinal);
        Assert.Contains("hincrby', @creatorCountsKey, nextQuotaOwner, 1", commit, StringComparison.Ordinal);
        Assert.Contains("hincrby', @creatorCountsKey, quotaOwner, -1", remove, StringComparison.Ordinal);
        Assert.Contains("hsetnx', @quotaOwnersKey", backfill, StringComparison.Ordinal);
        Assert.Contains("zscore', @capacityKey", backfill, StringComparison.Ordinal);
        Assert.Contains("DeserializeDirectoryEntry(directoryEntries[index])?.Summary.CreatorProfileId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LobbyAndMetricsContractsAreBoundedWithoutMementoFullScans()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var lobby = ExtractMethod(source, "ListLobbySummariesAsync", "MetricsSnapshotAsync");
        var metrics = ExtractMethod(source, "MetricsSnapshotAsync", "ResolveRoomIdByCodeAsync");

        Assert.Contains("limit + LobbyRepairAllowance", lobby, StringComparison.Ordinal);
        Assert.Contains("SortedSetRangeByRankAsync", lobby, StringComparison.Ordinal);
        Assert.Contains("DirectoryEntriesKey", lobby, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadRoomAsync", lobby, StringComparison.Ordinal);
        Assert.Contains("HashGetAsync", metrics, StringComparison.Ordinal);
        Assert.Contains("MetricsKey", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadRoomAsync", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("SortedSetRangeByRankAsync", metrics, StringComparison.Ordinal);
        Assert.Contains("room.Finished ? 0 : room.Participants.Count", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SweepClaimRemainsBoundedAndFairAcrossDirtyDueAndAuditQueues()
    {
        var script = StaticScript(typeof(RedisLiveRoomDispatcher), "ClaimSweepWorkScript").OriginalScript;

        Assert.Contains("zrangebyscore", script, StringComparison.Ordinal);
        Assert.Contains("'LIMIT', 0, limit", script, StringComparison.Ordinal);
        Assert.Contains("claim(@dirtyKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@reservationExpiryKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@dueKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@roomIndexKey", script, StringComparison.Ordinal);
        Assert.Contains("claim(@capacityKey", script, StringComparison.Ordinal);
        Assert.Contains("for roomId in string.gmatch(@roomIds", StaticScript(
            typeof(RedisLiveRoomDispatcher),
            "RetrySweepClaimScript").OriginalScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAndRunningIndexesAreAtomicDirectoryMetadata()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var commit = StaticScript(dispatcher, "CommitDirectoryScript").OriginalScript;
        var remove = StaticScript(dispatcher, "RemoveDirectoryScript").OriginalScript;
        var source = File.ReadAllText(FindDispatcherSource());

        Assert.Contains("hget', @profileKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @profileRoomsKey", commit, StringComparison.Ordinal);
        Assert.Contains("zadd', @profileRoomsKey", commit, StringComparison.Ordinal);
        Assert.Contains("zadd', @runningRoomsKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @runningRoomsKey", commit, StringComparison.Ordinal);
        Assert.Contains("zrem', @profileRoomsKey", remove, StringComparison.Ordinal);
        Assert.Contains("zrem', @runningRoomsKey", remove, StringComparison.Ordinal);
        Assert.Contains("before.ProfileKey != after.ProfileKey", source, StringComparison.Ordinal);
        Assert.Contains("!room.ExcludedProfileIds.Contains(profileId)", source, StringComparison.Ordinal);
        var removeProfile = ExtractMethod(source, "RemoveProfileAsync", "ClearProfileRemovalFenceAsync");
        Assert.Contains("ProfileRoomsKey", removeProfile, StringComparison.Ordinal);
        Assert.Contains("take: ProfileRoomPageSize", removeProfile, StringComparison.Ordinal);
        Assert.Contains("ProfileRoomsKey", removeProfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRemovalFenceSurvivesCasDirectoryCrashAndBlocksRematerialization()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var commit = StaticScript(dispatcher, "CommitDirectoryScript").OriginalScript;
        var source = File.ReadAllText(FindDispatcherSource());
        var removeProfile = ExtractMethod(source, "RemoveProfileAsync", "ClearProfileRemovalFenceAsync");
        var executeLocked = ExtractMethod(source, "ExecuteLockedAsync", "ReadRoomsBoundedAsync");
        var recovery = ExtractMethod(source, "RecoverPendingCompletionAsync", "ScrubTransientInput");
        var dirty = StaticScript(dispatcher, "MarkDirectoryDirtyScript").OriginalScript;

        Assert.Contains("ProfileRemovalTombstonesKey", removeProfile, StringComparison.Ordinal);
        Assert.Contains("MarkDirectoryDirtyAsync(roomId, updated", source, StringComparison.Ordinal);
        Assert.Contains("zadd', @profileRoomsKey, 'NX'", dirty, StringComparison.Ordinal);
        Assert.Contains("incr', @directoryEpochKey", dirty, StringComparison.Ordinal);
        Assert.Contains("profileRemovalTombstonesKey", commit, StringComparison.Ordinal);
        Assert.Contains("@profileReferences", commit, StringComparison.Ordinal);
        Assert.Contains("return -5", commit, StringComparison.Ordinal);
        Assert.Contains("zadd', @dirtyKey", commit, StringComparison.Ordinal);
        Assert.Contains("AuditDirectoryProfilesAsync", removeProfile, StringComparison.Ordinal);
        Assert.Contains("AuditRoomIndexAsync", removeProfile, StringComparison.Ordinal);
        Assert.Contains("AuditRoomStatesAsync", removeProfile, StringComparison.Ordinal);
        Assert.Contains("ReadDirectoryEpochAsync", removeProfile, StringComparison.Ordinal);
        Assert.Contains(".Concat(room.ExcludedProfileIds)", source, StringComparison.Ordinal);
        Assert.Contains("room.QuotaOwnerProfileId", source, StringComparison.Ordinal);
        Assert.Contains("ReadProfileRemovalFencesAsync", executeLocked, StringComparison.Ordinal);
        Assert.Contains("ReadProfileRemovalFencesAsync", recovery, StringComparison.Ordinal);
        Assert.True(
            executeLocked.IndexOf("ReadProfileRemovalFencesAsync", StringComparison.Ordinal) <
            executeLocked.IndexOf("EnsurePendingPersistenceQueued", StringComparison.Ordinal),
            "Privacy-Fences müssen vor einer Completion-Rekonstruktion angewendet werden.");
    }

    [Fact]
    public void ProfileRemovalPurgesEveryProgressRepresentationBeforeReturning()
    {
        var source = File.ReadAllText(FindDispatcherSource());
        var removeProfile = ExtractMethod(source, "RemoveProfileAsync", "ClearProfileRemovalFenceAsync");
        var relaySource = File.ReadAllText(FindRepositoryFile(
            "src",
            "KeyWars",
            "Infrastructure",
            "Cluster",
            "RedisLiveProgressRelay.cs"));
        var purge = ExtractMethod(relaySource, "PurgeParticipantAsync", "ExecuteAsync");

        Assert.Contains("progressRelay.PurgeParticipantAsync", removeProfile, StringComparison.Ordinal);
        Assert.Contains("PendingKey(roomId)", purge, StringComparison.Ordinal);
        Assert.Contains("LatestKey(roomId)", purge, StringComparison.Ordinal);
        Assert.Contains("SentKey(roomId)", purge, StringComparison.Ordinal);
        Assert.Contains("DueRoomsKey(roomId)", purge, StringComparison.Ordinal);
        Assert.Contains("LockKey(roomId)", purge, StringComparison.Ordinal);
    }

    [Fact]
    public void AbortJobsAreBoundedAndRecoverAfterFailureOrRestart()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var claim = StaticScript(dispatcher, "ClaimAbortRoomsScript").OriginalScript;
        var complete = StaticScript(dispatcher, "CompleteAbortRoomScript").OriginalScript;
        var retry = StaticScript(dispatcher, "RetryAbortRoomScript").OriginalScript;
        var source = File.ReadAllText(FindDispatcherSource());

        Assert.Contains("zrangebyscore', @runningRoomsKey", claim, StringComparison.Ordinal);
        Assert.Contains("'LIMIT', 0, @limit", claim, StringComparison.Ordinal);
        Assert.Contains("{tostring(#members)}", claim, StringComparison.Ordinal);
        Assert.Contains("zadd', @runningRoomsKey, @claimUntil", claim, StringComparison.Ordinal);
        Assert.Contains("zrem', @runningRoomsKey, roomId", claim, StringComparison.Ordinal);
        Assert.Contains("tonumber(score) == tonumber(@claimUntil)", complete, StringComparison.Ordinal);
        Assert.Contains("tonumber(score) == tonumber(@claimUntil)", retry, StringComparison.Ordinal);
        Assert.Contains("@retryAt", retry, StringComparison.Ordinal);
        Assert.Contains("limit = AbortRoomBatchSize", source, StringComparison.Ordinal);
        Assert.Contains("RetryAbortClaimsBestEffortAsync(ids.Skip(index)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadActiveRoomIdsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditSweepBackfillsNewDirectoryIndexesAfterRestart()
    {
        var dispatcher = typeof(RedisLiveRoomDispatcher);
        var claim = StaticScript(dispatcher, "ClaimSweepWorkScript").OriginalScript;
        var commit = StaticScript(dispatcher, "CommitDirectoryScript").OriginalScript;
        var source = File.ReadAllText(FindDispatcherSource());
        var sweepRoom = ExtractMethod(
            source,
            "private async Task<LiveRoomSnapshot?> SweepRoomAsync",
            "private async Task RetryClaimsAsync");

        Assert.Contains("claim(@roomIndexKey, tonumber(@auditLimit))", claim, StringComparison.Ordinal);
        Assert.Contains("auditLimit = SweepAuditBatchSize", source, StringComparison.Ordinal);
        Assert.Contains("await CommitDirectoryAsync", sweepRoom, StringComparison.Ordinal);
        Assert.Contains("zadd', @profileRoomsKey", commit, StringComparison.Ordinal);
        Assert.Contains("zadd', @runningRoomsKey", commit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LobbyPageLoadsOnlyLimitPlusBoundedRepairEntries()
    {
        var fake = new DirectoryReadRedis();
        for (var index = 0; index < 50; index++)
        {
            fake.AddPublicLobby(Guid.CreateVersion7(), $"Raum {index:D2}");
        }
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var page = await dispatcher.ListLobbySummariesAsync(Guid.CreateVersion7(), limit: 5);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(50, page.Total);
        Assert.InRange(fake.MaximumHashFieldCount, 1, 21);
        Assert.InRange(fake.MaximumSortedSetResultCount, 1, 21);
        Assert.Equal(0, fake.RoomStateHashReads);
    }

    [Fact]
    public async Task OpenRoomCompatibilityReadUsesBoundedSummaryPages()
    {
        var fake = new DirectoryReadRedis();
        for (var index = 0; index < 150; index++)
        {
            fake.AddPublicLobby(Guid.CreateVersion7(), $"Raum {index:D3}");
        }
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var rooms = await dispatcher.ListOpenRoomsAsync();

        Assert.Equal(150, rooms.Count);
        Assert.InRange(fake.MaximumHashFieldCount, 1, 64);
        Assert.InRange(fake.MaximumSortedSetResultCount, 1, 64);
        Assert.Equal(150, fake.RoomStateHashReads);
        var source = File.ReadAllText(FindDispatcherSource());
        var listOpen = ExtractMethod(source, "ListOpenRoomsAsync", "ListLobbySummariesAsync");
        Assert.Contains("DirectoryEntriesKey", listOpen, StringComparison.Ordinal);
        Assert.Contains("take: DirectoryPageSize", listOpen, StringComparison.Ordinal);
        Assert.DoesNotContain("SortedSetRangeByRankAsync", listOpen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileRemovalRepairsStaleReverseEntriesWithoutRoomReads()
    {
        var fake = new DirectoryReadRedis();
        var profileId = Guid.CreateVersion7();
        fake.AddStaleProfileRoom(profileId, Guid.CreateVersion7());
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        await dispatcher.RemoveProfileAsync(profileId);

        Assert.Equal(0, fake.ProfileRoomCount);
        Assert.InRange(fake.MaximumSortedSetResultCount, 1, 32);
        Assert.InRange(fake.RoomStateHashReads, 1, 3);
    }

    [Fact]
    public async Task ProfileRemovalAuditsMoreThanNinetySixRoomStatesWhenReverseIndexIsMissing()
    {
        var fake = new DirectoryReadRedis();
        var profileId = Guid.CreateVersion7();
        for (var index = 0; index < 129; index++)
        {
            fake.AddLegacyProfileRoomWithoutReverse(
                profileId,
                Guid.CreateVersion7(),
                $"Altbestand {index:D3}");
        }
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        Assert.Equal(0, fake.ProfileRoomCount);
        await dispatcher.RemoveProfileAsync(profileId);

        Assert.Equal(0, fake.ProfileRoomCount);
        Assert.Equal(0, fake.DirectoryProfileCount(profileId));
        Assert.Equal(0, fake.ActiveStateProfileCount(profileId));
        Assert.True(fake.RoomStateHashReads >= 129);
        Assert.InRange(fake.MaximumScanPageSize, 1, 32);
    }

    [Fact]
    public async Task ProfileRemovalRepeatsAuditWhenRoomAppearsDuringStablePass()
    {
        var fake = new DirectoryReadRedis();
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        fake.AddLegacyRoomDuringFirstRoomStateScan(profileId, roomId);
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        await dispatcher.RemoveProfileAsync(profileId);

        Assert.Equal(0, fake.DirectoryProfileCount(profileId));
        Assert.Equal(0, fake.ActiveStateProfileCount(profileId));
        Assert.True(fake.DirectoryEpochReads >= 2);
    }

    [Fact]
    public async Task ProfileRemovalFindsOrphanRoomStateWithoutAnyDirectoryIndex()
    {
        var fake = new DirectoryReadRedis();
        var profileId = Guid.CreateVersion7();
        fake.AddOrphanProfileRoomState(profileId, Guid.CreateVersion7());
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        await dispatcher.RemoveProfileAsync(profileId);

        Assert.Equal(0, fake.ActiveStateProfileCountAcrossKeys(profileId));
        Assert.InRange(fake.MaximumServerScanPageSize, 1, 32);
    }

    [Fact]
    public async Task ProfileRemovalRedactsRawRoomMementoAndTransfersCreator()
    {
        var fake = new DirectoryReadRedis();
        var profileId = Guid.CreateVersion7();
        var successor = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        fake.AddSensitiveProfileRoom(profileId, successor, roomId);
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        await dispatcher.RemoveProfileAsync(profileId);

        var raw = fake.RawRoomMemento(roomId);
        Assert.DoesNotContain(profileId.ToString("N"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profileId.ToString("D"), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Geheimer Anzeigename", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("private-typed-preview", raw, StringComparison.Ordinal);
        var memento = JsonSerializer.Deserialize<LiveRoomMemento>(raw, DirectoryReadRedis.MementoOptionsForTests);
        Assert.NotNull(memento);
        Assert.Equal(successor, memento.CreatorProfileId);
        Assert.DoesNotContain(memento.Participants, participant => participant.ProfileId == profileId);
        Assert.DoesNotContain(profileId, memento.InvitedProfileIds);
        Assert.DoesNotContain(profileId, memento.ExcludedProfileIds);
        var broadcast = Assert.Single(resources.Updates.Snapshots);
        Assert.DoesNotContain(broadcast.Participants, participant => participant.ProfileId == profileId);
        Assert.DoesNotContain("Geheimer Anzeigename", JsonSerializer.Serialize(broadcast), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetFenceLetsARejoinedProfileRelayProgressInTheSameRoom()
    {
        var fake = new DirectoryReadRedis();
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        fake.AddPublicLobby(roomId, "Reset-Raum", profileId);
        fake.MakeRoomRunning(roomId);
        fake.SetProfileRemovalFence(profileId);
        fake.SetProgressBlock(roomId, profileId, "old-removal-generation");
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        await dispatcher.ClearProfileRemovalFenceAsync(profileId);
        await dispatcher.JoinAsync(roomId, profileId, "Ersteller");
        var result = await dispatcher.SubmitProgressDeltaAsync(
            roomId,
            profileId,
            1,
            JsonSerializer.Serialize(new LiveProgressInputDelta(0, 1, 0, "E", "E")));

        Assert.False(fake.IsProgressBlocked(roomId, profileId));
        Assert.True(result.RelayedByDispatcher);
        Assert.Equal(1, fake.AcceptedProgressEnqueues);
    }

    [Fact]
    public async Task DirectoryFenceRaceReturnsTheCommittedScrubbedSnapshot()
    {
        var fake = new DirectoryReadRedis();
        var removedProfile = Guid.CreateVersion7();
        var joiningProfile = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        fake.AddPublicLobby(roomId, "Fence-Race", removedProfile);
        fake.RejectNextDirectoryCommitWithProfileFence(removedProfile);
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var snapshot = await dispatcher.JoinAsync(roomId, joiningProfile, "Neu");

        Assert.DoesNotContain(snapshot.Participants, participant => participant.ProfileId == removedProfile);
        Assert.Contains(snapshot.Participants, participant => participant.ProfileId == joiningProfile);
    }

    [Fact]
    public async Task LobbyPageRefillsPastMoreThanOneRepairAllowance()
    {
        var fake = new DirectoryReadRedis();
        for (var index = 0; index < 40; index++)
        {
            fake.AddStalePublicLobby($"0000:{Guid.CreateVersion7():N}:{index:D20}");
        }
        for (var index = 0; index < 10; index++)
        {
            fake.AddPublicLobby(Guid.CreateVersion7(), $"Raum {index:D2}");
        }
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var page = await dispatcher.ListLobbySummariesAsync(Guid.CreateVersion7(), limit: 5);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal(10, page.Total);
        Assert.InRange(fake.MaximumHashFieldCount, 1, 21);
        Assert.InRange(fake.MaximumSortedSetResultCount, 1, 21);
    }

    [Fact]
    public async Task MetricsReadOneCompactHashWithoutIndexesOrRoomStates()
    {
        var fake = new DirectoryReadRedis();
        fake.SetMetrics(active: 7, open: 3, running: 4, participants: 42);
        using var resources = CreateDispatcher(fake.Connection, out var dispatcher);

        var snapshot = await dispatcher.MetricsSnapshotAsync();

        Assert.Equal(new LiveRoomMetricsSnapshot(7, 3, 4, 42), snapshot);
        Assert.Equal(0, fake.SortedSetReads);
        Assert.Equal(4, fake.MaximumHashFieldCount);
        Assert.Equal(0, fake.RoomStateHashReads);
    }

    private static string StaticKey(Type type, string name) =>
        type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)?.ToString()
        ?? throw new InvalidOperationException($"Redis-Schlüssel {name} fehlt.");

    private static LuaScript StaticScript(Type type, string name) =>
        (LuaScript)(type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? throw new InvalidOperationException($"Redis-Skript {name} fehlt."));

    private static string InvokeKey(Type type, string name, Guid roomId) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [roomId])?.ToString()
        ?? throw new InvalidOperationException($"Redis-Schlüsselfunktion {name} fehlt.");

    private static string HashTag(string key)
    {
        var start = key.IndexOf('{');
        var end = key.IndexOf('}', start + 1);
        return start >= 0 && end > start + 1 ? key[(start + 1)..end] : key;
    }

    private static string FindDispatcherSource() => FindRepositoryFile(
        "src",
        "KeyWars",
        "Infrastructure",
        "Cluster",
        "RedisLiveRoomDispatcher.cs");

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Repository-Datei {string.Join('/', segments)} wurde nicht gefunden.");
    }

    private static string ExtractMethod(string source, string startName, string endName)
    {
        var start = source.IndexOf(startName, StringComparison.Ordinal);
        var end = source.IndexOf(endName, start + startName.Length, StringComparison.Ordinal);
        return source[start..end];
    }

    private static TestResources CreateDispatcher(
        IConnectionMultiplexer redis,
        out RedisLiveRoomDispatcher dispatcher)
    {
        var options = Options.Create(new LiveOptions
        {
            MaxConcurrentRooms = 100,
            CompletionQueueCapacity = 100
        });
        var time = TimeProvider.System;
        var queue = new RedisLiveRoomCompletionQueue(
            redis,
            new NoopCompletionWriter(),
            options,
            time,
            NullLogger<RedisLiveRoomCompletionQueue>.Instance);
        var sink = new ClusterLiveRoomCompletionSink(queue);
        var rooms = new LiveRoomManager(
            options,
            time,
            new TypingEngine(time),
            NullLogger<LiveRoomManager>.Instance,
            sink);
        var telemetry = new KeyWarsTelemetry();
        var relay = new RedisLiveProgressRelay(
            redis,
            new NoopProgressSender(),
            options,
            time,
            telemetry,
            NullLogger<RedisLiveProgressRelay>.Instance);
        var updates = new RecordingRoomUpdateSender();
        dispatcher = new RedisLiveRoomDispatcher(redis, rooms, relay, sink, updates, options, time);
        return new TestResources(queue, relay, telemetry, updates);
    }

    private sealed class NoopCompletionWriter : ILiveRoomCompletionWriter
    {
        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoopProgressSender : ILiveProgressSender
    {
        public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestResources(
        RedisLiveRoomCompletionQueue queue,
        RedisLiveProgressRelay relay,
        KeyWarsTelemetry telemetry,
        RecordingRoomUpdateSender updates) : IDisposable
    {
        public RecordingRoomUpdateSender Updates { get; } = updates;

        public void Dispose()
        {
            relay.Dispose();
            queue.Dispose();
            telemetry.Dispose();
        }
    }

    private sealed class RecordingRoomUpdateSender : ILiveRoomUpdateSender
    {
        public List<LiveRoomSnapshot> Snapshots { get; } = [];

        public Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class DirectoryReadRedis
    {
        private static readonly JsonSerializerOptions MementoOptions = CreateMementoOptions();
        public static JsonSerializerOptions MementoOptionsForTests => MementoOptions;
        private readonly Dictionary<string, Dictionary<string, RedisValue>> hashes = [];
        private readonly Dictionary<string, Dictionary<string, double>> sortedSets = [];
        private readonly IDatabase database;
        private readonly IServer server;
        private readonly ISubscriber subscriber;
        private readonly string entriesKey = StaticKey(typeof(RedisLiveRoomDispatcher), "DirectoryEntriesKey");
        private readonly string metricsKey = StaticKey(typeof(RedisLiveRoomDispatcher), "MetricsKey");
        private readonly string publicLobbyKey = StaticKey(typeof(RedisLiveRoomDispatcher), "PublicLobbyIndexKey");
        private readonly string privateLobbyKey = StaticKey(typeof(RedisLiveRoomDispatcher), "PrivateLobbyIndexKey");
        private readonly string profileRoomsKey = StaticKey(typeof(RedisLiveRoomDispatcher), "ProfileRoomsKey");
        private readonly string directoryProfilesKey = StaticKey(typeof(RedisLiveRoomDispatcher), "DirectoryProfilesKey");
        private readonly string directoryRevisionsKey = StaticKey(typeof(RedisLiveRoomDispatcher), "DirectoryRevisionsKey");
        private readonly string roomIndexKey = StaticKey(typeof(RedisLiveRoomDispatcher), "RoomIndexKey");
        private readonly string directoryEpochKey = StaticKey(typeof(RedisLiveRoomDispatcher), "DirectoryEpochKey");
        private readonly string profileRemovalTombstonesKey = StaticKey(
            typeof(RedisLiveRoomDispatcher),
            "ProfileRemovalTombstonesKey");
        private Action? firstRoomStateScan;
        private Guid? fenceOnNextDirectoryCommit;
        private long directoryEpoch;

        public DirectoryReadRedis()
        {
            database = CreateProxy<IDatabase>(InvokeDatabase);
            server = CreateProxy<IServer>((method, arguments) => method.Name switch
            {
                "get_IsReplica" => false,
                "get_IsConnected" => true,
                nameof(IServer.KeysAsync) => RoomStateKeysAsync(arguments),
                _ => throw new NotSupportedException(method.Name)
            });
            subscriber = CreateProxy<ISubscriber>((method, _) => method.Name switch
            {
                nameof(ISubscriber.PublishAsync) => Task.FromResult(1L),
                _ => throw new NotSupportedException(method.Name)
            });
            Connection = CreateProxy<IConnectionMultiplexer>((method, _) => method.Name switch
            {
                nameof(IConnectionMultiplexer.GetDatabase) => database,
                nameof(IConnectionMultiplexer.GetSubscriber) => subscriber,
                nameof(IConnectionMultiplexer.GetEndPoints) => new EndPoint[] { new DnsEndPoint("localhost", 6379) },
                nameof(IConnectionMultiplexer.GetServer) => server,
                _ => throw new NotSupportedException(method.Name)
            });
        }

        public IConnectionMultiplexer Connection { get; }
        public int MaximumHashFieldCount { get; private set; }
        public int RoomStateHashReads { get; private set; }
        public int SortedSetReads { get; private set; }
        public int MaximumSortedSetResultCount { get; private set; }
        public int MaximumScanPageSize { get; private set; }
        public int MaximumServerScanPageSize { get; private set; }
        public int DirectoryEpochReads { get; private set; }
        public int AcceptedProgressEnqueues { get; private set; }
        public int ProfileRoomCount => GetSortedSet(profileRoomsKey).Count;

        public void AddPublicLobby(Guid roomId, string title, Guid? creatorProfileId = null)
        {
            var revision = 1L;
            var creatorProfile = creatorProfileId ?? Guid.CreateVersion7();
            var sortMember = $"{title}:{roomId:N}:{revision:D20}";
            GetSortedSet(publicLobbyKey)[sortMember] = 0;
            var summary = new LiveRoomLobbySummary(
                roomId,
                creatorProfile,
                "Ersteller",
                "ABC234",
                title,
                LiveRoomMode.Classic,
                LiveRoomVisibility.InternalOpen,
                LiveRoomPhase.Lobby,
                1,
                1,
                1,
                8,
                false,
                1);
            GetHash(entriesKey)[roomId.ToString("N")] = JsonSerializer.Serialize(new
            {
                revision,
                summary,
                isLobby = true,
                isPublicLobby = true,
                consumesCapacity = true,
                sortMember,
                audienceProfileIds = Array.Empty<Guid>(),
                audienceKey = "",
                contribution = new { active = 1, open = 1, running = 0, participants = 1 },
                sweepScheduleKey = "Lobby",
                nextDueAt = DateTimeOffset.UtcNow.AddMinutes(30)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var now = DateTimeOffset.UtcNow;
            var memento = new LiveRoomMemento(
                Id: roomId,
                CreatorProfileId: creatorProfile,
                Code: "ABC234",
                Title: title,
                Text: "Ein kurzer Testtext.",
                Mode: LiveRoomMode.Classic,
                Visibility: LiveRoomVisibility.InternalOpen,
                RoundCount: 1,
                MaxParticipants: 8,
                CreatedAt: now,
                Phase: LiveRoomPhase.Lobby,
                CurrentRound: 1,
                RoundVersion: 1,
                StateVersion: 1,
                LobbyLocked: false,
                PhaseChangedAt: now,
                CountdownStartsAt: null,
                RaceStartsAt: null,
                RoundEndsAt: null,
                CloseReason: null,
                Started: false,
                Finished: false,
                CompletionReceipt: null,
                PersistenceState: null,
                StartedAt: null,
                FinishedAt: null,
                ExcludedProfileIds: [],
                InvitedProfileIds: [],
                TeamRoundWins: new Dictionary<int, int>(),
                Participants:
                [
                    new LiveParticipantMemento(
                        ProfileId: creatorProfile,
                        DisplayName: "Ersteller",
                        Status: ParticipantStatus.Joined,
                        JoinedAt: now,
                        TeamNumber: null,
                        Ready: false,
                        Sequence: 0,
                        CorrectCharacters: 0,
                        TypedTextPreview: "",
                        Wpm: 0,
                        Placement: null,
                        FinishedAt: null,
                        DisconnectedAt: null,
                        DurationMilliseconds: 0,
                        Accuracy: 0,
                        SeriesPoints: 0,
                        RoundWins: 0,
                        FinishedRounds: 0,
                        CompletedRounds: 0,
                        TotalDurationMilliseconds: 0,
                        TotalWpm: 0,
                        TotalAccuracy: 0)
                ]);
            var roomKey = InvokeKey(typeof(RedisLiveRoomDispatcher), "RoomKey", roomId);
            GetHash(roomKey)["revision"] = revision;
            GetHash(roomKey)["memento"] = JsonSerializer.Serialize(memento);
        }

        public void AddLegacyProfileRoomWithoutReverse(Guid profileId, Guid roomId, string title)
        {
            AddPublicLobby(roomId, title, profileId);
            GetSortedSet(roomIndexKey)[roomId.ToString("N")] = 0;
            GetHash(directoryProfilesKey)[roomId.ToString("N")] = profileId.ToString("N");
            GetHash(directoryRevisionsKey)[roomId.ToString("N")] = 1;
        }

        public void MakeRoomRunning(Guid roomId)
        {
            var roomKey = InvokeKey(typeof(RedisLiveRoomDispatcher), "RoomKey", roomId);
            var memento = JsonSerializer.Deserialize<LiveRoomMemento>(
                GetHash(roomKey)["memento"].ToString(),
                MementoOptions)!;
            var startsAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var running = memento with
            {
                Phase = LiveRoomPhase.Running,
                PhaseChangedAt = startsAt,
                CountdownStartsAt = startsAt,
                RaceStartsAt = startsAt,
                Started = true,
                StartedAt = startsAt,
                Participants = memento.Participants
                    .Select(participant => participant with
                    {
                        Status = ParticipantStatus.Running,
                        Ready = true
                    })
                    .ToArray()
            };
            GetHash(roomKey)["memento"] = JsonSerializer.Serialize(running, MementoOptions);
        }

        public void SetProfileRemovalFence(Guid profileId) =>
            GetHash(profileRemovalTombstonesKey)[profileId.ToString("N")] = "removal";

        public void RejectNextDirectoryCommitWithProfileFence(Guid profileId) =>
            fenceOnNextDirectoryCommit = profileId;

        public void SetProgressBlock(Guid roomId, Guid profileId, string generation)
        {
            GetHash(RedisLiveProgressRelay.BlockedKey(roomId).ToString())[profileId.ToString("N")] = generation;
            GetHash(RedisLiveProgressRelay.PurgeGenerationKey(roomId).ToString())[profileId.ToString("N")] = generation;
        }

        public bool IsProgressBlocked(Guid roomId, Guid profileId) =>
            GetHash(RedisLiveProgressRelay.BlockedKey(roomId).ToString()).ContainsKey(profileId.ToString("N"));

        public void AddLegacyRoomDuringFirstRoomStateScan(Guid profileId, Guid roomId) =>
            firstRoomStateScan = () => AddLegacyProfileRoomWithoutReverse(profileId, roomId, "Parallel angelegt");

        public void AddOrphanProfileRoomState(Guid profileId, Guid roomId)
        {
            AddPublicLobby(roomId, "Verwaister Zustand", profileId);
            GetSortedSet(publicLobbyKey).Clear();
            GetHash(entriesKey).Clear();
        }

        public void AddSensitiveProfileRoom(Guid profileId, Guid successor, Guid roomId)
        {
            AddLegacyProfileRoomWithoutReverse(profileId, roomId, "Sensibler Raum");
            var roomKey = InvokeKey(typeof(RedisLiveRoomDispatcher), "RoomKey", roomId);
            var memento = JsonSerializer.Deserialize<LiveRoomMemento>(
                GetHash(roomKey)["memento"].ToString(),
                MementoOptions)!;
            var sensitive = memento with
            {
                InvitedProfileIds = [profileId, successor],
                Participants =
                [
                    memento.Participants[0] with
                    {
                        DisplayName = "Geheimer Anzeigename",
                        TypedTextPreview = "private-typed-preview",
                        CorrectCharacters = 18,
                        Wpm = 72,
                        Accuracy = 97
                    },
                    memento.Participants[0] with
                    {
                        ProfileId = successor,
                        DisplayName = "Nachfolge",
                        JoinedAt = memento.CreatedAt.AddSeconds(1),
                        TypedTextPreview = ""
                    }
                ]
            };
            GetHash(roomKey)["memento"] = JsonSerializer.Serialize(sensitive, MementoOptions);
            GetHash(directoryProfilesKey)[roomId.ToString("N")] = $"{profileId:N},{successor:N}";
        }

        public string RawRoomMemento(Guid roomId)
        {
            var roomKey = InvokeKey(typeof(RedisLiveRoomDispatcher), "RoomKey", roomId);
            return GetHash(roomKey)["memento"].ToString();
        }

        public void AddStalePublicLobby(string sortMember) =>
            GetSortedSet(publicLobbyKey)[sortMember] = 0;

        public void AddStaleProfileRoom(Guid profileId, Guid roomId) =>
            GetSortedSet(profileRoomsKey)[$"{profileId:N}:{roomId:N}"] = 0;

        public void SetMetrics(int active, int open, int running, int participants)
        {
            var hash = GetHash(metricsKey);
            hash["active"] = active;
            hash["open"] = open;
            hash["running"] = running;
            hash["participants"] = participants;
        }

        public int DirectoryProfileCount(Guid profileId) =>
            GetHash(directoryProfilesKey).Values.Count(value => ContainsProfile(value, profileId));

        public int ActiveStateProfileCount(Guid profileId) => GetSortedSet(roomIndexKey).Keys.Count(roomId =>
        {
            if (!Guid.TryParseExact(roomId, "N", out var parsed))
            {
                return false;
            }

            var roomKey = InvokeKey(typeof(RedisLiveRoomDispatcher), "RoomKey", parsed);
            var hash = GetHash(roomKey);
            if (!hash.TryGetValue("memento", out var value))
            {
                return false;
            }

            var room = JsonSerializer.Deserialize<LiveRoomMemento>(
                value.ToString(),
                MementoOptions);
            return room is not null &&
                (room.CreatorProfileId == profileId ||
                    room.InvitedProfileIds.Contains(profileId) ||
                    room.ExcludedProfileIds.Contains(profileId) ||
                    room.Participants.Any(participant => participant.ProfileId == profileId));
        });

        public int ActiveStateProfileCountAcrossKeys(Guid profileId) => hashes
            .Where(item => item.Key.Contains("{room:", StringComparison.Ordinal) && item.Key.EndsWith(":state", StringComparison.Ordinal))
            .Count(item => RoomStateContainsProfile(item.Value, profileId));

        private object? InvokeDatabase(MethodInfo method, object?[] arguments)
        {
            return method.Name switch
            {
                "get_Database" => 0,
                nameof(IDatabase.SortedSetRangeByRankAsync) => RangeAsync(arguments),
                nameof(IDatabase.SortedSetLengthAsync) => Task.FromResult((long)GetSortedSet(Key(arguments[0])).Count),
                nameof(IDatabase.SortedSetLengthByValueAsync) => Task.FromResult(LexValues(arguments).LongCount()),
                nameof(IDatabase.SortedSetRangeByValueAsync) => LexRangeAsync(arguments),
                nameof(IDatabase.SortedSetScanAsync) => SortedSetScanAsync(arguments),
                nameof(IDatabase.SortedSetAddAsync) => SortedSetAddAsync(arguments),
                nameof(IDatabase.SortedSetRemoveAsync) => Task.FromResult(GetSortedSet(Key(arguments[0])).Remove(arguments[1]!.ToString()!)),
                nameof(IDatabase.HashGetAsync) => HashGetAsync(arguments),
                nameof(IDatabase.HashScanAsync) => HashScanAsync(arguments),
                nameof(IDatabase.HashSetAsync) => HashSetAsync(arguments),
                nameof(IDatabase.HashDeleteAsync) => Task.FromResult(GetHash(Key(arguments[0])).Remove(arguments[1]!.ToString()!)),
                nameof(IDatabase.HashExistsAsync) => Task.FromResult(GetHash(Key(arguments[0])).ContainsKey(arguments[1]!.ToString()!)),
                nameof(IDatabase.StringGetAsync) => StringGetAsync(arguments),
                nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
                nameof(IDatabase.ScriptEvaluateAsync) => ScriptEvaluateAsync(arguments),
                nameof(IDatabase.SortedSetLength) => (long)GetSortedSet(Key(arguments[0])).Count,
                _ => throw new NotSupportedException(method.Name)
            };
        }

        private async IAsyncEnumerable<HashEntry> HashScanAsync(object?[] arguments)
        {
            MaximumScanPageSize = Math.Max(MaximumScanPageSize, Convert.ToInt32(arguments[2]));
            foreach (var entry in GetHash(Key(arguments[0])).ToArray())
            {
                yield return new HashEntry(entry.Key, entry.Value);
                await Task.Yield();
            }
        }

        private async IAsyncEnumerable<SortedSetEntry> SortedSetScanAsync(object?[] arguments)
        {
            MaximumScanPageSize = Math.Max(MaximumScanPageSize, Convert.ToInt32(arguments[2]));
            var key = Key(arguments[0]);
            var snapshot = GetSortedSet(key).ToArray();
            foreach (var entry in snapshot)
            {
                yield return new SortedSetEntry(entry.Key, entry.Value);
                await Task.Yield();
            }
        }

        private async IAsyncEnumerable<RedisKey> RoomStateKeysAsync(object?[] arguments)
        {
            MaximumServerScanPageSize = Math.Max(MaximumServerScanPageSize, Convert.ToInt32(arguments[2]));
            var snapshot = hashes.Keys
                .Where(key => key.Contains("{room:", StringComparison.Ordinal) && key.EndsWith(":state", StringComparison.Ordinal))
                .Select(key => (RedisKey)key)
                .ToArray();
            if (firstRoomStateScan is { } addRoom)
            {
                firstRoomStateScan = null;
                addRoom();
                directoryEpoch++;
            }
            foreach (var key in snapshot)
            {
                yield return key;
                await Task.Yield();
            }
        }

        private Task<RedisValue> StringGetAsync(object?[] arguments)
        {
            if (Key(arguments[0]) == directoryEpochKey)
            {
                DirectoryEpochReads++;
                return Task.FromResult((RedisValue)directoryEpoch);
            }
            return Task.FromResult(RedisValue.Null);
        }

        private Task<bool> SortedSetAddAsync(object?[] arguments)
        {
            var set = GetSortedSet(Key(arguments[0]));
            var member = arguments[1]!.ToString()!;
            var added = !set.ContainsKey(member);
            set[member] = Convert.ToDouble(arguments[2]);
            return Task.FromResult(added);
        }

        private Task<RedisResult> ScriptEvaluateAsync(object?[] arguments)
        {
            var parameters = arguments.ElementAtOrDefault(1);
            if (fenceOnNextDirectoryCommit is { } fencedProfile &&
                Parameter(parameters, "entryKey") is not null)
            {
                fenceOnNextDirectoryCommit = null;
                GetHash(profileRemovalTombstonesKey)[fencedProfile.ToString("N")] = "raced-removal";
                return Task.FromResult(RedisResult.Create((RedisValue)(-5)));
            }
            if (Parameter(parameters, "expectedGeneration") is { } expectedGeneration &&
                Parameter(parameters, "blockedKey") is { } unblockKey &&
                Parameter(parameters, "participant") is { } unblockParticipant)
            {
                var blocked = GetHash(unblockKey.ToString()!);
                if (blocked.TryGetValue(unblockParticipant.ToString()!, out var generation) &&
                    generation == expectedGeneration.ToString())
                {
                    blocked.Remove(unblockParticipant.ToString()!);
                    return Task.FromResult(RedisResult.Create((RedisValue)1));
                }

                return Task.FromResult(RedisResult.Create((RedisValue)2));
            }
            if (Parameter(parameters, "pendingKey") is not null &&
                Parameter(parameters, "blockedKey") is { } enqueueBlockedKey &&
                Parameter(parameters, "participant") is { } enqueueParticipant &&
                Parameter(parameters, "payload") is not null)
            {
                if (GetHash(enqueueBlockedKey.ToString()!).ContainsKey(enqueueParticipant.ToString()!))
                {
                    return Task.FromResult(RedisResult.Create((RedisValue)3));
                }

                AcceptedProgressEnqueues++;
                return Task.FromResult(RedisResult.Create((RedisValue)1));
            }
            if (Parameter(parameters, "roomKey") is { } roomKey &&
                Parameter(parameters, "memento") is { } memento &&
                Parameter(parameters, "expectedRevision") is { } expectedRevision)
            {
                var hash = GetHash(roomKey.ToString()!);
                hash["revision"] = checked(Convert.ToInt64(expectedRevision) + 1);
                hash["memento"] = memento.ToString()!;
            }

            if (Parameter(parameters, "profileKey") is { } profileKey &&
                Parameter(parameters, "roomId") is { } roomId &&
                Parameter(parameters, "profiles") is { } profiles)
            {
                GetHash(profileKey.ToString()!)[roomId.ToString()!] = profiles.ToString()!;
            }

            return Task.FromResult(RedisResult.Create((RedisValue)1));
        }

        private static object? Parameter(object? parameters, string name) =>
            parameters?.GetType().GetProperty(name)?.GetValue(parameters);

        private static bool ContainsProfile(RedisValue value, Guid profileId) =>
            value.ToString().Split(',').Contains(profileId.ToString("N"), StringComparer.Ordinal);

        private static bool RoomStateContainsProfile(
            IReadOnlyDictionary<string, RedisValue> hash,
            Guid profileId)
        {
            if (!hash.TryGetValue("memento", out var value))
            {
                return false;
            }

            var room = JsonSerializer.Deserialize<LiveRoomMemento>(value.ToString(), MementoOptions);
            return room is not null &&
                (room.CreatorProfileId == profileId ||
                    room.InvitedProfileIds.Contains(profileId) ||
                    room.ExcludedProfileIds.Contains(profileId) ||
                    room.Participants.Any(participant => participant.ProfileId == profileId));
        }

        private static JsonSerializerOptions CreateMementoOptions()
        {
            var result = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            result.Converters.Add(new JsonStringEnumConverter());
            return result;
        }

        private Task<RedisValue[]> LexRangeAsync(object?[] arguments)
        {
            SortedSetReads++;
            var orderedOverload = arguments.Length >= 8;
            var skip = Convert.ToInt64(arguments[orderedOverload ? 5 : 4]);
            var take = Convert.ToInt64(arguments[orderedOverload ? 6 : 5]);
            var values = LexValues(arguments)
                .Skip(checked((int)skip))
                .Take(checked((int)take))
                .Select(value => (RedisValue)value)
                .ToArray();
            MaximumSortedSetResultCount = Math.Max(MaximumSortedSetResultCount, values.Length);
            return Task.FromResult(values);
        }

        private IEnumerable<string> LexValues(object?[] arguments)
        {
            var minimum = arguments[1]!.ToString()!;
            var maximum = arguments[2]!.ToString()!;
            var exclude = (Exclude)arguments[3]!;
            return GetSortedSet(Key(arguments[0])).Keys
                .Where(value => (minimum == "-" ||
                        StringComparer.Ordinal.Compare(value, minimum) > (exclude.HasFlag(Exclude.Start) ? 0 : -1)) &&
                    (maximum == "+" ||
                        StringComparer.Ordinal.Compare(value, maximum) < (exclude.HasFlag(Exclude.Stop) ? 0 : 1)))
                .Order(StringComparer.Ordinal);
        }

        private Task<RedisValue[]> RangeAsync(object?[] arguments)
        {
            SortedSetReads++;
            var start = Convert.ToInt64(arguments[1]);
            var stop = Convert.ToInt64(arguments[2]);
            var values = GetSortedSet(Key(arguments[0]))
                .OrderBy(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (RedisValue)item.Key)
                .Skip(checked((int)start))
                .Take(checked((int)Math.Max(0, stop - start + 1)))
                .ToArray();
            MaximumSortedSetResultCount = Math.Max(MaximumSortedSetResultCount, values.Length);
            return Task.FromResult(values);
        }

        private object HashGetAsync(object?[] arguments)
        {
            var key = Key(arguments[0]);
            if (key.Contains("{room:", StringComparison.Ordinal))
            {
                RoomStateHashReads++;
            }
            var hash = GetHash(key);
            if (arguments[1] is RedisValue[] fields)
            {
                MaximumHashFieldCount = Math.Max(MaximumHashFieldCount, fields.Length);
                return Task.FromResult(fields
                    .Select(field => hash.TryGetValue(field.ToString(), out var value) ? value : RedisValue.Null)
                    .ToArray());
            }

            var field = arguments[1]!.ToString()!;
            return Task.FromResult(hash.TryGetValue(field, out var result) ? result : RedisValue.Null);
        }

        private Task<bool> HashSetAsync(object?[] arguments)
        {
            GetHash(Key(arguments[0]))[arguments[1]!.ToString()!] = (RedisValue)arguments[2]!;
            return Task.FromResult(true);
        }

        private Dictionary<string, RedisValue> GetHash(string key)
        {
            if (!hashes.TryGetValue(key, out var hash))
            {
                hash = [];
                hashes[key] = hash;
            }
            return hash;
        }

        private Dictionary<string, double> GetSortedSet(string key)
        {
            if (!sortedSets.TryGetValue(key, out var set))
            {
                set = [];
                sortedSets[key] = set;
            }
            return set;
        }

        private static string Key(object? value) => value?.ToString()
            ?? throw new InvalidOperationException("Redis-Schlüssel fehlt.");

        private static T CreateProxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, RedisProxy>();
            ((RedisProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        public class RedisProxy : DispatchProxy
        {
            public Func<MethodInfo, object?[], object?> Handler { private get; set; } = null!;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                Handler(targetMethod ?? throw new InvalidOperationException(), args ?? []);
        }
    }
}
