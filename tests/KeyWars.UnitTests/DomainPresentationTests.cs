using KeyWars.Domain;

namespace KeyWars.UnitTests;

public sealed class DomainPresentationTests
{
    [Theory]
    [InlineData(int.MinValue, "Bronze")]
    [InlineData(1049, "Bronze")]
    [InlineData(1050, "Silber")]
    [InlineData(1099, "Silber")]
    [InlineData(1100, "Gold")]
    [InlineData(1199, "Gold")]
    [InlineData(1200, "Platin")]
    [InlineData(1299, "Platin")]
    [InlineData(1300, "Diamant")]
    [InlineData(int.MaxValue, "Diamant")]
    public void ArenaDivisionUsesOneStableRatingScale(int rating, string expected)
    {
        Assert.Equal(expected, ArenaDivision.NameFor(rating));
    }

    [Fact]
    public void TextHashNormalizesEquivalentLineEndingsAndUnicode()
    {
        var composed = "Grüße\r\nTeam";
        var decomposed = "Gru\u0308ße\nTeam";

        var first = TextHash.Compute(composed);
        var second = TextHash.Compute(decomposed);

        Assert.Equal(first, second);
        Assert.Equal("sha256:29c942720c1ea3209baaa401d34e606bd90cadcaabb38b861879ac6347a662e9", first);
    }
}
