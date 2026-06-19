using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class LiveRoomCompletionQueueTests
{
    [Fact]
    public async Task FlushPersistsRoomResultsAndRatingExactlyOnce()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var first = context.FirstProfileId;
        var second = context.SecondProfileId;
        var roomId = Guid.CreateVersion7();
        var record = CreateRecord(roomId, first, second);

        context.Queue.Enqueue(record);
        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var room = await db.LiveRoomSummaries.SingleAsync();
        var participants = await db.LiveRoomParticipantSummaries.OrderBy(item => item.Placement).ToListAsync();
        var profiles = await db.UserProfiles.OrderBy(item => item.DisplayName).ToListAsync();

        Assert.Equal(record.Id, room.Id);
        Assert.Equal(record.IdempotencyKey, room.IdempotencyKey);
        Assert.Equal(1, room.RoundNumber);
        Assert.Equal(2, participants.Count);
        Assert.Equal(1, profiles[0].RatedMatchCount);
        Assert.Equal(1, profiles[1].RatedMatchCount);
        Assert.True(profiles.Single(item => item.Id == first).ArenaRating > 1000);
        Assert.True(profiles.Single(item => item.Id == second).ArenaRating < 1000);
        Assert.Equal(profiles.Single(item => item.Id == first).ArenaRating - 1000, participants.Single(item => item.UserProfileId == first).RatingDelta);
        Assert.Equal(profiles.Single(item => item.Id == second).ArenaRating - 1000, participants.Single(item => item.UserProfileId == second).RatingDelta);
        Assert.Equal(2, await db.RewardLedgerEntries.CountAsync(item => item.Source == "arena"));
        Assert.Contains(await db.Missions.ToListAsync(), item => item.UserProfileId == first && item.Key == "daily-arena-or-team" && item.Completed);
    }

    [Fact]
    public async Task StopAsyncFlushesQueuedCompletions()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);

        context.Queue.Enqueue(record);
        await context.Queue.StopAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync());
        Assert.Equal(0, context.Queue.PendingCount);
    }

    [Fact]
    public async Task ServerAbortPersistsWithoutRatingChange()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId, abortedByServer: true);

        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var room = await db.LiveRoomSummaries.SingleAsync();
        var participants = await db.LiveRoomParticipantSummaries.ToListAsync();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.True(room.AbortedByServer);
        Assert.All(participants, participant => Assert.Equal(0, participant.RatingDelta));
        Assert.All(profiles, profile => Assert.Equal(1000, profile.ArenaRating));
        Assert.All(profiles, profile => Assert.Equal(0, profile.RatedMatchCount));
    }

    [Fact]
    public async Task ConcurrentRoomCompletionsUpdateRatingsOncePerRoom()
    {
        await using var context = await CompletionTestContext.CreateAsync();
        var firstRecord = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);
        var secondRecord = CreateRecord(Guid.CreateVersion7(), context.SecondProfileId, context.FirstProfileId);

        await Task.WhenAll(
            Task.Run(() => context.Queue.Enqueue(firstRecord)),
            Task.Run(() => context.Queue.Enqueue(secondRecord)));
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var profiles = await db.UserProfiles.ToListAsync();

        Assert.Equal(2, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(4, await db.LiveRoomParticipantSummaries.CountAsync());
        Assert.All(profiles, profile => Assert.Equal(2, profile.RatedMatchCount));
    }

    [Fact]
    public async Task FlushRetriesAfterTransientSqliteFailure()
    {
        await using var context = await CompletionTestContext.CreateAsync(transientFailureOnFirstWrite: true);
        var record = CreateRecord(Guid.CreateVersion7(), context.FirstProfileId, context.SecondProfileId);

        context.Queue.Enqueue(record);
        await context.Queue.FlushAsync(CancellationToken.None);

        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        Assert.Equal(2, context.WriterAttempts);
        Assert.Equal(1, await db.LiveRoomSummaries.CountAsync());
        Assert.Equal(2, await db.LiveRoomParticipantSummaries.CountAsync());
    }

    private static CompletedRoomRecord CreateRecord(Guid roomId, Guid first, Guid second, bool abortedByServer = false)
    {
        var createdAt = DateTimeOffset.Parse("2026-06-18T12:00:00Z");
        return new CompletedRoomRecord(
            roomId,
            1,
            2,
            $"{roomId:N}:1:2",
            first,
            "ABC123",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InternalOpen,
            1,
            createdAt,
            createdAt.AddSeconds(3),
            createdAt.AddSeconds(35),
            [
                new CompletedParticipantRecord(first, abortedByServer ? ParticipantStatus.AbortedByServer : ParticipantStatus.Finished, abortedByServer ? null : 1, 32000, 80, 100),
                new CompletedParticipantRecord(second, abortedByServer ? ParticipantStatus.AbortedByServer : ParticipantStatus.Finished, abortedByServer ? null : 2, 42000, 60, 100)
            ]);
    }

    private sealed class CompletionTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CompletionTestContext(SqliteConnection connection, ServiceProvider services, LiveRoomCompletionQueue queue, Guid firstProfileId, Guid secondProfileId)
        {
            this.connection = connection;
            Services = services;
            Queue = queue;
            FirstProfileId = firstProfileId;
            SecondProfileId = secondProfileId;
        }

        public ServiceProvider Services { get; }
        public LiveRoomCompletionQueue Queue { get; }
        public Guid FirstProfileId { get; }
        public Guid SecondProfileId { get; }
        public int WriterAttempts => flakyWriter?.Attempts ?? 0;

        private FlakyCompletionWriter? flakyWriter;

        public static async Task<CompletionTestContext> CreateAsync(bool transientFailureOnFirstWrite = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<KeyWarsDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton(Options.Create(new LiveOptions { CompletionQueueCapacity = 16 }));
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddScoped<MotivationService>();
            services.AddSingleton<SqliteLiveRoomCompletionWriter>();
            FlakyCompletionWriter? flakyWriter = null;
            if (transientFailureOnFirstWrite)
            {
                services.AddSingleton<ILiveRoomCompletionWriter>(provider =>
                {
                    flakyWriter = new FlakyCompletionWriter(provider.GetRequiredService<SqliteLiveRoomCompletionWriter>());
                    return flakyWriter;
                });
            }
            else
            {
                services.AddSingleton<ILiveRoomCompletionWriter>(provider => provider.GetRequiredService<SqliteLiveRoomCompletionWriter>());
            }

            services.AddSingleton<LiveRoomCompletionQueue>();
            services.AddSingleton<ILiveRoomCompletionSink>(provider => provider.GetRequiredService<LiveRoomCompletionQueue>());
            services.AddSingleton<ILogger<LiveRoomCompletionQueue>>(NullLogger<LiveRoomCompletionQueue>.Instance);
            var provider = services.BuildServiceProvider();

            Guid first;
            Guid second;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                await db.Database.EnsureCreatedAsync();
                var firstProfile = new UserProfile
                {
                    DisplayName = "Anna Arena",
                    SamAccountName = "aarena",
                    DirectoryObjectGuid = Guid.NewGuid().ToString(),
                    DirectorySid = "S-1"
                };
                var secondProfile = new UserProfile
                {
                    DisplayName = "Bernd Arena",
                    SamAccountName = "barena",
                    DirectoryObjectGuid = Guid.NewGuid().ToString(),
                    DirectorySid = "S-2"
                };
                db.UserProfiles.AddRange(firstProfile, secondProfile);
                await db.SaveChangesAsync();
                first = firstProfile.Id;
                second = secondProfile.Id;
            }

            var queue = provider.GetRequiredService<LiveRoomCompletionQueue>();
            return new CompletionTestContext(connection, provider, queue, first, second)
            {
                flakyWriter = flakyWriter
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FlakyCompletionWriter(SqliteLiveRoomCompletionWriter inner) : ILiveRoomCompletionWriter
    {
        public int Attempts { get; private set; }

        public Task PersistAsync(CompletedRoomRecord record, CancellationToken cancellationToken)
        {
            Attempts++;
            return Attempts == 1
                ? Task.FromException(new SqliteException("database is locked", 5))
                : inner.PersistAsync(record, cancellationToken);
        }
    }
}
