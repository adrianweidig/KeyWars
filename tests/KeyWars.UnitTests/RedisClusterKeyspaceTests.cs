using KeyWars.Infrastructure.Cluster;

namespace KeyWars.UnitTests;

public sealed class RedisClusterKeyspaceTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "37")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "a8")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "5a")]
    [InlineData("0198a1b2-c3d4-7e5f-8123-456789abcdef", "4e")]
    public void BucketContractUsesSha256OfCanonicalNetworkOrderGuid(string id, string expectedBucket)
    {
        var guid = Guid.Parse(id);

        Assert.Equal(expectedBucket, RedisClusterKeyspace.GetBucketHex(guid));
        Assert.Equal(Convert.ToByte(expectedBucket, 16), RedisClusterKeyspace.GetBucket(guid));
        Assert.Equal($"{{attempt-b{expectedBucket}}}", RedisClusterKeyspace.GetHashTag("attempt", guid));
    }

    [Fact]
    public void BucketContractIsPinnedToAllByteValuesAndRejectsInvalidSubsystems()
    {
        Assert.Equal(256, RedisClusterKeyspace.BucketCount);
        Assert.Throws<ArgumentException>(() => RedisClusterKeyspace.GetHashTag("", Guid.Empty));
        Assert.Throws<ArgumentException>(() => RedisClusterKeyspace.GetHashTag("bad{tag", Guid.Empty));
    }
}
