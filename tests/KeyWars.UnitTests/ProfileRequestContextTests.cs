using KeyWars.Domain;
using KeyWars.Infrastructure;

namespace KeyWars.UnitTests;

public sealed class ProfileRequestContextTests
{
    [Fact]
    public void ProfileIsOnlyVisibleWhileTheMatchingLeaseContextIsActive()
    {
        var profileId = Guid.CreateVersion7();
        var context = new ProfileRequestContext();
        var profile = new UserProfile { Id = profileId };

        context.Begin(profileId);
        context.SetProfile(profile);

        Assert.True(context.HasLease(profileId));
        Assert.True(context.TryGetProfile(profileId, out var cached));
        Assert.Same(profile, cached);
        Assert.False(context.TryGetProfile(Guid.CreateVersion7(), out _));

        context.Clear();

        Assert.False(context.HasLease(profileId));
        Assert.False(context.TryGetProfile(profileId, out _));
    }

    [Fact]
    public void DifferentProfileCannotBeCachedUnderTheLease()
    {
        var context = new ProfileRequestContext();
        context.Begin(Guid.CreateVersion7());

        Assert.Throws<InvalidOperationException>(() =>
            context.SetProfile(new UserProfile { Id = Guid.CreateVersion7() }));
    }
}
