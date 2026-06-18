using System.Collections.Concurrent;
using System.Security.Cryptography;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed record LiveParticipantSnapshot(
    Guid ProfileId,
    string DisplayName,
    ParticipantStatus Status,
    bool Ready,
    int Sequence,
    int CorrectCharacters,
    double Wpm,
    int? Placement,
    int DurationMilliseconds,
    double Accuracy);

public sealed record LiveRoomSnapshot(
    Guid RoomId,
    Guid CreatorProfileId,
    string Code,
    string Title,
    string TargetText,
    int TargetCharacterCount,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    int CurrentRound,
    int RoundVersion,
    LiveRoomPhase Phase,
    bool Started,
    bool Finished,
    DateTimeOffset ServerNow,
    DateTimeOffset PhaseChangedAt,
    DateTimeOffset? CountdownStartsAt,
    DateTimeOffset? RaceStartsAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CloseReason,
    IReadOnlyList<LiveParticipantSnapshot> Participants);

public sealed record CreateLiveRoomRequest(
    Guid CreatorProfileId,
    string CreatorDisplayName,
    string Title,
    string Text,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    int MaxParticipants);

public sealed class LiveRoomManager(
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    TypingEngine typingEngine,
    ILogger<LiveRoomManager> logger,
    IServiceScopeFactory? scopeFactory = null)
{
    private const int MinimumParticipants = 2;
    private readonly object createGate = new();
    private readonly ConcurrentDictionary<Guid, LiveRoomState> rooms = new();
    private readonly ConcurrentDictionary<string, Guid> roomCodes = new(StringComparer.OrdinalIgnoreCase);

    public LiveRoomSnapshot CreateRoom(CreateLiveRoomRequest request)
    {
        if (request.Mode != LiveRoomMode.Classic)
        {
            throw new InvalidOperationException("Aktuell ist nur der Arena-Modus \"Klassisches Rennen\" implementiert.");
        }

        if (request.Visibility == LiveRoomVisibility.InvitationOnly)
        {
            throw new InvalidOperationException("Einladungsräume sind noch nicht implementiert. Verwende Code oder intern offene Räume.");
        }

        var now = timeProvider.GetUtcNow();
        CleanupExpiredRooms(now);
        lock (createGate)
        {
            if (rooms.Count >= options.Value.MaxConcurrentRooms)
            {
                throw new InvalidOperationException("Die maximale Anzahl gleichzeitiger Live-Räume ist erreicht.");
            }

            var maxParticipants = Math.Clamp(request.MaxParticipants, MinimumParticipants, options.Value.MaxParticipantsPerRoom);
            var roundCount = ValidateRoundCount(request.RoundCount);
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

            room.Participants[request.CreatorProfileId] = new LiveParticipantState(request.CreatorProfileId, request.CreatorDisplayName, ParticipantStatus.Joined, now);
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
        if (!roomCodes.TryGetValue(code.Trim(), out var roomId) || !rooms.TryGetValue(roomId, out var room))
        {
            throw new InvalidOperationException("Der Raumcode ist ungültig.");
        }

        return Join(room.Id, profileId, displayName, viaCode: true);
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
            participant.Ready = ready;
            participant.Status = ready ? ParticipantStatus.Ready : ParticipantStatus.Joined;
            participant.DisconnectedAt = null;
            snapshot = SnapshotUnlocked(room, now);
        }

        QueuePersistence(completed);
        return snapshot;
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

            if (room.Phase is LiveRoomPhase.Countdown or LiveRoomPhase.Running or LiveRoomPhase.RoundResults or LiveRoomPhase.SeriesResults)
            {
                snapshot = SnapshotUnlocked(room, now);
            }
            else
            {
                var startParticipants = room.Participants.Values.Where(IsLobbyActive).ToList();
                if (startParticipants.Count < MinimumParticipants || startParticipants.Any(item => !item.Ready))
                {
                    throw new InvalidOperationException("Der Start ist erst möglich, wenn mindestens zwei Personen bereit sind.");
                }

                room.Phase = LiveRoomPhase.Countdown;
                room.PhaseChangedAt = now;
                room.CountdownStartsAt = now;
                room.RaceStartsAt = now.AddSeconds(Math.Clamp(options.Value.CountdownSeconds, 1, 10));
                room.RoundVersion++;
                foreach (var participant in room.Participants.Values)
                {
                    participant.Ready = true;
                    participant.DisconnectedAt = null;
                }

                snapshot = SnapshotUnlocked(room, now);
            }
        }

        QueuePersistence(completed);
        return snapshot;
    }

    public LiveRoomSnapshot SubmitProgress(Guid roomId, Guid profileId, int sequence, string input)
    {
        var room = GetRoom(roomId);
        CompletedRoomRecord? completed = null;
        LiveRoomSnapshot snapshot;
        lock (room.Gate)
        {
            var now = timeProvider.GetUtcNow();
            completed = ApplyDisconnectTimeouts(room, now);
            AdvancePhase(room, now);
            var participant = RequireParticipant(room, profileId);
            if (room.Phase != LiveRoomPhase.Running || participant.Status != ParticipantStatus.Running)
            {
                snapshot = SnapshotUnlocked(room, now);
            }
            else if (sequence <= participant.Sequence)
            {
                snapshot = SnapshotUnlocked(room, now);
            }
            else
            {
                var correctCharacters = CountCorrectPrefix(room.Text, input);
                participant.Sequence = sequence;
                participant.CorrectCharacters = correctCharacters;
                participant.Wpm = CalculateWpm(participant.CorrectCharacters, room.StartedAt, now);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        QueuePersistence(completed);
        return snapshot;
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
                if (room.Phase != LiveRoomPhase.Running || room.StartedAt is null)
                {
                    throw new InvalidOperationException("Dieses Rennen wurde noch nicht gestartet.");
                }

                if (participant.Status != ParticipantStatus.Running)
                {
                    throw new InvalidOperationException("Dieser Zieleinlauf ist für deinen aktuellen Status nicht gültig.");
                }

                var duration = NormalizeDuration(now - room.StartedAt.Value);
                var metrics = typingEngine.Analyze(room.Text, input, duration, backspaces, focusLosses);
                participant.Status = metrics.Completed ? ParticipantStatus.Finished : ParticipantStatus.Dnf;
                participant.Ready = true;
                participant.FinishedAt = now;
                participant.DurationMilliseconds = metrics.DurationMilliseconds;
                participant.Accuracy = metrics.Accuracy;
                participant.Wpm = metrics.Wpm;
                participant.CorrectCharacters = metrics.CorrectCharacters;
                ApplyPlacements(room);
                completed ??= TryCompleteRoom(room, now);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        QueuePersistence(completed);
        return snapshot;
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

            ApplyHostDisconnectRule(room);
            snapshot = SnapshotUnlocked(room, now);
        }

        QueuePersistence(completed);
        return snapshot;
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

        QueuePersistence(completed);
        return snapshot;
    }

    public void Sweep()
    {
        CleanupExpiredRooms(timeProvider.GetUtcNow());
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

                room.Participants[profileId] = new LiveParticipantState(profileId, displayName, ParticipantStatus.Joined, now);
                snapshot = SnapshotUnlocked(room, now);
            }
        }

        QueuePersistence(completed);
        return snapshot;
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
                participant.DurationMilliseconds = room.StartedAt is { } startedAt
                    ? (int)Math.Round(NormalizeDuration(participant.DisconnectedAt.Value - startedAt).TotalMilliseconds)
                    : 0;
            }

            changed = true;
        }

        if (changed)
        {
            ApplyPlacements(room);
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

        var terminal = room.Participants.Values.All(item => item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf);
        if (!terminal)
        {
            return null;
        }

        room.Finished = true;
        room.FinishedAt = now;
        room.RoundEndsAt = now;
        room.Phase = LiveRoomPhase.SeriesResults;
        room.PhaseChangedAt = now;
        room.RoundVersion++;
        if (room.PersistenceQueued)
        {
            return null;
        }

        room.PersistenceQueued = true;
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

    private static void ApplyPlacements(LiveRoomState room)
    {
        var ranked = RaceRanking.RankClassic(room.Participants.Values
            .Where(item => item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf)
            .Select(item => new RaceResult(
                item.ProfileId,
                item.Status,
                item.DurationMilliseconds,
                item.Accuracy,
                0,
                100,
                item.Wpm,
                item.CorrectCharacters)));

        foreach (var rankedResult in ranked)
        {
            room.Participants[rankedResult.Result.UserProfileId].Placement = rankedResult.Placement;
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

    private static LiveRoomSnapshot SnapshotUnlocked(LiveRoomState room, DateTimeOffset now)
    {
        var targetCharacters = TypingEngine.SplitGraphemes(room.Text).Count;
        var exposeTargetText = room.Phase is LiveRoomPhase.Running or LiveRoomPhase.RoundResults or LiveRoomPhase.SeriesResults or LiveRoomPhase.Closed;
        return new LiveRoomSnapshot(
            room.Id,
            room.CreatorProfileId,
            room.Code,
            room.Title,
            exposeTargetText ? room.Text : "",
            targetCharacters,
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
                    item.Wpm,
                    item.Placement,
                    item.DurationMilliseconds,
                    item.Accuracy))
                .ToList());
    }

    private static int ValidateRoundCount(int roundCount)
    {
        return roundCount is 1 or 3 or 5
            ? roundCount
            : throw new InvalidOperationException("Arena-Serien unterstützen aktuell 1, 3 oder 5 Runden.");
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

    private static int CountCorrectPrefix(string target, string input)
    {
        var targetElements = TypingEngine.SplitGraphemes(target);
        var inputElements = TypingEngine.SplitGraphemes(input);
        var count = 0;
        for (var index = 0; index < Math.Min(targetElements.Count, inputElements.Count); index++)
        {
            if (!StringComparer.Ordinal.Equals(targetElements[index], inputElements[index]))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static double CalculateWpm(int correctCharacters, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null)
        {
            return 0;
        }

        var minutes = Math.Max((now - startedAt.Value).TotalMinutes, 1d / 60d);
        return Math.Round(correctCharacters / 5d / minutes, 2);
    }

    private static TimeSpan NormalizeDuration(TimeSpan duration) => duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : duration;

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
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[6];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    private CompletedRoomRecord BuildPersistenceRecord(LiveRoomState room) => new(
        room.Id,
        room.CreatorProfileId,
        room.Code,
        room.Mode,
        room.Visibility,
        room.RoundCount,
        room.CreatedAt,
        room.StartedAt,
        room.FinishedAt,
        room.Participants.Values.Select(item => new CompletedParticipantRecord(
            item.ProfileId,
            item.Status,
            item.Placement,
            item.DurationMilliseconds,
            item.Wpm,
            item.Accuracy)).ToList());

    private void QueuePersistence(CompletedRoomRecord? record)
    {
        if (record is null || scopeFactory is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                if (await db.LiveRoomSummaries.AnyAsync(item => item.Id == record.Id))
                {
                    return;
                }

                db.LiveRoomSummaries.Add(new LiveRoomSummary
                {
                    Id = record.Id,
                    CreatorProfileId = record.CreatorProfileId,
                    RoomCode = record.RoomCode,
                    Mode = record.Mode,
                    Visibility = record.Visibility,
                    RoundCount = record.RoundCount,
                    CreatedAt = record.CreatedAt,
                    StartedAt = record.StartedAt,
                    FinishedAt = record.FinishedAt
                });

                foreach (var participant in record.Participants)
                {
                    db.LiveRoomParticipantSummaries.Add(new LiveRoomParticipantSummary
                    {
                        LiveRoomSummaryId = record.Id,
                        UserProfileId = participant.UserProfileId,
                        Status = participant.Status,
                        Placement = participant.Placement,
                        DurationMilliseconds = participant.DurationMilliseconds,
                        Wpm = participant.Wpm,
                        Accuracy = participant.Accuracy
                    });
                }

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Live-Raum {RoomId} konnte nicht persistiert werden.", record.Id);
            }
        });
    }
}

