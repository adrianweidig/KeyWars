using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.ConcurrencyTests;

public sealed class LiveRoomConcurrencyTests
{
    [Fact]
    public async Task ConcurrentJoinHonorsCapacity()
    {
        var manager = new LiveRoomManager(Options.Create(new LiveOptions { MaxParticipantsPerRoom = 3 }), TimeProvider.System, NullLogger<LiveRoomManager>.Instance);
        var creator = Guid.CreateVersion7();
        var snapshot = manager.CreateRoom(new CreateLiveRoomRequest(creator, "Ersteller", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.Code, 1, 3));
        var candidates = Enumerable.Range(0, 12).Select(index => (Id: Guid.CreateVersion7(), Name: $"Person {index}")).ToArray();

        await Task.WhenAll(candidates.Select(candidate => Task.Run(() =>
        {
            try
            {
                manager.Join(snapshot.RoomId, candidate.Id, candidate.Name);
            }
            catch (InvalidOperationException)
            {
            }
        })));

        var final = manager.Snapshot(snapshot.RoomId);
        Assert.Equal(3, final.Participants.Count);
    }

    [Fact]
    public void StartIsIdempotentAfterFirstSuccessfulStart()
    {
        var manager = new LiveRoomManager(Options.Create(new LiveOptions()), TimeProvider.System, NullLogger<LiveRoomManager>.Instance);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.Code, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);

        var started = manager.Start(room.RoomId, first);
        var secondStart = manager.Start(room.RoomId, second);

        Assert.True(started.Started);
        Assert.True(secondStart.Started);
        Assert.Equal(2, secondStart.Participants.Count);
    }

    [Fact]
    public void DuplicateFinishDoesNotCreateNewPlacement()
    {
        var manager = new LiveRoomManager(Options.Create(new LiveOptions()), TimeProvider.System, NullLogger<LiveRoomManager>.Instance);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.Code, 1, 8));
        manager.Join(room.RoomId, second, "B");
        var metrics = new TypingMetrics(4, 0, 4, 0, 0, 1000, 48, 48, 240, 100, 100, true);

        manager.Finish(room.RoomId, first, metrics);
        var duplicate = manager.Finish(room.RoomId, first, metrics);

        Assert.Equal(1, duplicate.Participants.Single(item => item.ProfileId == first).Placement);
        Assert.Null(duplicate.Participants.Single(item => item.ProfileId == second).Placement);
    }
}
