using KeyWars.Domain;

namespace KeyWars.Services;

internal static class LiveRoomProgress
{
    private const int MaxInputOverrunCharacters = 20;

    public static int CountCorrectPrefix(
        IReadOnlyList<string> targetElements,
        IReadOnlyList<string> inputElements)
    {
        var count = 0;
        for (var index = 0; index < Math.Min(targetElements.Count, inputElements.Count); index++)
        {
            if (!StringComparer.Ordinal.Equals(targetElements[index], inputElements[index]))
            {
                break;
            }

            count++;
        }

        return count;
    }

    public static string BuildTypedTextPreview(
        IReadOnlyList<string> targetElements,
        IReadOnlyList<string> inputElements)
    {
        var length = Math.Min(targetElements.Count, inputElements.Count);
        if (length == 0)
        {
            return "";
        }

        return string.Create(length, (targetElements, inputElements), (buffer, state) =>
        {
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = StringComparer.Ordinal.Equals(state.targetElements[index], state.inputElements[index])
                    ? 'c'
                    : 'w';
            }
        });
    }

    public static double CalculateWpm(int correctCharacters, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null)
        {
            return 0;
        }

        var minutes = Math.Max((now - startedAt.Value).TotalMinutes, 1d / 60d);
        return Math.Round(correctCharacters / 5d / minutes, 2);
    }

    public static double CalculateAccuracy(int correctCharacters, int inputCharacters)
    {
        return inputCharacters == 0 ? 100 : Math.Round(correctCharacters * 100d / inputCharacters, 2);
    }

    public static string NormalizeBoundedInput(LiveRoomState room, string input)
    {
        var normalized = TypingEngine.NormalizeText(input);
        var inputLength = TypingEngine.SplitGraphemes(normalized).Count;
        if (inputLength > room.TargetElements.Count + MaxInputOverrunCharacters)
        {
            throw new InvalidOperationException("Die Eingabe ist zu lang.");
        }

        return normalized;
    }

    public static int CalculateRankHint(LiveRoomState room, Guid profileId)
    {
        var ranked = room.Participants.Values
            .OrderByDescending(item => item.CorrectCharacters)
            .ThenByDescending(item => item.Wpm)
            .ThenBy(item => item.JoinedAt)
            .ThenBy(item => item.ProfileId)
            .Select((participant, index) => new { participant.ProfileId, Rank = index + 1 })
            .FirstOrDefault(item => item.ProfileId == profileId);
        return ranked?.Rank ?? room.Participants.Count;
    }

    public static TimeSpan NormalizeDuration(TimeSpan duration) =>
        duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : duration;
}
