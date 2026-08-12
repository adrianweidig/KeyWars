using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using StackExchange.Redis;

namespace KeyWars.UnitTests;

public sealed class RedisProfileAccessGateLeaseTests
{
    [Fact]
    public async Task OperationLeaseSignalsLossWhenRenewalKeepsFailing()
    {
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.ScriptEvaluateAsync) => Task.FromException<RedisResult>(
                new InvalidOperationException("Redis bleibt nicht erreichbar.")),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = CreateOperationLease(database, TimeSpan.FromMilliseconds(120));

        await AssertLeaseLostAsync(lease.LeaseLost);

        Assert.Throws<InvalidOperationException>(lease.ThrowIfLost);
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OperationLeaseSignalsLossWhenRenewalHangsPastDeadline()
    {
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangingRenewal = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.ScriptEvaluateAsync) when Interlocked.Increment(ref calls) == 1 => StartRenewal(
                renewalStarted,
                hangingRenewal.Task),
            nameof(IDatabase.ScriptEvaluateAsync) => Task.FromResult(RedisResult.Create((RedisValue)1)),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = CreateOperationLease(database, TimeSpan.FromSeconds(1));

        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertLeaseLostAsync(lease.LeaseLost);

        Assert.Throws<InvalidOperationException>(lease.ThrowIfLost);
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task ConcurrentOperationLeaseDisposeCallsAwaitOneRelease()
    {
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var database = CreateDatabase((method, _) => method.Name switch
        {
            nameof(IDatabase.ScriptEvaluateAsync) => StartRelease(
                ref calls,
                releaseStarted,
                release.Task),
            _ => throw new NotSupportedException(method.Name)
        });
        var lease = CreateOperationLease(database, TimeSpan.FromSeconds(5));

        var first = lease.DisposeAsync().AsTask();
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = lease.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        release.TrySetResult(RedisResult.Create((RedisValue)1));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls);
    }

    private static IOperationLease CreateOperationLease(IDatabase database, TimeSpan lifetime)
    {
        var leaseType = typeof(RedisProfileAccessGate).GetNestedType(
            "OperationLease",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException("OperationLease fehlt.");
        var ownerType = typeof(ConcurrentDictionary<,>).MakeGenericType(typeof(Guid), leaseType);
        var owner = Activator.CreateInstance(ownerType) ?? throw new InvalidOperationException("Lease-Register fehlt.");
        var constructor = leaseType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IDatabase), ownerType, typeof(Guid), typeof(RedisValue), typeof(long)],
            modifiers: null) ?? throw new InvalidOperationException("OperationLease-Konstruktor fehlt.");
        var validUntil = Stopwatch.GetTimestamp() + checked((long)Math.Ceiling(lifetime.TotalSeconds * Stopwatch.Frequency));
        return (IOperationLease)constructor.Invoke(
            [database, owner, Guid.CreateVersion7(), (RedisValue)$"op:{Guid.NewGuid():N}", validUntil]);
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

    private static Task<RedisResult> StartRelease(
        ref int calls,
        TaskCompletionSource started,
        Task<RedisResult> release)
    {
        Interlocked.Increment(ref calls);
        started.TrySetResult();
        return release;
    }

    private static Task<RedisResult> StartRenewal(
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