internal sealed class LiveRoomState(
    Guid id,
    Guid creatorProfileId,
    string code,
    string title,
    string text,
    LiveRoomMode mode,
    LiveRoomVisibility visibility,
    int roundCount,
    int maxParticipants,
    DateTimeOffset createdAt)
{
    public Guid Id { get; } = id;
    public Guid CreatorProfileId { get; set; } = creatorProfileId;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Text { get; } = text;
    public LiveRoomMode Mode { get; } = mode;
    public LiveRoomVisibility Visibility { get; } = visibility;
    public int RoundCount { get; } = roundCount;
    public int MaxParticipants { get; } = maxParticipants;
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public object Gate { get; } = new();
    public Dictionary<Guid, LiveParticipantState> Participants { get; } = [];
    public LiveRoomPhase Phase { get; set; } = LiveRoomPhase.Lobby;
    public int CurrentRound { get; set; } = 1;
    public int RoundVersion { get; set; } = 1;
    public DateTimeOffset PhaseChangedAt { get; set; } = createdAt;
    public DateTimeOffset? CountdownStartsAt { get; set; }
    public DateTimeOffset? RaceStartsAt { get; set; }
    public DateTimeOffset? RoundEndsAt { get; set; }
    public string? CloseReason { get; set; }
    public bool Started { get; set; }
    public bool Finished { get; set; }
    public bool PersistenceQueued { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

internal sealed class LiveParticipantState(Guid profileId, string displayName, ParticipantStatus status, DateTimeOffset joinedAt)
{
    public Guid ProfileId { get; } = profileId;
    public DateTimeOffset JoinedAt { get; } = joinedAt;
    public string DisplayName { get; set; } = displayName;
    public ParticipantStatus Status { get; set; } = status;
    public bool Ready { get; set; }
    public int Sequence { get; set; }
    public int CorrectCharacters { get; set; }
    public double Wpm { get; set; }
    public int? Placement { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public int DurationMilliseconds { get; set; }
    public double Accuracy { get; set; }
}

internal sealed record CompletedRoomRecord(
    Guid Id,
    Guid CreatorProfileId,
    string RoomCode,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<CompletedParticipantRecord> Participants);

internal sealed record CompletedParticipantRecord(
    Guid UserProfileId,
    ParticipantStatus Status,
    int? Placement,
    int DurationMilliseconds,
    double Wpm,
    double Accuracy);
