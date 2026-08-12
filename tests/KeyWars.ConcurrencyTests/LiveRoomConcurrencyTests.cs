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
    public void CreateRoomInitializesLobbySnapshotWithoutTargetText()
    {
        var now = DateTimeOffset.Parse("2026-06-18T12:00:00Z");
        var time = new ManualTimeProvider(now);
        var manager = CreateManager(timeProvider: time);
        var creator = Guid.CreateVersion7();

        var room = manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Ärger", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        Assert.Equal(LiveRoomPhase.Lobby, room.Phase);
        Assert.False(room.Started);
        Assert.False(room.Finished);
        Assert.Equal(1, room.RoundCount);
        Assert.Equal(1, room.CurrentRound);
        Assert.Equal(1, room.RoundVersion);
        Assert.Equal("", room.TargetText);
        Assert.Equal(5, room.TargetCharacterCount);
        Assert.Equal(now, room.PhaseChangedAt);
        Assert.Null(room.CountdownStartsAt);
        Assert.Null(room.RaceStartsAt);
        Assert.Null(room.StartedAt);
        Assert.Null(room.FinishedAt);

        var participant = Assert.Single(room.Participants);
        Assert.Equal(creator, participant.ProfileId);
        Assert.Equal(ParticipantStatus.Joined, participant.Status);
        Assert.False(participant.Ready);
    }

    [Fact]
    public async Task ConcurrentStartsShareSingleCountdownTransition()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 3 }, time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);

        var starts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => manager.Start(room.RoomId, first))));

        var raceStart = Assert.Single(starts.Select(item => item.RaceStartsAt).Distinct());
        var countdownStart = Assert.Single(starts.Select(item => item.CountdownStartsAt).Distinct());
        Assert.NotNull(raceStart);
        Assert.NotNull(countdownStart);
        Assert.All(starts, snapshot => Assert.Equal(LiveRoomPhase.Countdown, snapshot.Phase));
        Assert.All(starts, snapshot => Assert.False(snapshot.Started));

        var final = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.Countdown, final.Phase);
        Assert.Equal(2, final.RoundVersion);
        Assert.Equal(raceStart, final.RaceStartsAt);
        Assert.Equal(countdownStart, final.CountdownStartsAt);
    }

    [Fact]
    public async Task ConcurrentSnapshotsAdvanceCountdownToRunningOnce()
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

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => manager.Snapshot(room.RoomId))));

        Assert.All(snapshots, snapshot => Assert.Equal(LiveRoomPhase.Running, snapshot.Phase));
        Assert.All(snapshots, snapshot => Assert.True(snapshot.Started));
        var final = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.Running, final.Phase);
        Assert.Equal(3, final.RoundVersion);
        Assert.Equal(final.RaceStartsAt, final.StartedAt);
        Assert.All(final.Participants, participant => Assert.Equal(ParticipantStatus.Running, participant.Status));
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
        var final = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.SeriesResults, final.Phase);
        Assert.True(final.Finished);
        Assert.NotNull(final.FinishedAt);
        Assert.Equal(CompletionState.Pending, final.PersistenceState);

        sink.States[room.RoomId] = CompletionState.Persisted;
        Assert.Equal(CompletionState.Persisted, manager.Snapshot(room.RoomId).PersistenceState);
    }

    [Fact]
    public void ParticipantWhoLeftBeforeStartDoesNotBlockOrEnterCompletion()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1, ReconnectGraceSeconds = 0 }, time, sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var departed = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.Join(room.RoomId, departed, "C");
        manager.Disconnect(room.RoomId, departed);
        manager.Snapshot(room.RoomId);
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));

        manager.Finish(room.RoomId, first, "Text", 0, 0);
        var completed = manager.Finish(room.RoomId, second, "Text", 0, 0);

        Assert.True(completed.Finished);
        var record = Assert.Single(sink.Records);
        Assert.Equal(
            new[] { first, second }.Order().ToArray(),
            record.Participants.Select(item => item.UserProfileId).Order().ToArray());
        Assert.DoesNotContain(record.Participants, item => item.UserProfileId == departed);
    }

    [Fact]
    public void PrivacyRemovalExcludesProfileFromFutureCompletionAndReadyTransition()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time, sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Snapshot(room.RoomId);

        manager.RemoveProfile(first);
        var completed = manager.Finish(room.RoomId, second, "Text", 0, 0);

        Assert.True(completed.Finished);
        var record = Assert.Single(sink.Records);
        Assert.DoesNotContain(record.Participants, item => item.UserProfileId == first);
        Assert.Single(record.Participants, item => item.UserProfileId == second);
    }

    [Fact]
    public void RemovedLobbyProfileCannotBecomeReadyAgain()
    {
        var manager = CreateManager();
        var profileId = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(profileId, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        manager.RemoveProfile(profileId);

        Assert.Throws<InvalidOperationException>(() => manager.SetReady(room.RoomId, profileId, true));
        Assert.Equal(ParticipantStatus.LeftBeforeStart, manager.Snapshot(room.RoomId).Participants.Single().Status);
    }

    [Fact]
    public void PrivacyRemovalDoesNotExcludeProfileFromRoomsItNeverJoined()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time, sink);
        var host = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(host, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));

        manager.RemoveProfile(profileId);
        manager.Join(room.RoomId, profileId, "B");
        manager.SetReady(room.RoomId, host, true);
        manager.SetReady(room.RoomId, profileId, true);
        manager.Start(room.RoomId, host);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Finish(room.RoomId, host, "Text", 0, 0);
        manager.Finish(room.RoomId, profileId, "Text", 0, 0);

        Assert.Contains(
            Assert.Single(sink.Records).Participants,
            participant => participant.UserProfileId == profileId);
    }

    [Fact]
    public void RemovingEveryRunningParticipantAbortsRoomWithoutPersistence()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time, sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Snapshot(room.RoomId);

        manager.RemoveProfile(first);
        manager.RemoveProfile(second);
        var aborted = manager.Snapshot(room.RoomId);

        Assert.True(aborted.Finished);
        Assert.Equal(LiveRoomPhase.Aborted, aborted.Phase);
        Assert.Equal(CompletionState.AbortedUnconfirmed, aborted.PersistenceState);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public void FailedEnqueueNeverClaimsPendingPersistence()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink { EnqueueState = CompletionState.Failed };
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
        var final = manager.Finish(room.RoomId, second, "Text", 0, 0);

        Assert.Equal(CompletionState.Failed, final.PersistenceState);
        Assert.NotEqual(CompletionState.Pending, manager.Snapshot(room.RoomId).PersistenceState);
        Assert.Single(sink.Records);
    }

    [Fact]
    public void SnapshotDuringEnqueueReportsPendingInsteadOfAbortedUnconfirmed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        LiveRoomManager? manager = null;
        CompletionState? observed = null;
        var sink = new RecordingCompletionSink
        {
            OnEnqueue = record => observed = manager!.Snapshot(record.Id).PersistenceState
        };
        manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time, sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Finish(room.RoomId, first, "Text", 0, 0);

        manager.Finish(room.RoomId, second, "Text", 0, 0);

        Assert.Equal(CompletionState.Pending, observed);
    }

    [Fact]
    public void CreateRoomFailsClosedWhenCompletionSinkHasNoCapacity()
    {
        var sink = new RecordingCompletionSink { AcceptNewRooms = false };
        var manager = CreateManager(completionSink: sink);

        var exception = Assert.Throws<InvalidOperationException>(() => manager.CreateRoom(new CreateLiveRoomRequest(
            Guid.CreateVersion7(),
            "A",
            "Raum",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8)));

        Assert.Contains("Ergebnispersistenz", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetainedCompletedRoomDoesNotConsumeConcurrentRoomCapacity()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(
            new LiveOptions
            {
                CountdownSeconds = 1,
                MaxConcurrentRooms = 1,
                CompletedRoomRetentionMinutes = 60
            },
            time,
            sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var firstRoom = manager.CreateRoom(CreateRequest(first, "Erster Raum"));
        manager.Join(firstRoom.RoomId, second, "B");
        manager.SetReady(firstRoom.RoomId, first, true);
        manager.SetReady(firstRoom.RoomId, second, true);
        manager.Start(firstRoom.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Finish(firstRoom.RoomId, first, "Text", 0, 0);
        manager.Finish(firstRoom.RoomId, second, "Text", 0, 0);

        var retained = manager.Snapshot(firstRoom.RoomId);
        var secondRoom = manager.CreateRoom(CreateRequest(Guid.CreateVersion7(), "Zweiter Raum"));

        Assert.True(retained.Finished);
        Assert.NotEqual(firstRoom.RoomId, secondRoom.RoomId);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            manager.CreateRoom(CreateRequest(Guid.CreateVersion7(), "Dritter Raum")));
        Assert.Contains("maximale Anzahl", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([0, 0, 1], sink.ObservedRoomCounts);

        static CreateLiveRoomRequest CreateRequest(Guid creator, string title) => new(
            creator,
            title,
            title,
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8);
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
        Assert.Equal(0, manager.AbortActiveRooms());
        Assert.Single(sink.Records);
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
    public void ClassicRoomRejectsMultipleRounds()
    {
        var manager = CreateManager();
        var creator = Guid.CreateVersion7();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            manager.CreateRoom(new CreateLiveRoomRequest(creator, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 3, 8)));

        Assert.Contains("genau eine Runde", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesRaceCarriesPointsAcrossRoundsAndPersistsOnlyFinalStanding()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z"));
        var sink = new RecordingCompletionSink();
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time, sink);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Serie", "Text", LiveRoomMode.Series, LiveRoomVisibility.InternalOpen, 3, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);

        CompleteRound(first, second);
        var firstResult = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.RoundResults, firstResult.Phase);
        Assert.Equal(2, firstResult.Participants.Single(item => item.ProfileId == first).SeriesPoints);
        Assert.Equal(1, firstResult.Participants.Single(item => item.ProfileId == second).SeriesPoints);
        Assert.Empty(sink.Records);

        CompleteRound(second, first);
        var secondResult = manager.Snapshot(room.RoomId);
        Assert.Equal(2, secondResult.CurrentRound);
        Assert.Equal(3, secondResult.Participants.Single(item => item.ProfileId == first).SeriesPoints);
        Assert.Equal(3, secondResult.Participants.Single(item => item.ProfileId == second).SeriesPoints);
        Assert.Empty(sink.Records);

        CompleteRound(first, second);
        var final = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.SeriesResults, final.Phase);
        Assert.True(final.Finished);
        Assert.Equal(3, final.CurrentRound);
        Assert.Equal(1, final.Participants.Single(item => item.ProfileId == first).Placement);
        Assert.Equal(2, final.Participants.Single(item => item.ProfileId == second).Placement);
        var persisted = Assert.Single(sink.Records);
        Assert.Equal(3, persisted.RoundNumber);
        Assert.Equal(7_000, persisted.Participants.Single(item => item.UserProfileId == first).DurationMilliseconds);
        Assert.Equal(21.33, persisted.Participants.Single(item => item.UserProfileId == first).Wpm, 2);

        void CompleteRound(Guid winner, Guid runnerUp)
        {
            manager.Start(room.RoomId, first);
            time.Advance(TimeSpan.FromSeconds(1));
            manager.Snapshot(room.RoomId);
            time.Advance(TimeSpan.FromSeconds(2));
            manager.Finish(room.RoomId, winner, "Text", 0, 0);
            time.Advance(TimeSpan.FromSeconds(1));
            manager.Finish(room.RoomId, runnerUp, "Text", 0, 0);
        }
    }

    [Fact]
    public void TeamRaceBalancesParticipantsAndRanksCombinedPoints()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time);
        var players = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(players[0], "A", "Teams", "Text", LiveRoomMode.Team, LiveRoomVisibility.InternalOpen, 1, 8));
        for (var index = 1; index < players.Length; index++)
        {
            manager.Join(room.RoomId, players[index], ((char)('A' + index)).ToString());
        }

        var lobby = manager.Snapshot(room.RoomId);
        Assert.Equal([1, 2, 1, 2], players.Select(id => lobby.Participants.Single(item => item.ProfileId == id).TeamNumber).ToArray());
        foreach (var player in players)
        {
            manager.SetReady(room.RoomId, player, true);
        }

        manager.Start(room.RoomId, players[0]);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Snapshot(room.RoomId);
        foreach (var player in players)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            manager.Finish(room.RoomId, player, "Text", 0, 0);
        }

        var final = manager.Snapshot(room.RoomId);
        Assert.Equal(LiveRoomPhase.SeriesResults, final.Phase);
        var teams = Assert.IsAssignableFrom<IReadOnlyList<LiveTeamSnapshot>>(final.Teams);
        Assert.Equal(6, teams.Single(item => item.TeamNumber == 1).Points);
        Assert.Equal(4, teams.Single(item => item.TeamNumber == 2).Points);
        Assert.Equal(1, teams.Single(item => item.TeamNumber == 1).Placement);
        Assert.All(final.Participants.Where(item => item.TeamNumber == 1), item => Assert.Equal(1, item.Placement));
        Assert.All(final.Participants.Where(item => item.TeamNumber == 2), item => Assert.Equal(2, item.Placement));
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
        Assert.Equal("", beforeStart.Participants.Single(item => item.ProfileId == first).TypedTextPreview);
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
        Assert.Equal("cc", corrected.Participants.Single(item => item.ProfileId == first).TypedTextPreview);
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
        Assert.Equal(4, result.Delta.TypedCharacters);
        Assert.Equal("Dw==", result.Delta.TypedStateBits);
        Assert.Equal(100, result.Delta.Accuracy);
        Assert.Null(result.Delta.RankHint);
    }

    [Fact]
    public void ProgressPreviewMarksWrongTargetPositions()
    {
        var (manager, room, first, _, _) = CreateRunningRoom();

        var result = manager.SubmitProgressDelta(room.RoomId, first, 1, "Tert");

        Assert.NotNull(result.Delta);
        Assert.Equal(2, result.Delta.CorrectCharacters);
        Assert.Equal(4, result.Delta.TypedCharacters);
        Assert.Equal("Cw==", result.Delta.TypedStateBits);
    }

    [Fact]
    public void FinishWithWrongLastGraphemeKeepsParticipantRunning()
    {
        var (manager, room, first, _, _) = CreateRunningRoom();

        var ex = Assert.Throws<InvalidOperationException>(() => manager.Finish(room.RoomId, first, "Texx", 0, 0));
        var snapshot = manager.Snapshot(room.RoomId);
        var participant = snapshot.Participants.Single(item => item.ProfileId == first);

        Assert.Contains("fehlerfrei", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LiveRoomPhase.Running, snapshot.Phase);
        Assert.Equal(ParticipantStatus.Running, participant.Status);
        Assert.Null(participant.Placement);
    }

    [Fact]
    public void ExplicitGiveUpMarksParticipantDnf()
    {
        var (manager, room, first, _, _) = CreateRunningRoom();

        var snapshot = manager.GiveUp(room.RoomId, first);
        var participant = snapshot.Participants.Single(item => item.ProfileId == first);

        Assert.Equal(ParticipantStatus.Dnf, participant.Status);
        Assert.NotNull(participant.Placement);
        Assert.Equal(LiveRoomPhase.Running, snapshot.Phase);
    }

    [Fact]
    public void SubmitProgressNormalizesCombiningGraphemeBeforeCountingPrefix()
    {
        var (manager, room, first, _, _) = CreateRunningRoom("Ärger");

        var snapshot = manager.SubmitProgress(room.RoomId, first, 1, "A\u0308");
        var participant = snapshot.Participants.Single(item => item.ProfileId == first);

        Assert.Equal(1, participant.CorrectCharacters);
        Assert.Equal(100, participant.Accuracy);
        Assert.Equal("c", participant.TypedTextPreview);
    }

    [Fact]
    public void SubmitProgressRejectsOversizedInputWithoutAdvancingSequence()
    {
        var (manager, room, first, _, _) = CreateRunningRoom();
        manager.SubmitProgress(room.RoomId, first, 1, "Te");

        Assert.Throws<InvalidOperationException>(() => manager.SubmitProgress(room.RoomId, first, 2, new string('x', 40)));
        var participant = manager.Snapshot(room.RoomId).Participants.Single(item => item.ProfileId == first);

        Assert.Equal(1, participant.Sequence);
        Assert.Equal(2, participant.CorrectCharacters);
        Assert.Equal("cc", participant.TypedTextPreview);
    }

    [Fact]
    public void OlderProgressSequenceDoesNotOverwriteCurrentProgress()
    {
        var (manager, room, first, _, _) = CreateRunningRoom();

        manager.SubmitProgress(room.RoomId, first, 2, "Text");
        var snapshot = manager.SubmitProgress(room.RoomId, first, 1, "T");
        var participant = snapshot.Participants.Single(item => item.ProfileId == first);

        Assert.Equal(2, participant.Sequence);
        Assert.Equal(4, participant.CorrectCharacters);
        Assert.Equal("cccc", participant.TypedTextPreview);
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
    public void RoundResultsHostTransfersAndNewHostStartsNextSeriesRound()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time);
        var creator = Guid.CreateVersion7();
        var successor = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            creator,
            "A",
            "Serie",
            "Text",
            LiveRoomMode.Series,
            LiveRoomVisibility.InternalOpen,
            3,
            8));
        manager.Join(room.RoomId, successor, "B");
        manager.SetReady(room.RoomId, creator, true);
        manager.SetReady(room.RoomId, successor, true);
        manager.Start(room.RoomId, creator);
        time.Advance(TimeSpan.FromSeconds(1));

        manager.Finish(room.RoomId, creator, "Text", 0, 0);
        manager.Disconnect(room.RoomId, creator);
        var betweenRounds = manager.Finish(room.RoomId, successor, "Text", 0, 0);

        Assert.Equal(LiveRoomPhase.RoundResults, betweenRounds.Phase);
        Assert.Equal(successor, betweenRounds.CreatorProfileId);
        Assert.Throws<InvalidOperationException>(() => manager.Start(room.RoomId, creator));

        var nextRound = manager.Start(room.RoomId, successor);
        Assert.Equal(LiveRoomPhase.Countdown, nextRound.Phase);
        Assert.Equal(2, nextRound.CurrentRound);
    }

    [Fact]
    public void SweepPublishesRunningDisconnectTransitionWithoutSnapshotSideEffect()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(
            new LiveOptions { CountdownSeconds = 1, ReconnectGraceSeconds = 2 },
            time);
        var creator = Guid.CreateVersion7();
        var successor = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            creator,
            "A",
            "Serie",
            "Text",
            LiveRoomMode.Series,
            LiveRoomVisibility.InternalOpen,
            3,
            8));
        manager.Join(room.RoomId, successor, "B");
        manager.SetReady(room.RoomId, creator, true);
        manager.SetReady(room.RoomId, successor, true);
        manager.Start(room.RoomId, creator);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Disconnect(room.RoomId, creator);
        manager.Finish(room.RoomId, successor, "Text", 0, 0);

        time.Advance(TimeSpan.FromSeconds(3));
        var snapshot = Assert.Single(manager.Sweep());

        Assert.Equal(room.RoomId, snapshot.RoomId);
        Assert.Equal(LiveRoomPhase.RoundResults, snapshot.Phase);
        Assert.Equal(successor, snapshot.CreatorProfileId);
        Assert.Equal(
            ParticipantStatus.Dnf,
            snapshot.Participants.Single(participant => participant.ProfileId == creator).Status);
    }

    [Fact]
    public async Task SweepServiceBroadcastsEachChangedRoomOnce()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { ReconnectGraceSeconds = 2 }, time);
        var sender = new RecordingRoomUpdateSender();
        var service = new LiveRoomSweepService(
            new LocalLiveRoomDispatcher(manager),
            sender,
            time,
            NullLogger<LiveRoomSweepService>.Instance);
        var creator = Guid.CreateVersion7();
        var departed = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            creator,
            "A",
            "Raum",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8));
        manager.Join(room.RoomId, departed, "B");
        manager.Disconnect(room.RoomId, departed);
        time.Advance(TimeSpan.FromSeconds(3));

        await service.SweepOnceAsync(CancellationToken.None);

        var update = Assert.Single(sender.Snapshots);
        Assert.Equal(room.RoomId, update.RoomId);
        Assert.Equal(
            ParticipantStatus.LeftBeforeStart,
            update.Participants.Single(participant => participant.ProfileId == departed).Status);
    }

    [Fact]
    public async Task SweepRemovesExpiredRoomProgressBuffer()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var options = new LiveOptions
        {
            CountdownSeconds = 1,
            CompletedRoomRetentionMinutes = 5,
            ProgressBroadcastHz = 1
        };
        var broadcaster = new LiveProgressBroadcaster(
            new NoOpProgressSender(),
            Options.Create(options),
            time,
            NullLogger<LiveProgressBroadcaster>.Instance);
        var manager = CreateManager(options, time, progressBroadcaster: broadcaster);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", "Text", LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));
        await broadcaster.PublishAsync(new LiveProgressDelta(
            room.RoomId,
            3,
            4,
            first,
            1,
            1,
            1,
            "AQ==",
            1,
            100,
            1), CancellationToken.None);
        manager.Finish(room.RoomId, first, "Text", 0, 0);
        manager.Finish(room.RoomId, second, "Text", 0, 0);

        Assert.Equal(1, broadcaster.Snapshot().ActiveRooms);
        Assert.Equal(1, broadcaster.Snapshot().BroadcastCount);
        time.Advance(TimeSpan.FromMinutes(5));
        manager.Sweep();

        var metrics = broadcaster.Snapshot();
        Assert.Equal(0, metrics.ActiveRooms);
        Assert.Equal(0, metrics.PendingProgressMessages);
        Assert.Equal(1, metrics.BroadcastCount);
        Assert.Throws<InvalidOperationException>(() => manager.Snapshot(room.RoomId));
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
        var snapshot = Assert.Single(manager.Sweep());

        Assert.Equal(ParticipantStatus.LeftBeforeStart, snapshot.Participants.Single(item => item.ProfileId == second).Status);
        Assert.False(snapshot.Participants.Single(item => item.ProfileId == second).Ready);
    }

    [Fact]
    public void StateVersionAdvancesForMutationsAndProgressCarriesParticipantSequence()
    {
        var (manager, room, first, _, _) = CreateRunningRoom();
        var before = manager.Snapshot(room.RoomId);

        var progress = manager.SubmitProgressDelta(room.RoomId, first, 8, "Te");
        var delta = Assert.IsType<LiveProgressDelta>(progress.Delta);
        var after = manager.Snapshot(room.RoomId);

        Assert.True(after.StateVersion > before.StateVersion);
        Assert.Equal(after.StateVersion, delta.StateVersion);
        Assert.Equal(8, delta.ParticipantSequence);
        Assert.Equal(2, delta.TypedCharacters);
        Assert.Equal("Aw==", delta.TypedStateBits);

        var stale = manager.SubmitProgressDelta(room.RoomId, first, 7, "T");
        Assert.Null(stale.Delta);
        Assert.Equal(after.StateVersion, manager.Snapshot(room.RoomId).StateVersion);
    }

    [Fact]
    public void InvitationLockKickAndRejoinRulesAreServerAuthoritative()
    {
        var manager = CreateManager();
        var host = Guid.CreateVersion7();
        var invited = Guid.CreateVersion7();
        var outsider = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            host,
            "Host",
            "Privat",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InvitationOnly,
            1,
            4,
            [new LiveRoomInvitation(invited, "Gast")]));

        Assert.Empty(manager.ListLobbySummaries(outsider).Items);
        var summary = Assert.Single(manager.ListLobbySummaries(invited).Items);
        Assert.Equal("Host", summary.CreatorDisplayName);
        Assert.Equal(1, summary.RoundCount);
        Assert.Equal(1, summary.CurrentRound);
        Assert.Equal(1, summary.ParticipantCount);
        Assert.Throws<InvalidOperationException>(() => manager.Join(room.RoomId, outsider, "Fremd"));

        var locked = manager.SetLobbyLocked(room.RoomId, host, true);
        Assert.True(locked.LobbyLocked);
        Assert.Throws<InvalidOperationException>(() => manager.Join(room.RoomId, invited, "Gast"));

        manager.SetLobbyLocked(room.RoomId, host, false);
        var joined = manager.Join(room.RoomId, invited, "Gast");
        Assert.Equal(ParticipantStatus.Joined, joined.Participants.Single(item => item.ProfileId == invited).Status);

        manager.SetLobbyLocked(room.RoomId, host, true);
        var rejoined = manager.Join(room.RoomId, invited, "Gast");
        Assert.Equal(ParticipantStatus.Joined, rejoined.Participants.Single(item => item.ProfileId == invited).Status);

        var kicked = manager.Kick(room.RoomId, host, invited);
        Assert.Equal(ParticipantStatus.LeftBeforeStart, kicked.Participants.Single(item => item.ProfileId == invited).Status);
        Assert.Throws<InvalidOperationException>(() => manager.Join(room.RoomId, invited, "Gast"));
    }

    [Fact]
    public void ExplicitHostTransferAndCloseAreIdempotent()
    {
        var manager = CreateManager();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(
            first,
            "A",
            "Raum",
            "Text",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            8));
        manager.Join(room.RoomId, second, "B");

        var transferred = manager.TransferHost(room.RoomId, first, second);
        var repeated = manager.TransferHost(room.RoomId, second, second);
        Assert.Equal(second, transferred.CreatorProfileId);
        Assert.Equal(transferred.StateVersion, repeated.StateVersion);

        var closed = manager.Close(room.RoomId, second);
        var closedAgain = manager.Close(room.RoomId, second);
        Assert.Equal(LiveRoomPhase.Closed, closed.Phase);
        Assert.True(closed.Finished);
        Assert.Equal(closed.StateVersion, closedAgain.StateVersion);
        Assert.Throws<InvalidOperationException>(() => manager.Join(room.RoomId, Guid.CreateVersion7(), "C"));
    }

    [Fact]
    public async Task PresenceRollbackRestoresPreviousRoomAndNoOpTransitionIsSafe()
    {
        var presence = CreatePresence();
        var profileId = Guid.CreateVersion7();
        var previousRoomId = Guid.CreateVersion7();
        var rejectedRoomId = Guid.CreateVersion7();
        await presence.EnterRoomAsync(profileId, "tab", previousRoomId);

        var transition = await presence.EnterRoomAsync(profileId, "tab", rejectedRoomId);
        Assert.True(transition.Changed);
        await presence.RollbackEnterRoomAsync(profileId, "tab", rejectedRoomId, transition);
        Assert.Equal(1, await presence.CountRoomConnectionsAsync(profileId, previousRoomId));
        Assert.Equal(0, await presence.CountRoomConnectionsAsync(profileId, rejectedRoomId));

        var unchanged = await presence.EnterRoomAsync(profileId, "tab", previousRoomId);
        Assert.False(unchanged.Changed);
        await presence.RollbackEnterRoomAsync(profileId, "tab", previousRoomId, unchanged);
        Assert.Equal(1, await presence.CountRoomConnectionsAsync(profileId, previousRoomId));
    }

    [Fact]
    public void RoomMementoRoundTripPreservesVersionedAuthorityAndRejectsStaleImport()
    {
        var source = CreateManager();
        var host = Guid.CreateVersion7();
        var guest = Guid.CreateVersion7();
        var room = source.CreateRoom(new CreateLiveRoomRequest(
            host,
            "A",
            "Verteilt",
            "Schlüssel",
            LiveRoomMode.Series,
            LiveRoomVisibility.InvitationOnly,
            3,
            8,
            [new LiveRoomInvitation(guest, "B")]));
        source.Join(room.RoomId, guest, "B");
        source.SetReady(room.RoomId, guest, true);
        var memento = source.ExportRoomState(room.RoomId);

        var replica = CreateManager();
        Assert.True(replica.ImportRoomState(memento));
        var restored = replica.Snapshot(room.RoomId);
        Assert.Equal(memento.StateVersion, restored.StateVersion);
        Assert.Equal(room.Code, restored.Code);
        Assert.Equal(ParticipantStatus.Ready, restored.Participants.Single(item => item.ProfileId == guest).Status);

        var advanced = replica.SetLobbyLocked(room.RoomId, host, true);
        Assert.False(replica.ImportRoomState(memento));
        Assert.Equal(advanced.StateVersion, replica.Snapshot(room.RoomId).StateVersion);
    }

    private static LiveRoomManager CreateManager(
        LiveOptions? options = null,
        TimeProvider? timeProvider = null,
        ILiveRoomCompletionSink? completionSink = null,
        LiveProgressBroadcaster? progressBroadcaster = null) => new(
        Options.Create(options ?? new LiveOptions()),
        timeProvider ?? TimeProvider.System,
        new TypingEngine(timeProvider ?? TimeProvider.System),
        NullLogger<LiveRoomManager>.Instance,
        completionSink,
        progressBroadcaster);

    private static LivePresenceTracker CreatePresence(LiveOptions? options = null, TimeProvider? timeProvider = null) => new(
        Options.Create(options ?? new LiveOptions()),
        timeProvider ?? TimeProvider.System);

    private static (LiveRoomManager Manager, LiveRoomSnapshot Room, Guid First, Guid Second, ManualTimeProvider Time) CreateRunningRoom(string text = "Text")
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-18T12:00:00Z"));
        var manager = CreateManager(new LiveOptions { CountdownSeconds = 1 }, time);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var room = manager.CreateRoom(new CreateLiveRoomRequest(first, "A", "Raum", text, LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, 8));
        manager.Join(room.RoomId, second, "B");
        manager.SetReady(room.RoomId, first, true);
        manager.SetReady(room.RoomId, second, true);
        manager.Start(room.RoomId, first);
        time.Advance(TimeSpan.FromSeconds(1));
        manager.Snapshot(room.RoomId);
        return (manager, room, first, second, time);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class NoOpProgressSender : ILiveProgressSender
    {
        public Task SendAsync(Guid roomId, LiveProgressBatch batch, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingRoomUpdateSender : ILiveRoomUpdateSender
    {
        public List<LiveRoomSnapshot> Snapshots { get; } = [];

        public Task SendAsync(LiveRoomSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCompletionSink : ILiveRoomCompletionSink
    {
        public List<CompletedRoomRecord> Records { get; } = [];
        public Dictionary<Guid, CompletionState> States { get; } = [];
        public List<int> ObservedRoomCounts { get; } = [];
        public bool AcceptNewRooms { get; set; } = true;
        public CompletionState EnqueueState { get; set; } = CompletionState.Pending;
        public Action<CompletedRoomRecord>? OnEnqueue { get; set; }

        public CompletionReceipt Enqueue(CompletedRoomRecord record)
        {
            OnEnqueue?.Invoke(record);
            Records.Add(record);
            States[record.Id] = EnqueueState;
            return new CompletionReceipt(record.Id, record.IdempotencyKey, EnqueueState);
        }

        public CompletionStatusSnapshot GetStatus(Guid roomId) => new(
            States.GetValueOrDefault(roomId, CompletionState.AbortedUnconfirmed));

        public bool CanAcceptNewRoom(int currentRoomCount)
        {
            ObservedRoomCounts.Add(currentRoomCount);
            return AcceptNewRooms;
        }
    }
}
