using KeyWars.Data;
using KeyWars.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class DataRetentionHostedServiceTests
{
    [Fact]
    public async Task ClusterWorkerSkipsCycleWhenAnotherWorkerHoldsTheLease()
    {
        var scopes = new RejectingScopeFactory();
        var maintenanceLease = new UnavailableMaintenanceLease();
        var service = new DataRetentionHostedService(
            scopes,
            maintenanceLease,
            Options.Create(new RetentionOptions { Enabled = true, IntervalHours = 1 }),
            TimeProvider.System,
            NullLogger<DataRetentionHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await maintenanceLease.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("retention", maintenanceLease.Operation);
        Assert.Equal(1, maintenanceLease.Attempts);
        Assert.Equal(0, scopes.CreatedScopes);
    }

    [Fact]
    public async Task SingleNodeMaintenanceLeaseAlwaysAllowsTheCycle()
    {
        var maintenanceLease = new SingleNodeMaintenanceLease();

        await using var lease = await maintenanceLease.TryAcquireAsync("retention");

        Assert.NotNull(lease);
    }

    private sealed class UnavailableMaintenanceLease : IMaintenanceLease
    {
        public TaskCompletionSource Attempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts { get; private set; }
        public string? Operation { get; private set; }

        public ValueTask<IOperationLease?> TryAcquireAsync(
            string operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            Operation = operation;
            Attempted.TrySetResult();
            return ValueTask.FromResult<IOperationLease?>(null);
        }
    }

    private sealed class RejectingScopeFactory : IServiceScopeFactory
    {
        public int CreatedScopes { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopes++;
            throw new InvalidOperationException("Bei fehlendem Lease darf kein Retention-Scope entstehen.");
        }
    }
}
