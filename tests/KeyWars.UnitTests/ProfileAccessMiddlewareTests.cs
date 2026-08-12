using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;

namespace KeyWars.UnitTests;

public sealed class ProfileAccessMiddlewareTests
{
    [Fact]
    public async Task LostAccessLeaseCancelsRequestBeforePrivacyOperationCanContinue()
    {
        var gate = new LosingAccessGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ProfileAccessMiddleware(async context =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(KeyWarsClaims.ProfileId, Guid.CreateVersion7().ToString("D"))],
            "test"));

        var invocation = middleware.InvokeAsync(context, gate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.LoseLease();
        await invocation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.True(gate.Disposed);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Contains("profile_access_lost", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    private sealed class LosingAccessGate : IProfileAccessGate
    {
        private readonly CancellationTokenSource leaseLost = new();

        public bool Disposed { get; private set; }

        public void LoseLease() => leaseLost.Cancel();

        public ValueTask<ProfileAccessState> GetStateAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ProfileAccessState.Available);

        public ValueTask<IOperationLease> AcquireAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IOperationLease>(new LosingLease(leaseLost.Token, () => Disposed = true));

        public ValueTask<IOperationLease> AcquireManyAsync(IEnumerable<Guid> profileIds, CancellationToken cancellationToken = default) =>
            AcquireAsync(Guid.Empty, cancellationToken);

        public ValueTask<IOperationLease?> TryBeginOperationAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IOperationLease?>(null);

        public Task WaitForIdleAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask CompleteOperationAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask MarkDeletedAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        private sealed class LosingLease(CancellationToken leaseLost, Action dispose) : IOperationLease
        {
            public CancellationToken LeaseLost => leaseLost;

            public void ThrowIfLost()
            {
                if (LeaseLost.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Lease verloren.");
                }
            }

            public ValueTask DisposeAsync()
            {
                dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
