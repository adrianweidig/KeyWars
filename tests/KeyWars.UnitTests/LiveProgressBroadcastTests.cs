using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeyWars.UnitTests;

public sealed class LiveProgressBroadcastTests
{
    [Fact]
    public void ParticipantSnapshotSerializesBoundedWindowCoordinates()
    {
        var participant = new LiveParticipantSnapshot(
            ProfileId: Guid.CreateVersion7(),
            DisplayName: "A",
            Status: ParticipantStatus.Running,
            Ready: true,
            Sequence: 7,
            CorrectCharacters: 70,
            TypedTextPreview: $"{new string('c', 8)}w{new string('c', 23)}",
            Wpm: 42,
            Placement: null,
            DurationMilliseconds: 0,
            Accuracy: 99,
            TypedStateOffset: 62,
            TypedCharacters: 100);

        var json = JsonSerializer.Serialize(
            participant,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"typedStateOffset\":62", json, StringComparison.Ordinal);
        Assert.Contains("\"typedCharacters\":100", json, StringComparison.Ordinal);
        Assert.DoesNotContain("appendText", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyParticipantMementoWithoutWindowCoordinatesDefaultsSafely()
    {
        var participant = new LiveParticipantMemento(
            ProfileId: Guid.CreateVersion7(),
            DisplayName: "A",
            Status: ParticipantStatus.Running,
            JoinedAt: DateTimeOffset.Parse("2026-08-13T12:00:00Z"),
            TeamNumber: null,
            Ready: true,
            Sequence: 7,
            CorrectCharacters: 70,
            TypedTextPreview: "",
            Wpm: 42,
            Placement: null,
            FinishedAt: null,
            DisconnectedAt: null,
            DurationMilliseconds: 0,
            Accuracy: 99,
            SeriesPoints: 0,
            RoundWins: 0,
            FinishedRounds: 0,
            CompletedRounds: 0,
            TotalDurationMilliseconds: 0,
            TotalWpm: 0,
            TotalAccuracy: 0,
            CompetitionEligible: true,
            TypedStateOffset: 62,
            TypedCharacters: 100);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(participant, options))!.AsObject();
        legacyJson.Remove("typedStateOffset");
        legacyJson.Remove("typedCharacters");

        var restored = legacyJson.Deserialize<LiveParticipantMemento>(options);

        Assert.NotNull(restored);
        Assert.Equal(0, restored.TypedStateOffset);
        Assert.Equal(0, restored.TypedCharacters);
    }

    [Fact]
    public async Task ProgressBroadcasterCoalescesLatestDeltaPerParticipant()
    {
        var sender = new RecordingProgressSender();
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 10, RoomCommandQueueCapacity = 8 }),
            TimeProvider.System,
            NullLogger<LiveProgressBroadcaster>.Instance);
        var roomId = Guid.CreateVersion7();
        var participantId = Guid.CreateVersion7();

        await broadcaster.PublishAsync(CreateDelta(roomId, participantId, correctCharacters: 10), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(roomId, participantId, correctCharacters: 12), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(roomId, participantId, correctCharacters: 15), CancellationToken.None);
        await broadcaster.FlushAsync(roomId, CancellationToken.None);

        Assert.Equal(2, sender.Batches.Count);
        Assert.Equal(10, sender.Batches[0].Deltas.Single().CorrectCharacters);
        Assert.Equal(15, sender.Batches[1].Deltas.Single().CorrectCharacters);
        Assert.Equal(15, sender.Batches[1].Deltas.Single().TypedCharacters);
        Assert.True(sender.Batches[1].Deltas.Single().TypedStateBits.Length < 15);
        Assert.True(broadcaster.Snapshot().CoalescedProgressMessages >= 1);
    }

    [Fact]
    public async Task ProgressBroadcasterDropsNewParticipantsWhenPendingCapacityIsFull()
    {
        var sender = new RecordingProgressSender();
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 1, RoomCommandQueueCapacity = 1 }),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-12T12:00:00Z")),
            NullLogger<LiveProgressBroadcaster>.Instance);
        var roomId = Guid.CreateVersion7();
        await broadcaster.PublishAsync(CreateDelta(roomId, Guid.CreateVersion7(), correctCharacters: 1), CancellationToken.None);

        await broadcaster.PublishAsync(CreateDelta(roomId, Guid.CreateVersion7(), correctCharacters: 2), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(roomId, Guid.CreateVersion7(), correctCharacters: 3), CancellationToken.None);
        await broadcaster.FlushAsync(roomId, CancellationToken.None);

        Assert.Equal(2, sender.Batches.Count);
        Assert.Single(sender.Batches[1].Deltas);
        Assert.Equal(1, broadcaster.Snapshot().DroppedProgressMessages);
    }

    [Fact]
    public async Task RemovingRoomClearsOnlyItsBufferAndPreservesActiveRoomInterval()
    {
        var sender = new RecordingProgressSender();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 1, RoomCommandQueueCapacity = 8 }),
            time,
            NullLogger<LiveProgressBroadcaster>.Instance);
        var activeRoomId = Guid.CreateVersion7();
        var removedRoomId = Guid.CreateVersion7();

        await broadcaster.PublishAsync(CreateDelta(activeRoomId, Guid.CreateVersion7(), 1), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(removedRoomId, Guid.CreateVersion7(), 1), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(removedRoomId, Guid.CreateVersion7(), 2), CancellationToken.None);

        var beforeRemoval = broadcaster.Snapshot();
        Assert.Equal(2, beforeRemoval.ActiveRooms);
        Assert.Equal(1, beforeRemoval.PendingProgressMessages);
        Assert.True(broadcaster.RemoveRoom(removedRoomId));

        var afterRemoval = broadcaster.Snapshot();
        Assert.Equal(1, afterRemoval.ActiveRooms);
        Assert.Equal(0, afterRemoval.PendingProgressMessages);
        Assert.Equal(2, afterRemoval.BroadcastCount);

        await broadcaster.PublishAsync(CreateDelta(activeRoomId, Guid.CreateVersion7(), 2), CancellationToken.None);

        Assert.Single(sender.Batches, batch => batch.RoomId == activeRoomId);
        Assert.Equal(1, broadcaster.Snapshot().PendingProgressMessages);
        Assert.True(broadcaster.RemoveRoom(activeRoomId));
        var final = broadcaster.Snapshot();
        Assert.Equal(0, final.ActiveRooms);
        Assert.Equal(0, final.PendingProgressMessages);
        Assert.Equal(2, final.BroadcastCount);
    }

    [Fact]
    public async Task ProgressBroadcasterNeverMixesOrReplaysRoomVersions()
    {
        var sender = new RecordingProgressSender();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 1, RoomCommandQueueCapacity = 8 }),
            time,
            NullLogger<LiveProgressBroadcaster>.Instance);
        var roomId = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await broadcaster.PublishAsync(CreateDelta(roomId, first, 10, roomVersion: 2), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(roomId, first, 11, roomVersion: 2), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(roomId, second, 1, roomVersion: 4), CancellationToken.None);
        await broadcaster.PublishAsync(CreateDelta(roomId, first, 12, roomVersion: 2), CancellationToken.None);
        await broadcaster.FlushAsync(roomId, CancellationToken.None);

        Assert.Equal(2, sender.Batches.Count);
        Assert.All(sender.Batches[1].Deltas, delta => Assert.Equal(4, delta.RoomVersion));
        Assert.Equal(second, Assert.Single(sender.Batches[1].Deltas).ParticipantId);
        Assert.True(broadcaster.Snapshot().DroppedProgressMessages >= 1);
    }

    [Fact]
    public void RedisProgressRelaySelectsOnlyTheNewestPendingRoomVersion()
    {
        var roomId = Guid.CreateVersion7();
        var oldDelta = CreateDelta(roomId, Guid.CreateVersion7(), 10, roomVersion: 2);
        var currentDelta = CreateDelta(roomId, Guid.CreateVersion7(), 1, roomVersion: 4);

        var selected = KeyWars.Infrastructure.Cluster.RedisLiveProgressRelay.SelectNewestRoomVersion(
            [oldDelta, currentDelta]);

        Assert.Equal(currentDelta, Assert.Single(selected));
    }

    [Fact]
    public async Task ProgressBroadcasterKeepsOneSendInFlightAndCoalescesWhileSenderIsBlocked()
    {
        var sender = new BlockingProgressSender();
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 60, RoomCommandQueueCapacity = 8 }),
            TimeProvider.System,
            NullLogger<LiveProgressBroadcaster>.Instance);
        var roomId = Guid.CreateVersion7();
        var participantId = Guid.CreateVersion7();

        var firstPublish = broadcaster.PublishAsync(
            CreateDelta(roomId, participantId, correctCharacters: 1),
            CancellationToken.None);
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var sequence = 2; sequence <= 500; sequence++)
        {
            await broadcaster.PublishAsync(
                CreateDelta(roomId, participantId, correctCharacters: sequence),
                CancellationToken.None);
        }

        Assert.Equal(1, sender.SendCount);
        Assert.Equal(1, sender.MaxConcurrentSends);
        Assert.Equal(1, broadcaster.Snapshot().PendingProgressMessages);
        Assert.True(sender.FirstSendToken.CanBeCanceled);

        sender.ReleaseFirstSend();
        await firstPublish.WaitAsync(TimeSpan.FromSeconds(2));
        await sender.LatestDeltaSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, sender.SendCount);
        Assert.Equal(1, sender.MaxConcurrentSends);
        Assert.Equal(500, sender.Batches[^1].Deltas.Single().ParticipantSequence);
    }

    [Fact]
    public async Task FailedSendNeverRequeuesBeyondThePendingCapacity()
    {
        var sender = new BlockingProgressSender(failFirstSend: true);
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 1, RoomCommandQueueCapacity = 1 }),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z")),
            NullLogger<LiveProgressBroadcaster>.Instance);
        var roomId = Guid.CreateVersion7();

        var firstPublish = broadcaster.PublishAsync(
            CreateDelta(roomId, Guid.CreateVersion7(), correctCharacters: 1),
            CancellationToken.None);
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await broadcaster.PublishAsync(
            CreateDelta(roomId, Guid.CreateVersion7(), correctCharacters: 2),
            CancellationToken.None);

        sender.ReleaseFirstSend();
        await firstPublish.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, broadcaster.Snapshot().PendingProgressMessages);
        Assert.Equal(1, broadcaster.Snapshot().DroppedProgressMessages);
    }

    private static LiveProgressDelta CreateDelta(
        Guid roomId,
        Guid participantId,
        int correctCharacters,
        int roomVersion = 2) => new(
        roomId,
        roomVersion,
        correctCharacters + 1L,
        participantId,
        correctCharacters,
        correctCharacters,
        correctCharacters,
        EncodeCorrectBits(correctCharacters),
        42,
        100,
        1);

    private static string EncodeCorrectBits(int length)
    {
        var bytes = new byte[(length + 7) / 8];
        Array.Fill(bytes, byte.MaxValue);
        if (length % 8 is { } remainder and not 0)
        {
            bytes[^1] = (byte)((1 << remainder) - 1);
        }

        return Convert.ToBase64String(bytes);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingProgressSender : ILiveProgressSender
    {
        public List<LiveProgressBatch> Batches { get; } = [];

        public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingProgressSender(bool failFirstSend = false) : ILiveProgressSender
    {
        private readonly TaskCompletionSource firstSendRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeSends;
        private int sendCount;
        private int maxConcurrentSends;

        public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LatestDeltaSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<LiveProgressBatch> Batches { get; } = [];
        public int SendCount => Volatile.Read(ref sendCount);
        public int MaxConcurrentSends => Volatile.Read(ref maxConcurrentSends);
        public CancellationToken FirstSendToken { get; private set; }

        public async Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeSends);
            var observedMaximum = Volatile.Read(ref maxConcurrentSends);
            while (active > observedMaximum)
            {
                var previous = Interlocked.CompareExchange(ref maxConcurrentSends, active, observedMaximum);
                if (previous == observedMaximum)
                {
                    break;
                }

                observedMaximum = previous;
            }
            var call = Interlocked.Increment(ref sendCount);
            lock (Batches)
            {
                Batches.Add(batch);
            }

            try
            {
                if (call == 1)
                {
                    FirstSendToken = cancellationToken;
                    FirstSendStarted.TrySetResult();
                    await firstSendRelease.Task.WaitAsync(cancellationToken);
                    if (failFirstSend)
                    {
                        throw new InvalidOperationException("Simulierter Sendefehler");
                    }
                }

                if (batch.Deltas.Any(delta => delta.ParticipantSequence == 500))
                {
                    LatestDeltaSent.TrySetResult();
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeSends);
            }
        }

        public void ReleaseFirstSend() => firstSendRelease.TrySetResult();
    }
}
