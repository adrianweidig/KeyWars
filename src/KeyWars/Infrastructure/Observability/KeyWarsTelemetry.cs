using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace KeyWars.Infrastructure.Observability;

public sealed class KeyWarsTelemetry : IDisposable
{
    public const string MeterName = "KeyWars";
    public const string ActivitySourceName = "KeyWars";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> hubCommands;
    private readonly Histogram<double> hubCommandDuration;
    private readonly UpDownCounter<long> activeConnections;
    private readonly Counter<long> progressEvents;
    private readonly Histogram<long> progressBatchSize;
    private readonly Histogram<long> progressPayloadBytes;
    private readonly Histogram<double> progressBroadcastDuration;
    private readonly Histogram<double> databaseOperationDuration;
    private readonly Counter<long> sqliteBusyRetries;
    private long activeRooms;
    private long activeParticipants;
    private long completionQueueDepth;
    private double completionOldestAgeSeconds;

    public KeyWarsTelemetry()
    {
        ActivitySource = new ActivitySource(ActivitySourceName);
        hubCommands = meter.CreateCounter<long>("keywars.hub.commands");
        hubCommandDuration = meter.CreateHistogram<double>("keywars.hub.command.duration", "s");
        activeConnections = meter.CreateUpDownCounter<long>("keywars.connections.active");
        progressEvents = meter.CreateCounter<long>("keywars.progress.events");
        progressBatchSize = meter.CreateHistogram<long>("keywars.progress.batch.size");
        progressPayloadBytes = meter.CreateHistogram<long>("keywars.progress.payload.bytes", "By");
        progressBroadcastDuration = meter.CreateHistogram<double>("keywars.progress.broadcast.duration", "s");
        databaseOperationDuration = meter.CreateHistogram<double>("keywars.database.operation.duration", "s");
        sqliteBusyRetries = meter.CreateCounter<long>("keywars.sqlite.busy.retries");
        meter.CreateObservableGauge("keywars.rooms.active", () => Interlocked.Read(ref activeRooms));
        meter.CreateObservableGauge("keywars.participants.active", () => Interlocked.Read(ref activeParticipants));
        meter.CreateObservableGauge("keywars.completion.queue.depth", () => Interlocked.Read(ref completionQueueDepth));
        meter.CreateObservableGauge("keywars.completion.oldest.age", () => Volatile.Read(ref completionOldestAgeSeconds), "s");
    }

    public ActivitySource ActivitySource { get; }

    public void RecordHubCommand(string command, string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "command", command }, { "outcome", outcome } };
        hubCommands.Add(1, tags);
        hubCommandDuration.Record(duration.TotalSeconds, tags);
    }

    public void ConnectionOpened(string node) =>
        activeConnections.Add(1, new TagList { { "node", node } });

    public void ConnectionClosed(string node) =>
        activeConnections.Add(-1, new TagList { { "node", node } });

    public void RecordProgress(string outcome, long batchSize, long payloadBytes, TimeSpan duration)
    {
        progressEvents.Add(1, new TagList { { "outcome", outcome } });
        progressBatchSize.Record(batchSize);
        progressPayloadBytes.Record(payloadBytes);
        progressBroadcastDuration.Record(duration.TotalSeconds);
    }

    public void SetArenaSnapshot(long rooms, long participants)
    {
        Interlocked.Exchange(ref activeRooms, Math.Max(0, rooms));
        Interlocked.Exchange(ref activeParticipants, Math.Max(0, participants));
    }

    public void SetCompletionQueueSnapshot(long depth, TimeSpan oldestAge)
    {
        Interlocked.Exchange(ref completionQueueDepth, Math.Max(0, depth));
        Volatile.Write(ref completionOldestAgeSeconds, Math.Max(0, oldestAge.TotalSeconds));
    }

    public void RecordDatabaseOperation(string provider, string operation, string outcome, TimeSpan duration) =>
        databaseOperationDuration.Record(
            duration.TotalSeconds,
            new TagList { { "provider", provider }, { "operation", operation }, { "outcome", outcome } });

    public void RecordSqliteBusyRetry(string operation, TimeSpan delay)
    {
        sqliteBusyRetries.Add(1, new TagList { { "operation", operation } });
        databaseOperationDuration.Record(
            delay.TotalSeconds,
            new TagList { { "provider", "sqlite" }, { "operation", operation }, { "outcome", "retry" } });
    }

    public void Dispose()
    {
        ActivitySource.Dispose();
        meter.Dispose();
    }
}

public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddKeyWarsObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<KeyWarsTelemetry>();
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("KeyWars"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(KeyWarsTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health") &&
                            !context.Request.Path.StartsWithSegments("/metrics");
                    });
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(KeyWarsTelemetry.MeterName)
                    .AddMeter("Microsoft.AspNetCore.Hosting")
                    .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                    .AddMeter("Microsoft.AspNetCore.Http.Connections")
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddPrometheusExporter();
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter();
                }
            });
        return services;
    }
}
