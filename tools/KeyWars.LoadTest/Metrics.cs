using System.Collections.Concurrent;
using System.Diagnostics;

namespace KeyWars.LoadTesting;

internal sealed class BoundedMetricSeries(int capacity)
{
    private readonly object gate = new();
    private readonly double[] samples = new double[capacity];
    private long total;
    private long errors;
    private int sampleCount;
    private int nextIndex;
    private double maximum;

    public void Record(double milliseconds, bool error)
    {
        lock (gate)
        {
            total++;
            if (error)
            {
                errors++;
            }

            maximum = Math.Max(maximum, milliseconds);
            samples[nextIndex] = milliseconds;
            nextIndex = (nextIndex + 1) % samples.Length;
            sampleCount = Math.Min(sampleCount + 1, samples.Length);
        }
    }

    public OperationMetric Snapshot(string operation)
    {
        lock (gate)
        {
            var ordered = samples.AsSpan(0, sampleCount).ToArray();
            Array.Sort(ordered);
            return new OperationMetric(
                operation,
                total,
                sampleCount,
                errors,
                total == 0 ? 0 : Math.Round(errors * 100d / total, 4),
                Percentile(ordered, 0.50),
                Percentile(ordered, 0.95),
                Percentile(ordered, 0.99),
                Math.Round(maximum, 3));
        }
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        if (ordered.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return Math.Round(ordered[index], 3);
    }
}

internal sealed class MetricRegistry(int capacity)
{
    private readonly ConcurrentDictionary<string, BoundedMetricSeries> series = new(StringComparer.Ordinal);

    public void Record(string operation, double milliseconds, bool error = false) =>
        series.GetOrAdd(operation, _ => new BoundedMetricSeries(capacity)).Record(milliseconds, error);

    public async Task MeasureAsync(
        string operation,
        Func<CancellationToken, Task> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await MeasureAsync<object?>(operation, async token =>
        {
            await action(token);
            return null;
        }, timeout, cancellationToken);
    }

    public async Task<T> MeasureAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(timeout);
        try
        {
            var result = await action(operationTimeout.Token);
            Record(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && operationTimeout.IsCancellationRequested)
        {
            Record(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            throw new TimeoutException($"Operation '{operation}' überschritt {timeout}.", exception);
        }
        catch
        {
            Record(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            throw;
        }
    }

    public IReadOnlyList<OperationMetric> Snapshot() => series
        .Select(entry => entry.Value.Snapshot(entry.Key))
        .OrderBy(metric => metric.Operation, StringComparer.Ordinal)
        .ToArray();
}

internal readonly record struct ProgressKey(Guid RoomId, Guid ParticipantId, int CorrectCharacters);

internal sealed class FanoutTracker(MetricRegistry metrics)
{
    private readonly ConcurrentDictionary<ProgressKey, ConcurrentQueue<FanoutExpectation>> pending = new();
    private long expectedDeliveries;
    private long observedDeliveries;
    private long missingDeliveries;
    private int emittedMissingDiagnostics;

    public FanoutExpectation Register(
        Guid roomId,
        Guid participantId,
        int correctCharacters,
        IReadOnlyCollection<int> expectedClientIndexes,
        int? sourceClientIndex = null)
    {
        var key = new ProgressKey(roomId, participantId, correctCharacters);
        var expectation = new FanoutExpectation(key, expectedClientIndexes, sourceClientIndex);
        pending.GetOrAdd(key, _ => new ConcurrentQueue<FanoutExpectation>()).Enqueue(expectation);
        Interlocked.Add(ref expectedDeliveries, expectedClientIndexes.Count);
        return expectation;
    }

    public void Observe(int clientIndex, LiveProgressEnvelope progress)
    {
        var key = new ProgressKey(progress.RoomId, progress.ParticipantId, progress.CorrectCharacters);
        if (!pending.TryGetValue(key, out var queue))
        {
            return;
        }

        while (queue.TryPeek(out var expectation))
        {
            var latency = expectation.Observe(clientIndex);
            if (latency is not null)
            {
                metrics.Record("progress.fanout-recipient", latency.Value);
                Interlocked.Increment(ref observedDeliveries);
            }

            if (!expectation.IsComplete)
            {
                return;
            }

            queue.TryDequeue(out _);
            if (queue.IsEmpty)
            {
                pending.TryRemove(key, out _);
            }

            if (latency is not null)
            {
                return;
            }
        }
    }

    public async Task<int> WaitAsync(FanoutExpectation expectation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await expectation.Completion.WaitAsync(timeout, cancellationToken);
            return 0;
        }
        catch (TimeoutException)
        {
            var missingClients = expectation.MarkMissing();
            if (missingClients.Length > 0 && Interlocked.Increment(ref emittedMissingDiagnostics) <= 20)
            {
                Console.WriteLine(
                    $"Fan-out fehlt: Raum={expectation.Key.RoomId:N}, Quelle={expectation.SourceClientIndex?.ToString() ?? "?"}, " +
                    $"Empfänger={string.Join(',', missingClients)}");
            }

            for (var index = 0; index < missingClients.Length; index++)
            {
                metrics.Record("progress.fanout-recipient", timeout.TotalMilliseconds, true);
            }

            Interlocked.Add(ref missingDeliveries, missingClients.Length);
            RemoveCompleted(expectation);
            return missingClients.Length;
        }
    }

    private void RemoveCompleted(FanoutExpectation expectation)
    {
        if (!pending.TryGetValue(expectation.Key, out var queue))
        {
            return;
        }

        if (queue.TryPeek(out var current) && ReferenceEquals(current, expectation))
        {
            queue.TryDequeue(out _);
        }

        if (queue.IsEmpty)
        {
            pending.TryRemove(expectation.Key, out _);
        }
    }

    public FanoutSummary Snapshot() => new(
        Interlocked.Read(ref expectedDeliveries),
        Interlocked.Read(ref observedDeliveries),
        Interlocked.Read(ref missingDeliveries));
}

internal sealed class FanoutExpectation
{
    private readonly ConcurrentDictionary<int, byte> remaining;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FanoutExpectation(
        ProgressKey key,
        IReadOnlyCollection<int> expectedClientIndexes,
        int? sourceClientIndex)
    {
        Key = key;
        SourceClientIndex = sourceClientIndex;
        remaining = new ConcurrentDictionary<int, byte>(expectedClientIndexes.Select(index => new KeyValuePair<int, byte>(index, 0)));
        if (remaining.IsEmpty)
        {
            completion.TrySetResult();
        }
    }

