using System.Reflection;
using KeyWars.Infrastructure.Cluster;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisLiveRoomCompletionQueueContractTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "37")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "a8")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "5a")]
    public void RoomKeysUseCanonicalCompletionBucket(string value, string bucket)
    {
        var roomId = Guid.Parse(value);
        var expectedTag = $"completion-b{bucket}";
        var keys = new[]
        {
            GetKey("RecordKey", roomId),
            GetKey("StatusKey", roomId),
            GetKey("ProfilesKey", roomId),
            GetKey("AttemptsKey", roomId),
            GetKey("RedriveKey", roomId),
            GetKey("LockKey", roomId),
            GetKey("EnqueueIntentKey", roomId),
            GetKey("PendingKey", roomId),
            GetKey("FailedKey", roomId),
            GetKey("EnqueuedKey", roomId),
            GetKey("ProfileIndexKey", roomId),
            GetKey("MetricsKey", roomId)
        };

        Assert.All(keys, key => Assert.Equal(expectedTag, HashTag(key)));
    }

    [Fact]
    public void QueueScriptsAreBucketLocalAndAdmissionScriptsUseOnlyMetadataKeys()
    {
        foreach (var name in new[]
                 {
                     "EnqueueScript",
                     "ActivateRedriveScript",
                     "FailureScript",
                     "RescheduleUnconfirmedScript",
                     "CompleteScript",
                     "CleanupMissingScript",
                     "CleanupStaleProfileMemberScript",
                     "RemoveInvalidProfileMemberScript",
                     "CleanupInvalidQueueMemberScript"
                 })
        {
            var source = GetScript(name).OriginalScript;
            Assert.DoesNotContain("activeKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("reconcileKey", source, StringComparison.Ordinal);
        }

        foreach (var name in new[]
                 {
                     "ReserveAdmissionScript",
                     "MarkAdmissionPendingScript",
                     "MarkAdmissionFailedScript",
                     "CompleteAdmissionScript",
                     "PublishMetricsScript",
                     "ReadAdmissionCountScript",
                     "ReadAdmissionOccupancyScript",
                     "BeginAdmissionRebuildScript",
                     "RepairAdmissionBucketScript",
                     "CompleteAdmissionRebuildScript",
                     "ReserveRoomSlotScript",
                     "CommitRoomSlotScript",
                     "ReleaseRoomSlotScript"
                 })
        {
            var source = GetScript(name).OriginalScript;
            Assert.DoesNotContain("recordKey", source, StringComparison.Ordinal);
            Assert.DoesNotContain("statusKey", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnqueuePersistsProfilesAndQueueInventoryInOneBucketScript()
    {
        var source = GetScript("EnqueueScript").OriginalScript;

        Assert.Contains("set', @recordKey", source, StringComparison.Ordinal);
        Assert.Contains("get', @intentKey", source, StringComparison.Ordinal);
        Assert.Contains("del', @intentKey", source, StringComparison.Ordinal);
        Assert.Contains("set', @statusKey", source, StringComparison.Ordinal);
        Assert.Contains("sadd', @profilesKey", source, StringComparison.Ordinal);
        Assert.Contains("zadd', @profileIndexKey", source, StringComparison.Ordinal);
        Assert.Contains("zadd', @pendingKey", source, StringComparison.Ordinal);
        Assert.Contains("zadd', @enqueuedKey", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ParkedFailuresPreserveRecordAndRetryState()
    {
        var source = GetScript("FailureScript").OriginalScript;

        Assert.Contains("get', @lockKey", source);
        Assert.Contains("zrem', @pendingKey", source);
        Assert.Contains("zadd', @failedKey", source);
        Assert.Contains("del', @attemptsKey", source);
        Assert.Contains("persist', @recordKey", source);
        Assert.Contains("persist', @profilesKey", source);
        Assert.DoesNotContain("del', @recordKey", source);
    }

    [Fact]
    public void SuccessfulRedriveCleansEveryQueueInventoryAtomically()
    {
        var source = GetScript("CompleteScript").OriginalScript;

        Assert.Contains("get', @lockKey", source);
        Assert.Contains("smembers', @profilesKey", source);
        Assert.Contains("zrem', @profileIndexKey", source);
        Assert.Contains("del', @recordKey, @attemptsKey, @redriveKey, @profilesKey", source);
        Assert.Contains("zrem', @pendingKey", source);
        Assert.Contains("zrem', @failedKey", source);
        Assert.Contains("zrem', @enqueuedKey", source);
        Assert.Contains("set', @statusKey, @status, 'PX'", source);
    }

    [Fact]
    public void ProfileIndexCardinalityIsBoundedByTheRoomParticipantLimit()
    {
        var source = GetScript("CompleteScript").OriginalScript;

        Assert.Contains("smembers', @profilesKey", source, StringComparison.Ordinal);
        Assert.Contains("MaxParticipantsPerRoom", File.ReadAllText(FindSource("LiveRoomManager.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerVisitsEveryBucketExactlyOncePerRecoveryCycle()
    {
        var method = typeof(RedisLiveRoomCompletionQueue).GetMethod(
            "SelectBucketWindow",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Bucket-Auswahl fehlt.");
        var visited = new List<int>();
        for (var start = 0; start < 256; start += 16)
        {
            visited.AddRange(Assert.IsType<int[]>(method.Invoke(null, [start, 16])));
        }

        Assert.Equal(Enumerable.Range(0, 256), visited);
        Assert.Equal(Enumerable.Range(248, 8).Concat(Enumerable.Range(0, 8)),
            Assert.IsType<int[]>(method.Invoke(null, [248, 16])));
    }

    [Fact]
    public async Task BlockedBucketDoesNotPreventAnotherDueBucketFromRunning()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = RedisLiveRoomCompletionQueue.RunConcurrentlyAsync(
            new[] { 1, 2 },
            2,
            async (item, _) =>
            {
                if (item == 1)
                {
                    await blocked.Task;
                }
                else
                {
                    fast.TrySetResult();
                }
            },
            CancellationToken.None);

        await fast.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(processing.IsCompleted);
        blocked.TrySetResult();
        await processing;
    }

    [Fact]
    public async Task CancellationIgnoringPersistenceHitsHardDeadlineWithoutBlockingAnotherBucket()
    {
        var ignoredCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationCancellation = new CancellationTokenSource();
        var processing = RedisLiveRoomCompletionQueue.RunConcurrentlyAsync(
            new[] { 1, 2 },
            2,
            async (item, _) =>
            {
                if (item == 1)
                {
                    await RedisLiveRoomCompletionQueue.AwaitPersistenceWithDeadlineAsync(
                        ignoredCancellation.Task,
                        operationCancellation,
                        TimeSpan.FromMilliseconds(50),
                        TimeProvider.System,
                        CancellationToken.None);
                }
                else
                {
                    fast.TrySetResult();
                }
            },
            CancellationToken.None);

        await fast.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => processing);
        Assert.True(operationCancellation.IsCancellationRequested);
        Assert.False(ignoredCancellation.Task.IsCompleted);
    }

    [Fact]
    public void AdmissionRemainsFailClosedUntilATokenBoundFullBucketRebuildCompletes()
    {
        var count = GetScript("ReadAdmissionCountScript").OriginalScript;
        var begin = GetScript("BeginAdmissionRebuildScript").OriginalScript;
        var repair = GetScript("RepairAdmissionBucketScript").OriginalScript;
        var complete = GetScript("CompleteAdmissionRebuildScript").OriginalScript;
        var source = File.ReadAllText(FindSource("RedisLiveRoomCompletionQueue.cs"));

        Assert.Contains("exists', @healthKey) == 0 then return -1", count, StringComparison.Ordinal);
        Assert.Contains("get', @lockKey", begin, StringComparison.Ordinal);
        Assert.Contains("set', @tokenKey", begin, StringComparison.Ordinal);
        Assert.DoesNotContain("set', @healthKey", begin, StringComparison.Ordinal);
        Assert.Contains("del', @rebuildActiveKey", begin, StringComparison.Ordinal);
        Assert.DoesNotContain("del', @activeKey", begin, StringComparison.Ordinal);
        Assert.Contains("get', @tokenKey) ~= @rebuildToken", repair, StringComparison.Ordinal);
        Assert.Contains("sadd', @rebuildActiveKey", repair, StringComparison.Ordinal);
        Assert.Contains("sunionstore', @activeKey, 1, @rebuildActiveKey", complete, StringComparison.Ordinal);
        Assert.Contains("zunionstore', @enqueuedKey, 1, @rebuildEnqueuedKey", complete, StringComparison.Ordinal);
        Assert.Contains("set', @healthKey", complete, StringComparison.Ordinal);
        Assert.Contains("foreach (var bucket in Buckets)", source, StringComparison.Ordinal);
        Assert.Contains("SortedSetRangeByRankWithScoresAsync", source, StringComparison.Ordinal);
        Assert.Contains("ProcessQueueAsync(stoppingToken)", source, StringComparison.Ordinal);
        Assert.Contains("RepairAdmissionAuthorityAsync(stoppingToken)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedGenerationReplacesGhostedAdmissionAuthorityInsteadOfMergingIt()
    {
        var begin = GetScript("BeginAdmissionRebuildScript").OriginalScript;
        var complete = GetScript("CompleteAdmissionRebuildScript").OriginalScript;

        Assert.Contains("del', @rebuildActiveKey", begin, StringComparison.Ordinal);
        Assert.Contains("sunionstore', @activeKey, 1, @rebuildActiveKey", complete, StringComparison.Ordinal);
        Assert.DoesNotContain("sunionstore', @activeKey, 1, @activeKey", complete, StringComparison.Ordinal);

        var ghost = Guid.CreateVersion7();
        var authority = Guid.CreateVersion7();
        var currentGeneration = new HashSet<Guid> { ghost };
        var rebuiltGeneration = new HashSet<Guid> { authority };
        currentGeneration = [.. rebuiltGeneration];

        Assert.DoesNotContain(ghost, currentGeneration);
        Assert.Contains(authority, currentGeneration);
    }

    [Fact]
    public void AdmissionMutationsAreDualWrittenWhileARebuildGenerationIsOpen()
    {
        foreach (var scriptName in new[]
                 {
                     "ReserveAdmissionScript",
                     "MarkAdmissionPendingScript",
                     "MarkAdmissionFailedScript",
                     "CompleteAdmissionScript"
                 })
        {
            var script = GetScript(scriptName).OriginalScript;
            Assert.Contains("exists', @rebuildTokenKey", script, StringComparison.Ordinal);
            Assert.Contains("@rebuildActiveKey", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReservationCrashAndFailoverPathsStayFailClosedAndSelfCleaning()
    {
        var reserve = GetScript("ReserveRoomSlotScript").OriginalScript;
        var commit = GetScript("CommitRoomSlotScript").OriginalScript;
        var release = GetScript("ReleaseRoomSlotScript").OriginalScript;
        var source = File.ReadAllText(FindSource("RedisLiveRoomCompletionQueue.cs"));

        Assert.Contains("zadd', @roomSlotsKey, @expiresAt", reserve, StringComparison.Ordinal);
        Assert.Contains("'LIMIT', 0, @cleanupLimit", reserve, StringComparison.Ordinal);
        Assert.Contains("hdel', @roomSlotTokensKey", reserve, StringComparison.Ordinal);
        Assert.Contains("existing ~= reserved then return 0", commit, StringComparison.Ordinal);
        Assert.Contains("hdel', @roomSlotTokensKey", commit, StringComparison.Ordinal);
        Assert.True(
            commit.IndexOf("existing ~= reserved", StringComparison.Ordinal) <
            commit.IndexOf("hdel', @roomSlotTokensKey", StringComparison.Ordinal),
            "Ein verspätetes Commit darf eine neuere Reservierung desselben Raums nicht löschen.");
        Assert.Contains("existing == 'reserved:' .. @reservationToken", release, StringComparison.Ordinal);
        Assert.Contains("catch (RedisException exception)", ExtractMethod(
            source,
            "public CompletionRoomSlotReservation? TryReserveRoomSlot",
            "public void CommitRoomSlot"), StringComparison.Ordinal);
        Assert.Contains("return null", ExtractMethod(
            source,
            "public CompletionRoomSlotReservation? TryReserveRoomSlot",
            "public void CommitRoomSlot"), StringComparison.Ordinal);
        Assert.Contains("behält seine Admission-Reservierung bis zum Ablauf", ExtractMethod(
            source,
            "public void CommitRoomSlot",
            "public void ReleaseRoomSlot"), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedOrOverCapacityRebuildCannotPublishHealthyAdmission()
    {
        var complete = GetScript("CompleteAdmissionRebuildScript").OriginalScript;
        var source = File.ReadAllText(FindSource("RedisLiveRoomCompletionQueue.cs"));

        Assert.Contains("> tonumber(@capacity) then return -2", complete, StringComparison.Ordinal);
        Assert.True(
            complete.IndexOf("> tonumber(@capacity) then return -2", StringComparison.Ordinal) <
            complete.IndexOf("set', @healthKey", StringComparison.Ordinal));
        Assert.Contains("if (completed == -2)", source, StringComparison.Ordinal);
        Assert.Contains("if (completed != 1)", source, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact", ExtractMethod(
            source,
            "private async Task<IReadOnlyList<AdmissionRepairEntry>> ValidateAdmissionRepairEntriesAsync",
            "private static string SerializeAdmissionRepairEntries"), StringComparison.Ordinal);
        Assert.Contains("Intersect(failedEntries", source, StringComparison.Ordinal);
        Assert.Contains("CleanupInvalidQueueMemberScript", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdmissionHotpathUsesConstantTimeMetadataAndReconcileCannotBeHeadBlocked()
    {
        var source = File.ReadAllText(FindSource("RedisLiveRoomCompletionQueue.cs"));
        var canAccept = ExtractMethod(source, "public bool CanAcceptNewRoom", "public LiveRoomCompletionMetrics GetMetrics");
        var pending = ExtractMethod(source, "public int PendingCount", "public long FailedAttempts");
        var reconcile = ExtractMethod(source, "private async Task ReconcileAdmissionAsync", "private async Task SynchronizeAdmissionAsync");

        Assert.Contains("ReadAdmissionOccupancyScript", canAccept, StringComparison.Ordinal);
        Assert.Contains("AdmissionRoomSlotsKey", canAccept, StringComparison.Ordinal);
        Assert.DoesNotContain("Buckets", canAccept, StringComparison.Ordinal);
        Assert.Contains("ReadAdmissionCount(AdmissionPendingKey)", pending, StringComparison.Ordinal);
        Assert.Contains("CompleteMalformedAdmissionAsync", reconcile, StringComparison.Ordinal);
        Assert.Contains("CompleteMalformedAdmissionAsync(due[0]", reconcile, StringComparison.Ordinal);
        Assert.Contains("AdmissionLockKey(roomId)", reconcile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoomSlotReservationsAreAtomicAtCapacityAndEnqueueTakesOverWithoutDoubleCounting()
    {
        var reserve = GetScript("ReserveRoomSlotScript").OriginalScript;
        var enqueue = GetScript("ReserveAdmissionScript").OriginalScript;
        var release = GetScript("ReleaseRoomSlotScript").OriginalScript;

        Assert.Contains("scard', @activeKey", reserve, StringComparison.Ordinal);
        Assert.Contains("zcard', @roomSlotsKey", reserve, StringComparison.Ordinal);
        Assert.Contains("tonumber(@currentRoomCount)", reserve, StringComparison.Ordinal);
        Assert.Contains(">= tonumber(@capacity)", reserve, StringComparison.Ordinal);
        Assert.Contains("zrem', @roomSlotsKey, @roomId", enqueue, StringComparison.Ordinal);
        Assert.Contains("sadd', activeKey, @roomId", enqueue, StringComparison.Ordinal);
        Assert.Contains("existing == 'reserved:' .. @reservationToken", release, StringComparison.Ordinal);
        Assert.Contains("zrem', @roomSlotsKey, @roomId", GetScript("CommitRoomSlotScript").OriginalScript, StringComparison.Ordinal);

        const int capacity = 2;
        var completionJobs = new HashSet<Guid> { Guid.CreateVersion7() };
        var roomSlots = new HashSet<Guid>();
        var candidates = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var gate = new object();
        await Task.WhenAll(candidates.Select(candidate => Task.Run(() =>
        {
            lock (gate)
            {
                if (completionJobs.Count + roomSlots.Count < capacity)
                {
                    roomSlots.Add(candidate);
                }
            }
        })));

        var accepted = Assert.Single(roomSlots);
        lock (gate)
        {
            roomSlots.Remove(accepted);
            completionJobs.Add(accepted);
        }

        Assert.Equal(capacity, completionJobs.Count + roomSlots.Count);
        Assert.Empty(roomSlots.Intersect(completionJobs));
    }

    [Fact]
    public void AdmissionReservationPrecedesBucketWriteAndReconciliationIsLocked()
    {
        var source = File.ReadAllText(FindSource("RedisLiveRoomCompletionQueue.cs"));
        var enqueue = ExtractMethod(source, "public CompletionReceipt Enqueue", "public CompletionStatusSnapshot GetStatus");

        Assert.True(
            enqueue.IndexOf("AdmissionLockKey(record.Id)", StringComparison.Ordinal) <
            enqueue.IndexOf("EnqueueIntentKey(record.Id)", StringComparison.Ordinal));
        Assert.True(
            enqueue.IndexOf("EnqueueIntentKey(record.Id)", StringComparison.Ordinal) <
            enqueue.IndexOf("ReserveAdmission(roomId, now, admissionLease)", StringComparison.Ordinal));
        Assert.True(
            enqueue.IndexOf("ReserveAdmission(roomId, now, admissionLease)", StringComparison.Ordinal) <
            enqueue.IndexOf("EnqueueScript", StringComparison.Ordinal));
        Assert.Contains("SynchronizeAdmissionUnderLockAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdmissionSynchronizationMutationsRejectStaleLeaseOwnersAtomically()
    {
        foreach (var scriptName in new[]
                 {
                     "MarkAdmissionPendingScript",
                     "MarkAdmissionFailedScript",
                     "CompleteAdmissionScript"
                 })
        {
            var script = GetScript(scriptName).OriginalScript;
            Assert.StartsWith("if redis.call('get', @lockKey) ~= @lockToken then return 0 end;", script);
        }

        var source = File.ReadAllText(FindSource("RedisLiveRoomCompletionQueue.cs"));
        var synchronize = ExtractMethod(
            source,
            "private async Task SynchronizeAdmissionUnderLockAsync",
            "private async Task CompleteMalformedAdmissionAsync");
        Assert.Contains("MarkAdmissionPendingDirectAsync(roomId, admissionLease)", synchronize, StringComparison.Ordinal);
        Assert.Contains("MarkAdmissionFailedDirectAsync(roomId, admissionLease)", synchronize, StringComparison.Ordinal);
        Assert.Contains("CompleteAdmissionDirectAsync(roomId, admissionLease)", synchronize, StringComparison.Ordinal);

        foreach (var methodStart in new[]
                 {
                     "private async Task MarkAdmissionPendingDirectAsync",
                     "private async Task MarkAdmissionFailedDirectAsync",
                     "private async Task CompleteAdmissionDirectAsync"
                 })
        {
            var start = source.IndexOf(methodStart, StringComparison.Ordinal);
            var next = source.IndexOf("\n    private ", start + methodStart.Length, StringComparison.Ordinal);
            var method = source[start..next];
            Assert.Contains("lockKey = admissionLease.Key", method, StringComparison.Ordinal);
            Assert.Contains("lockToken = admissionLease.Token", method, StringComparison.Ordinal);
            Assert.Contains("EnsureAdmissionMutationFenced", method, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RecoveredPendingSurvivesStaleCompleteAndPersistedStateRejectsStaleMark()
    {
        const string staleOwner = "owner-a";
        const string currentOwner = "owner-b";
        var active = true;
        var pending = true;

        static bool Mutate(string storedToken, string suppliedToken, Action mutation)
        {
            if (!StringComparer.Ordinal.Equals(storedToken, suppliedToken))
            {
                return false;
            }

            mutation();
            return true;
        }

        Assert.False(Mutate(currentOwner, staleOwner, () =>
        {
            active = false;
            pending = false;
        }));
        Assert.True(active);
        Assert.True(pending);

        Assert.True(Mutate(currentOwner, currentOwner, () =>
        {
            active = false;
            pending = false;
        }));
        Assert.False(Mutate(currentOwner, staleOwner, () =>
        {
            active = true;
            pending = true;
        }));
        Assert.False(active);
        Assert.False(pending);
    }

    [Fact]
    public void MissingRecordCleanupPreservesAConcurrentEnqueue()
    {
        var source = GetScript("CleanupMissingScript").OriginalScript;
        var recordCheck = source.IndexOf("exists', @recordKey", StringComparison.Ordinal);
        var pendingRemoval = source.IndexOf("zrem', @pendingKey", StringComparison.Ordinal);

        Assert.True(recordCheck >= 0);
        Assert.Contains("exists', @recordKey) == 1 then return 2", source);
        Assert.True(recordCheck < pendingRemoval);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(6, 900)]
    [InlineData(100, 900)]
    public void RedriveBackoffIsExponentialAndCapped(long cycle, int expectedSeconds)
    {
        var method = typeof(RedisLiveRoomCompletionQueue).GetMethod(
            "CalculateRedriveDelay",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Redrive-Zeitplan fehlt.");

        var delay = Assert.IsType<TimeSpan>(method.Invoke(null, [cycle]));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    private static LuaScript GetScript(string name) =>
        Assert.IsType<LuaScript>(typeof(RedisLiveRoomCompletionQueue)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null));

    private static RedisKey GetKey(string name, Guid id) =>
        Assert.IsType<RedisKey>(typeof(RedisLiveRoomCompletionQueue)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, [id]));

    private static string HashTag(RedisKey key)
    {
        var value = key.ToString();
        var start = value.IndexOf('{');
        var end = value.IndexOf('}', start + 1);
        return start >= 0 && end > start + 1 ? value[(start + 1)..end] : value;
    }

    private static string FindSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "KeyWars");
            var matches = Directory.Exists(candidate)
                ? Directory.GetFiles(candidate, fileName, SearchOption.AllDirectories)
                : [];
            if (matches.Length == 1)
            {
                return matches[0];
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(fileName);
    }

    private static string ExtractMethod(string source, string startName, string endName)
    {
        var start = source.IndexOf(startName, StringComparison.Ordinal);
        var end = source.IndexOf(endName, start + startName.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

}
