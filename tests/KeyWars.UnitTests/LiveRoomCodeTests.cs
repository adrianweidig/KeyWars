using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class LiveRoomCodeTests
{
    [Theory]
    [InlineData("abc234", "ABC234")]
    [InlineData(" ABC234 ", "ABC234")]
    public void RoomCodeNormalizationAcceptsSixAllowedCharacters(string input, string expected)
    {
        Assert.Equal(expected, LiveRoomManager.NormalizeRoomCode(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC12")]
    [InlineData("ABC1234")]
    [InlineData("ABC10O")]
    public void RoomCodeNormalizationRejectsInvalidCodes(string input)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LiveRoomManager.NormalizeRoomCode(input));

        Assert.Contains("sechs", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