    public ProgressKey Key { get; }
    public int? SourceClientIndex { get; }
    public Task Completion => completion.Task;
    public bool IsComplete => completion.Task.IsCompleted;

    public double? Observe(int clientIndex)
    {
        if (!remaining.TryRemove(clientIndex, out _))
        {
            return null;
        }

        if (remaining.IsEmpty)
        {
            completion.TrySetResult();
        }

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public int[] MarkMissing()
    {
        var missing = remaining.Keys.Order().ToArray();
        remaining.Clear();
        completion.TrySetResult();
        return missing;
    }
}

internal sealed class ResourceProbe
{
    private readonly int? targetProcessId;
    private readonly ResourcePoint start;

    private ResourceProbe(int? targetProcessId)
    {
        this.targetProcessId = targetProcessId;
        start = ResourcePoint.Capture(targetProcessId);
    }

    public static ResourceProbe Start(int? targetProcessId) => new(targetProcessId);

    public ResourceReport Complete(TimeSpan elapsed)
    {
        var end = ResourcePoint.Capture(targetProcessId);
        return new ResourceReport(
            ProcessDelta.From(start.LoadGenerator, end.LoadGenerator, elapsed),
            ProcessDelta.From(start.Target, end.Target, elapsed),
            new GcDelta(
                end.AllocatedBytes - start.AllocatedBytes,
                end.Gen0Collections - start.Gen0Collections,
                end.Gen1Collections - start.Gen1Collections,
                end.Gen2Collections - start.Gen2Collections,
                Math.Round((end.PauseDuration - start.PauseDuration).TotalMilliseconds, 3)),
            end.ThreadPool);
    }
}

internal sealed record ResourcePoint(
    ProcessPoint? LoadGenerator,
    ProcessPoint? Target,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan PauseDuration,
    ThreadPoolPoint ThreadPool)
{
    public static ResourcePoint Capture(int? targetProcessId) => new(
        ProcessPoint.Capture(Environment.ProcessId),
        targetProcessId is null ? null : ProcessPoint.Capture(targetProcessId.Value),
        GC.GetTotalAllocatedBytes(false),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalPauseDuration(),
        ThreadPoolPoint.Capture());
}

internal sealed record ProcessPoint(int ProcessId, TimeSpan CpuTime, long WorkingSetBytes, long PeakWorkingSetBytes, int ThreadCount)
{
    public static ProcessPoint? Capture(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            return new ProcessPoint(processId, process.TotalProcessorTime, process.WorkingSet64, process.PeakWorkingSet64, process.Threads.Count);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed record ThreadPoolPoint(
    int ThreadCount,
    long PendingWorkItems,
    long CompletedWorkItems,
    int AvailableWorkerThreads,
    int MaximumWorkerThreads)
{
    public static ThreadPoolPoint Capture()
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
        ThreadPool.GetMaxThreads(out var maximumWorkers, out _);
        return new ThreadPoolPoint(
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.CompletedWorkItemCount,
            availableWorkers,
            maximumWorkers);
    }
}

internal static class SloEvaluator
{
    public static SloReport Evaluate(
        SloOptions options,
        IReadOnlyList<OperationMetric> metrics,
        FanoutSummary fanout,
        IReadOnlyList<RoomResult> rooms)
    {
        var checks = new List<SloCheck>();
        foreach (var metric in metrics.Where(metric => metric.TotalCount > 0 && metric.Operation != "progress.fanout-recipient"))
        {
            checks.Add(new SloCheck(
                $"{metric.Operation}.p95",
                metric.P95Milliseconds <= options.OperationP95Milliseconds,
                metric.P95Milliseconds,
                options.OperationP95Milliseconds,
                "ms"));
            checks.Add(new SloCheck(
                $"{metric.Operation}.p99",
                metric.P99Milliseconds <= options.OperationP99Milliseconds,
                metric.P99Milliseconds,
                options.OperationP99Milliseconds,
                "ms"));
        }

        var total = metrics.Sum(metric => metric.TotalCount);
        var errors = metrics.Sum(metric => metric.ErrorCount);
        var errorRate = total == 0 ? 0 : errors * 100d / total;
        checks.Add(new SloCheck("operations.error-rate", errorRate <= options.MaximumErrorRatePercent, Math.Round(errorRate, 4), options.MaximumErrorRatePercent, "%"));

        var fanoutMetric = metrics.SingleOrDefault(metric => metric.Operation == "progress.fanout-recipient");
        checks.Add(new SloCheck(
            "progress.fanout-recipient.p95",
            fanoutMetric is null || fanoutMetric.P95Milliseconds <= options.FanoutP95Milliseconds,
            fanoutMetric?.P95Milliseconds ?? 0,
            options.FanoutP95Milliseconds,
            "ms"));
        checks.Add(new SloCheck(
            "progress.missing-broadcasts",
            fanout.MissingDeliveries <= options.MaximumMissingBroadcasts,
            fanout.MissingDeliveries,
            options.MaximumMissingBroadcasts,
            "deliveries"));
        checks.Add(new SloCheck(
            "rooms.failed",
            rooms.Count(room => room.Error is not null) == 0,
            rooms.Count(room => room.Error is not null),
            0,
            "rooms"));

        return new SloReport(checks.All(check => check.Passed), checks);
    }
}

internal sealed record OperationMetric(
    string Operation,
    long TotalCount,
    int SampledCount,
    long ErrorCount,
    double ErrorRatePercent,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds);

internal sealed record FanoutSummary(long ExpectedDeliveries, long ObservedDeliveries, long MissingDeliveries);
internal sealed record ProcessDelta(int ProcessId, double AverageCpuPercent, long WorkingSetBytes, long PeakWorkingSetBytes, int ThreadCount)
{
    public static ProcessDelta? From(ProcessPoint? start, ProcessPoint? end, TimeSpan elapsed)
    {
        if (start is null || end is null || start.ProcessId != end.ProcessId || elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var cpu = (end.CpuTime - start.CpuTime).TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100d;
        return new ProcessDelta(end.ProcessId, Math.Round(Math.Max(0, cpu), 3), end.WorkingSetBytes, end.PeakWorkingSetBytes, end.ThreadCount);
    }
}

internal sealed record GcDelta(long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, double PauseMilliseconds);
internal sealed record ResourceReport(ProcessDelta? LoadGenerator, ProcessDelta? TargetProcess, GcDelta LoadGeneratorGc, ThreadPoolPoint LoadGeneratorThreadPool);
internal sealed record SloCheck(string Name, bool Passed, double Actual, double Limit, string Unit);
internal sealed record SloReport(bool Passed, IReadOnlyList<SloCheck> Checks);
internal sealed record LiveProgressEnvelope(Guid RoomId, Guid ParticipantId, int CorrectCharacters);
