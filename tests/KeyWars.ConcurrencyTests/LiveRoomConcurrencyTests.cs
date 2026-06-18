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
        var manager = CreateManager(new LiveOptions { MaxParticipantsPerRoom = 3 });
        var creator = Guid.CreateVersion7();
        var snapshot = manager.CreateRoom(new CreateLiveRoomRequest(creator, "Ersteller", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 3));
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
        var manager = CreateManager();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);

        var started = manager.Start(room.RoomId, first);
        var secondStart = manager.Start(room.RoomId, first);

        Assert.True(started.Started);
        Assert.True(secondStart.Started);
        Assert.Equal(2, secondStart.Participants.Count);
    }

    [Fact]
    public void DuplicateFinishDoesNotCreateNewPlacement()
    {
        var manager = CreateManager();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);

        manager.Finish(room.RoomId, first, "Text", 0, 0);
        var duplicate = manager.Finish(room.RoomId, first, "Text", 0, 0);

        Assert.Equal(1, duplicate.Participants.Single(item => item.ProfileId == first).Placement);
        Assert.Null(duplicate.Participants.Single(item => item.ProfileId == second).Placement);
    }

    [Fact]
    public void ReadyStateSurvivesIdempotentJoin()
    {
        var manager = CreateManager();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, second, true);

        var rejoined = manager.Join(room.RoomId, second, "B Neu");

        var participant = rejoined.Participants.Single(item => item.ProfileId == second);
        Assert.True(participant.Ready);
        Assert.Equal(ParticipantStatus.Ready, participant.Status);
        Assert.Equal("B Neu", participant.DisplayName);
    }

    [Fact]
    public void CodeRoomRejectsDirectJoinForNewParticipant()
    {
        var manager = CreateManager();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.Code, 1, 8));

        Assert.Throws<InvalidOperationException>(() => manager.Join(room.RoomId, second, "B"));

        var joined = manager.JoinByCode(room.Code, second, "B");
        Assert.Contains(joined.Participants, item => item.ProfileId == second);
    }

    [Fact]
    public void OnlyCreatorCanStartRoom()
    {
        var manager = CreateManager();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);

        Assert.Throws<InvalidOperationException>(() => manager.Start(room.RoomId, second));
        Assert.True(manager.Start(room.RoomId, first).Started);
    }

    private static LiveRoomManager CreateManager(LiveOptions? options = null) => new(
        Options.Create(options ?? new LiveOptions()),
        TimeProvider.System,
        new TypingEngine(TimeProvider.System),
        NullLogger<LiveRoomManager>.Instance);
}
