using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class ProfileExportConcurrencyGateTests
{
    [Fact]
    public async Task SingleNodeGateAllowsOnlyOneExportPerProfileUntilRelease()
    {
        var gate = new SingleNodeProfileExportConcurrencyGate();
        var profileId = Guid.CreateVersion7();

        await using var first = await gate.TryAcquireAsync(profileId);
        var concurrent = await gate.TryAcquireAsync(profileId);

        Assert.NotNull(first);
        Assert.Null(concurrent);
        await first!.DisposeAsync();
        await using var next = await gate.TryAcquireAsync(profileId);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task SingleNodeGateFailsClosedAtCapacityAndReclaimsReleasedSlot()
    {
        var gate = new SingleNodeProfileExportConcurrencyGate(capacity: 1);
        var firstProfile = Guid.CreateVersion7();
        var secondProfile = Guid.CreateVersion7();

        await using var first = await gate.TryAcquireAsync(firstProfile);
        Assert.Null(await gate.TryAcquireAsync(secondProfile));

        await first!.DisposeAsync();

        await using var second = await gate.TryAcquireAsync(secondProfile);
        Assert.NotNull(second);
    }

    [Fact]
    public void RedisGateUsesStableProfileBucket()
    {
        var profileId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        var key = RedisProfileExportConcurrencyGate.LockKey(profileId).ToString();

        Assert.StartsWith("keywars:{profile-export-", key, StringComparison.Ordinal);
        Assert.EndsWith($":lock:{profileId:N}", key, StringComparison.Ordinal);
    }
}
