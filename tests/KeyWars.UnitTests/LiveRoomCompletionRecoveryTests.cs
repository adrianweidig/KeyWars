using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class LiveRoomCompletionRecoveryTests
{
    [Theory]
    [InlineData(CompletionState.Pending)]
    [InlineData(CompletionState.Failed)]
    public void MissingDurableCompletionIsRequeuedFromFinishedRoomMemento(CompletionState initialState)
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-12T12:00:00Z"));
        var sink = new SequencedCompletionSink(initialState, CompletionState.Pending);
        var options = Options.Create(new LiveOptions { CountdownSeconds = 1 });
        var manager = new LiveRoomManager(
            options,
            time,
            new TypingEngine(time),
            NullLogger<LiveRoomManager>.Instance,
            sink);
        var creator = Guid.CreateVersion7();
        var participant = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            creator,
            "Ersteller",
            "Recovery",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8));
        manager.Join(room.RoomId, participant, "Teilnehmer");
        manager.SetReady(room.RoomId, creator, true);
        manager.SetReady(room.RoomId, participant, true);
        manager.Start(room.RoomId, creator);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Finish(room.RoomId, creator, "Text", 0, 0);

        var finished = manager.Finish(room.RoomId, participant, "Text", 0, 0);
        Assert.Equal(initialState, finished.PersistenceState);

        manager.EnsurePendingPersistenceQueued(room.RoomId);

        Assert.Equal(2, sink.Records.Count);
        Assert.Equal(sink.Records[0].IdempotencyKey, sink.Records[1].IdempotencyKey);
        Assert.Equal(CompletionState.Pending, manager.ExportRoomState(room.RoomId).PersistenceState);
    }

    private sealed class SequencedCompletionSink(params CompletionState[] states) : ILiveRoomCompletionSink
    {
        private readonly Queue<CompletionState> states = new(states);

        public List<CompletedRoomRecord> Records { get; } = [];

        public CompletionReceipt Enqueue(CompletedRoomRecord record)
        {
            Records.Add(record);
            return new CompletionReceipt(record.Id, record.IdempotencyKey, states.Dequeue());
        }

        public CompletionStatusSnapshot GetStatus(Guid roomId) => new(CompletionState.Pending);

        public bool CanAcceptNewRoom(int currentRoomCount) => true;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
