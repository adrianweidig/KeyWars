using System.Reflection;
using KeyWars.Infrastructure.Cluster;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

[Collection(RedisLeaseTimingCollection.Name)]
public sealed class RedisDistributedLeaseTests
{
    [Fact]
    public async Task SlowAcquisitionRenewsBeforeItsOriginalDeadline()
    {
        var acquisition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scriptCalls = 0;
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => acquisition.Task,
            nameof(IDatabase.ScriptEvaluateAsync) => CompleteLeaseScript(ref scriptCalls, renewal),
            _ => throw new NotSupportedException(method.Name)
        });
        var leaseTask = RedisDistributedLease.TryAcquireAsync(
            database,
            "test:lease:slow-acquisition",
            TimeSpan.FromSeconds(2),
            CancellationToken.None).AsTask();

        await Task.Delay(1_200);
        acquisition.TrySetResult(true);
        var lease = await leaseTask;

        Assert.NotNull(lease);
        await renewal.Task.WaitAsync(TimeSpan.FromMilliseconds(900));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDuringAcquisitionReleasesALateSuccessfulLock()
    {
        var acquisition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => acquisition.Task,
            nameof(IDatabase.ScriptEvaluateAsync) => CompleteRenewal(released),
            _ => throw new NotSupportedException(method.Name)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await RedisDistributedLease.TryAcquireAsync(
                database,
                "test:lease:cancelled-acquisition",
                TimeSpan.FromMilliseconds(300),
                cancellation.Token));
        acquisition.TrySetResult(true);

        await released.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReleaseWithForeignTokenMarksTheLeaseAsLost()
    {
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
            nameof(IDatabase.ScriptEvaluateAsync) => Task.FromResult(RedisResult.Create((RedisValue)0)),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = await RedisDistributedLease.TryAcquireAsync(
            database,
            "test:lease:foreign-release",
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        Assert.NotNull(lease);
        await lease.DisposeAsync();
        Assert.True(lease.LeaseLost.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(lease.ThrowIfLost);
    }

    [Fact]
    public async Task TransientRenewalFailureIsRetriedBeforeLeaseExpires()
    {
        var renewals = 0;
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = CreateDatabase((method, arguments) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
            nameof(IDatabase.ScriptEvaluateAsync) => ++renewals == 1
                ? Task.FromException<RedisResult>(new InvalidOperationException("Redis kurz nicht erreichbar"))
                : CompleteRenewal(renewed),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = await RedisDistributedLease.TryAcquireAsync(
            database,
            "test:lease:transient",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.NotNull(lease);
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lease.LeaseLost.IsCancellationRequested);
        lease.ThrowIfLost();
        await lease.DisposeAsync();
        Assert.True(renewals >= 2);
    }

    [Fact]
    public async Task ExhaustedRenewalWindowCancelsProtectedOperation()
    {
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
            nameof(IDatabase.ScriptEvaluateAsync) => Task.FromException<RedisResult>(
                new InvalidOperationException("Redis bleibt nicht erreichbar")),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = await RedisDistributedLease.TryAcquireAsync(
            database,
            "test:lease:lost",
            TimeSpan.FromMilliseconds(90),
            CancellationToken.None);

        Assert.NotNull(lease);
        await AssertLeaseLostAsync(lease.LeaseLost);
        Assert.Throws<InvalidOperationException>(lease.ThrowIfLost);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task HangingRenewalCancelsAtTheConservativeDeadline()
    {
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangingRenewal = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scriptCalls = 0;
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
            nameof(IDatabase.ScriptEvaluateAsync) when ++scriptCalls == 1 => StartHangingRenewal(
                renewalStarted,
                hangingRenewal.Task),
            nameof(IDatabase.ScriptEvaluateAsync) => Task.FromResult(RedisResult.Create((RedisValue)1)),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = await RedisDistributedLease.TryAcquireAsync(
            database,
            "test:lease:hanging",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.NotNull(lease);
        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertLeaseLostAsync(lease.LeaseLost);
        Assert.Throws<InvalidOperationException>(lease.ThrowIfLost);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDisposeCallsAwaitTheSameRelease()
    {
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.StringSetAsync) => Task.FromResult(true),
            nameof(IDatabase.ScriptEvaluateAsync) => StartHangingRenewal(releaseStarted, release.Task),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = await RedisDistributedLease.TryAcquireAsync(
            database,
            "test:lease:dispose",
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        Assert.NotNull(lease);
        var first = lease.DisposeAsync().AsTask();
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = lease.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        release.TrySetResult(RedisResult.Create((RedisValue)1));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static Task<RedisResult> CompleteRenewal(TaskCompletionSource renewed)
    {
        renewed.TrySetResult();
        return Task.FromResult(RedisResult.Create((RedisValue)1));
    }

    private static Task<RedisResult> CompleteLeaseScript(ref int scriptCalls, TaskCompletionSource renewed)
    {
        scriptCalls++;
        if (scriptCalls == 1)
        {
            renewed.TrySetResult();
        }

        return Task.FromResult(RedisResult.Create((RedisValue)1));
    }

    private static async Task AssertLeaseLostAsync(CancellationToken leaseLost)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, leaseLost).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("Das Lease-Verlustsignal wurde nicht ausgelöst.");
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
        }

        Assert.True(leaseLost.IsCancellationRequested);
    }

    private static Task<RedisResult> StartHangingRenewal(
        TaskCompletionSource started,
        Task<RedisResult> renewal)
    {
        started.TrySetResult();
        return renewal;
    }

    private static IDatabase CreateDatabase(Func<MethodInfo, object?[], object?> handler)
    {
        var proxy = DispatchProxy.Create<IDatabase, RedisProxy>();
        ((RedisProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class RedisProxy : DispatchProxy
    {
        public required Func<MethodInfo, object?[], object?> Handler { private get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(
                targetMethod ?? throw new InvalidOperationException("Redis-Proxyaufruf ohne Methode."),
                args ?? []);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisLeaseTimingCollection
{
    public const string Name = "Redis lease timing";
}
