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
    public void RoomCompletionEnqueuesPersistenceExactlyOnce()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(timeProvider: time, completionSink: sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(3));

        manager.Finish(room.RoomId, first, "Text", 0, 0);
        manager.Finish(room.RoomId, second, "Text", 0, 0);
        manager.Finish(room.RoomId, first, "Text", 0, 0);
        manager.Finish(room.RoomId, second, "Text", 0, 0);

        var record = Assert.Single(sink.Records);
        Assert.Equal(room.RoomId, record.Id);
        Assert.Equal(1, record.RoundNumber);
        Assert.Equal(2, record.Participants.Count);
    }

    [Fact]
    public void ShutdownAbortEnqueuesServerAbortWithoutRatingResult()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(timeProvider: time, completionSink: sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(LiveRoomPhase.Running, manager.Snapshot(room.RoomId).Phase);

        var aborted = manager.AbortActiveRooms();

        Assert.Equal(1, aborted);
        var record = Assert.Single(sink.Records);
        Assert.All(record.Participants, participant => Assert.Equal(ParticipantStatus.AbortedByServer, participant.Status));
        Assert.Equal(LiveRoomPhase.Aborted, manager.Snapshot(room.RoomId).Phase);
    }

    [Fact]
    public void ShutdownAbortSkipsLobbyRooms()
    {
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(completionSink: sink);
        var first = Guid.CreateVersion7();
        manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        Assert.Equal(0, manager.AbortActiveRooms());
        Assert.Empty(sink.Records);
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
    public void CreateRoomUsesSingleRoundContract()
    {
        var manager = CreateManager();
        var creator = Guid.CreateVersion7();

        var room = manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        Assert.Equal(1, room.RoundCount);
        Assert.Equal(1, room.CurrentRound);
        Assert.Equal(LiveRoomPhase.Lobby, room.Phase);
    }

    [Fact]
    public void CreateRoomRejectsSeriesUntilRoundFlowExists()
    {
        var manager = CreateManager();
        var creator = Guid.CreateVersion7();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 3, 8)));

        Assert.Contains("Arena-Serien", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public void ProgressDeltaPathDoesNotReturnFullSnapshot()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Schlüssel", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));

        var result = manager.SubmitProgressDelta(room.RoomId, first, 1, "Schl");

        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Delta);
        Assert.Equal(room.RoomId, result.Delta.RoomId);
        Assert.Equal(first, result.Delta.ParticipantId);
        Assert.Equal(4, result.Delta.CorrectCharacters);
        Assert.Equal(100, result.Delta.Accuracy);
        Assert.Equal(1, result.Delta.RankHint);
    }

    [Fact]
    public void PresenceKeepsParticipantConnectedWhileSecondTabIsActive()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(timeProvider: time);
        var presence = CreatePresence(timeProvider: time);
        var profileId = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(profileId, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        presence.EnterRoom(profileId, "tab-1", room.RoomId);
        presence.EnterRoom(profileId, "tab-2", room.RoomId);

        var firstLeave = presence.RemoveConnection("tab-1");
        if (firstLeave is { RoomLostLastConnection: true })
        {
            manager.Disconnect(firstLeave.RoomId, firstLeave.ProfileId);
        }

        var afterFirstLeave = manager.Snapshot(room.RoomId);
        Assert.Equal(ParticipantStatus.Joined, afterFirstLeave.Participants.Single().Status);

        var secondLeave = presence.RemoveConnection("tab-2");
        if (secondLeave is { RoomLostLastConnection: true })
        {
            manager.Disconnect(secondLeave.RoomId, secondLeave.ProfileId);
        }

        Assert.Equal(ParticipantStatus.Disconnected, manager.Snapshot(room.RoomId).Participants.Single().Status);
    }

    [Fact]
    public void PresenceEnforcesConnectionLimit()
    {
        var presence = CreatePresence(new LiveOptions { MaxConnectionsPerUser = 1 });
        var profileId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        presence.EnterRoom(profileId, "tab-1", roomId);

        var error = Assert.Throws<InvalidOperationException>(() => presence.EnsureCanConnect(profileId, "tab-2"));

        Assert.Contains("maximal 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresenceRoomSwitchLeavesPreviousRoomWhenLastConnectionMoves()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(timeProvider: time);
        var presence = CreatePresence(timeProvider: time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var roomA = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum A", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        var roomB = manager.CreateRoom(new CreateLiveRoomRequest(second, "B", "Raum B", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        presence.EnterRoom(first, "tab-1", roomA.RoomId);
        manager.Join(roomB.RoomId, first, "A");

        var roomSwitch = presence.EnterRoom(first, "tab-1", roomB.RoomId);
        if (roomSwitch is { PreviousRoomId: { } previousRoomId, PreviousRoomLostLastConnection: true })
        {
            manager.Disconnect(previousRoomId, first);
        }

        Assert.Equal(ParticipantStatus.Disconnected, manager.Snapshot(roomA.RoomId).Participants.Single(item => item.ProfileId == first).Status);
        Assert.Equal(ParticipantStatus.Joined, manager.Snapshot(roomB.RoomId).Participants.Single(item => item.ProfileId == first).Status);
    }

    [Fact]
    public void LobbyHostTransfersToOldestActiveParticipantWhenCreatorDisconnects()
    {
        var manager = CreateManager();
        var creator = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.Join(room.RoomId, third, "C");

        var snapshot = manager.Disconnect(room.RoomId, creator);

        Assert.Equal(second, snapshot.CreatorProfileId);
        Assert.Single(snapshot.Participants, item => item.ProfileId == snapshot.CreatorProfileId);
    }

    [Fact]
    public void SweepConvertsExpiredLobbyDisconnectToLeftBeforeStart()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { ReconnectGraceSeconds = 2 }, time);
        var creator = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, second, true);
        manager.Disconnect(room.RoomId, second);

        time.Advance(TimeSpan.FromSeconds(3));
        manager.Sweep();
        var snapshot = manager.Snapshot(room.RoomId);

        Assert.Equal(ParticipantStatus.LeftBeforeStart, snapshot.Participants.Single(item => item.ProfileId == second).Status);
        Assert.False(snapshot.Participants.Single(item => item.ProfileId == second).Ready);
    }

    private static LiveRoomManager CreateManager(LiveOptions? options = null, TimeProvider? timeProvider = null, ILiveRoomCompletionSink? completionSink = null) => new(
        Options.Create(options ?? new LiveOptions()),
        timeProvider ?? TimeProvider.System,
        new TypingEngine(timeProvider ?? TimeProvider.System),
        NullLogger<LiveRoomManager>.Instance,
        completionSink);

    private static LivePresenceTracker CreatePresence(LiveOptions? options = null, TimeProvider? timeProvider = null) => new(
        Options.Create(options ?? new LiveOptions()),
        timeProvider ?? TimeProvider.System);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class RecordingCompletionSink : ILiveRoomCompletionSink
    {
        public List<CompletedRoomRecord> Records { get; } = [];

        public void Enqueue(CompletedRoomRecord record)
        {
            Records.Add(record);
        }
    }
}
