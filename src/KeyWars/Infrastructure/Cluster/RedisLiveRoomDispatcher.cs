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
    private const string Prefix = "keywars:room";
    private static readonly RedisKey ActiveRoomsKey = $"{Prefix}:active";
    private static readonly RedisKey CreateLockKey = $"{Prefix}:create-lock";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly LuaScript CreateScript = LuaScript.Prepare(
        "if redis.call('exists', @roomKey) == 1 or redis.call('exists', @codeKey) == 1 then return 0 end; " +
        "if redis.call('zcard', @activeKey) >= tonumber(@capacity) then return -1 end; " +
        "redis.call('hset', @roomKey, 'revision', 1, 'memento', @memento, 'code', @code); " +
        "redis.call('expire', @roomKey, @ttlSeconds); " +
        "redis.call('set', @codeKey, @roomId, 'EX', @ttlSeconds); " +
        "redis.call('zadd', @activeKey, @updatedAt, @roomId); return 1");
    private static readonly LuaScript CompareExchangeScript = LuaScript.Prepare(
        "if tonumber(redis.call('hget', @roomKey, 'revision') or '-1') ~= tonumber(@expectedRevision) then return 0 end; " +
        "redis.call('hset', @roomKey, 'revision', tonumber(@expectedRevision) + 1, 'memento', @memento, 'code', @code); " +
        "redis.call('expire', @roomKey, @ttlSeconds); " +
        "redis.call('set', @codeKey, @roomId, 'EX', @ttlSeconds); " +
        "redis.call('zadd', @activeKey, @updatedAt, @roomId); return 1");
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TimeSpan roomStateLifetime = TimeSpan.FromMinutes(Math.Max(
        60,
        Math.Max(options.Value.LobbyRoomRetentionMinutes, options.Value.CompletedRoomRetentionMinutes) + 60));

    public bool IsAuthoritative => true;
    public string InstanceId { get; } = ResolveInstanceId();

    public async ValueTask<LiveRoomSnapshot> CreateRoomAsync(
        CreateLiveRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var createLock = await RedisDistributedLease.AcquireAsync(database, CreateLockKey, cancellationToken);
        await CleanupExpiredIndexEntriesAsync(cancellationToken);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = rooms.CreateRoom(request);
            try
            {
                var memento = ScrubTransientInput(rooms.ExportRoomState(snapshot.RoomId));
                var result = (int)await database.ScriptEvaluateAsync(
                    CreateScript,
                    new
                    {
                        roomKey = RoomKey(snapshot.RoomId),
                        codeKey = CodeKey(snapshot.Code),
                        activeKey = ActiveRoomsKey,
                        roomId = snapshot.RoomId.ToString("N"),
                        code = snapshot.Code,
                        memento = Serialize(memento),
                        ttlSeconds = (long)roomStateLifetime.TotalSeconds,
                        updatedAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        capacity = Math.Max(1, options.Value.MaxConcurrentRooms)
                    });
                if (result == 1)
                {
                    return snapshot;
                }

                if (result == -1)
                {
                    throw new InvalidOperationException("Die maximale Anzahl gleichzeitiger Live-Räume ist erreicht.");
                }
            }
            finally
            {
                rooms.RemoveRoomState(snapshot.RoomId);
            }
        }

        throw new InvalidOperationException("Es konnte kein clusterweit eindeutiger Raumcode erzeugt werden.");
    }

    public async ValueTask<IReadOnlyList<LiveRoomSnapshot>> ListOpenRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        var mementos = await ReadActiveMementosAsync(cancellationToken);
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
        var mementos = await ReadActiveMementosAsync(cancellationToken);
        var items = mementos
            .Where(room => room.Phase == LiveRoomPhase.Lobby && !room.Finished && CanDiscover(room, viewerProfileId))
            .Select(ToLobbySummary)
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.RoomId)
            .ToArray();
        return new LiveRoomLobbyPage(items.Skip(offset).Take(limit).ToArray(), offset, limit, items.Length);
    }

    public async ValueTask<LiveRoomMetricsSnapshot> MetricsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var rooms = await ReadActiveMementosAsync(cancellationToken);
        return new LiveRoomMetricsSnapshot(
            rooms.Count(room => !room.Finished),
            rooms.Count(room => !room.Finished && room.Phase == LiveRoomPhase.Lobby),
            rooms.Count(room => !room.Finished && room.Phase is LiveRoomPhase.Countdown or LiveRoomPhase.Running),
            rooms.Sum(room => room.Participants.Count(CountsTowardCapacity)));
    }

    public async ValueTask<Guid> ResolveRoomIdByCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = LiveRoomManager.NormalizeRoomCode(code);
        var value = await database.StringGetAsync(CodeKey(normalized));
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
            manager => manager.JoinByCode(code, profileId, displayName),
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
        var ids = await ReadActiveRoomIdsAsync(cancellationToken);
        var changed = new List<LiveRoomSnapshot>();
        foreach (var id in ids)
        {
            try
            {
                var before = await ReadRoomAsync(id, cancellationToken);
                if (before is null)
                {
                    continue;
                }

                var snapshot = await SnapshotAsync(id, cancellationToken);
                if (snapshot.StateVersion != before.Memento.StateVersion)
                {
                    changed.Add(snapshot);
                }
            }
            catch (InvalidOperationException)
            {
                await RemoveIndexEntryAsync(id);
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
                        manager.RemoveProfile(profileId);
                        return true;
                    },
                    cancellationToken);
            }
            catch (InvalidOperationException)
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
                aborted += await ExecuteAsync(id, manager => manager.AbortActiveRooms(), cancellationToken);
            }
            catch (InvalidOperationException)
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
        var record = await ReadRoomAsync(roomId, cancellationToken)
            ?? throw new InvalidOperationException("Der Live-Raum wurde nicht gefunden.");
        try
        {
            rooms.RemoveRoomState(roomId);
            if (!rooms.ImportRoomState(record.Memento))
            {
                throw new InvalidOperationException("Der Live-Raum konnte nicht auf den aktuellen Clusterstand gebracht werden.");
            }

            using var completionBatch = completionSink.BeginBatch();
            var result = operation(rooms);
            var updated = ScrubTransientInput(rooms.ExportRoomState(roomId));
            if (updated.StateVersion == record.Memento.StateVersion)
            {
                completionBatch.Commit();
                return result;
            }

            var saved = (int)await database.ScriptEvaluateAsync(
                CompareExchangeScript,
                new
                {
                    roomKey = RoomKey(roomId),
                    codeKey = CodeKey(updated.Code),
                    activeKey = ActiveRoomsKey,
                    roomId = roomId.ToString("N"),
                    code = updated.Code,
                    expectedRevision = record.Revision,
                    memento = Serialize(updated),
                    ttlSeconds = (long)roomStateLifetime.TotalSeconds,
                    updatedAt = timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                });
            if (saved != 1)
            {
                throw new InvalidOperationException(
                    "Der Live-Raum wurde parallel geändert. Lade den aktuellen Stand neu.");
            }

            completionBatch.Commit();
            return result;
        }
        finally
        {
            rooms.RemoveRoomState(roomId);
        }
    }

    private async Task<IReadOnlyList<LiveRoomMemento>> ReadActiveMementosAsync(CancellationToken cancellationToken)
    {
        var result = new List<LiveRoomMemento>();
        foreach (var id in await ReadActiveRoomIdsAsync(cancellationToken))
        {
            var record = await ReadRoomAsync(id, cancellationToken);
            if (record is null)
            {
                await RemoveIndexEntryAsync(id);
                continue;
            }

            result.Add(record.Memento);
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> ReadActiveRoomIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await database.SortedSetRangeByRankAsync(ActiveRoomsKey);
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

    private async Task CleanupExpiredIndexEntriesAsync(CancellationToken cancellationToken)
    {
        foreach (var id in await ReadActiveRoomIdsAsync(cancellationToken))
        {
            if (!await database.KeyExistsAsync(RoomKey(id)))
            {
                await RemoveIndexEntryAsync(id);
            }
        }
    }

    private async Task RemoveIndexEntryAsync(Guid roomId) =>
        await database.SortedSetRemoveAsync(ActiveRoomsKey, roomId.ToString("N"));

    private static LiveRoomMemento ScrubTransientInput(LiveRoomMemento memento) =>
        memento with
        {
            Participants = memento.Participants
                .Select(participant => participant with { TypedTextPreview = "" })
                .ToArray()
        };

    private static bool CanDiscover(LiveRoomMemento room, Guid viewerProfileId) =>
        room.Visibility == LiveRoomVisibility.InternalOpen ||
        room.CreatorProfileId == viewerProfileId ||
        room.Participants.Any(participant => participant.ProfileId == viewerProfileId) ||
        room.Visibility == LiveRoomVisibility.InvitationOnly && room.InvitedProfileIds.Contains(viewerProfileId);

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

    private static string Serialize(LiveRoomMemento memento) =>
        JsonSerializer.Serialize(memento, SerializerOptions);

    private static RedisKey RoomKey(Guid roomId) => $"{Prefix}:state:{roomId:N}";
    private static RedisKey LockKey(Guid roomId) => $"{Prefix}:lock:{roomId:N}";
    private static RedisKey CodeKey(string code) => $"{Prefix}:code:{code.ToUpperInvariant()}";

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
}
