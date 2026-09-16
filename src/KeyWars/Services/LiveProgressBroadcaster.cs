using System.Collections.Concurrent;
using KeyWars.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record LiveProgressDelta(
    Guid RoomId,
    int RoomVersion,
    long StateVersion,
    Guid ParticipantId,
    int ParticipantSequence,
    int CorrectCharacters,
    int TypedCharacters,
    string TypedStateBits,
    double Wpm,
    double Accuracy,
    int? RankHint,
    int TypedStateOffset = 0,
    int TypedStateLength = 0,
    LiveProgressInputStatus InputStatus = LiveProgressInputStatus.Applied);

public sealed record LiveProgressBatch(
    Guid RoomId,
    int RoomVersion,
    DateTimeOffset ServerNow,
    IReadOnlyList<LiveProgressDelta> Deltas);

public sealed record LiveProgressMetrics(
    int ActiveRooms,
    int PendingProgressMessages,
    long CoalescedProgressMessages,
    long DroppedProgressMessages,
    long BroadcastCount);

public interface ILiveProgressSender
{
    Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken);
}

public sealed class SignalRLiveProgressSender(IHubContext<ArenaHub> hubContext) : ILiveProgressSender
{
    public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken)
    {
        return hubContext.Clients.Group(roomId.ToString("N")).SendAsync("progressChanged", batch, cancellationToken);
    }
}

