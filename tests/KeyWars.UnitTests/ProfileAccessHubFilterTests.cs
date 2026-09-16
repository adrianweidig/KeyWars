using System.Reflection;
using System.Security.Claims;
using System.Data.Common;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Infrastructure;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeyWars.UnitTests;

public sealed class ProfileAccessHubFilterTests
{
    [Fact]
    public async Task RedisMissForDeletedProfileAbortsConnectionAndRestoresPermanentMarker()
    {
        await using var harness = await HubHarness.CreateAsync(deleted: true);
        var caller = Caller(harness.ProfileId);
        var nextCalled = false;

        var error = await Assert.ThrowsAsync<HubException>(() =>
            harness.Filter.InvokeMethodAsync(
                Invocation(caller, harness.Services),
                _ =>
                {
                    nextCalled = true;
                    return ValueTask.FromResult<object?>(null);
                }).AsTask());

        Assert.Equal("Die Profilsitzung ist nicht mehr gültig.", error.Message);
        Assert.True(caller.Aborted);
        Assert.False(nextCalled);
        Assert.Equal(harness.ProfileId, Assert.Single(harness.AccessGate.RestoredMarkers));
    }

    [Fact]
    public async Task PreviouslyValidatedConnectionIsBlockedOnTheNextInvokeAfterDeletionAndRedisLoss()
    {
        await using var harness = await HubHarness.CreateAsync(deleted: false);
        var caller = Caller(harness.ProfileId);
        var successfulCalls = 0;

        await harness.Filter.InvokeMethodAsync(
            Invocation(caller, harness.Services),
            _ =>
            {
                successfulCalls++;
                return ValueTask.FromResult<object?>(null);
            });
        Assert.True(harness.DatabaseCommands.ReaderCommands > 0);
        Assert.False(harness.AccessGate.RequiresValidation);

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
            var profile = await db.UserProfiles.SingleAsync(item => item.Id == harness.ProfileId);
            profile.Deleted = true;
            await db.SaveChangesAsync();
        }

        harness.AccessGate.SimulateRedisStateLoss();
        harness.DatabaseCommands.Reset();

