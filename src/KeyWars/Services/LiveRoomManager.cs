using System.Collections.Concurrent;
using System.Security.Cryptography;
using KeyWars.Domain;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed class LiveRoomManager(
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    TypingEngine typingEngine,
    ILogger<LiveRoomManager> logger,
    ILiveRoomCompletionSink? completionSink = null)
{
    private const int MinimumParticipants = 2;
    private const string RoomCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly object createGate = new();
    private readonly ConcurrentDictionary<Guid, LiveRoomState> rooms = new();
    private readonly ConcurrentDictionary<string, Guid> roomCodes = new(StringComparer.OrdinalIgnoreCase);

    public LiveRoomSnapshot CreateRoom(CreateLiveRoomRequest request)
    {
        if (request.Mode is not (LiveRoomMode.Classic or LiveRoomMode.Series or LiveRoomMode.Team))
        {
            throw new InvalidOperationException("Dieser Arena-Modus ist noch nicht freigeschaltet.");
        }

        if (request.Visibility == LiveRoomVisibility.InvitationOnly)
        {
            throw new InvalidOperationException("Einladungsräume sind noch nicht implementiert. Verwende Code oder intern offene Räume.");
        }

        var now = timeProvider.GetUtcNow();
        CleanupExpiredRooms(now);
        lock (createGate)
        {
            if (completionSink is not null && !completionSink.CanAcceptNewRoom(rooms.Count))
            {
                throw new InvalidOperationException("Die Arena nimmt vorübergehend keine neuen Räume an, weil die Ergebnispersistenz ausgelastet ist.");
            }

            if (rooms.Count >= options.Value.MaxConcurrentRooms)
            {
                throw new InvalidOperationException("Die maximale Anzahl gleichzeitiger Live-Räume ist erreicht.");
            }

            var maxParticipants = Math.Clamp(request.MaxParticipants, MinimumParticipants, options.Value.MaxParticipantsPerRoom);
            var roundCount = ValidateRoundCount(request.Mode, request.RoundCount);
            var room = new LiveRoomState(
                Guid.CreateVersion7(),
                request.CreatorProfileId,
                GenerateUniqueCode(),
                string.IsNullOrWhiteSpace(request.Title) ? "Live-Raum" : request.Title.Trim(),
                TypingEngine.NormalizeText(request.Text),
                request.Mode,
                request.Visibility,
                roundCount,
                maxParticipants,
                now);

            room.Participants[request.CreatorProfileId] = new LiveParticipantState(
                request.CreatorProfileId,
                request.CreatorDisplayName,
                ParticipantStatus.Joined,
                now,
                request.Mode == LiveRoomMode.Team ? 1 : null);
            rooms[room.Id] = room;
            roomCodes[room.Code] = room.Id;
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

    public LiveRoomSnapshot JoinByCode(string code, Guid profileId, string displayName)
    {
        var normalizedCode = NormalizeRoomCode(code);
        if (!roomCodes.TryGetValue(normalizedCode, out var roomId) || !rooms.TryGetValue(roomId, out var room))
        {
            throw new InvalidOperationException("Der Raumcode ist ungültig.");
        }

        return Join(room.Id, profileId, displayName, viaCode: true);
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

            participant.Ready = ready;
            participant.Status = ready ? ParticipantStatus.Ready : ParticipantStatus.Joined;
            participant.DisconnectedAt = null;
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

    public LiveProgressResult SubmitProgressDelta(Guid roomId, Guid profileId, int sequence, string input)
    {
        return SubmitProgressCore(roomId, profileId, sequence, input, includeSnapshot: false);
    }

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
                participant.TypedTextPreview = LiveRoomProgress.BuildTypedTextPreview(
                    room.TargetElements,
                    TypingEngine.SplitGraphemes(normalizedInput));
                participant.FinishedAt = now;
                participant.DurationMilliseconds = metrics.DurationMilliseconds;
                participant.Accuracy = metrics.Accuracy;
                participant.Wpm = metrics.Wpm;
                participant.CorrectCharacters = metrics.CorrectCharacters;
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
            if (participant.Status is ParticipantStatus.Joined or ParticipantStatus.Ready)
            {
                participant.Status = ParticipantStatus.Disconnected;
                participant.DisconnectedAt = now;
            }
            else if (participant.Status == ParticipantStatus.Running)
            {
                participant.Status = ParticipantStatus.Disconnected;
                participant.DisconnectedAt = now;
            }
            else if (room.Phase == LiveRoomPhase.RoundResults)
            {
                participant.Status = ParticipantStatus.Disconnected;
                participant.DisconnectedAt = now;
            }

            ApplyHostDisconnectRule(room);
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

    public void Sweep()
    {
        CleanupExpiredRooms(timeProvider.GetUtcNow());
    }

    public void RemoveProfile(Guid profileId)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var room in rooms.Values)
        {
            CompletedRoomRecord? completed = null;
            lock (room.Gate)
            {
                if (!room.Participants.TryGetValue(profileId, out var participant))
                {
                    continue;
                }

                room.ExcludedProfileIds.Add(profileId);
                if (participant.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf or ParticipantStatus.LeftBeforeStart)
                {
                    continue;
                }

                if (room.Phase == LiveRoomPhase.Lobby)
                {
                    participant.Status = ParticipantStatus.LeftBeforeStart;
                    participant.Ready = false;
                    participant.FinishedAt = now;
                    ApplyHostDisconnectRule(room);
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

            QueuePersistence(completed);
        }
    }

    public int AbortActiveRooms()
    {
        var now = timeProvider.GetUtcNow();
        var abortedRooms = 0;
        foreach (var room in rooms.Values)
        {
            CompletedRoomRecord? completed = null;
            lock (room.Gate)
            {
                if (room.Finished || room.Phase is not (LiveRoomPhase.Countdown or LiveRoomPhase.Running))
                {
                    continue;
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
                room.PersistenceState = CompletionState.Pending;
                completed = BuildPersistenceRecord(room);
                abortedRooms++;
            }

            QueuePersistence(completed);
        }

        return abortedRooms;
    }

    private LiveRoomSnapshot Join(Guid roomId, Guid profileId, string displayName, bool viaCode)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            if (room.Participants.TryGetValue(profileId, out var existing))
            {
                existing.DisplayName = displayName;
                if (existing.Status == ParticipantStatus.Disconnected)
                {
                    existing.Status = room.Phase == LiveRoomPhase.Running
                        ? ParticipantStatus.Running
                        : existing.Ready ? ParticipantStatus.Ready : ParticipantStatus.Joined;
                    existing.DisconnectedAt = null;
                }

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
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        return QueuePersistence(completed, snapshot);
    }

    private LiveProgressResult SubmitProgressCore(Guid roomId, Guid profileId, int sequence, string input, bool includeSnapshot)
    {
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
            else if (sequence <= participant.Sequence)
            {
                snapshot = includeSnapshot ? SnapshotUnlocked(room, now) : null;
            }
            else
            {
                var normalizedInput = LiveRoomProgress.NormalizeBoundedInput(room, input);
                var inputElements = TypingEngine.SplitGraphemes(normalizedInput);
                var correctCharacters = LiveRoomProgress.CountCorrectPrefix(room.TargetElements, inputElements);
                participant.Sequence = sequence;
                participant.CorrectCharacters = correctCharacters;
                participant.TypedTextPreview = LiveRoomProgress.BuildTypedTextPreview(room.TargetElements, inputElements);
                participant.Accuracy = LiveRoomProgress.CalculateAccuracy(correctCharacters, inputElements.Count);
                participant.Wpm = LiveRoomProgress.CalculateWpm(participant.CorrectCharacters, room.RaceStartsAt, now);
                delta = new LiveProgressDelta(
                    room.Id,
                    room.RoundVersion,
                    participant.ProfileId,
                    participant.CorrectCharacters,
                    participant.TypedTextPreview,
                    participant.Wpm,
                    participant.Accuracy,
                    LiveRoomProgress.CalculateRankHint(room, participant.ProfileId));
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
        var grace = TimeSpan.FromSeconds(Math.Clamp(options.Value.ReconnectGraceSeconds, 0, 300));
        var changed = false;
        foreach (var participant in room.Participants.Values)
        {
            if (participant.Status != ParticipantStatus.Disconnected || participant.DisconnectedAt is null)
            {
                continue;
            }

            if (now - participant.DisconnectedAt.Value < grace)
            {
                continue;
            }

            if (room.Phase == LiveRoomPhase.Lobby)
            {
                participant.Status = ParticipantStatus.LeftBeforeStart;
                participant.Ready = false;
                participant.FinishedAt = participant.DisconnectedAt;
                ApplyHostDisconnectRule(room);
            }
            else
            {
                participant.Status = ParticipantStatus.Dnf;
                participant.Ready = false;
                participant.FinishedAt = participant.DisconnectedAt;
                participant.DurationMilliseconds = room.RaceStartsAt is { } raceStartsAt
                    ? (int)Math.Round(LiveRoomProgress.NormalizeDuration(
                        participant.DisconnectedAt.Value - raceStartsAt).TotalMilliseconds)
                    : 0;
            }

            changed = true;
        }

        if (changed)
        {
            LiveRoomScoring.ApplyPlacements(room);
        }

        return TryCompleteRoom(room, now);
    }

    private static void ApplyHostDisconnectRule(LiveRoomState room)
    {
        if (room.Phase != LiveRoomPhase.Lobby)
        {
            return;
        }

        if (room.Participants.TryGetValue(room.CreatorProfileId, out var creator) &&
            creator.Status is ParticipantStatus.Joined or ParticipantStatus.Ready)
        {
            return;
        }

        var nextHost = room.Participants.Values
            .Where(IsLobbyActive)
            .OrderBy(item => item.JoinedAt)
            .FirstOrDefault();
        if (nextHost is null || nextHost.ProfileId == room.CreatorProfileId)
        {
            return;
        }

        room.CreatorProfileId = nextHost.ProfileId;
        room.RoundVersion++;
    }

    private CompletedRoomRecord? TryCompleteRoom(LiveRoomState room, DateTimeOffset now)
    {
        if (room.Finished || room.Phase != LiveRoomPhase.Running)
        {
            return null;
        }

        var competingParticipants = room.Participants.Values
            .Where(item => !room.ExcludedProfileIds.Contains(item.ProfileId) && item.Status != ParticipantStatus.LeftBeforeStart)
            .ToArray();
        if (competingParticipants.Length == 0)
        {
            room.Finished = true;
            room.FinishedAt = now;
            room.RoundEndsAt = now;
            room.Phase = LiveRoomPhase.Aborted;
            room.PhaseChangedAt = now;
            room.CloseReason = "Der Raum wurde beendet, weil keine wertbaren Teilnehmenden mehr vorhanden sind.";
            room.RoundVersion++;
            room.PersistenceState = CompletionState.AbortedUnconfirmed;
            return null;
        }

        var terminal = competingParticipants.All(item => item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf);
        if (!terminal)
        {
            return null;
        }

        LiveRoomScoring.ScoreCompletedRound(room, competingParticipants);
        room.RoundEndsAt = now;
        room.Phase = room.CurrentRound < room.RoundCount
            ? LiveRoomPhase.RoundResults
            : LiveRoomPhase.SeriesResults;
        room.PhaseChangedAt = now;
        room.RoundVersion++;
        if (room.Phase == LiveRoomPhase.RoundResults)
        {
            room.PersistenceState = null;
            return null;
        }

        room.Finished = true;
        room.FinishedAt = now;
        LiveRoomScoring.ApplyOverallPlacements(room);
        room.PersistenceState = CompletionState.Pending;
        return BuildPersistenceRecord(room);
    }

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

            rooms.TryRemove(room.Id, out _);
            roomCodes.TryRemove(room.Code, out _);
        }
    }

    private static int NextTeamNumber(LiveRoomState room)
    {
        var teamOne = room.Participants.Values.Count(item => item.TeamNumber == 1 && CountsTowardCapacity(item));
        var teamTwo = room.Participants.Values.Count(item => item.TeamNumber == 2 && CountsTowardCapacity(item));
        return teamOne <= teamTwo ? 1 : 2;
    }

    private void BeginCountdown(LiveRoomState room, DateTimeOffset now)
    {
        room.Phase = LiveRoomPhase.Countdown;
        room.PhaseChangedAt = now;
        room.CountdownStartsAt = now;
        room.RaceStartsAt = now.AddSeconds(Math.Clamp(options.Value.CountdownSeconds, 1, 10));
        room.RoundEndsAt = null;
        room.RoundVersion++;
        foreach (var participant in room.Participants.Values.Where(item => item.Status is ParticipantStatus.Ready or ParticipantStatus.Joined))
        {
            participant.Ready = true;
            participant.DisconnectedAt = null;
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
            participant.Wpm = 0;
            participant.Placement = null;
            participant.FinishedAt = null;
            participant.DurationMilliseconds = 0;
            participant.Accuracy = 0;
        }
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
        var exposeTargetText = room.Phase is LiveRoomPhase.Running or LiveRoomPhase.RoundResults or LiveRoomPhase.SeriesResults or LiveRoomPhase.Closed;
        var persistenceState = room.PersistenceState;
        if (room.Finished &&
            persistenceState == CompletionState.Pending &&
            room.CompletionReceipt is not null &&
            completionSink is not null)
        {
            persistenceState = completionSink.GetStatus(room.Id).State;
            room.PersistenceState = persistenceState;
            if (room.CompletionReceipt is not null)
            {
                room.CompletionReceipt = room.CompletionReceipt with { State = persistenceState.Value };
            }
        }

        if (room.Finished)
        {
            persistenceState ??= CompletionState.AbortedUnconfirmed;
        }

        return new LiveRoomSnapshot(
            room.Id,
            room.CreatorProfileId,
            room.Code,
            room.Title,
            exposeTargetText ? room.Text : "",
            room.TargetCharacterCount,
            room.MaxParticipants,
            room.Mode,
            room.Visibility,
            room.RoundCount,
            room.CurrentRound,
            room.RoundVersion,
            room.Phase,
            room.Started,
            room.Finished,
            now,
            room.PhaseChangedAt,
            room.CountdownStartsAt,
            room.RaceStartsAt,
            room.StartedAt,
            room.FinishedAt,
            room.CloseReason,
            room.Participants.Values
                .OrderBy(item => item.Placement ?? int.MaxValue)
                .ThenByDescending(item => item.CorrectCharacters)
                .ThenBy(item => item.DisplayName)
                .Select(item => new LiveParticipantSnapshot(
                    item.ProfileId,
                    item.DisplayName,
                    item.Status,
                    item.Ready,
                    item.Sequence,
                    item.CorrectCharacters,
                    exposeTargetText ? item.TypedTextPreview : "",
                    item.Wpm,
                    item.Placement,
                    item.DurationMilliseconds,
                    item.Accuracy,
                    item.TeamNumber,
                    item.SeriesPoints,
                    item.RoundWins))
                .ToList(),
            persistenceState,
            LiveRoomScoring.BuildTeamSnapshots(room),
            room.RoundEndsAt);
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

    private CompletedRoomRecord BuildPersistenceRecord(LiveRoomState room) => new(
        room.Id,
        room.CurrentRound,
        room.RoundVersion,
        $"{room.Id:N}:{room.CurrentRound}:{room.RoundVersion}",
        room.CreatorProfileId,
        room.Code,
        room.Mode,
        room.Visibility,
        room.RoundCount,
        room.CreatedAt,
        room.StartedAt,
        room.FinishedAt,
        room.Participants.Values
            .Where(item =>
                !room.ExcludedProfileIds.Contains(item.ProfileId) &&
                item.Status != ParticipantStatus.LeftBeforeStart)
            .Select(item => new CompletedParticipantRecord(
            item.ProfileId,
            item.Status == ParticipantStatus.AbortedByServer
                ? ParticipantStatus.AbortedByServer
                : item.FinishedRounds > 0 ? ParticipantStatus.Finished : ParticipantStatus.Dnf,
            item.Placement,
            item.CompletedRounds > 0 ? item.TotalDurationMilliseconds : item.DurationMilliseconds,
            item.CompletedRounds > 0 ? item.AverageWpm : item.Wpm,
            item.CompletedRounds > 0 ? item.AverageAccuracy : item.Accuracy,
            item.TeamNumber)).ToList());

    private LiveRoomSnapshot QueuePersistence(CompletedRoomRecord? record, LiveRoomSnapshot snapshot)
    {
        var state = QueuePersistence(record);
        return record is null ? snapshot : snapshot with { PersistenceState = state };
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
                room.PersistenceState = receipt.State;
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
}
