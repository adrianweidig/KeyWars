using System.Diagnostics;
using System.Text.Json;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace KeyWars.Infrastructure.Cluster;

public sealed class RedisLiveProgressRelay(
    IConnectionMultiplexer redis,
    ILiveProgressSender sender,
    IOptions<LiveOptions> options,
    TimeProvider timeProvider,
    KeyWarsTelemetry telemetry,
    ILogger<RedisLiveProgressRelay> logger) : BackgroundService
{
    private const string Prefix = "keywars:progress";
    private static readonly RedisKey DueRoomsKey = $"{Prefix}:due";
    private readonly IDatabase database = redis.GetDatabase();
    private readonly TimeSpan broadcastInterval = TimeSpan.FromSeconds(
        1d / Math.Clamp(options.Value.ProgressBroadcastHz, 1, 60));

    public async ValueTask EnqueueAsync(LiveProgressDelta delta, CancellationToken cancellationToken)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(delta.RoomId),
            cancellationToken);
        var payload = JsonSerializer.Serialize(delta);
        var participant = delta.ParticipantId.ToString("N");
        await database.HashSetAsync(PendingKey(delta.RoomId), participant, payload);
        await database.HashSetAsync(LatestKey(delta.RoomId), participant, payload);
        await database.KeyExpireAsync(PendingKey(delta.RoomId), TimeSpan.FromMinutes(10));
        await database.KeyExpireAsync(LatestKey(delta.RoomId), TimeSpan.FromHours(2));
        await database.SortedSetAddAsync(
            DueRoomsKey,
            delta.RoomId.ToString("N"),
            timeProvider.GetUtcNow().Add(broadcastInterval).ToUnixTimeMilliseconds(),
            When.NotExists);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var due = await database.SortedSetRangeByScoreAsync(
                    DueRoomsKey,
                    stop: timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    take: 64);
                foreach (var value in due)
                {
                    if (Guid.TryParseExact(value.ToString(), "N", out var roomId))
                    {
                        await TryBroadcastAsync(roomId, stoppingToken);
                    }
                    else
                    {
                        await database.SortedSetRemoveAsync(DueRoomsKey, value);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Der verteilte Arena-Fortschritt konnte nicht gesendet werden.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeProvider, stoppingToken);
        }
    }

    private async Task TryBroadcastAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var roomLock = await RedisDistributedLease.AcquireAsync(
            database,
            LockKey(roomId),
            cancellationToken);
        var dueAt = await database.SortedSetScoreAsync(DueRoomsKey, roomId.ToString("N"));
        if (dueAt is null || dueAt > timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            return;
        }

        var pending = await database.HashGetAllAsync(PendingKey(roomId));
        await database.KeyDeleteAsync(PendingKey(roomId));
        await database.SortedSetRemoveAsync(DueRoomsKey, roomId.ToString("N"));
        if (pending.Length == 0)
        {
            return;
        }

        var latest = (await database.HashGetAllAsync(LatestKey(roomId)))
            .Select(entry => Deserialize(entry.Value))
            .Where(delta => delta is not null)
            .Cast<LiveProgressDelta>()
            .ToArray();
        var ranks = latest
            .OrderByDescending(delta => delta.CorrectCharacters)
            .ThenByDescending(delta => delta.Wpm)
            .ThenBy(delta => delta.ParticipantId)
            .Select((delta, index) => new { delta.ParticipantId, Rank = index + 1 })
            .ToDictionary(item => item.ParticipantId, item => item.Rank);
        var deltas = new List<LiveProgressDelta>();
        foreach (var entry in pending)
        {
            var delta = Deserialize(entry.Value);
            if (delta is null)
            {
                continue;
            }

            var sentSequence = await database.HashGetAsync(SentKey(roomId), entry.Name);
            if (!sentSequence.IsNull && int.TryParse(sentSequence.ToString(), out var sent) && delta.ParticipantSequence <= sent)
            {
                continue;
            }

            deltas.Add(delta with { RankHint = ranks.GetValueOrDefault(delta.ParticipantId) });
        }

        if (deltas.Count == 0)
        {
            return;
        }

        var ordered = deltas
            .OrderBy(delta => delta.RankHint)
            .ThenBy(delta => delta.ParticipantId)
            .ToArray();
        var batch = new LiveProgressBatch(
            roomId,
            ordered.Max(delta => delta.RoomVersion),
            timeProvider.GetUtcNow(),
            ordered);
        var stopwatch = Stopwatch.StartNew();
        await sender.SendAsync(roomId, batch, CancellationToken.None);
        foreach (var delta in ordered)
        {
            await database.HashSetAsync(
                SentKey(roomId),
                delta.ParticipantId.ToString("N"),
                delta.ParticipantSequence);
        }

        await database.KeyExpireAsync(SentKey(roomId), TimeSpan.FromHours(2));
        telemetry.RecordProgress(
            "broadcast",
            ordered.Length,
            ordered.Sum(delta => delta.TypedStateBits.Length),
            stopwatch.Elapsed);
    }

    private static LiveProgressDelta? Deserialize(RedisValue value) =>
        JsonSerializer.Deserialize<LiveProgressDelta>(value.ToString());

    private static RedisKey PendingKey(Guid roomId) => $"{Prefix}:pending:{roomId:N}";
    private static RedisKey LatestKey(Guid roomId) => $"{Prefix}:latest:{roomId:N}";
    private static RedisKey SentKey(Guid roomId) => $"{Prefix}:sent:{roomId:N}";
    private static RedisKey LockKey(Guid roomId) => $"{Prefix}:lock:{roomId:N}";
}