        await Assert.ThrowsAsync<HubException>(() =>
            harness.Filter.InvokeMethodAsync(
                Invocation(caller, harness.Services),
                _ =>
                {
                    successfulCalls++;
                    return ValueTask.FromResult<object?>(null);
                }).AsTask());
        Assert.True(caller.Aborted);
        Assert.Equal(1, successfulCalls);
        Assert.True(harness.DatabaseCommands.ReaderCommands > 0);
        Assert.Equal(harness.ProfileId, Assert.Single(harness.AccessGate.RestoredMarkers));
    }

    [Fact]
    public async Task PersistentActiveStateRemovesDatabaseReadsFromThePositiveHotPath()
    {
        await using var harness = await HubHarness.CreateAsync(deleted: false);
        var caller = Caller(harness.ProfileId);

        await harness.Filter.InvokeMethodAsync(
            Invocation(caller, harness.Services),
            _ => ValueTask.FromResult<object?>(null));
        Assert.False(harness.AccessGate.RequiresValidation);

        harness.DatabaseCommands.Reset();
        await harness.Filter.InvokeMethodAsync(
            Invocation(caller, harness.Services),
            _ => ValueTask.FromResult<object?>(null));

        Assert.Equal(0, harness.DatabaseCommands.ReaderCommands);
        Assert.False(caller.Aborted);
    }

    [Fact]
    public async Task DeletedProfileRemainsBlockedWhenRedisMarkerBackfillFails()
    {
        await using var harness = await HubHarness.CreateAsync(deleted: true);
        harness.AccessGate.ThrowOnRestore = true;
        var caller = Caller(harness.ProfileId);
        var nextCalled = false;

        await Assert.ThrowsAsync<HubException>(() =>
            harness.Filter.InvokeMethodAsync(
                Invocation(caller, harness.Services),
                _ =>
                {
                    nextCalled = true;
                    return ValueTask.FromResult<object?>(null);
                }).AsTask());

        Assert.True(caller.Aborted);
        Assert.False(nextCalled);
        Assert.Empty(harness.AccessGate.RestoredMarkers);
    }

    [Fact]
    public async Task ExistingDeletedMarkerAbortsBeforeInvokingHubMethod()
    {
        await using var harness = await HubHarness.CreateAsync(deleted: false);
        harness.AccessGate.Deleted = true;
        var caller = Caller(harness.ProfileId);
        var nextCalled = false;

        await Assert.ThrowsAsync<HubException>(() =>
            harness.Filter.InvokeMethodAsync(
                Invocation(caller, harness.Services),
                _ =>
                {
                    nextCalled = true;
                    return ValueTask.FromResult<object?>(null);
                }).AsTask());

        Assert.True(caller.Aborted);
        Assert.False(nextCalled);
        Assert.Empty(harness.AccessGate.RestoredMarkers);
    }

    private static TestHubCallerContext Caller(Guid profileId) => new(new ClaimsPrincipal(
        new ClaimsIdentity(
            [new Claim(KeyWarsClaims.ProfileId, profileId.ToString("D"))],
            "test")));

    private static HubInvocationContext Invocation(
        HubCallerContext caller,
        IServiceProvider services) => new(
        caller,
        services,
        new TestHub(),
        typeof(TestHub).GetMethod(nameof(TestHub.Invoke), BindingFlags.Instance | BindingFlags.Public)!,
        []);

    private sealed class TestHub : Hub
    {
        public Task Invoke() => Task.CompletedTask;
    }

    private sealed class TestHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        private readonly CancellationTokenSource aborted = new();

        public bool Aborted { get; private set; }
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => aborted.Token;

        public override void Abort()
        {
            Aborted = true;
            aborted.Cancel();
        }
    }

    private sealed class RecordingAccessGate : IProfileAccessGate, IAuthoritativeProfileDeletionMarker
    {
        public bool Deleted { get; set; }
        public bool ThrowOnRestore { get; set; }
        public bool RequiresValidation { get; private set; } = true;
        public List<Guid> RestoredMarkers { get; } = [];

        public void SimulateRedisStateLoss() => RequiresValidation = true;

        public ValueTask<ProfileAccessState> GetStateAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Deleted ? ProfileAccessState.Deleted : ProfileAccessState.Available);

        public ValueTask<IOperationLease> AcquireAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Deleted
                ? ValueTask.FromException<IOperationLease>(new ProfileOperationException(
                    "profile_deleted",
                    "Dieses Profil wurde bereits gelöscht."))
                : ValueTask.FromResult<IOperationLease>(new Lease(this, RequiresValidation));
        }

        public async ValueTask<IOperationLease> AcquireManyAsync(
            IEnumerable<Guid> profileIds,
            CancellationToken cancellationToken = default) =>
            await AcquireAsync(profileIds.First(), cancellationToken);

        public ValueTask<IOperationLease?> TryBeginOperationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IOperationLease?>(null);

        public Task WaitForIdleAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask CompleteOperationAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask MarkDeletedAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask MarkDeletedAuthoritativelyAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnRestore)
            {
                throw new InvalidOperationException("Simulierter Redis-Ausfall.");
            }

            RestoredMarkers.Add(profileId);
            Deleted = true;
            return ValueTask.CompletedTask;
        }

        private sealed class Lease(
            RecordingAccessGate owner,
            bool requiresValidation) : IAuthoritativeProfileValidationLease
        {
            public CancellationToken LeaseLost => CancellationToken.None;
            public bool RequiresAuthoritativeValidation { get; private set; } = requiresValidation;
            public void ThrowIfLost()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public ValueTask MarkAuthoritativelyValidatedAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Deleted)
                {
                    return ValueTask.FromException(new ProfileOperationException(
                        "profile_deleted",
                        "Dieses Profil wurde bereits gelöscht."));
                }

                owner.RequiresValidation = false;
                RequiresAuthoritativeValidation = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int readerCommands;

        public int ReaderCommands => Volatile.Read(ref readerCommands);

        public void Reset() => Interlocked.Exchange(ref readerCommands, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readerCommands);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class HubHarness(
        SqliteConnection connection,
        ServiceProvider services,
        RecordingAccessGate accessGate,
        CountingCommandInterceptor databaseCommands,
        Guid profileId) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public RecordingAccessGate AccessGate { get; } = accessGate;
        public CountingCommandInterceptor DatabaseCommands { get; } = databaseCommands;
        public Guid ProfileId { get; } = profileId;
        public ProfileAccessHubFilter Filter { get; } = new(
            accessGate,
            new SingleNodeSharedRateLimiter(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProfileAccessHubFilter>.Instance);

        public static async Task<HubHarness> CreateAsync(bool deleted)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var databaseCommands = new CountingCommandInterceptor();
            var services = new ServiceCollection()
                .AddDbContext<KeyWarsDbContext>(options =>
                    options.UseSqlite(connection).AddInterceptors(databaseCommands))
                .BuildServiceProvider();
            var profileId = Guid.CreateVersion7();
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.UserProfiles.Add(new UserProfile
                {
                    Id = profileId,
                    DirectoryObjectGuid = Guid.CreateVersion7().ToString("D"),
                    DirectorySid = $"S-1-5-21-{Random.Shared.Next(100000, 999999)}",
                    SamAccountName = $"hub-{profileId:N}",
                    UserPrincipalName = $"hub-{profileId:N}@internal.invalid",
                    DisplayName = "Hub-Sicherheitstest",
                    Deleted = deleted
                });
                await db.SaveChangesAsync();
            }
            databaseCommands.Reset();

            return new HubHarness(
                connection,
                services,
                new RecordingAccessGate(),
                databaseCommands,
                profileId);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
