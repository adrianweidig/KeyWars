using System.Diagnostics.Metrics;
using KeyWars.Infrastructure.Observability;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeyWars.UnitTests;

public sealed class OperationalMetricsServiceTests
{
    [Fact]
    public async Task PublishesArenaAndCompletionQueueSnapshots()
    {
        using var telemetry = new KeyWarsTelemetry();
        var dispatcher = new MetricsDispatcher(new LiveRoomMetricsSnapshot(3, 1, 2, 17));
        var completion = new CompletionMonitor(4, TimeSpan.FromSeconds(23));
        var observed = new Dictionary<string, double>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == KeyWarsTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => observed[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => observed[instrument.Name] = value);
        listener.Start();
        var service = new OperationalMetricsService(
            dispatcher,
            completion,
            telemetry,
            NullLogger<OperationalMetricsService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await dispatcher.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        listener.RecordObservableInstruments();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(3, observed["keywars.rooms.active"]);
        Assert.Equal(17, observed["keywars.participants.active"]);
        Assert.Equal(4, observed["keywars.completion.queue.depth"]);
        Assert.Equal(23, observed["keywars.completion.oldest.age"]);
    }

    private sealed class MetricsDispatcher(LiveRoomMetricsSnapshot snapshot) : ILiveRoomDispatcher
    {
        public TaskCompletionSource Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<LiveRoomMetricsSnapshot> MetricsSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observed.TrySetResult();
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<LiveRoomSnapshot> CreateRoomAsync(CreateLiveRoomRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<LiveRoomSnapshot>> ListOpenRoomsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomLobbyPage> ListLobbySummariesAsync(Guid viewerProfileId, int offset = 0, int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Guid> ResolveRoomIdByCodeAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> JoinByCodeAsync(string code, Guid profileId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> JoinAsync(Guid roomId, Guid profileId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> SetReadyAsync(Guid roomId, Guid profileId, bool ready, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> SetLobbyLockedAsync(Guid roomId, Guid hostProfileId, bool locked, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> TransferHostAsync(Guid roomId, Guid hostProfileId, Guid nextHostProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> KickAsync(Guid roomId, Guid hostProfileId, Guid targetProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> CloseAsync(Guid roomId, Guid hostProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> StartAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> SubmitProgressAsync(Guid roomId, Guid profileId, int sequence, string input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveProgressResult> SubmitProgressDeltaAsync(Guid roomId, Guid profileId, int sequence, string input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> FinishAsync(Guid roomId, Guid profileId, string input, int backspaces, int focusLosses, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> GiveUpAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> DisconnectAsync(Guid roomId, Guid profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<LiveRoomSnapshot> SnapshotAsync(Guid roomId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<LiveRoomSnapshot>> SweepAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RemoveProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<int> AbortActiveRoomsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CompletionMonitor(int pending, TimeSpan oldest) : ILiveRoomCompletionMonitor
    {
        public int Capacity => 100;
        public int PendingCount => pending;
        public int FailedRecordCount => 0;
        public long FailedAttempts => 0;
        public TimeSpan OldestPendingAge => oldest;
        public LiveRoomCompletionMetrics GetMetrics() => new(pending, 0, 0, 0, 0, 0, 0);
    }
}
