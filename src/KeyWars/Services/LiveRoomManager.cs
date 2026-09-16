using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KeyWars.Domain;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed class LiveRoomManager(
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    TypingEngine typingEngine,
    ILogger<LiveRoomManager> logger,
    ILiveRoomCompletionSink? completionSink = null,
    LiveProgressBroadcaster? progressBroadcaster = null,
    ILiveRoomUpdateSender? updateSender = null)
{
    private const int MinimumParticipants = 2;
    private const string RoomCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly object createGate = new();
    private readonly object rematchGate = new();
    private readonly object progressInputRoomsGate = new();
    private readonly ConcurrentDictionary<Guid, LiveRoomState> rooms = new();
    private readonly LinkedList<Guid> localRoomAuditOrder = new();
    private readonly Dictionary<Guid, LinkedListNode<Guid>> localRoomAuditNodes = [];
    private readonly ConcurrentDictionary<string, Guid> roomCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Guid> rematchRooms = new();
    private readonly ConcurrentDictionary<Guid, RoomProgressInputs> progressInputs = new();
    private readonly LinkedList<Guid> progressInputRoomOrder = new();
    private readonly int progressInputRoomCapacity = Math.Clamp(options.Value.MaxConcurrentRooms, 1, 65_536);

    public LiveRoomSnapshot CreateRoom(CreateLiveRoomRequest request, bool enforceLocalRoomCapacity = true) =>
        CreateRoomCore(request, Guid.CreateVersion7(), null, enforceLocalRoomCapacity);

    public LiveRoomSnapshot CreateRematch(Guid sourceRoomId, Guid hostProfileId)
    {
        var template = BuildRematchTemplate(sourceRoomId, hostProfileId);
        lock (rematchGate)
        {
            if (rematchRooms.TryGetValue(sourceRoomId, out var existingRoomId))
            {
                return Snapshot(existingRoomId);
            }

            var roomId = Guid.CreateVersion7();
            var snapshot = CreateRematchRoom(template, roomId);
            if (!rematchRooms.TryAdd(sourceRoomId, roomId))
            {
                throw new InvalidOperationException("Die Arena-Revanche konnte nicht eindeutig registriert werden.");
            }

            return snapshot;
        }
    }

    internal LiveRoomRematchTemplate BuildRematchTemplate(Guid sourceRoomId, Guid hostProfileId)
    {
        var source = GetRoom(sourceRoomId);
        lock (source.Gate)
        {
            RequireHost(source, hostProfileId);
            if (!source.Finished)
            {
                throw new InvalidOperationException("Eine Revanche ist erst nach dem vollständigen Abschluss des Raums möglich.");
            }

            var participants = source.Participants.Values
                .Where(participant => !source.ExcludedProfileIds.Contains(participant.ProfileId))
                .OrderBy(participant => participant.JoinedAt)
                .ThenBy(participant => participant.ProfileId)
                .ToArray();
            var host = participants.SingleOrDefault(participant => participant.ProfileId == hostProfileId)
                ?? throw new InvalidOperationException("Die Raumleitung gehört nicht mehr zur Revanche-Gruppe.");
            var invitations = participants
                .Where(participant => participant.ProfileId != hostProfileId)
                .Select(participant => new LiveRoomInvitation(participant.ProfileId, participant.DisplayName))
                .ToArray();
            var teamAssignments = participants.ToDictionary(
                participant => participant.ProfileId,
                participant => participant.TeamNumber);
            return new LiveRoomRematchTemplate(
                new CreateLiveRoomRequest(
                    hostProfileId,
                    host.DisplayName,
                    BuildRematchTitle(source.Title),
                    source.Text,
                    source.Mode,
                    source.Visibility,
                    source.RoundCount,
                    source.MaxParticipants,
                    invitations,
                    source.TargetCompetitionEligible),
                teamAssignments);
        }
    }

    internal LiveRoomSnapshot CreateRematchRoom(
        LiveRoomRematchTemplate template,
        Guid roomId,
        bool enforceLocalRoomCapacity = true) =>
        CreateRoomCore(template.Request, roomId, template.TeamAssignments, enforceLocalRoomCapacity);

    private LiveRoomSnapshot CreateRoomCore(
        CreateLiveRoomRequest request,
        Guid roomId,
        IReadOnlyDictionary<Guid, int?>? teamAssignments,
        bool enforceLocalRoomCapacity)
    {
        if (roomId == Guid.Empty)
        {
            throw new InvalidOperationException("Ein Arena-Raum benötigt eine eindeutige ID.");
        }

        if (request.Mode is not (LiveRoomMode.Classic or LiveRoomMode.Series or LiveRoomMode.Team))
        {
            throw new InvalidOperationException("Dieser Arena-Modus ist noch nicht freigeschaltet.");
        }

        var normalizedTarget = NormalizeArenaTarget(request.Text);
        var now = timeProvider.GetUtcNow();
        if (enforceLocalRoomCapacity)
        {
            CleanupExpiredRooms(now);
        }
        lock (createGate)
        {
            if (rooms.ContainsKey(roomId))
            {
                throw new InvalidOperationException("Für diese Arena-ID existiert bereits ein Raum.");
            }

            var activeRoomCount = enforceLocalRoomCapacity ? CountRoomsTowardCapacity() : 0;
            if (completionSink is not null && !completionSink.CanAcceptNewRoom(activeRoomCount))
            {
                throw new InvalidOperationException("Die Arena nimmt vorübergehend keine neuen Räume an, weil die Ergebnispersistenz ausgelastet ist.");
            }

            if (enforceLocalRoomCapacity && activeRoomCount >= options.Value.MaxConcurrentRooms)
            {
                throw new InvalidOperationException("Die maximale Anzahl gleichzeitiger Live-Räume ist erreicht.");
            }

            if (enforceLocalRoomCapacity &&
                CountActiveRoomsForCreator(request.CreatorProfileId) >=
                Math.Max(1, options.Value.MaxActiveRoomsPerCreator))
            {
                throw CreatorRoomQuotaExceeded();
            }

            var maxParticipants = Math.Clamp(request.MaxParticipants, MinimumParticipants, options.Value.MaxParticipantsPerRoom);
            var roundCount = ValidateRoundCount(request.Mode, request.RoundCount);
            var room = new LiveRoomState(
                roomId,
                request.CreatorProfileId,
                GenerateUniqueCode(),
                string.IsNullOrWhiteSpace(request.Title) ? "Live-Raum" : request.Title.Trim(),
                normalizedTarget,
                request.Mode,
                request.Visibility,
                roundCount,
                maxParticipants,
                now,
                request.TargetCompetitionEligible);

            room.Participants[request.CreatorProfileId] = new LiveParticipantState(
                request.CreatorProfileId,
                request.CreatorDisplayName,
                ParticipantStatus.Joined,
                now,
                request.Mode == LiveRoomMode.Team
                    ? ResolveTeamNumber(teamAssignments, request.CreatorProfileId, 1)
                    : null);

            var invitations = (request.Invitations ?? [])
                .Where(item => item.ProfileId != Guid.Empty && item.ProfileId != request.CreatorProfileId)
                .GroupBy(item => item.ProfileId)
                .Select(group => group.First())
                .ToArray();
            if (teamAssignments is null && request.Visibility != LiveRoomVisibility.InvitationOnly && invitations.Length > 0)
            {
                throw new InvalidOperationException("Einladungen können nur für Einladungsräume festgelegt werden.");
            }

            if (invitations.Length + 1 > maxParticipants)
            {
                throw new InvalidOperationException("Die Einladungsliste überschreitet die maximale Raumgröße.");
            }

            foreach (var invitation in invitations)
            {
                room.InvitedProfileIds.Add(invitation.ProfileId);
                room.Participants[invitation.ProfileId] = new LiveParticipantState(
                    invitation.ProfileId,
                    string.IsNullOrWhiteSpace(invitation.DisplayName) ? "Eingeladen" : invitation.DisplayName.Trim(),
                    ParticipantStatus.Invited,
                    now,
                    request.Mode == LiveRoomMode.Team
                        ? ResolveTeamNumber(teamAssignments, invitation.ProfileId, NextTeamNumber(room))
                        : null);
            }

            rooms[room.Id] = room;
            roomCodes[room.Code] = room.Id;
            TrackLocalRoom(room.Id);
            return Snapshot(room, now);
        }
    }

    public IReadOnlyList<LiveRoomSnapshot> ListOpenRooms()
    {
        var now = timeProvider.GetUtcNow();
        CleanupExpiredRooms(now);
        return rooms.Values
            .Where(room => room.Visibility == LiveRoomVisibility.InternalOpen && room.Phase == LiveRoomPhase.Lobby)
            .Select(room => Snapshot(room, now))
            .OrderBy(room => room.Title)
            .ToList();
    }

    public LiveRoomLobbyPage ListLobbySummaries(Guid viewerProfileId, int offset = 0, int limit = 20)
    {
        var now = timeProvider.GetUtcNow();
        CleanupExpiredRooms(now);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        var summaries = new List<LiveRoomLobbySummary>();
        foreach (var room in rooms.Values)
        {
            lock (room.Gate)
            {
                if (room.Phase != LiveRoomPhase.Lobby || room.Finished || !CanDiscover(room, viewerProfileId))
                {
                    continue;
                }

                summaries.Add(BuildLobbySummary(room));
            }
        }

        var ordered = summaries
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.RoomId)
            .ToArray();
        return new LiveRoomLobbyPage(ordered.Skip(offset).Take(limit).ToArray(), offset, limit, ordered.Length);
    }

    public LiveRoomMetricsSnapshot MetricsSnapshot()
    {
        var active = 0;
        var open = 0;
        var running = 0;
        var participants = 0;
        foreach (var room in rooms.Values)
        {
            lock (room.Gate)
            {
                if (!room.Finished)
                {
                    active++;
                }

                if (!room.Finished && room.Phase == LiveRoomPhase.Lobby)
                {
                    open++;
                }

                if (!room.Finished && room.Phase is LiveRoomPhase.Countdown or LiveRoomPhase.Running)
                {
                    running++;
                }

                if (!room.Finished)
                {
                    participants += room.Participants.Values.Count(CountsTowardCapacity);
                }
            }
        }

        return new LiveRoomMetricsSnapshot(active, open, running, participants);
    }

    public LiveRoomMemento ExportRoomState(Guid roomId)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            return CreateMemento(room);
        }
    }

    public LiveRoomSnapshot ProjectSnapshot(LiveRoomMemento memento)
    {
        ArgumentNullException.ThrowIfNull(memento);
        var room = RestoreRoom(memento);
        return LiveRoomSnapshotProjector.Create(
            room,
            timeProvider.GetUtcNow(),
            memento.PersistenceState);
    }

    public bool ImportRoomState(LiveRoomMemento memento)
    {
        ArgumentNullException.ThrowIfNull(memento);
        lock (createGate)
        {
            if (rooms.TryGetValue(memento.Id, out var existing))
            {
                lock (existing.Gate)
                {
                    if (existing.StateVersion > memento.StateVersion)
                    {
                        return false;
                    }
                }
            }

            var room = RestoreRoom(memento);
            if (rooms.TryGetValue(memento.Id, out existing) &&
                !StringComparer.OrdinalIgnoreCase.Equals(existing.Code, room.Code))
            {
                RemoveRoomCodeMapping(existing.Code, existing.Id);
            }

            rooms[memento.Id] = room;
            roomCodes[room.Code] = room.Id;
            TrackLocalRoom(room.Id);
            return true;
        }
    }

    public bool RemoveRoomState(Guid roomId) => RemoveRoomState(roomId, notifyDependents: true);

    internal bool UnloadRoomState(Guid roomId) => RemoveRoomState(roomId, notifyDependents: false);

    private bool RemoveRoomState(Guid roomId, bool notifyDependents)
    {
        lock (createGate)
        {
            var removed = rooms.TryRemove(roomId, out var room);
            if (removed)
            {
                RemoveRoomCodeMapping(room!.Code, roomId);
                UntrackLocalRoom(roomId);
            }

            if (notifyDependents)
            {
                RemoveProgressInputs(roomId);
                progressBroadcaster?.RemoveRoom(roomId);
                updateSender?.RemoveRoom(roomId);
            }
            return removed;
        }
    }

    public LiveRoomSnapshot JoinByCode(string code, Guid profileId, string displayName)
    {
        var roomId = ResolveRoomIdByCode(code);
        return JoinByResolvedCode(roomId, code, profileId, displayName);
    }

    public LiveRoomSnapshot JoinByResolvedCode(Guid roomId, string code, Guid profileId, string displayName) =>
        Join(roomId, profileId, displayName, viaCode: true, expectedCode: NormalizeRoomCode(code));

    public Guid ResolveRoomIdByCode(string code)
    {
        var normalizedCode = NormalizeRoomCode(code);
        if (!roomCodes.TryGetValue(normalizedCode, out var roomId) || !rooms.ContainsKey(roomId))
        {
            throw new InvalidOperationException("Der Raumcode ist ungültig.");
        }

        return roomId;
    }

    public static string NormalizeRoomCode(string code)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length != 6 || normalized.Any(character => !RoomCodeAlphabet.Contains(character, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Der Raumcode muss aus genau sechs Zeichen bestehen. Erlaubt sind A-Z ohne I/O sowie 2-9.");
        }

        return normalized;
    }

    public LiveRoomSnapshot Join(Guid roomId, Guid profileId, string displayName) => Join(roomId, profileId, displayName, viaCode: false);

    public LiveRoomSnapshot SetReady(Guid roomId, Guid profileId, bool ready)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            if (room.Phase != LiveRoomPhase.Lobby)
            {
                throw new InvalidOperationException("Der Bereitschaftsstatus kann nur in der Lobby geändert werden.");
            }

            var participant = RequireParticipant(room, profileId);
            if (!IsLobbyActive(participant))
            {
                throw new InvalidOperationException("Dieses Profil nimmt nicht mehr aktiv an der Lobby teil.");
            }

            var nextStatus = ready ? ParticipantStatus.Ready : ParticipantStatus.Joined;
            if (participant.Ready != ready || participant.Status != nextStatus || participant.DisconnectedAt is not null)
            {
                participant.Ready = ready;
                participant.Status = nextStatus;
                participant.DisconnectedAt = null;
                Touch(room);
            }
            snapshot = SnapshotUnlocked(room, now);
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot SetLobbyLocked(Guid roomId, Guid hostProfileId, bool locked)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            AdvancePhase(room, now);
            RequireHost(room, hostProfileId);
            if (room.Phase != LiveRoomPhase.Lobby)
            {
                throw new InvalidOperationException("Die Lobby kann nur vor dem Start gesperrt oder entsperrt werden.");
            }

            if (room.LobbyLocked != locked)
            {
                room.LobbyLocked = locked;
                Touch(room);
            }

            return SnapshotUnlocked(room, now);
        }
    }

    public LiveRoomSnapshot TransferHost(Guid roomId, Guid hostProfileId, Guid nextHostProfileId)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            AdvancePhase(room, now);
            RequireHost(room, hostProfileId);
            if (room.CreatorProfileId == nextHostProfileId)
            {
                return SnapshotUnlocked(room, now);
            }

            if (room.Phase is not (LiveRoomPhase.Lobby or LiveRoomPhase.RoundResults))
            {
                throw new InvalidOperationException("Die Raumleitung kann nur in der Lobby oder zwischen Runden übertragen werden.");
            }

            var nextHost = RequireParticipant(room, nextHostProfileId);
            if (!IsHostCandidate(room, nextHost))
            {
                throw new InvalidOperationException("Die gewählte Person ist aktuell nicht als Raumleitung verfügbar.");
            }

            room.CreatorProfileId = nextHostProfileId;
            Touch(room);
            return SnapshotUnlocked(room, now);
        }
    }

    public LiveRoomSnapshot Kick(Guid roomId, Guid hostProfileId, Guid targetProfileId)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            AdvancePhase(room, now);
            RequireHost(room, hostProfileId);
            if (targetProfileId == room.CreatorProfileId)
            {
                throw new InvalidOperationException("Übertrage zuerst die Raumleitung, bevor du den bisherigen Host entfernst.");
            }

            if (room.Phase != LiveRoomPhase.Lobby)
            {
                throw new InvalidOperationException("Teilnehmende können nur vor dem Rennstart in der Lobby entfernt werden.");
            }

            var participant = RequireParticipant(room, targetProfileId);
            if (room.ExcludedProfileIds.Add(targetProfileId))
            {
                participant.Ready = false;
                participant.DisconnectedAt = now;
                participant.Status = ParticipantStatus.LeftBeforeStart;
                participant.FinishedAt = now;

                Touch(room);
            }

            return SnapshotUnlocked(room, now);
        }
    }

    public LiveRoomSnapshot Close(Guid roomId, Guid hostProfileId)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            AdvancePhase(room, now);
            RequireHost(room, hostProfileId);
            if (room.Phase == LiveRoomPhase.Closed)
            {
                return SnapshotUnlocked(room, now);
            }

            if (room.Started || room.Phase != LiveRoomPhase.Lobby)
            {
                throw new InvalidOperationException("Der Raum kann durch die Raumleitung nur vor dem Rennstart geschlossen werden.");
            }

            if (room.Finished)
            {
                throw new InvalidOperationException("Dieser Raum ist bereits beendet.");
            }

            var activeRound = room.Phase is LiveRoomPhase.Countdown or LiveRoomPhase.Running;
            foreach (var participant in room.Participants.Values)
            {
                if (participant.Status is ParticipantStatus.LeftBeforeStart or ParticipantStatus.Declined or ParticipantStatus.Cancelled)
                {
                    continue;
                }

                participant.Ready = false;
                participant.FinishedAt ??= now;
                participant.Status = activeRound ? ParticipantStatus.AbortedByServer : ParticipantStatus.Cancelled;
                if (activeRound && participant.DurationMilliseconds == 0 && room.RaceStartsAt is { } raceStartsAt)
                {
                    participant.DurationMilliseconds = (int)Math.Round(
                        LiveRoomProgress.NormalizeDuration(now - raceStartsAt).TotalMilliseconds);
                }
            }

            room.Finished = true;
            room.FinishedAt = now;
            room.RoundEndsAt = now;
            room.Phase = LiveRoomPhase.Closed;
            room.PhaseChangedAt = now;
            room.CloseReason = "Der Raum wurde durch die Raumleitung geschlossen.";
            room.RoundVersion++;
            RemoveProgressInputs(room.Id);
            Touch(room);
            if (activeRound)
            {
                room.PersistenceState = CompletionState.Pending;
                completed = BuildPersistenceRecord(room);
            }
            else
            {
                room.PersistenceState = CompletionState.AbortedUnconfirmed;
            }

            snapshot = SnapshotUnlocked(room, now);
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot Start(Guid roomId, Guid profileId)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            ApplyHostDisconnectRule(room);
            TryAbortStrandedSeries(room, now);
            if (profileId != room.CreatorProfileId)
            {
                throw new InvalidOperationException("Nur die Raumleitung darf das Rennen starten.");
            }

            if (room.Phase is LiveRoomPhase.Countdown or LiveRoomPhase.Running or LiveRoomPhase.SeriesResults)
            {
                snapshot = SnapshotUnlocked(room, now);
            }
            else
            {
                if (room.Phase == LiveRoomPhase.RoundResults)
                {
                    PrepareNextRound(room);
                }
                else
                {
                    var startParticipants = room.Participants.Values.Where(IsLobbyActive).ToList();
                    if (startParticipants.Count < MinimumParticipants || startParticipants.Any(item => !item.Ready))
                    {
                        throw new InvalidOperationException("Der Start ist erst möglich, wenn mindestens zwei Personen bereit sind.");
                    }

                    if (room.Mode == LiveRoomMode.Team && room.RatingEligible)
                    {
                        var teamSizes = startParticipants
                            .Where(participant => participant.TeamNumber is 1 or 2)
                            .GroupBy(participant => participant.TeamNumber!.Value)
                            .ToDictionary(group => group.Key, group => group.Count());
                        if (teamSizes.Count != 2 ||
                            teamSizes.GetValueOrDefault(1) == 0 ||
                            teamSizes.GetValueOrDefault(1) != teamSizes.GetValueOrDefault(2) ||
                            teamSizes.Values.Sum() != startParticipants.Count)
                        {
                            throw new InvalidOperationException("Ein gewertetes Teamrennen benötigt zwei gleich große Teams.");
                        }
                    }
                }

                BeginCountdown(room, now);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot SubmitProgress(Guid roomId, Guid profileId, int sequence, string input)
    {
        return SubmitProgressCore(roomId, profileId, sequence, input, includeSnapshot: true).Snapshot
            ?? Snapshot(roomId);
    }

    public LiveRoomSnapshot SubmitProgress(Guid roomId, Guid profileId, LiveProgressInputDelta input) =>
        SubmitProgress(roomId, profileId, input.Revision, LiveRoomProgress.SerializeInputDelta(input));

    public LiveProgressResult SubmitProgressDelta(Guid roomId, Guid profileId, int sequence, string input)
    {
        return SubmitProgressCore(roomId, profileId, sequence, input, includeSnapshot: false);
    }

    public LiveProgressResult SubmitProgressDelta(Guid roomId, Guid profileId, LiveProgressInputDelta input) =>
        SubmitProgressDelta(roomId, profileId, input.Revision, LiveRoomProgress.SerializeInputDelta(input));

    public LiveRoomSnapshot Finish(Guid roomId, Guid profileId, string input, int backspaces, int focusLosses)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            var participant = RequireParticipant(room, profileId);
            if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf)
            {
                snapshot = SnapshotUnlocked(room, now);
            }
            else
            {
                if (room.Phase != LiveRoomPhase.Running || room.RaceStartsAt is null)
                {
                    throw new InvalidOperationException("Dieses Rennen wurde noch nicht gestartet.");
                }

                if (participant.Status != ParticipantStatus.Running)
                {
                    throw new InvalidOperationException("Dieser Zieleinlauf ist für deinen aktuellen Status nicht gültig.");
                }

                var duration = LiveRoomProgress.NormalizeDuration(now - room.RaceStartsAt.Value);
                var normalizedInput = LiveRoomProgress.NormalizeBoundedInput(room, input);
                var metrics = typingEngine.Analyze(room.Text, normalizedInput, duration, backspaces, focusLosses);
                if (!metrics.Completed)
                {
                    throw new InvalidOperationException("Der Zieltext ist noch nicht fehlerfrei abgeschlossen.");
                }

                participant.Status = ParticipantStatus.Finished;
                participant.Ready = true;
                var finalInputElements = TypingEngine.SplitGraphemes(normalizedInput);
                var finalWindow = LiveRoomProgress.BuildTypedStateWindow(
                    room.TargetElements,
                    finalInputElements,
                    metrics.CorrectCharacters);
                participant.TypedTextPreview = finalWindow.Preview;
                participant.TypedStateOffset = finalWindow.Offset;
                participant.TypedCharacters = finalInputElements.Count;
                participant.FinishedAt = now;
                participant.DurationMilliseconds = metrics.DurationMilliseconds;
                participant.Accuracy = metrics.Accuracy;
                participant.Wpm = metrics.Wpm;
                participant.CorrectCharacters = metrics.CorrectCharacters;
                participant.CompetitionEligible &= LiveProgressEvidence.SupportsFinish(
                    room,
                    participant,
                    metrics.CorrectCharacters,
                    now);
                RemoveProgressInput(room.Id, participant.ProfileId, room.RoundVersion);
                Touch(room);
                LiveRoomScoring.ApplyPlacements(room);
                completed ??= TryCompleteRoom(room, now);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot GiveUp(Guid roomId, Guid profileId)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            var participant = RequireParticipant(room, profileId);
            if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf)
            {
                snapshot = SnapshotUnlocked(room, now);
            }
            else
            {
                if (room.Phase != LiveRoomPhase.Running || room.RaceStartsAt is null)
                {
                    throw new InvalidOperationException("Diese Runde läuft aktuell nicht.");
                }

                if (participant.Status != ParticipantStatus.Running)
                {
                    throw new InvalidOperationException("Die Runde kann für deinen aktuellen Status nicht aufgegeben werden.");
                }

                participant.Status = ParticipantStatus.Dnf;
                participant.Ready = true;
                participant.FinishedAt = now;
                participant.DurationMilliseconds = (int)Math.Round(
                    LiveRoomProgress.NormalizeDuration(now - room.RaceStartsAt.Value).TotalMilliseconds);
                RemoveProgressInput(room.Id, participant.ProfileId, room.RoundVersion);
                Touch(room);
                LiveRoomScoring.ApplyPlacements(room);
                completed ??= TryCompleteRoom(room, now);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot Disconnect(Guid roomId, Guid profileId)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            var participant = RequireParticipant(room, profileId);
            if (LiveRoomDisconnectRules.MarkDisconnected(room, participant, now))
            {
                Touch(room);
            }

            ApplyHostDisconnectRule(room);
            TryAbortStrandedSeries(room, now);
            snapshot = SnapshotUnlocked(room, now);
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot Snapshot(Guid roomId)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            snapshot = SnapshotUnlocked(room, now);
        }

        return QueuePersistence(completed, snapshot);
    }

    public LiveRoomSnapshot SnapshotForViewer(Guid roomId, Guid viewerProfileId)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            RequireParticipant(room, viewerProfileId);
            if (room.ExcludedProfileIds.Contains(viewerProfileId))
            {
                throw new InvalidOperationException("Du bist nicht in diesem Raum.");
            }

            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            snapshot = SnapshotUnlocked(room, now);
        }

        return QueuePersistence(completed, snapshot);
    }

    public IReadOnlyList<LiveRoomSnapshot> Sweep()
    {
        var now = timeProvider.GetUtcNow();
        var changedSnapshots = new List<LiveRoomSnapshot>();
        foreach (var room in rooms.Values)
        {
            CompletedRoomRecord? completed;
            LiveRoomSnapshot? changedSnapshot = null;
            lock (room.Gate)
            {
                var previousVersion = room.StateVersion;
                completed = ApplyDisconnectTimeouts(room, now);
                AdvancePhase(room, now);
                if (room.StateVersion != previousVersion)
                {
                    changedSnapshot = SnapshotUnlocked(room, now);
                }
            }

            if (changedSnapshot is not null)
            {
                changedSnapshots.Add(QueuePersistence(completed, changedSnapshot));
            }
            else
            {
                QueuePersistence(completed);
            }
        }

        CleanupExpiredRooms(now);
        return changedSnapshots;
    }

    public void RemoveProfile(Guid profileId)
    {
        foreach (var roomId in rooms.Keys)
        {
            RemoveProfileFromRoom(roomId, profileId);
        }
    }

    public void RemoveProfileFromRoom(Guid roomId, Guid profileId)
    {
        if (!rooms.TryGetValue(roomId, out var room))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        CompletedRoomRecord? completed = null;
        lock (room.Gate)
        {
            var isReferenced = room.CreatorProfileId == profileId ||
                room.QuotaOwnerProfileId == profileId ||
                room.InvitedProfileIds.Contains(profileId) ||
                room.ExcludedProfileIds.Contains(profileId) ||
                room.Participants.ContainsKey(profileId);
            if (!isReferenced)
            {
                return;
            }

            if (room.Started)
            {
                room.RatingEligible = false;
            }

            room.ExcludedProfileIds.Add(profileId);
            if (room.Participants.TryGetValue(profileId, out var participant) &&
                participant.Status is not (ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.LeftBeforeStart))
            {
                if (room.Phase == LiveRoomPhase.Lobby)
                {
                    participant.Status = ParticipantStatus.LeftBeforeStart;
                    participant.Ready = false;
                    participant.FinishedAt = now;
                }
                else
                {
                    participant.Status = ParticipantStatus.Dnf;
                    participant.Ready = false;
                    participant.FinishedAt = now;
                    participant.DurationMilliseconds = room.RaceStartsAt is { } raceStartsAt
                        ? (int)Math.Round(LiveRoomProgress.NormalizeDuration(now - raceStartsAt).TotalMilliseconds)
                        : 0;
                    LiveRoomScoring.ApplyPlacements(room);
                    completed = TryCompleteRoom(room, now);
                }
            }

            RemoveProgressInput(room.Id, profileId, room.RoundVersion);
            room.Participants.Remove(profileId);
            room.InvitedProfileIds.Remove(profileId);
            room.ExcludedProfileIds.Remove(profileId);
            if (room.CreatorProfileId == profileId ||
                (room.CreatorProfileId != Guid.Empty && !room.Participants.ContainsKey(room.CreatorProfileId)))
            {
                var successor = room.Participants.Values
                    .OrderBy(candidate => IsHostCandidate(room, candidate) ? 0 : 1)
                    .ThenBy(candidate => candidate.JoinedAt)
                    .ThenBy(candidate => candidate.ProfileId)
                    .FirstOrDefault();
                room.CreatorProfileId = successor?.ProfileId ?? Guid.Empty;
                room.RoundVersion++;
            }
            if (room.QuotaOwnerProfileId == profileId)
            {
                room.QuotaOwnerProfileId = room.CreatorProfileId;
            }

            if (room.Participants.Count == 0)
            {
                room.CreatorProfileId = Guid.Empty;
                room.QuotaOwnerProfileId = Guid.Empty;
                room.Finished = true;
                room.FinishedAt ??= now;
                room.RoundEndsAt ??= now;
                room.Phase = LiveRoomPhase.Aborted;
                room.PhaseChangedAt = now;
                room.CloseReason = "Der Raum wurde beendet, weil keine Teilnehmenden mehr vorhanden sind.";
                room.PersistenceState = CompletionState.AbortedUnconfirmed;
                room.CompletionReceipt = null;
                completed = null;
                RemoveProgressInputs(room.Id);
            }
            else
            {
                completed ??= TryCompleteRoom(room, now);
                ApplyHostDisconnectRule(room);
                TryAbortStrandedSeries(room, now);
            }
            if (completed is not null)
            {
                completed = completed with
                {
                    CreatorProfileId = room.CreatorProfileId,
                    Participants = completed.Participants
                        .Where(candidate => candidate.UserProfileId != profileId)
                        .ToArray()
                };
            }
            Touch(room);
        }

        QueuePersistence(completed);
    }

    public int AbortActiveRooms()
    {
        var abortedRooms = 0;
        foreach (var roomId in rooms.Keys)
        {
            abortedRooms += AbortActiveRoom(roomId);
        }

        return abortedRooms;
    }

    public int AbortActiveRoom(Guid roomId)
    {
        if (!rooms.TryGetValue(roomId, out var room))
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        CompletedRoomRecord? completed;
        lock (room.Gate)
        {
            if (room.Finished || room.Phase is not (LiveRoomPhase.Countdown or LiveRoomPhase.Running))
            {
                return 0;
            }

            foreach (var participant in room.Participants.Values)
            {
                if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.LeftBeforeStart)
                {
                    continue;
                }

                participant.Status = ParticipantStatus.AbortedByServer;
                participant.Ready = false;
                participant.FinishedAt = now;
                participant.DurationMilliseconds = room.RaceStartsAt is { } raceStartsAt
                    ? (int)Math.Round(LiveRoomProgress.NormalizeDuration(now - raceStartsAt).TotalMilliseconds)
                    : 0;
            }

            room.Finished = true;
            room.FinishedAt = now;
            room.RoundEndsAt = now;
            room.Phase = LiveRoomPhase.Aborted;
            room.PhaseChangedAt = now;
            room.CloseReason = "Der Server wurde beendet; diese Runde wurde ohne Rating abgebrochen.";
            room.RoundVersion++;
            RemoveProgressInputs(room.Id);
            Touch(room);
            room.PersistenceState = CompletionState.Pending;
            completed = BuildPersistenceRecord(room);
        }

        QueuePersistence(completed);
        return 1;
    }

    private LiveRoomSnapshot Join(
        Guid roomId,
        Guid profileId,
        string displayName,
        bool viaCode,
        string? expectedCode = null)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            if (expectedCode is not null && !StringComparer.OrdinalIgnoreCase.Equals(room.Code, expectedCode))
            {
                throw new InvalidOperationException("Der Raumcode ist ungültig.");
            }

            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            if (room.Participants.TryGetValue(profileId, out var existing))
            {
                if (room.ExcludedProfileIds.Contains(profileId))
                {
                    throw new InvalidOperationException("Du wurdest aus diesem Raum entfernt.");
                }

                var changed = false;
                if (!StringComparer.Ordinal.Equals(existing.DisplayName, displayName))
                {
                    existing.DisplayName = displayName;
                    changed = true;
                }

                if (existing.Status == ParticipantStatus.Invited)
                {
                    if (room.Finished || room.Phase != LiveRoomPhase.Lobby)
                    {
                        throw new InvalidOperationException("Dieser Raum läuft bereits.");
                    }

                    if (room.LobbyLocked)
                    {
                        throw new InvalidOperationException("Die Lobby ist aktuell gesperrt.");
                    }

                    existing.Status = ParticipantStatus.Joined;
                    changed = true;
                }
                if (existing.Status == ParticipantStatus.Disconnected)
                {
                    existing.Status = room.Phase == LiveRoomPhase.Running
                        ? ParticipantStatus.Running
                        : existing.Ready ? ParticipantStatus.Ready : ParticipantStatus.Joined;
                    changed = true;
                }
                else if (existing.Status == ParticipantStatus.LeftBeforeStart && room.Phase == LiveRoomPhase.Lobby)
                {
                    existing.Status = ParticipantStatus.Joined;
                    existing.Ready = false;
                    changed = true;
                }

                if (existing.DisconnectedAt is not null)
                {
                    existing.DisconnectedAt = null;
                    changed = true;
                }

                if (changed)
                {
                    Touch(room);
                }
                ApplyHostDisconnectRule(room);
                snapshot = SnapshotUnlocked(room, now);
            }
            else
            {
                if (room.Finished)
                {
                    throw new InvalidOperationException("Dieser Raum ist bereits beendet.");
                }

                if (room.Phase != LiveRoomPhase.Lobby)
                {
                    throw new InvalidOperationException("Der Raum läuft bereits.");
                }

                if (room.Visibility == LiveRoomVisibility.Code && !viaCode)
                {
                    throw new InvalidOperationException("Für diesen Raum ist der Raumcode erforderlich.");
                }

                if (room.Visibility == LiveRoomVisibility.InvitationOnly && !room.InvitedProfileIds.Contains(profileId))
                {
                    throw new InvalidOperationException("Dieser Raum ist nur für eingeladene Personen zugänglich.");
                }

                if (room.LobbyLocked)
                {
                    throw new InvalidOperationException("Die Lobby ist aktuell gesperrt.");
                }

                if (room.Participants.Values.Count(CountsTowardCapacity) >= room.MaxParticipants)
                {
                    throw new InvalidOperationException("Dieser Raum ist voll.");
                }

                room.Participants[profileId] = new LiveParticipantState(
                    profileId,
                    displayName,
                    ParticipantStatus.Joined,
                    now,
                    room.Mode == LiveRoomMode.Team ? NextTeamNumber(room) : null);
                Touch(room);
                ApplyHostDisconnectRule(room);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        return QueuePersistence(completed, snapshot);
    }

    private LiveProgressResult SubmitProgressCore(Guid roomId, Guid profileId, int sequence, string input, bool includeSnapshot)
    {
        var inputDelta = LiveRoomProgress.DeserializeInputDelta(input);
        if (inputDelta.Revision != sequence)
        {
            throw new InvalidOperationException("Die Arena-Eingaberevision ist widersprüchlich.");
        }

        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot? snapshot = null;
        LiveProgressDelta? delta = null;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            var participant = RequireParticipant(room, profileId);
            if (room.Phase != LiveRoomPhase.Running || participant.Status != ParticipantStatus.Running)
            {
                snapshot = includeSnapshot ? SnapshotUnlocked(room, now) : null;
            }
            else
            {
                var roomInputs = GetOrCreateProgressInputs(room.Id);
                var key = new ProgressInputKey(participant.ProfileId, room.RoundVersion);
                var progressInput = roomInputs.Inputs.GetOrAdd(
                    key,
                    _ => new ProgressInputState(participant.Sequence, participant.Sequence == 0));
                if (progressInput.Revision != participant.Sequence)
                {
                    progressInput = new ProgressInputState(participant.Sequence, participant.Sequence == 0);
                    roomInputs.Inputs[key] = progressInput;
                }

                var inputStatus = ApplyInputDelta(room, progressInput, inputDelta);
                LiveProgressStateWindow window;
                if (inputStatus == LiveProgressInputStatus.Applied)
                {
                    participant.Sequence = progressInput.Revision;
                    participant.CorrectCharacters = progressInput.CorrectCharacters;
                    window = LiveRoomProgress.BuildTypedStateWindow(
                        room.TargetElements,
                        progressInput.Elements,
                        participant.CorrectCharacters);
                    participant.TypedTextPreview = window.Preview;
                    participant.TypedStateOffset = window.Offset;
                    participant.TypedCharacters = progressInput.Elements.Count;
                    participant.Accuracy = LiveRoomProgress.CalculateAccuracy(
                        participant.CorrectCharacters,
                        progressInput.Elements.Count);
                    participant.Wpm = LiveRoomProgress.CalculateWpm(participant.CorrectCharacters, room.RaceStartsAt, now);
                    LiveProgressEvidence.Observe(room, participant, inputDelta, now);
                    Touch(room);
                }
                else
                {
                    window = new LiveProgressStateWindow(0, 0, "", "");
                }

                delta = new LiveProgressDelta(
                    room.Id,
                    room.RoundVersion,
                    room.StateVersion,
                    participant.ProfileId,
                    participant.Sequence,
                    participant.CorrectCharacters,
                    progressInput.HasInput ? progressInput.Elements.Count : 0,
                    window.Bits,
                    participant.Wpm,
                    participant.Accuracy,
                    null,
                    window.Offset,
                    window.Length,
                    inputStatus);
                snapshot = includeSnapshot ? SnapshotUnlocked(room, now) : null;
            }
        }

        if (completed is not null && snapshot is not null)
        {
            snapshot = QueuePersistence(completed, snapshot);
        }
        else
        {
            QueuePersistence(completed);
        }

        return new LiveProgressResult(delta, snapshot);
    }

    private CompletedRoomRecord? ApplyDisconnectTimeouts(LiveRoomState room, DateTimeOffset now)
    {
        AdvancePhase(room, now);
        var completed = ApplyRoundDeadline(room, now);
        var grace = TimeSpan.FromSeconds(Math.Clamp(options.Value.ReconnectGraceSeconds, 0, 300));
        LiveRoomDisconnectRules.ApplyTimeouts(room, now, grace);

        completed ??= TryCompleteRoom(room, now);
        ApplyHostDisconnectRule(room);
        TryAbortStrandedSeries(room, now);
        completed ??= ApplyRoundResultsIdleDeadline(room, now);
        return completed;
    }

    private CompletedRoomRecord? ApplyRoundDeadline(LiveRoomState room, DateTimeOffset now)
    {
        if (room.Phase != LiveRoomPhase.Running ||
            room.RaceStartsAt is not { } raceStartsAt ||
            room.RoundDeadlineAt is not { } deadline ||
            now < deadline)
        {
            return null;
        }

        var changed = false;
        foreach (var participant in room.Participants.Values.Where(participant =>
                     !room.ExcludedProfileIds.Contains(participant.ProfileId) &&
                     participant.Status is not ParticipantStatus.Finished and
                         not ParticipantStatus.Dnf and
                         not ParticipantStatus.LeftBeforeStart and
                         not ParticipantStatus.Declined and
                         not ParticipantStatus.Cancelled and
                         not ParticipantStatus.AbortedByServer))
        {
            participant.Status = ParticipantStatus.Dnf;
            participant.Ready = true;
            participant.FinishedAt = deadline;
            participant.DurationMilliseconds = (int)Math.Round(
                LiveRoomProgress.NormalizeDuration(deadline - raceStartsAt).TotalMilliseconds);
            RemoveProgressInput(room.Id, participant.ProfileId, room.RoundVersion);
            changed = true;
        }

        if (changed)
        {
            Touch(room);
            LiveRoomScoring.ApplyPlacements(room);
        }

        return TryCompleteRoom(room, deadline);
    }

    private CompletedRoomRecord? ApplyRoundResultsIdleDeadline(LiveRoomState room, DateTimeOffset now)
    {
        if (room.Finished || room.Phase != LiveRoomPhase.RoundResults)
        {
            return null;
        }

        var deadline = LiveRoomDeadline.RoundResultsIdleAt(room.PhaseChangedAt, options.Value);
        if (now < deadline)
        {
            return null;
        }

        room.RatingEligible = false;
        room.Finished = true;
        room.FinishedAt = deadline;
        room.RoundEndsAt = deadline;
        room.Phase = LiveRoomPhase.SeriesResults;
        room.PhaseChangedAt = deadline;
        room.CloseReason = "Die Serie wurde nach der maximalen Pause zwischen zwei Runden beendet.";
        room.PersistenceState = CompletionState.Pending;
        room.RoundVersion++;
        LiveRoomScoring.ApplyOverallPlacements(room);
        RemoveProgressInputs(room.Id);
        Touch(room);
        return BuildPersistenceRecord(room);
    }

    private static void ApplyHostDisconnectRule(LiveRoomState room)
        => LiveRoomHostRules.ApplyAutomaticTransfer(room);

    private void TryAbortStrandedSeries(LiveRoomState room, DateTimeOffset now)
    {
        if (room.Phase != LiveRoomPhase.RoundResults || room.Finished)
        {
            return;
        }

        var eligible = room.Participants.Values
            .Where(participant =>
                !room.ExcludedProfileIds.Contains(participant.ProfileId) &&
                participant.Status != ParticipantStatus.LeftBeforeStart)
            .ToArray();
        if (eligible.Length == 0 || eligible.Any(participant => participant.DisconnectedAt is null))
        {
            return;
        }

        var grace = TimeSpan.FromSeconds(Math.Clamp(options.Value.ReconnectGraceSeconds, 0, 300));
        if (eligible.Any(participant => now - participant.DisconnectedAt!.Value < grace))
        {
            return;
        }

        room.Finished = true;
        room.FinishedAt = now;
        room.RoundEndsAt = now;
        room.Phase = LiveRoomPhase.Aborted;
        room.PhaseChangedAt = now;
        room.CloseReason = "Die Serie wurde beendet, weil nach der Wiederverbindungsfrist niemand mehr verbunden war.";
        room.PersistenceState = CompletionState.AbortedUnconfirmed;
        room.RoundVersion++;
        RemoveProgressInputs(room.Id);
        Touch(room);
    }

    private static bool IsHostCandidate(LiveRoomState room, LiveParticipantState participant)
        => LiveRoomHostRules.IsCandidate(room, participant);

    private CompletedRoomRecord? TryCompleteRoom(LiveRoomState room, DateTimeOffset now)
    {
        var transition = LiveRoomCompletionRules.TryComplete(room, now);
        if (room.Finished)
        {
            RemoveProgressInputs(room.Id);
        }
        if (transition.EnteredRoundResults)
        {
            ApplyHostDisconnectRule(room);
            TryAbortStrandedSeries(room, now);
        }

        return transition.Record;
    }

    private int CountRoomsTowardCapacity()
    {
        var count = 0;
        foreach (var room in rooms.Values)
        {
            lock (room.Gate)
            {
                if (!room.Finished)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private int CountActiveRoomsForCreator(Guid creatorProfileId)
    {
        var count = 0;
        foreach (var room in rooms.Values.Where(room => room.QuotaOwnerProfileId == creatorProfileId))
        {
            lock (room.Gate)
            {
                if (!room.Finished)
                {
                    count++;
                }
            }
        }

        return count;
    }

    internal static InvalidOperationException CreatorRoomQuotaExceeded() =>
        new("Du hast bereits die maximal zulässige Anzahl aktiver Arena-Räume erstellt.");

    private void CleanupExpiredRooms(DateTimeOffset now)
    {
        var completedRetention = TimeSpan.FromMinutes(Math.Clamp(options.Value.CompletedRoomRetentionMinutes, 5, 24 * 60));
        var lobbyRetention = TimeSpan.FromMinutes(Math.Clamp(options.Value.LobbyRoomRetentionMinutes, 30, 7 * 24 * 60));

        foreach (var room in rooms.Values)
        {
            var remove = false;
            CompletedRoomRecord? completed;
            lock (room.Gate)
            {
                completed = ApplyDisconnectTimeouts(room, now);
                AdvancePhase(room, now);
                remove = room.Finished && room.FinishedAt is { } finishedAt && now - finishedAt >= completedRetention;
                remove = remove || (room.Phase == LiveRoomPhase.Lobby && now - room.CreatedAt >= lobbyRetention);
            }

            QueuePersistence(completed);
            if (!remove)
            {
                continue;
            }

            lock (createGate)
            {
                if (!rooms.TryRemove(room.Id, out _))
                {
                    continue;
                }
                UntrackLocalRoom(room.Id);
            }

            RemoveRoomCodeMapping(room.Code, room.Id);
            RemoveProgressInputs(room.Id);
            progressBroadcaster?.RemoveRoom(room.Id);
            updateSender?.RemoveRoom(room.Id);
        }
    }

    private void RemoveRoomCodeMapping(string code, Guid roomId)
    {
        lock (createGate)
        {
            if (roomCodes.TryGetValue(code, out var mappedRoomId) && mappedRoomId == roomId)
            {
                roomCodes.TryRemove(code, out _);
            }
        }
    }

    private void TrackLocalRoom(Guid roomId)
    {
        if (!localRoomAuditNodes.ContainsKey(roomId))
        {
            localRoomAuditNodes[roomId] = localRoomAuditOrder.AddLast(roomId);
        }
    }

    private void UntrackLocalRoom(Guid roomId)
    {
        if (localRoomAuditNodes.Remove(roomId, out var node))
        {
            localRoomAuditOrder.Remove(node);
        }
    }

    private static int NextTeamNumber(LiveRoomState room)
    {
        var teamOne = room.Participants.Values.Count(item => item.TeamNumber == 1 && CountsTowardCapacity(item));
        var teamTwo = room.Participants.Values.Count(item => item.TeamNumber == 2 && CountsTowardCapacity(item));
        return teamOne <= teamTwo ? 1 : 2;
    }

    private static int ResolveTeamNumber(
        IReadOnlyDictionary<Guid, int?>? assignments,
        Guid profileId,
        int fallback)
    {
        if (assignments is not null &&
            assignments.TryGetValue(profileId, out var teamNumber) &&
            teamNumber is 1 or 2)
        {
            return teamNumber.Value;
        }

        return fallback;
    }

    private static string BuildRematchTitle(string title)
    {
        const string suffix = " – Revanche";
        const int maximumLength = 160;
        var prefix = title.Length <= maximumLength - suffix.Length
            ? title
            : title[..(maximumLength - suffix.Length)].TrimEnd();
        return $"{prefix}{suffix}";
    }

    private string NormalizeArenaTarget(string text)
    {
        var normalized = TypingEngine.NormalizeText(text);
        var graphemeLimit = Math.Clamp(
            options.Value.MaxArenaTargetGraphemes,
            1,
            LiveOptions.MaximumSafeArenaTargetGraphemes);
        if (TypingEngine.SplitGraphemes(normalized).Count > graphemeLimit)
        {
            throw new InvalidOperationException($"Arena-Zieltexte dürfen höchstens {graphemeLimit} Grapheme enthalten.");
        }

        if (Encoding.UTF8.GetByteCount(normalized) > LiveOptions.MaximumSafeArenaTargetUtf8Bytes)
        {
            throw new InvalidOperationException(
                $"Arena-Zieltexte dürfen höchstens {LiveOptions.MaximumSafeArenaTargetUtf8Bytes / 1024} KiB UTF-8 umfassen.");
        }

        return normalized;
    }

    private void BeginCountdown(LiveRoomState room, DateTimeOffset now)
    {
        RemoveProgressInputs(room.Id);
        room.Phase = LiveRoomPhase.Countdown;
        room.PhaseChangedAt = now;
        room.CountdownStartsAt = now;
        room.RaceStartsAt = now.AddSeconds(Math.Clamp(options.Value.CountdownSeconds, 1, 10));
        room.RoundDeadlineAt = LiveRoomDeadline.ForNewRound(
            room.RaceStartsAt.Value,
            room.TargetCharacterCount,
            options.Value);
        room.RoundEndsAt = null;
        room.RoundVersion++;
        Touch(room);
        foreach (var participant in room.Participants.Values.Where(item => item.Status is ParticipantStatus.Ready or ParticipantStatus.Joined))
        {
            participant.Ready = true;
            participant.DisconnectedAt = null;
            LiveProgressEvidence.Reset(participant);
        }
    }

    private static void PrepareNextRound(LiveRoomState room)
    {
        if (room.CurrentRound >= room.RoundCount)
        {
            throw new InvalidOperationException("Die Serie ist bereits beendet.");
        }

        room.CurrentRound++;
        foreach (var participant in room.Participants.Values.Where(item =>
                     !room.ExcludedProfileIds.Contains(item.ProfileId) && item.Status != ParticipantStatus.LeftBeforeStart))
        {
            participant.Status = participant.DisconnectedAt is null ? ParticipantStatus.Ready : ParticipantStatus.Disconnected;
            participant.Ready = participant.Status == ParticipantStatus.Ready;
            participant.Sequence = 0;
            participant.CorrectCharacters = 0;
            participant.TypedTextPreview = "";
            participant.TypedStateOffset = 0;
            participant.TypedCharacters = 0;
            participant.Wpm = 0;
            participant.Placement = null;
            participant.FinishedAt = null;
            participant.DurationMilliseconds = 0;
            participant.Accuracy = 0;
            LiveProgressEvidence.Reset(participant);
        }

        Touch(room);
    }

    private LiveRoomState GetRoom(Guid roomId)
    {
        return rooms.TryGetValue(roomId, out var room) ? room : throw new InvalidOperationException("Der Live-Raum wurde nicht gefunden.");
    }

    private static LiveParticipantState RequireParticipant(LiveRoomState room, Guid profileId)
    {
        return room.Participants.TryGetValue(profileId, out var participant)
            ? participant
            : throw new InvalidOperationException("Du bist nicht in diesem Raum.");
    }

    private static LiveProgressInputStatus ApplyInputDelta(
        LiveRoomState room,
        ProgressInputState state,
        LiveProgressInputDelta delta)
    {
        if (delta.BaseRevision < 0 || delta.Revision <= delta.BaseRevision ||
            delta.Revision != delta.BaseRevision + 1 || delta.BackspaceCount < 0 ||
            delta.AppendText is null)
        {
            throw new InvalidOperationException("Das Arena-Eingabedelta ist ungültig.");
        }

        if (delta.Revision <= state.Revision)
        {
            return state.HasInput
                ? LiveProgressInputStatus.Duplicate
                : LiveProgressInputStatus.ResyncRequired;
        }

        if (delta.BaseRevision != state.Revision)
        {
            return LiveProgressInputStatus.ResyncRequired;
        }

        if (delta.ResyncInput is not null)
        {
            var resyncElements = LiveRoomProgress.NormalizeBoundedDeltaText(room, delta.ResyncInput);
            state.Elements.Clear();
            state.Elements.AddRange(resyncElements);
            state.CorrectCharacters = LiveRoomProgress.CountCorrectPrefix(room.TargetElements, state.Elements);
            state.Revision = delta.Revision;
            state.HasInput = true;
            return LiveProgressInputStatus.Applied;
        }

        if (!state.HasInput)
        {
            return LiveProgressInputStatus.ResyncRequired;
        }

        if (delta.BackspaceCount > state.Elements.Count)
        {
            return LiveProgressInputStatus.ResyncRequired;
        }

        var appended = LiveRoomProgress.NormalizeBoundedDeltaText(room, delta.AppendText);
        var retainedCount = state.Elements.Count - delta.BackspaceCount;
        if (retainedCount + appended.Count > room.TargetElements.Count + 20)
        {
            throw new InvalidOperationException("Die Eingabe ist zu lang.");
        }

        if (delta.BackspaceCount > 0)
        {
            state.Elements.RemoveRange(retainedCount, delta.BackspaceCount);
        }
        state.Elements.AddRange(appended);

        if (retainedCount <= state.CorrectCharacters)
        {
            state.CorrectCharacters = retainedCount;
            while (state.CorrectCharacters < Math.Min(room.TargetElements.Count, state.Elements.Count) &&
                   StringComparer.Ordinal.Equals(
                       room.TargetElements[state.CorrectCharacters],
                       state.Elements[state.CorrectCharacters]))
            {
                state.CorrectCharacters++;
            }
        }

        state.Revision = delta.Revision;
        return LiveProgressInputStatus.Applied;
    }

    private void RemoveProgressInputs(Guid roomId)
    {
        lock (progressInputRoomsGate)
        {
            if (progressInputs.TryRemove(roomId, out var roomInputs))
            {
                progressInputRoomOrder.Remove(roomInputs.OrderNode);
            }
        }
    }

    private void RemoveProgressInput(Guid roomId, Guid profileId, int roundVersion)
    {
        if (!progressInputs.TryGetValue(roomId, out var roomInputs))
        {
            return;
        }

        roomInputs.Inputs.TryRemove(new ProgressInputKey(profileId, roundVersion), out _);
        RemoveEmptyProgressInputRoom(roomId, roomInputs);
    }

    internal void RemoveProgressInput(Guid roomId, Guid profileId)
    {
        if (!progressInputs.TryGetValue(roomId, out var roomInputs))
        {
            return;
        }

        foreach (var key in roomInputs.Inputs.Keys.Where(key => key.ProfileId == profileId))
        {
            roomInputs.Inputs.TryRemove(key, out _);
        }

        RemoveEmptyProgressInputRoom(roomId, roomInputs);
    }

    internal void SynchronizeProgressInputPurge(
        Guid roomId,
        Guid profileId,
        string? generation)
    {
        var roomInputs = GetOrCreateProgressInputs(roomId);
        roomInputs.PurgeGenerations.TryGetValue(profileId, out var observedGeneration);
        if (!StringComparer.Ordinal.Equals(observedGeneration, generation))
        {
            foreach (var key in roomInputs.Inputs.Keys.Where(key => key.ProfileId == profileId))
            {
                roomInputs.Inputs.TryRemove(key, out _);
            }
        }

        if (generation is null)
        {
            roomInputs.PurgeGenerations.TryRemove(profileId, out _);
        }
        else
        {
            roomInputs.PurgeGenerations[profileId] = generation;
        }

        RemoveEmptyProgressInputRoom(roomId, roomInputs);
    }

    private void RemoveEmptyProgressInputRoom(Guid roomId, RoomProgressInputs roomInputs)
    {
        if (roomInputs.Inputs.IsEmpty && roomInputs.PurgeGenerations.IsEmpty)
        {
            lock (progressInputRoomsGate)
            {
                if (roomInputs.Inputs.IsEmpty && roomInputs.PurgeGenerations.IsEmpty &&
                    progressInputs.TryRemove(new KeyValuePair<Guid, RoomProgressInputs>(roomId, roomInputs)))
                {
                    progressInputRoomOrder.Remove(roomInputs.OrderNode);
                }
            }
        }
    }

    private RoomProgressInputs GetOrCreateProgressInputs(Guid roomId)
    {
        if (progressInputs.TryGetValue(roomId, out var existing))
        {
            return existing;
        }

        lock (progressInputRoomsGate)
        {
            if (progressInputs.TryGetValue(roomId, out existing))
            {
                return existing;
            }

            while (progressInputs.Count >= progressInputRoomCapacity &&
                   progressInputRoomOrder.First is { } oldest)
            {
                progressInputRoomOrder.RemoveFirst();
                progressInputs.TryRemove(oldest.Value, out _);
            }

            var orderNode = progressInputRoomOrder.AddLast(roomId);
            var created = new RoomProgressInputs(orderNode);
            progressInputs[roomId] = created;
            return created;
        }
    }

    private static LiveRoomMemento CreateMemento(LiveRoomState room) => new(
        room.Id,
        room.CreatorProfileId,
        room.Code,
        room.Title,
        room.Text,
        room.Mode,
        room.Visibility,
        room.RoundCount,
        room.MaxParticipants,
        room.CreatedAt,
        room.Phase,
        room.CurrentRound,
        room.RoundVersion,
        room.StateVersion,
        room.LobbyLocked,
        room.PhaseChangedAt,
        room.CountdownStartsAt,
        room.RaceStartsAt,
        room.RoundEndsAt,
        room.CloseReason,
        room.Started,
        room.Finished,
        room.CompletionReceipt,
        room.PersistenceState,
        room.StartedAt,
        room.FinishedAt,
        room.ExcludedProfileIds.Order().ToArray(),
        room.InvitedProfileIds.Order().ToArray(),
        new Dictionary<int, int>(room.TeamRoundWins),
        room.Participants.Values
            .OrderBy(participant => participant.JoinedAt)
            .ThenBy(participant => participant.ProfileId)
            .Select(participant => new LiveParticipantMemento(
                participant.ProfileId,
                participant.DisplayName,
                participant.Status,
                participant.JoinedAt,
                participant.TeamNumber,
                participant.Ready,
                participant.Sequence,
                participant.CorrectCharacters,
                participant.TypedTextPreview,
                participant.Wpm,
                participant.Placement,
                participant.FinishedAt,
                participant.DisconnectedAt,
                participant.DurationMilliseconds,
                participant.Accuracy,
                participant.SeriesPoints,
                participant.RoundWins,
                participant.FinishedRounds,
                participant.CompletedRounds,
                participant.TotalDurationMilliseconds,
                participant.TotalWpm,
                participant.TotalAccuracy,
                participant.CompetitionEligible,
                participant.TypedStateOffset,
                participant.TypedCharacters,
                participant.ProgressEvidenceRoundVersion,
                participant.ProgressEvidenceRevision,
                participant.ProgressEvidenceCorrectCharacters,
                participant.ProgressEvidenceCount,
                participant.ProgressEvidenceObservedAt,
                participant.ProgressEvidenceInvalid))
            .ToArray(),
        room.QuotaOwnerProfileId,
        room.RatingEligible,
        room.TargetCompetitionEligible,
        room.RoundDeadlineAt);

    private static LiveRoomState RestoreRoom(LiveRoomMemento memento)
    {
        if (memento.Id == Guid.Empty ||
            (memento.CreatorProfileId == Guid.Empty &&
                (!memento.Finished || memento.Participants.Count != 0)) ||
            memento.StateVersion < 1 ||
            memento.RoundVersion < 1 ||
            memento.CurrentRound < 1 ||
            memento.RoundCount < 1 ||
            memento.MaxParticipants < MinimumParticipants ||
            !StringComparer.Ordinal.Equals(NormalizeRoomCode(memento.Code), memento.Code))
        {
            throw new InvalidOperationException("Der verteilte Arena-Zustand ist ungültig.");
        }

        var room = new LiveRoomState(
            memento.Id,
            memento.CreatorProfileId,
            memento.Code,
            memento.Title,
            memento.Text,
            memento.Mode,
            memento.Visibility,
            memento.RoundCount,
            memento.MaxParticipants,
            memento.CreatedAt,
            memento.TargetCompetitionEligible,
            memento.QuotaOwnerProfileId ?? memento.CreatorProfileId)
        {
            Phase = memento.Phase,
            CurrentRound = memento.CurrentRound,
            RoundVersion = memento.RoundVersion,
            StateVersion = memento.StateVersion,
            LobbyLocked = memento.LobbyLocked,
            PhaseChangedAt = memento.PhaseChangedAt,
            CountdownStartsAt = memento.CountdownStartsAt,
            RaceStartsAt = memento.RaceStartsAt,
            RoundDeadlineAt = memento.RaceStartsAt is { } raceStartsAt
                ? LiveRoomDeadline.Resolve(raceStartsAt, TypingEngine.SplitGraphemes(memento.Text).Count, memento.RoundDeadlineAt)
                : null,
            RoundEndsAt = memento.RoundEndsAt,
            CloseReason = memento.CloseReason,
            Started = memento.Started,
            Finished = memento.Finished,
            RatingEligible = memento.TargetCompetitionEligible && memento.RatingEligible,
            CompletionReceipt = memento.CompletionReceipt,
            PersistenceState = memento.PersistenceState,
            StartedAt = memento.StartedAt,
            FinishedAt = memento.FinishedAt
        };

        foreach (var profileId in memento.ExcludedProfileIds)
        {
            room.ExcludedProfileIds.Add(profileId);
        }

        foreach (var profileId in memento.InvitedProfileIds)
        {
            room.InvitedProfileIds.Add(profileId);
        }

        foreach (var (teamNumber, roundWins) in memento.TeamRoundWins)
        {
            room.TeamRoundWins[teamNumber] = roundWins;
        }

        foreach (var item in memento.Participants)
        {
            if (item.ProfileId == Guid.Empty || room.Participants.ContainsKey(item.ProfileId))
            {
                throw new InvalidOperationException("Der verteilte Arena-Teilnehmerzustand ist ungültig.");
            }

            var typedPreview = item.TypedTextPreview[..Math.Min(
                item.TypedTextPreview.Length,
                LiveRoomProgress.MaxTypedStateDetailCharacters)];
            var typedStateOffset = Math.Clamp(
                item.TypedStateOffset,
                0,
                Math.Max(0, room.TargetCharacterCount - typedPreview.Length));
            var typedCharacters = Math.Clamp(
                Math.Max(
                    item.CorrectCharacters,
                    Math.Max(item.TypedCharacters, typedStateOffset + typedPreview.Length)),
                0,
                room.TargetCharacterCount + LiveRoomProgress.MaxInputOverrunCharacters);

            var participant = new LiveParticipantState(
                item.ProfileId,
                item.DisplayName,
                item.Status,
                item.JoinedAt,
                item.TeamNumber)
            {
                Ready = item.Ready,
                Sequence = item.Sequence,
                CorrectCharacters = item.CorrectCharacters,
                TypedTextPreview = typedPreview,
                TypedStateOffset = typedStateOffset,
                TypedCharacters = typedCharacters,
                Wpm = item.Wpm,
                Placement = item.Placement,
                FinishedAt = item.FinishedAt,
                DisconnectedAt = item.DisconnectedAt,
                DurationMilliseconds = item.DurationMilliseconds,
                Accuracy = item.Accuracy,
                SeriesPoints = item.SeriesPoints,
                RoundWins = item.RoundWins,
                FinishedRounds = item.FinishedRounds,
                CompletedRounds = item.CompletedRounds,
                TotalDurationMilliseconds = item.TotalDurationMilliseconds,
                TotalWpm = item.TotalWpm,
                TotalAccuracy = item.TotalAccuracy,
                CompetitionEligible = item.CompetitionEligible
            };
            LiveProgressEvidence.Restore(room, participant, item);
            room.Participants[item.ProfileId] = participant;
        }

        if (room.CreatorProfileId != Guid.Empty && !room.Participants.ContainsKey(room.CreatorProfileId))
        {
            throw new InvalidOperationException("Der verteilte Arena-Zustand enthält keine Raumleitung.");
        }

        return room;
    }

    private static void RequireHost(LiveRoomState room, Guid profileId)
        => LiveRoomHostRules.RequireHost(room, profileId);

    private static bool CanDiscover(LiveRoomState room, Guid viewerProfileId)
    {
        if (room.Visibility == LiveRoomVisibility.InternalOpen)
        {
            return true;
        }

        if (room.CreatorProfileId == viewerProfileId || room.Participants.ContainsKey(viewerProfileId))
        {
            return true;
        }

        return room.Visibility == LiveRoomVisibility.InvitationOnly && room.InvitedProfileIds.Contains(viewerProfileId);
    }

    private static LiveRoomLobbySummary BuildLobbySummary(LiveRoomState room) => new(
        room.Id,
        room.CreatorProfileId,
        room.Participants.TryGetValue(room.CreatorProfileId, out var creator)
            ? creator.DisplayName
            : "Raumleitung",
        room.Visibility == LiveRoomVisibility.Code ? "" : room.Code,
        room.Title,
        room.Mode,
        room.Visibility,
        room.Phase,
        room.RoundCount,
        room.CurrentRound,
        room.Participants.Values.Count(participant => participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready),
        room.MaxParticipants,
        room.LobbyLocked,
        room.StateVersion);

    internal static void Touch(LiveRoomState room)
    {
        room.StateVersion = checked(room.StateVersion + 1);
    }

    private LiveRoomSnapshot Snapshot(LiveRoomState room, DateTimeOffset now)
    {
        lock (room.Gate)
        {
            AdvancePhase(room, now);
            return SnapshotUnlocked(room, now);
        }
    }

    private LiveRoomSnapshot SnapshotUnlocked(LiveRoomState room, DateTimeOffset now)
    {
        var persistenceState = room.PersistenceState;
        if (room.Finished &&
            persistenceState == CompletionState.Pending &&
            room.CompletionReceipt is not null &&
            completionSink is not null)
        {
            persistenceState = completionSink.GetStatus(room.Id).State;
            if (room.PersistenceState != persistenceState)
            {
                room.PersistenceState = persistenceState;
                Touch(room);
            }
            if (room.CompletionReceipt is not null)
            {
                room.CompletionReceipt = room.CompletionReceipt with { State = persistenceState.Value };
            }
        }

        if (room.Finished)
        {
            persistenceState ??= CompletionState.AbortedUnconfirmed;
        }

        return LiveRoomSnapshotProjector.Create(room, now, persistenceState);
    }

    private static int ValidateRoundCount(LiveRoomMode mode, int roundCount)
    {
        if (mode == LiveRoomMode.Series && roundCount is 3 or 5)
        {
            return roundCount;
        }

        if (mode is LiveRoomMode.Classic or LiveRoomMode.Team && roundCount == 1)
        {
            return roundCount;
        }

        throw new InvalidOperationException(mode == LiveRoomMode.Series
            ? "Serienrennen müssen über drei oder fünf Runden laufen."
            : "Klassische Rennen und Teamwertungen laufen über genau eine Runde.");
    }

    private static bool IsLobbyActive(LiveParticipantState participant)
    {
        return participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready;
    }

    private static bool CountsTowardCapacity(LiveParticipantState participant)
    {
        return participant.Status is not ParticipantStatus.LeftBeforeStart and not ParticipantStatus.Dnf and not ParticipantStatus.Finished;
    }

    private static void AdvancePhase(LiveRoomState room, DateTimeOffset now)
    {
        if (room.Phase != LiveRoomPhase.Countdown || room.RaceStartsAt is null || now < room.RaceStartsAt.Value)
        {
            return;
        }

        room.Started = true;
        room.StartedAt ??= room.RaceStartsAt;
        room.Phase = LiveRoomPhase.Running;
        room.PhaseChangedAt = room.RaceStartsAt.Value;
        room.RoundVersion++;
        Touch(room);
        foreach (var participant in room.Participants.Values)
        {
            if (participant.Status is ParticipantStatus.Ready or ParticipantStatus.Joined)
            {
                participant.Status = ParticipantStatus.Running;
                participant.Ready = true;
                participant.DisconnectedAt = null;
            }
        }
    }

    private string GenerateUniqueCode()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var code = GenerateCode();
            if (!roomCodes.ContainsKey(code))
            {
                return code;
            }
        }

        throw new InvalidOperationException("Es konnte kein freier Raumcode erzeugt werden.");
    }

    private static string GenerateCode()
    {
        Span<char> chars = stackalloc char[6];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = RoomCodeAlphabet[RandomNumberGenerator.GetInt32(RoomCodeAlphabet.Length)];
        }

        return new string(chars);
    }

    private static CompletedRoomRecord BuildPersistenceRecord(LiveRoomState room) =>
        LiveRoomCompletionRules.BuildPersistenceRecord(room);

    private LiveRoomSnapshot QueuePersistence(CompletedRoomRecord? record, LiveRoomSnapshot snapshot)
    {
        var state = QueuePersistence(record);
        if (record is null)
        {
            return snapshot;
        }

        if (!rooms.TryGetValue(record.Id, out var room))
        {
            return snapshot with { PersistenceState = state };
        }

        lock (room.Gate)
        {
            return SnapshotUnlocked(room, timeProvider.GetUtcNow()) with { PersistenceState = state };
        }
    }

    private CompletionState? QueuePersistence(CompletedRoomRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        if (rooms.TryGetValue(record.Id, out var room))
        {
            lock (room.Gate)
            {
                record = record with
                {
                    Participants = record.Participants
                        .Where(participant => !room.ExcludedProfileIds.Contains(participant.UserProfileId))
                        .ToArray()
                };
                var receipt = EnqueueCompletion(record);
                room.CompletionReceipt = receipt;
                if (room.PersistenceState != receipt.State)
                {
                    room.PersistenceState = receipt.State;
                    Touch(room);
                }
                return receipt.State;
            }
        }

        return EnqueueCompletion(record).State;
    }

    private CompletionReceipt EnqueueCompletion(CompletedRoomRecord record)
    {
        try
        {
            return completionSink?.Enqueue(record)
                ?? new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.AbortedUnconfirmed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ein Arena-Ergebnis konnte nicht für die Persistenz eingereiht werden.");
            return new CompletionReceipt(record.Id, record.IdempotencyKey, CompletionState.Failed);
        }
    }

    internal void EnsurePendingPersistenceQueued(Guid roomId)
    {
        if (completionSink is null || !rooms.TryGetValue(roomId, out var room))
        {
            return;
        }

        lock (room.Gate)
        {
            if (!room.Finished ||
                room.PersistenceState is not (CompletionState.Pending or CompletionState.Failed) ||
                room.CompletionReceipt is null)
            {
                return;
            }

            var record = BuildPersistenceRecord(room);
            record = record with
            {
                Participants = record.Participants
                    .Where(participant => !room.ExcludedProfileIds.Contains(participant.UserProfileId))
                    .ToArray()
            };
            try
            {
                var receipt = completionSink.Enqueue(record);
                room.CompletionReceipt = receipt;
                if (room.PersistenceState != receipt.State)
                {
                    room.PersistenceState = receipt.State;
                    Touch(room);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Der fehlende Arena-Ergebnisauftrag {RoomId} konnte noch nicht rekonstruiert werden.", roomId);
            }
        }
    }

    internal int ProgressInputRoomCount => progressInputs.Count;

    internal int ProgressInputCount => progressInputs.Values.Sum(room => room.Inputs.Count);

    internal LiveProgressInputCacheSnapshot? SnapshotProgressInputCache(Guid roomId)
    {
        if (!progressInputs.TryGetValue(roomId, out var roomInputs))
        {
            return null;
        }

        return new LiveProgressInputCacheSnapshot(
            roomId,
            roomInputs.Inputs.Keys
                .Select(key => key.ProfileId)
                .Distinct()
                .ToDictionary(
                    profileId => profileId,
                    profileId => roomInputs.PurgeGenerations.GetValueOrDefault(profileId)));
    }

    internal IReadOnlyList<LiveLocalRoomCacheSnapshot> SnapshotLocalRoomCache(int take)
    {
        take = Math.Clamp(take, 1, 256);
        var includeProgress = !progressInputs.IsEmpty;
        var includeRooms = !rooms.IsEmpty;
        var roomBudget = includeProgress && includeRooms ? Math.Max(1, take / 2) : take;
        var progressBudget = includeProgress && includeRooms ? Math.Max(1, take - roomBudget) : take;
        Guid[] localRoomIds;
        lock (createGate)
        {
            var count = Math.Min(roomBudget, localRoomAuditOrder.Count);
            localRoomIds = new Guid[count];
            for (var index = 0; index < count; index++)
            {
                var node = localRoomAuditOrder.First!;
                localRoomIds[index] = node.Value;
                localRoomAuditOrder.RemoveFirst();
                localRoomAuditOrder.AddLast(node);
            }
        }

        Guid[] progressRoomIds;
        lock (progressInputRoomsGate)
        {
            var count = Math.Min(progressBudget, progressInputRoomOrder.Count);
            progressRoomIds = new Guid[count];
            for (var index = 0; index < count; index++)
            {
                var node = progressInputRoomOrder.First!;
                progressRoomIds[index] = node.Value;
                progressInputRoomOrder.RemoveFirst();
                progressInputRoomOrder.AddLast(node);
            }
        }

        var candidates = localRoomIds.Concat(progressRoomIds).Distinct().Take(take).ToArray();
        var snapshots = new List<LiveLocalRoomCacheSnapshot>(candidates.Length);
        foreach (var roomId in candidates)
        {
            if (!rooms.TryGetValue(roomId, out var room))
            {
                snapshots.Add(new LiveLocalRoomCacheSnapshot(roomId, null));
                continue;
            }
            lock (room.Gate)
            {
                snapshots.Add(new LiveLocalRoomCacheSnapshot(roomId, room.StateVersion));
            }
        }
        return snapshots;
    }

    private readonly record struct ProgressInputKey(Guid ProfileId, int RoundVersion);

    private sealed class RoomProgressInputs(LinkedListNode<Guid> orderNode)
    {
        public ConcurrentDictionary<ProgressInputKey, ProgressInputState> Inputs { get; } = new();
        public ConcurrentDictionary<Guid, string> PurgeGenerations { get; } = new();
        public LinkedListNode<Guid> OrderNode { get; } = orderNode;
    }

    private sealed class ProgressInputState(int revision, bool hasInput)
    {
        public int Revision { get; set; } = revision;
        public bool HasInput { get; set; } = hasInput;
        public List<string> Elements { get; } = [];
        public int CorrectCharacters { get; set; }
    }
}

internal sealed record LiveProgressInputCacheSnapshot(
    Guid RoomId,
    IReadOnlyDictionary<Guid, string?> ProfileGenerations);

internal sealed record LiveLocalRoomCacheSnapshot(Guid RoomId, long? StateVersion);

internal sealed record LiveRoomRematchTemplate(
    CreateLiveRoomRequest Request,
    IReadOnlyDictionary<Guid, int?> TeamAssignments);
