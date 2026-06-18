using System.Collections.Concurrent;
using System.Threading.Channels;
using KeyWars.Domain;
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
    int? Placement);

public sealed record LiveRoomSnapshot(
    Guid RoomId,
    string Code,
    string Title,
    string TargetText,
    LiveRoomMode Mode,
    LiveRoomVisibility Visibility,
    int RoundCount,
    bool Started,
    bool Finished,
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

public abstract record LiveRoomCommand;
public sealed record ProgressCommand(Guid ProfileId, int Sequence, int CorrectCharacters, double Wpm) : LiveRoomCommand;

public sealed class LiveRoomManager(IOptions<LiveOptions> options, TimeProvider timeProvider, ILogger<LiveRoomManager> logger)
{
    private readonly ConcurrentDictionary<Guid, LiveRoomState> rooms = new();
    private readonly ConcurrentDictionary<string, Guid> roomCodes = new(StringComparer.OrdinalIgnoreCase);

    public LiveRoomSnapshot CreateRoom(CreateLiveRoomRequest request)
    {
        if (rooms.Count >= options.Value.MaxConcurrentRooms)
        {
            throw new InvalidOperationException("Die maximale Anzahl gleichzeitiger Live-Räume ist erreicht.");
        }

        var maxParticipants = Math.Clamp(request.MaxParticipants, 2, options.Value.MaxParticipantsPerRoom);
        var room = new LiveRoomState(
            Guid.CreateVersion7(),
            GenerateCode(),
            request.Title,
            request.Text,
            request.Mode,
            request.Visibility,
            Math.Clamp(request.RoundCount, 1, 7),
            maxParticipants,
            options.Value.RoomCommandQueueCapacity);
        room.Participants[request.CreatorProfileId] = new LiveParticipantState(request.CreatorProfileId, request.CreatorDisplayName, ParticipantStatus.Joined);
        room.StartProcessor(ProcessCommandsAsync(room));
        rooms[room.Id] = room;
        roomCodes[room.Code] = room.Id;
        return Snapshot(room);
    }

    public IReadOnlyList<LiveRoomSnapshot> ListOpenRooms()
    {
        return rooms.Values
            .Where(room => room.Visibility == LiveRoomVisibility.InternalOpen && !room.Finished)
            .Select(Snapshot)
            .OrderBy(room => room.Title)
            .ToList();
    }

    public LiveRoomSnapshot JoinByCode(string code, Guid profileId, string displayName)
    {
        if (!roomCodes.TryGetValue(code, out var roomId) || !rooms.TryGetValue(roomId, out var room))
        {
            throw new InvalidOperationException("Der Raumcode ist ungültig.");
        }

        return Join(room.Id, profileId, displayName);
    }

    public LiveRoomSnapshot Join(Guid roomId, Guid profileId, string displayName)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            if (room.Started)
            {
                throw new InvalidOperationException("Der Raum läuft bereits.");
            }

            if (!room.Participants.ContainsKey(profileId) && room.Participants.Count >= room.MaxParticipants)
            {
                throw new InvalidOperationException("Dieser Raum ist voll.");
            }

            room.Participants[profileId] = new LiveParticipantState(profileId, displayName, ParticipantStatus.Joined);
            return Snapshot(room);
        }
    }

    public LiveRoomSnapshot SetReady(Guid roomId, Guid profileId, bool ready)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            var participant = RequireParticipant(room, profileId);
            participant.Ready = ready;
            participant.Status = ready ? ParticipantStatus.Ready : ParticipantStatus.Joined;
            return Snapshot(room);
        }
    }

    public LiveRoomSnapshot Start(Guid roomId, Guid profileId)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            _ = RequireParticipant(room, profileId);
            if (room.Started)
            {
                return Snapshot(room);
            }

            if (room.Participants.Count < 2 || room.Participants.Values.Any(item => !item.Ready))
            {
                throw new InvalidOperationException("Der Start ist erst möglich, wenn mindestens zwei Personen bereit sind.");
            }

            room.Started = true;
            room.StartedAt = timeProvider.GetUtcNow();
            foreach (var participant in room.Participants.Values)
            {
                participant.Status = ParticipantStatus.Running;
            }

            return Snapshot(room);
        }
    }

    public bool SubmitProgress(Guid roomId, Guid profileId, int sequence, int correctCharacters, double wpm)
    {
        var room = GetRoom(roomId);
        return room.Commands.Writer.TryWrite(new ProgressCommand(profileId, sequence, correctCharacters, wpm));
    }

    public LiveRoomSnapshot Finish(Guid roomId, Guid profileId, TypingMetrics metrics)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            var participant = RequireParticipant(room, profileId);
            if (participant.Status == ParticipantStatus.Finished)
            {
                return Snapshot(room);
            }

            participant.Status = metrics.Completed ? ParticipantStatus.Finished : ParticipantStatus.Dnf;
            participant.FinishedAt = timeProvider.GetUtcNow();
            participant.DurationMilliseconds = metrics.DurationMilliseconds;
            participant.Accuracy = metrics.Accuracy;
            participant.Wpm = metrics.Wpm;
            participant.CorrectCharacters = metrics.CorrectCharacters;
            ApplyPlacements(room);
            room.Finished = room.Participants.Values.All(item => item.Status is ParticipantStatus.Finished or ParticipantStatus.Dnf);
            return Snapshot(room);
        }
    }

    public LiveRoomSnapshot Disconnect(Guid roomId, Guid profileId)
    {
        var room = GetRoom(roomId);
        lock (room.Gate)
        {
            var participant = RequireParticipant(room, profileId);
            if (participant.Status == ParticipantStatus.Running)
            {
                participant.Status = ParticipantStatus.Disconnected;
                participant.DisconnectedAt = timeProvider.GetUtcNow();
            }

            return Snapshot(room);
        }
    }

    public LiveRoomSnapshot Snapshot(Guid roomId) => Snapshot(GetRoom(roomId));

    private async Task ProcessCommandsAsync(LiveRoomState room)
    {
        await foreach (var command in room.Commands.Reader.ReadAllAsync())
        {
            if (command is not ProgressCommand progress)
            {
                continue;
            }

            lock (room.Gate)
            {
                if (!room.Participants.TryGetValue(progress.ProfileId, out var participant))
                {
                    continue;
                }

                if (progress.Sequence <= participant.Sequence)
                {
                    continue;
                }

                participant.Sequence = progress.Sequence;
                participant.CorrectCharacters = Math.Max(participant.CorrectCharacters, progress.CorrectCharacters);
                participant.Wpm = Math.Max(0, progress.Wpm);
            }
        }

        logger.LogDebug("Live-Raumprozessor für {RoomId} beendet.", room.Id);
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

    private static LiveRoomSnapshot Snapshot(LiveRoomState room)
    {
        lock (room.Gate)
        {
            return new LiveRoomSnapshot(
                room.Id,
                room.Code,
                room.Title,
                room.Text,
                room.Mode,
                room.Visibility,
                room.RoundCount,
                room.Started,
                room.Finished,
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
                        item.Placement))
                    .ToList());
        }
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[6];
        for (var index = 0; index < chars.Length; index++)
        {
            chars[index] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return new string(chars);
    }
}

internal sealed class LiveRoomState(
    Guid id,
    string code,
    string title,
    string text,
    LiveRoomMode mode,
    LiveRoomVisibility visibility,
    int roundCount,
    int maxParticipants,
    int queueCapacity)
{
    public Guid Id { get; } = id;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Text { get; } = text;
    public LiveRoomMode Mode { get; } = mode;
    public LiveRoomVisibility Visibility { get; } = visibility;
    public int RoundCount { get; } = roundCount;
    public int MaxParticipants { get; } = maxParticipants;
    public object Gate { get; } = new();
    public Dictionary<Guid, LiveParticipantState> Participants { get; } = [];
    public Channel<LiveRoomCommand> Commands { get; } = Channel.CreateBounded<LiveRoomCommand>(new BoundedChannelOptions(queueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public bool Started { get; set; }
    public bool Finished { get; set; }
    public DateTimeOffset? StartedAt { get; set; }

    public void StartProcessor(Task processor)
    {
        _ = processor;
    }
}

internal sealed class LiveParticipantState(Guid profileId, string displayName, ParticipantStatus status)
{
    public Guid ProfileId { get; } = profileId;
    public string DisplayName { get; } = displayName;
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
