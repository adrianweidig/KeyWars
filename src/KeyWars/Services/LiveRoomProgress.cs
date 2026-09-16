using System.Text;
using System.Text.Json;
using KeyWars.Domain;

namespace KeyWars.Services;

public sealed record LiveProgressInputDelta(
    int BaseRevision,
    int Revision,
    int BackspaceCount,
    string AppendText,
    string? ResyncInput = null);

public sealed record LiveProgressInputAck(
    int Revision,
    bool Accepted,
    bool RequiresResync);

public enum LiveProgressInputStatus
{
    Applied,
    Duplicate,
    ResyncRequired
}

internal static class LiveRoomProgress
{
    internal const int MaxInputOverrunCharacters = 20;
    private const int MaxInputUtf8Bytes = LiveOptions.MaximumSafeArenaTargetUtf8Bytes + 1024;
    internal const int MaxTypedStateDetailCharacters = 32;

    public static string SerializeInputDelta(LiveProgressInputDelta delta) =>
        JsonSerializer.Serialize(delta);

    public static LiveProgressInputDelta DeserializeInputDelta(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<LiveProgressInputDelta>(payload)
                ?? throw new InvalidOperationException("Das Arena-Eingabedelta fehlt.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Das Arena-Eingabedelta ist ungültig.", exception);
        }
    }

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
        var correctCharacters = CountCorrectPrefix(targetElements, inputElements);
        return BuildTypedStateWindow(targetElements, inputElements, correctCharacters).Preview;
    }

    public static LiveProgressStateWindow BuildTypedStateWindow(
        IReadOnlyList<string> targetElements,
        IReadOnlyList<string> inputElements,
        int correctCharacters)
    {
        var typedLength = Math.Min(targetElements.Count, inputElements.Count);
        if (typedLength == 0)
        {
            return new LiveProgressStateWindow(0, 0, "", "");
        }

        var hasMismatch = correctCharacters < typedLength;
        var focus = hasMismatch ? correctCharacters : typedLength - 1;
        var offset = hasMismatch
            ? Math.Max(0, focus - 8)
            : Math.Max(0, typedLength - MaxTypedStateDetailCharacters);
        var length = Math.Min(MaxTypedStateDetailCharacters, typedLength - offset);
        var bytes = new byte[(length + 7) / 8];
        var preview = new char[length];
        for (var index = 0; index < length; index++)
        {
            var matches = StringComparer.Ordinal.Equals(
                targetElements[offset + index],
                inputElements[offset + index]);
            preview[index] = matches ? 'c' : 'w';
            if (matches)
            {
                bytes[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return new LiveProgressStateWindow(
            offset,
            length,
            Convert.ToBase64String(bytes),
            new string(preview));
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
        EnsureBoundedUtf8(normalized);
        var inputLength = TypingEngine.SplitGraphemes(normalized).Count;
        if (inputLength > room.TargetElements.Count + MaxInputOverrunCharacters)
        {
            throw new InvalidOperationException("Die Eingabe ist zu lang.");
        }

        return normalized;
    }

    public static IReadOnlyList<string> NormalizeBoundedDeltaText(LiveRoomState room, string input)
    {
        var normalized = TypingEngine.NormalizeText(input);
        EnsureBoundedUtf8(normalized);
        var elements = TypingEngine.SplitGraphemes(normalized);
        if (elements.Count > room.TargetElements.Count + MaxInputOverrunCharacters)
        {
            throw new InvalidOperationException("Die Eingabe ist zu lang.");
        }

        return elements;
    }

    public static TimeSpan NormalizeDuration(TimeSpan duration) =>
        duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : duration;

    private static void EnsureBoundedUtf8(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > MaxInputUtf8Bytes)
        {
            throw new InvalidOperationException("Die Eingabe ist zu groß.");
        }
    }
}

internal readonly record struct LiveProgressStateWindow(
    int Offset,
    int Length,
    string Bits,
    string Preview);
