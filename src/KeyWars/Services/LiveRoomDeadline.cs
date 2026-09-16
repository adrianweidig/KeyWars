namespace KeyWars.Services;

internal static class LiveRoomDeadline
{
    private const int LegacyBaseSeconds = 60;
    private const int LegacyMillisecondsPerGrapheme = 1000;

    public static DateTimeOffset ForNewRound(
        DateTimeOffset raceStartsAt,
        int targetGraphemes,
        LiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseSeconds = Math.Clamp(options.RoundDeadlineBaseSeconds, 15, 300);
        var millisecondsPerGrapheme = Math.Clamp(
            options.RoundDeadlineMillisecondsPerGrapheme,
            100,
            5000);
        var maximumSeconds = Math.Clamp(
            options.RoundDeadlineMaxSeconds,
            60,
            LiveOptions.MaximumSafeRoundDeadlineSeconds);
        return AddBounded(
            raceStartsAt,
            targetGraphemes,
            baseSeconds,
            millisecondsPerGrapheme,
            maximumSeconds);
    }

    public static DateTimeOffset Resolve(
        DateTimeOffset raceStartsAt,
        int targetGraphemes,
        DateTimeOffset? persistedDeadline)
    {
        var maximum = raceStartsAt.AddSeconds(LiveOptions.MaximumSafeRoundDeadlineSeconds);
        if (persistedDeadline is { } deadline)
        {
            return deadline < raceStartsAt
                ? raceStartsAt
                : deadline > maximum ? maximum : deadline;
        }

        return AddBounded(
            raceStartsAt,
            targetGraphemes,
            LegacyBaseSeconds,
            LegacyMillisecondsPerGrapheme,
            LiveOptions.MaximumSafeRoundDeadlineSeconds);
    }

    public static DateTimeOffset RoundResultsIdleAt(DateTimeOffset phaseChangedAt, LiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return phaseChangedAt.AddSeconds(Math.Clamp(
            options.RoundResultsIdleSeconds,
            30,
            LiveOptions.MaximumSafeRoundResultsIdleSeconds));
    }

    private static DateTimeOffset AddBounded(
        DateTimeOffset raceStartsAt,
        int targetGraphemes,
        int baseSeconds,
        int millisecondsPerGrapheme,
        int maximumSeconds)
    {
        var target = Math.Clamp(targetGraphemes, 1, LiveOptions.MaximumSafeArenaTargetGraphemes);
        var requestedMilliseconds = checked(
            (long)baseSeconds * 1000L + (long)target * millisecondsPerGrapheme);
        var boundedMilliseconds = Math.Min(requestedMilliseconds, checked((long)maximumSeconds * 1000L));
        return raceStartsAt.AddMilliseconds(boundedMilliseconds);
    }
}
