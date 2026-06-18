using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class LiveProgressBroadcastTests
{
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
        Assert.True(broadcaster.Snapshot().CoalescedProgressMessages >= 1);
    }

    [Fact]
    public async Task ProgressBroadcasterDropsNewParticipantsWhenPendingCapacityIsFull()
    {
        var sender = new RecordingProgressSender();
        var broadcaster = new LiveProgressBroadcaster(
            sender,
            Options.Create(new LiveOptions { ProgressBroadcastHz = 10, RoomCommandQueueCapacity = 1 }),
            TimeProvider.System,
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

    private static LiveProgressDelta CreateDelta(Guid roomId, Guid participantId, int correctCharacters) => new(
        roomId,
        2,
        participantId,
        correctCharacters,
        42,
        100,
        1);

    private sealed class RecordingProgressSender : ILiveProgressSender
    {
        public List<LiveProgressBatch> Batches { get; } = [];

        public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }
    }
}
