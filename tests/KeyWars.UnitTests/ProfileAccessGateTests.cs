using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class ProfileAccessGateTests
{
    [Fact]
    public async Task ExclusiveOperationClosesAdmissionAndWaitsForActiveLeases()
    {
        var gate = new ProfileAccessGate();
        var profileId = Guid.CreateVersion7();
        var first = gate.Acquire(profileId);
        var second = gate.Acquire(profileId);

        Assert.True(gate.TryBeginOperation(profileId));
        var idle = gate.WaitForIdleAsync(profileId);
        Assert.False(idle.IsCompleted);
        var blocked = Assert.Throws<ProfileOperationException>(() => gate.Acquire(profileId));
        Assert.Equal("profile_operation_in_progress", blocked.Code);

        first.Dispose();
        Assert.False(idle.IsCompleted);
        second.Dispose();
        await idle;

        gate.CompleteOperation(profileId);
        using var admittedAgain = gate.Acquire(profileId);
        Assert.Equal(ProfileAccessState.Available, gate.GetState(profileId));
    }

    [Fact]
    public void OperationIsExclusiveAndCanBeReleased()
    {
        var gate = new ProfileAccessGate();
        var profileId = Guid.CreateVersion7();

        Assert.True(gate.TryBeginOperation(profileId));
        Assert.False(gate.TryBeginOperation(profileId));
        Assert.True(gate.IsBlocked(profileId));

        gate.CompleteOperation(profileId);

        Assert.False(gate.IsBlocked(profileId));
        Assert.True(gate.TryBeginOperation(profileId));
    }

    [Fact]
    public void DeletedProfileRemainsBlocked()
    {
        var gate = new ProfileAccessGate();
        var profileId = Guid.CreateVersion7();
        Assert.True(gate.TryBeginOperation(profileId));

        gate.MarkDeleted(profileId);
        gate.CompleteOperation(profileId);

        Assert.Equal(ProfileAccessState.Deleted, gate.GetState(profileId));
        Assert.True(gate.IsBlocked(profileId));
        Assert.False(gate.TryBeginOperation(profileId));
    }

    [Fact]
    public async Task AsyncExclusiveLeaseReleasesOperationExactlyOnce()
    {
        var gate = new ProfileAccessGate();
        var profileId = Guid.CreateVersion7();

        var operation = await gate.TryBeginOperationAsync(profileId);

        Assert.NotNull(operation);
        Assert.Equal(ProfileAccessState.OperationInProgress, gate.GetState(profileId));
        await operation.DisposeAsync();
        await operation.DisposeAsync();
        Assert.Equal(ProfileAccessState.Available, gate.GetState(profileId));
    }
}