public sealed class LiveProgressBroadcaster(
    ILiveProgressSender sender,
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    ILogger<LiveProgressBroadcaster> logger)
{
    private static readonly TimeSpan BroadcastSendTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, RoomProgressBuffer> rooms = new();
    private readonly TimeSpan minimumBroadcastInterval = TimeSpan.FromSeconds(1d / Math.Clamp(options.Value.ProgressBroadcastHz, 1, 60));
    private readonly int capacity = Math.Clamp(options.Value.RoomCommandQueueCapacity, 1, 65_536);
    private long coalescedProgressMessages;
    private long droppedProgressMessages;
    private long broadcastCount;

    public async Task PublishAsync(LiveProgressDelta delta, CancellationToken cancellationToken)
    {
        var room = rooms.GetOrAdd(delta.RoomId, _ => new RoomProgressBuffer());
        IReadOnlyList<LiveProgressDelta>? dueDeltas = null;
        var now = timeProvider.GetUtcNow();
        TimeSpan? delay = null;
        lock (room.Gate)
        {
            if (delta.RoomVersion < room.CurrentRoomVersion)
            {
                Interlocked.Increment(ref droppedProgressMessages);
                return;
            }

            if (delta.RoomVersion > room.CurrentRoomVersion)
            {
                room.CurrentRoomVersion = delta.RoomVersion;
                room.Pending.Clear();
                room.Latest.Clear();
            }

            if (room.Latest.TryGetValue(delta.ParticipantId, out var latest) &&
                (delta.RoomVersion < latest.RoomVersion ||
                    delta.RoomVersion == latest.RoomVersion && delta.ParticipantSequence <= latest.ParticipantSequence))
            {
                Interlocked.Increment(ref droppedProgressMessages);
                return;
            }

            room.Latest[delta.ParticipantId] = delta;
            if (room.Pending.ContainsKey(delta.ParticipantId))
            {
                Interlocked.Increment(ref coalescedProgressMessages);
            }
            else if (room.Pending.Count >= capacity)
            {
                Interlocked.Increment(ref droppedProgressMessages);
                return;
            }

            room.Pending[delta.ParticipantId] = delta;
            var elapsed = now - room.LastBroadcastAt;
            if (!room.SendInProgress &&
                (room.LastBroadcastAt == default || elapsed >= minimumBroadcastInterval))
            {
                dueDeltas = DrainUnlocked(room, now);
                room.SendInProgress = true;
            }
            else if (!room.SendInProgress && !room.FlushScheduled)
            {
                room.FlushScheduled = true;
                delay = minimumBroadcastInterval - elapsed;
            }
        }

        if (dueDeltas is not null)
        {
            await SendBatchAsync(delta.RoomId, room, dueDeltas, now).WaitAsync(cancellationToken);
        }

        if (delay is { } flushDelay)
        {
            _ = ScheduleFlushAsync(delta.RoomId, flushDelay);
        }
    }

    public async Task FlushAsync(Guid roomId, CancellationToken cancellationToken)
    {
        if (!rooms.TryGetValue(roomId, out var room))
        {
            return;
        }

        IReadOnlyList<LiveProgressDelta>? dueDeltas = null;
        var now = timeProvider.GetUtcNow();
        lock (room.Gate)
        {
            room.FlushScheduled = false;
            if (room.Pending.Count == 0)
            {
                return;
            }

            if (room.SendInProgress)
            {
                return;
            }

            dueDeltas = DrainUnlocked(room, now);
            room.SendInProgress = true;
        }

        await SendBatchAsync(roomId, room, dueDeltas, now).WaitAsync(cancellationToken);
    }

    public bool RemoveRoom(Guid roomId)
    {
        if (!rooms.TryRemove(roomId, out var room))
        {
            return false;
        }

        lock (room.Gate)
        {
            room.Removed = true;
            room.Lifetime.Cancel();
            room.Pending.Clear();
            room.Latest.Clear();
        }

        return true;
    }

    public LiveProgressMetrics Snapshot()
    {
        return new LiveProgressMetrics(
            rooms.Count,
            rooms.Values.Sum(room =>
            {
                lock (room.Gate)
                {
                    return room.Pending.Count;
                }
            }),
            Volatile.Read(ref coalescedProgressMessages),
            Volatile.Read(ref droppedProgressMessages),
            Volatile.Read(ref broadcastCount));
    }

    private async Task ScheduleFlushAsync(Guid roomId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await FlushAsync(roomId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ein Arena-Progress-Broadcast ist fehlgeschlagen.");
        }
    }

    private static IReadOnlyList<LiveProgressDelta> DrainUnlocked(RoomProgressBuffer room, DateTimeOffset now)
    {
        var ranks = room.Latest.Values
            .OrderByDescending(delta => delta.CorrectCharacters)
            .ThenByDescending(delta => delta.Wpm)
            .ThenBy(delta => delta.ParticipantId)
            .Select((delta, index) => new { delta.ParticipantId, Rank = index + 1 })
            .ToDictionary(item => item.ParticipantId, item => item.Rank);
        var dueDeltas = room.Pending.Values
            .Select(delta => delta with { RankHint = ranks[delta.ParticipantId] })
            .OrderBy(delta => delta.RankHint)
            .ThenBy(delta => delta.ParticipantId)
            .ToList();
        room.Pending.Clear();
        room.LastBroadcastAt = now;
        return dueDeltas;
    }

    private async Task SendBatchAsync(
        Guid roomId,
        RoomProgressBuffer room,
        IReadOnlyList<LiveProgressDelta> deltas,
        DateTimeOffset now)
    {
        IReadOnlyList<LiveProgressDelta> current = [];
        var sent = false;
        try
        {
            lock (room.Gate)
            {
                current = deltas
                    .Where(delta => delta.RoomVersion == room.CurrentRoomVersion)
                    .Where(delta => !room.LastSentWatermarks.TryGetValue(delta.ParticipantId, out var watermark) ||
                        delta.RoomVersion > watermark.RoomVersion ||
                        delta.RoomVersion == watermark.RoomVersion && delta.ParticipantSequence > watermark.ParticipantSequence)
                    .OrderBy(delta => delta.RankHint)
                    .ThenBy(delta => delta.ParticipantId)
                    .ToArray();
            }

            if (current.Count == 0)
            {
                return;
            }

            var batch = new LiveProgressBatch(roomId, current.Max(delta => delta.RoomVersion), now, current);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(room.Lifetime.Token);
            deadline.CancelAfter(BroadcastSendTimeout);
            await sender.SendAsync(roomId, batch, deadline.Token);
            sent = true;
            lock (room.Gate)
            {
                foreach (var delta in current.Where(delta => delta.RoomVersion == room.CurrentRoomVersion))
                {
                    room.LastSentWatermarks[delta.ParticipantId] = new ProgressWatermark(
                        delta.RoomVersion,
                        delta.ParticipantSequence);
                }
            }

            Interlocked.Increment(ref broadcastCount);
        }
        catch (OperationCanceledException) when (room.Lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Ein Arena-Progress-Broadcast hat das Zeitlimit überschritten.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ein Arena-Progress-Broadcast ist fehlgeschlagen.");
        }
        finally
        {
            bool startNext = false;
            TimeSpan? nextDelay = null;
            IReadOnlyList<LiveProgressDelta>? nextDeltas = null;
            var nextNow = timeProvider.GetUtcNow();
            lock (room.Gate)
            {
                if (!sent && !room.Removed)
                {
                    foreach (var delta in current)
                    {
                        if (room.Latest.TryGetValue(delta.ParticipantId, out var latest) &&
                            latest.RoomVersion == room.CurrentRoomVersion)
                        {
                            if (room.Pending.ContainsKey(delta.ParticipantId))
                            {
                                continue;
                            }

                            if (room.Pending.Count < capacity)
                            {
                                room.Pending[delta.ParticipantId] = latest;
                            }
                            else
                            {
                                Interlocked.Increment(ref droppedProgressMessages);
                            }
                        }
                    }
                }

                room.SendInProgress = false;
                if (!room.Removed && room.Pending.Count > 0)
                {
                    var elapsed = nextNow - room.LastBroadcastAt;
                    if (elapsed >= minimumBroadcastInterval)
                    {
                        nextDeltas = DrainUnlocked(room, nextNow);
                        room.SendInProgress = true;
                        startNext = true;
                    }
                    else if (!room.FlushScheduled)
                    {
                        room.FlushScheduled = true;
                        nextDelay = minimumBroadcastInterval - elapsed;
                    }
                }
            }

            if (startNext)
            {
                _ = SendBatchAsync(roomId, room, nextDeltas!, nextNow);
            }
            else if (nextDelay is { } delay)
            {
                _ = ScheduleFlushAsync(roomId, delay);
            }
        }
    }

    private sealed class RoomProgressBuffer
    {
        public object Gate { get; } = new();
        public Dictionary<Guid, LiveProgressDelta> Pending { get; } = [];
        public Dictionary<Guid, LiveProgressDelta> Latest { get; } = [];
        public Dictionary<Guid, ProgressWatermark> LastSentWatermarks { get; } = [];
        public CancellationTokenSource Lifetime { get; } = new();
        public DateTimeOffset LastBroadcastAt { get; set; }
        public bool FlushScheduled { get; set; }
        public bool SendInProgress { get; set; }
        public bool Removed { get; set; }
        public int CurrentRoomVersion { get; set; }
    }

    private readonly record struct ProgressWatermark(int RoomVersion, int ParticipantSequence);
}
