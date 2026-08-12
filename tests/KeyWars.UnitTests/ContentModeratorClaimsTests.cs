using KeyWars.Auth;
using KeyWars.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace KeyWars.UnitTests;

public sealed class ContentModeratorClaimsTests
{
    [Fact]
    public void ConfigurationAliasesBindUnderscoreNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:MODERATION:MODERATOR_GROUP_DNS"] = "CN=Moderation,DC=example,DC=local",
                ["KEYWARS:MODERATION:MODERATOR_GROUP_VALUES"] = "KeyWars Moderators"
            })
            .Build();
        var options = new ContentModerationOptions();

        ConfigurationAliases.BindModeration(configuration, options);

        Assert.Equal("CN=Moderation,DC=example,DC=local", options.ModeratorGroupDns);
        Assert.Equal("KeyWars Moderators", options.ModeratorGroupValues);
    }

    [Fact]
    public void EmptyConfigurationFailsClosed()
    {
        var identity = Identity("CN=KeyWars Moderation,OU=Groups,DC=example,DC=local");

        Assert.False(ContentModeratorClaims.IsModerator(identity, new ContentModerationOptions()));
        Assert.Empty(ContentModeratorClaims.Create(identity, new ContentModerationOptions()));
    }

    [Fact]
    public void DistinguishedNameMatchesCaseInsensitively()
    {
        var identity = Identity("CN=KeyWars Moderation,OU=Groups,DC=example,DC=local");
        var options = new ContentModerationOptions
        {
            ModeratorGroupDns = " cn=keywars moderation,ou=groups,dc=example,dc=local "
        };

        Assert.True(ContentModeratorClaims.IsModerator(identity, options));
        Assert.Contains(
            ContentModeratorClaims.Create(identity, options),
            claim => claim.Type == KeyWarsClaims.ContentModerator && claim.Value == "true");
    }

    [Fact]
    public void ConfiguredGroupValueMatchesFirstRdn()
    {
        var identity = Identity("CN=Moderation\\, KeyWars,OU=Groups,DC=example,DC=local");
        var options = new ContentModerationOptions
        {
            ModeratorGroupValues = "Moderation, KeyWars"
        };

        Assert.True(ContentModeratorClaims.IsModerator(identity, options));
    }

    [Fact]
    public void UnrelatedMembershipDoesNotCreateClaim()
    {
        var identity = Identity("CN=Training,OU=Groups,DC=example,DC=local");
        var options = new ContentModerationOptions
        {
            ModeratorGroupDns = "CN=KeyWars Moderation,OU=Groups,DC=example,DC=local",
            ModeratorGroupValues = "Content Admins"
        };

        Assert.False(ContentModeratorClaims.IsModerator(identity, options));
        Assert.Empty(ContentModeratorClaims.Create(identity, options));
    }

    private static DirectoryIdentity Identity(params string[] groups) =>
        new(
            Guid.NewGuid().ToString("D"),
            "S-1-5-21-test",
            "moderator",
            "moderator@example.local",
            "Test Moderator",
            "Test",
            "Moderator",
            null,
            null,
            null)
        {
            GroupValues = groups
        };
}
