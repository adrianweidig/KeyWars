using KeyWars.Domain;

namespace KeyWars.UnitTests;

public sealed class CompetitionIntegrityTests
{
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(30, 1, true)]
    [InlineData(31, 1, false)]
    [InlineData(150, 5, true)]
    [InlineData(151, 5, false)]
    [InlineData(151, 6, true)]
    public void PlausibilityUsesConservativeWholeSecondServerBoundary(
        int correctGraphemes,
        int serverSeconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            CompetitionEligibility.HasPlausibleServerPace(
                correctGraphemes,
                TimeSpan.FromSeconds(serverSeconds)));
    }

    [Fact]
    public void ZeroProgressDoesNotCreateAnArtificialSpeedViolation()
    {
        Assert.True(CompetitionEligibility.HasPlausibleServerPace(0, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, CompetitionEligibility.MinimumPlausibleDuration(0));
    }

    [Fact]
    public void NewPersistenceModelsDefaultToCompetitionIneligible()
    {
        Assert.False(new TypingAttempt().CompetitionIntegrityEligible);
        Assert.False(new ChallengeRoundResult().CompetitionEligible);
        Assert.False(new LiveRoomParticipantSummary().CompetitionEligible);
    }
}
