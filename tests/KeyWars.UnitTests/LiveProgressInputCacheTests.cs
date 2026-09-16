using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class LiveProgressInputCacheTests
{
    [Fact]
    public void ProgressInputCacheIsRoomBoundedAndClearsTerminalParticipants()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"));
        var manager = new LiveRoomManager(
            Options.Create(new LiveOptions
            {
                CountdownSeconds = 1,
                MaxConcurrentRooms = 2,
                MaxParticipantsPerRoom = 8
            }),
            time,
            new TypingEngine(time),
            NullLogger<LiveRoomManager>.Instance);

        var rooms = Enumerable.Range(0, 3)
            .Select(_ => CreateRunningRoom(manager, time))
            .ToArray();
        foreach (var room in rooms)
        {
            manager.SubmitProgressDelta(
                room.RoomId,
                room.CreatorProfileId,
                new LiveProgressInputDelta(0, 1, 0, "T", "T"));
        }

        Assert.Equal(2, manager.ProgressInputRoomCount);
        Assert.Equal(2, manager.ProgressInputCount);

        manager.GiveUp(rooms[^1].RoomId, rooms[^1].CreatorProfileId);

        Assert.Equal(1, manager.ProgressInputRoomCount);
        Assert.Equal(1, manager.ProgressInputCount);

        manager.AbortActiveRoom(rooms[^2].RoomId);

        Assert.Equal(0, manager.ProgressInputRoomCount);
        Assert.Equal(0, manager.ProgressInputCount);
    }

    private static LiveRoomSnapshot CreateRunningRoom(LiveRoomManager manager, ManualTimeProvider time)
    {
        var creator = Guid.CreateVersion7();
        var peer = Guid.CreateVersion7();
        var room = manager.CreateRoom(
            new CreateLiveRoomRequest(
                creator,
                "A",
                "Raum",
                "Text",
                LiveRoomMode.Classic,
                LiveRoomVisibility.InternalOpen,
                1,
                8),
            enforceLocalRoomCapacity: false);
        manager.Join(room.RoomId, peer, "B");
        manager.SetReady(room.RoomId, creator, true);
        manager.SetReady(room.RoomId, peer, true);
        manager.Start(room.RoomId, creator);
        time.Advance(TimeSpan.FromSeconds(1));
        return manager.Snapshot(room.RoomId);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
