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
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(timeProvider: time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);

        var started = manager.Start(room.RoomId, first);
        var secondStart = manager.Start(room.RoomId, first);

        Assert.Equal(LiveRoomPhase.Countdown, started.Phase);
        Assert.False(started.Started);
        Assert.Equal(started.RaceStartsAt, secondStart.RaceStartsAt);
        Assert.Equal(2, secondStart.Participants.Count);

        time.Advance(TimeSpan.FromSeconds(3));
        var running = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.Running, running.Phase);
        Assert.True(running.Started);
    }

    [Fact]
    public void DuplicateFinishDoesNotCreateNewPlacement()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(timeProvider: time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(3));

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
        Assert.Equal(LiveRoomPhase.Countdown, manager.Start(room.RoomId, first).Phase);
    }

    [Fact]
    public void CreateRoomUsesRequestedRoundCount()
    {
        var manager = CreateManager();
        var creator = Guid.CreateVersion7();

        var room = manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 3, 8));

        Assert.Equal(3, room.RoundCount);
        Assert.Equal(1, room.CurrentRound);
        Assert.Equal(LiveRoomPhase.Lobby, room.Phase);
    }

    [Fact]
    public void ProgressBeforeRaceStartIsIgnored()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 3 }, time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);

        var beforeStart = manager.SubmitProgress(room.RoomId, first, 1, "Text");

        Assert.Equal(LiveRoomPhase.Countdown, beforeStart.Phase);
        Assert.Equal(0, beforeStart.Participants.Single(item => item.ProfileId == first).CorrectCharacters);
    }

    [Fact]
    public void BackspaceCanReduceProgressAfterRaceStart()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));

        manager.SubmitProgress(room.RoomId, first, 1, "Tex");
        var corrected = manager.SubmitProgress(room.RoomId, first, 2, "Te");

        Assert.Equal(LiveRoomPhase.Running, corrected.Phase);
        Assert.Equal(2, corrected.Participants.Single(item => item.ProfileId == first).CorrectCharacters);
    }

    private static LiveRoomManager CreateManager(LiveOptions? options = null, TimeProvider? timeProvider = null) => new(
        Options.Create(options ?? new LiveOptions()),
        timeProvider ?? TimeProvider.System,
        new TypingEngine(timeProvider ?? TimeProvider.System),
        NullLogger<LiveRoomManager>.Instance);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
