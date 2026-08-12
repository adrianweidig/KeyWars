using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KeyWars.Domain;

public sealed record TypingError(
    int Position,
    TypingErrorKind Kind,
    string Expected,
    string Actual,
    string Pattern);

public sealed record TypingMetrics(
    int CorrectCharacters,
    int IncorrectCharacters,
    int TotalCharacters,
    int Backspaces,
    int FocusLosses,
    int DurationMilliseconds,
    double Wpm,
    double RawWpm,
    double CharactersPerMinute,
    double Accuracy,
    double Consistency,
    int ConsistencySampleCount,
    double MeanWordMilliseconds,
    double WordTimingVariation,
    bool Completed,
    IReadOnlyList<TypingError> Errors);

public sealed record AttemptStart(Guid AttemptId, string Nonce, string Text, DateTimeOffset StartedAt);

public sealed class TypingEngine(TimeProvider timeProvider)
{
    private static readonly char[] WordSeparators = [' ', '\r', '\n', '\t'];
    private const long ExactAlignmentCellLimit = 1_000_000;
    private const int BandedAlignmentMaxDistance = 128;
    private const int BandedAlignmentWidth = BandedAlignmentMaxDistance * 2 + 1;
    private const int AlignmentCheckpointBlockSize = 256;

    public AttemptStart Start(string text)
    {
        var normalized = NormalizeText(text);
        var nonceBytes = RandomNumberGenerator.GetBytes(12);
        return new AttemptStart(Guid.CreateVersion7(), Convert.ToHexString(nonceBytes), normalized, timeProvider.GetUtcNow());
    }

    public TypingMetrics Analyze(
        string target,
        string input,
        TimeSpan duration,
        int backspaces,
        int focusLosses,
        bool timeMode = false,
        IReadOnlyList<int>? wordDurationsMilliseconds = null)
    {
        var targetElements = SplitGraphemes(NormalizeText(target));
        var inputElements = SplitGraphemes(NormalizeText(input));
        var alignment = Align(targetElements, inputElements);
        var lastInputStepIndex = alignment.FindLastIndex(step => step.Operation != AlignmentOperation.Delete);
        var correct = 0;
        var incorrect = 0;
        var errors = new List<TypingError>();

        for (var index = 0; index < alignment.Count; index++)
        {
            var step = alignment[index];
            if (step.Operation == AlignmentOperation.Match)
            {
                correct++;
                continue;
            }

            if (step.Operation == AlignmentOperation.Delete && index > lastInputStepIndex)
            {
                continue;
            }

            incorrect++;
            errors.Add(ToError(step, targetElements, inputElements));
        }

        var timing = CalculateConsistency(wordDurationsMilliseconds);
        var totalInput = inputElements.Count;
        var attempted = correct + incorrect;
        var minutes = Math.Max(duration.TotalMinutes, 1d / 60d);
        var accuracy = attempted == 0 ? 0 : (double)correct / attempted * 100d;
        var wpm = correct / 5d / minutes;
        var rawWpm = totalInput / 5d / minutes;
        var cpm = correct / minutes;
        var completed = timeMode
            ? totalInput > 0 && correct > 0
            : targetElements.Count == correct && incorrect == 0 && inputElements.Count == targetElements.Count;

        return new TypingMetrics(
            correct,
            incorrect,
            targetElements.Count,
            backspaces,
            focusLosses,
            (int)Math.Round(duration.TotalMilliseconds),
            Math.Round(wpm, 2),
            Math.Round(rawWpm, 2),
            Math.Round(cpm, 2),
            Math.Round(accuracy, 2),
            Math.Round(timing.Consistency, 2),
            timing.SampleCount,
            Math.Round(timing.MeanMilliseconds, 2),
            Math.Round(timing.CoefficientOfVariation, 4),
            completed,
            errors);
    }

    private static List<AlignmentStep> Align(IReadOnlyList<string> targetElements, IReadOnlyList<string> inputElements)
    {
        var suffixLength = 0;
        while (suffixLength < targetElements.Count &&
               suffixLength < inputElements.Count &&
               StringComparer.Ordinal.Equals(
                   targetElements[targetElements.Count - suffixLength - 1],
                   inputElements[inputElements.Count - suffixLength - 1]))
        {
            suffixLength++;
        }

        var targetPrefixLength = targetElements.Count - suffixLength;
        var inputPrefixLength = inputElements.Count - suffixLength;
        var steps = AlignPrefixes(targetElements, targetPrefixLength, inputElements, inputPrefixLength);
        for (var index = 0; index < suffixLength; index++)
        {
            steps.Add(new AlignmentStep(
                AlignmentOperation.Match,
                targetPrefixLength + index,
                inputPrefixLength + index));
        }

        return steps;
    }

    private static List<AlignmentStep> AlignPrefixes(
        IReadOnlyList<string> targetElements,
        int targetLength,
        IReadOnlyList<string> inputElements,
        int inputLength)
    {
        if (targetLength == 0)
        {
            var insertions = new List<AlignmentStep>(inputLength);
            for (var inputIndex = 0; inputIndex < inputLength; inputIndex++)
            {
                insertions.Add(new AlignmentStep(AlignmentOperation.Insert, 0, inputIndex));
            }

            return insertions;
        }

        if (inputLength == 0)
        {
            var deletions = new List<AlignmentStep>(targetLength);
            for (var targetIndex = 0; targetIndex < targetLength; targetIndex++)
            {
                deletions.Add(new AlignmentStep(AlignmentOperation.Delete, targetIndex, -1));
            }

            return deletions;
        }

        var cellCount = ((long)targetLength + 1) * (inputLength + 1);
        if (cellCount <= ExactAlignmentCellLimit)
        {
            return AlignExact(targetElements, targetLength, inputElements, inputLength);
        }

        var bandedSteps = TryAlignBanded(targetElements, targetLength, inputElements, inputLength);
        if (bandedSteps is not null)
        {
            return bandedSteps;
        }

        if (HaveNoCommonElements(targetElements, targetLength, inputElements, inputLength))
        {
            return AlignWithoutMatches(targetLength, inputLength);
        }

        return AlignWithCheckpoints(targetElements, targetLength, inputElements, inputLength);
    }

    private static List<AlignmentStep> AlignExact(
        IReadOnlyList<string> targetElements,
        int targetLength,
        IReadOnlyList<string> inputElements,
        int inputLength)
    {
        var columns = inputLength + 1;
        var operations = new AlignmentOperation[checked((targetLength + 1) * columns)];
        var previous = new int[columns];
        var current = new int[columns];
        for (var inputIndex = 1; inputIndex <= inputLength; inputIndex++)
        {
            previous[inputIndex] = inputIndex;
            operations[inputIndex] = AlignmentOperation.Insert;
        }

        for (var targetIndex = 1; targetIndex <= targetLength; targetIndex++)
        {
            CalculateFullDistanceRow(
                targetElements, targetIndex, inputElements, inputLength,
                previous, current, operations, targetIndex, columns);
            (previous, current) = (current, previous);
        }

        return TraceBackFull(operations, targetLength, inputLength, columns);
    }

    private static List<AlignmentStep>? TryAlignBanded(
        IReadOnlyList<string> targetElements,
        int targetLength,
        IReadOnlyList<string> inputElements,
        int inputLength)
    {
        if (Math.Abs((long)targetLength - inputLength) > BandedAlignmentMaxDistance ||
            targetLength > (int.MaxValue / BandedAlignmentWidth) - 1)
        {
            return null;
        }

        if (CalculateBandedDistance(targetElements, targetLength, inputElements, inputLength, null) >
            BandedAlignmentMaxDistance)
        {
            return null;
        }

        var operations = new AlignmentOperation[checked((targetLength + 1) * BandedAlignmentWidth)];
        CalculateBandedDistance(targetElements, targetLength, inputElements, inputLength, operations);

        var steps = new List<AlignmentStep>(Math.Max(targetLength, inputLength));
        var targetCursor = targetLength;
        var inputCursor = inputLength;
        while (targetCursor > 0 || inputCursor > 0)
        {
            var operation = operations[GetBandedOperationIndex(targetCursor, inputCursor)];
            AppendReverseStep(operation, ref targetCursor, ref inputCursor, steps);
        }

        steps.Reverse();
        return steps;
    }

    private static int CalculateBandedDistance(
        IReadOnlyList<string> targetElements,
        int targetLength,
        IReadOnlyList<string> inputElements,
        int inputLength,
        AlignmentOperation[]? operations)
    {
        const int unreachable = int.MaxValue / 4;
        var previous = new int[inputLength + 1];
        var current = new int[inputLength + 1];
        var initialInputEnd = Math.Min(inputLength, BandedAlignmentMaxDistance);
        for (var inputIndex = 1; inputIndex <= initialInputEnd; inputIndex++)
        {
            previous[inputIndex] = inputIndex;
            if (operations is not null)
            {
                operations[GetBandedOperationIndex(0, inputIndex)] = AlignmentOperation.Insert;
            }
        }

        for (var targetIndex = 1; targetIndex <= targetLength; targetIndex++)
        {
            var inputStart = Math.Max(0, targetIndex - BandedAlignmentMaxDistance);
            var inputEnd = Math.Min(inputLength, targetIndex + BandedAlignmentMaxDistance);
            var previousInputEnd = Math.Min(inputLength, targetIndex - 1 + BandedAlignmentMaxDistance);
            if (inputStart == 0)
            {
                current[0] = targetIndex;
                if (operations is not null)
                {
                    operations[GetBandedOperationIndex(targetIndex, 0)] = AlignmentOperation.Delete;
                }
            }

            for (var inputIndex = Math.Max(1, inputStart); inputIndex <= inputEnd; inputIndex++)
            {
                var matches = StringComparer.Ordinal.Equals(
                    targetElements[targetIndex - 1], inputElements[inputIndex - 1]);
                var substituteCost = previous[inputIndex - 1] + (matches ? 0 : 1);
                var deleteCost = inputIndex <= previousInputEnd ? previous[inputIndex] + 1 : unreachable;
                var insertCost = inputIndex > inputStart ? current[inputIndex - 1] + 1 : unreachable;
                current[inputIndex] = SelectAlignment(
                    matches, substituteCost, deleteCost, insertCost, out var operation);
                if (operations is not null)
                {
                    operations[GetBandedOperationIndex(targetIndex, inputIndex)] = operation;
                }
            }

            (previous, current) = (current, previous);
        }

        return previous[inputLength];
    }

    private static int GetBandedOperationIndex(int targetIndex, int inputIndex)
    {
        return checked(
            targetIndex * BandedAlignmentWidth +
            inputIndex - targetIndex + BandedAlignmentMaxDistance);
    }

    private static bool HaveNoCommonElements(
        IReadOnlyList<string> targetElements, int targetLength,
        IReadOnlyList<string> inputElements, int inputLength)
    {
        var targetIsSmaller = targetLength <= inputLength;
        var smallerElements = targetIsSmaller ? targetElements : inputElements;
        var smallerLength = targetIsSmaller ? targetLength : inputLength;
        var largerElements = targetIsSmaller ? inputElements : targetElements;
        var largerLength = targetIsSmaller ? inputLength : targetLength;
        var distinctElements = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < smallerLength; index++)
        {
            distinctElements.Add(smallerElements[index]);
        }

        for (var index = 0; index < largerLength; index++)
        {
            if (distinctElements.Contains(largerElements[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<AlignmentStep> AlignWithoutMatches(int targetLength, int inputLength)
    {
        var steps = new List<AlignmentStep>(Math.Max(targetLength, inputLength));
        var targetOffset = Math.Max(0, targetLength - inputLength);
        var inputOffset = Math.Max(0, inputLength - targetLength);
        for (var targetIndex = 0; targetIndex < targetOffset; targetIndex++)
        {
            steps.Add(new AlignmentStep(AlignmentOperation.Delete, targetIndex, -1));
        }

        for (var inputIndex = 0; inputIndex < inputOffset; inputIndex++)
        {
            steps.Add(new AlignmentStep(AlignmentOperation.Insert, 0, inputIndex));
        }

        var pairedLength = Math.Min(targetLength, inputLength);
        for (var index = 0; index < pairedLength; index++)
        {
            steps.Add(new AlignmentStep(
                AlignmentOperation.Substitute,
                targetOffset + index,
                inputOffset + index));
        }

        return steps;
    }

    private static int SelectAlignment(
        bool matches, int substituteCost, int deleteCost, int insertCost,
        out AlignmentOperation operation)
    {
        var bestCost = substituteCost;
        operation = matches ? AlignmentOperation.Match : AlignmentOperation.Substitute;

        // Strict comparisons preserve the public diagonal, delete, insert tie order.
        if (deleteCost < bestCost)
        {
            bestCost = deleteCost;
            operation = AlignmentOperation.Delete;
        }

        if (insertCost < bestCost)
        {
            bestCost = insertCost;
            operation = AlignmentOperation.Insert;
        }

        return bestCost;
    }

    private static void CalculateFullDistanceRow(
        IReadOnlyList<string> target, int targetIndex, IReadOnlyList<string> input, int inputLength,
        int[] previous, int[] current, AlignmentOperation[]? operations = null,
        int operationRow = 0, int operationColumns = 0)
    {
        current[0] = targetIndex;
        if (operations is not null)
        {
            operations[operationRow * operationColumns] = AlignmentOperation.Delete;
        }

        for (var inputIndex = 1; inputIndex <= inputLength; inputIndex++)
        {
            var matches = StringComparer.Ordinal.Equals(target[targetIndex - 1], input[inputIndex - 1]);
            current[inputIndex] = SelectAlignment(
                matches,
                previous[inputIndex - 1] + (matches ? 0 : 1),
                previous[inputIndex] + 1,
                current[inputIndex - 1] + 1,
                out var operation);
            if (operations is not null)
            {
                operations[operationRow * operationColumns + inputIndex] = operation;
            }
        }
    }

    private static List<AlignmentStep> TraceBackFull(
        AlignmentOperation[] operations, int targetLength, int inputLength, int columns)
    {
        var steps = new List<AlignmentStep>(Math.Max(targetLength, inputLength));
        var targetCursor = targetLength;
        var inputCursor = inputLength;
        while (targetCursor > 0 || inputCursor > 0)
        {
            var operation = operations[targetCursor * columns + inputCursor];
            AppendReverseStep(operation, ref targetCursor, ref inputCursor, steps);
        }

        steps.Reverse();
        return steps;
    }

    private static void AppendReverseStep(
        AlignmentOperation operation, ref int targetCursor, ref int inputCursor,
        List<AlignmentStep> steps)
    {
        switch (operation)
        {
            case AlignmentOperation.Match:
            case AlignmentOperation.Substitute:
                targetCursor--;
                inputCursor--;
                steps.Add(new AlignmentStep(operation, targetCursor, inputCursor));
                break;
            case AlignmentOperation.Delete:
                targetCursor--;
                steps.Add(new AlignmentStep(operation, targetCursor, -1));
                break;
            case AlignmentOperation.Insert:
                inputCursor--;
                steps.Add(new AlignmentStep(operation, targetCursor, inputCursor));
                break;
            default:
                throw new InvalidOperationException("Ungültiger Alignment-Zustand.");
        }
    }

    // Stored distance rows and exact block recomputation preserve the original tie-breaking.
    private static List<AlignmentStep> AlignWithCheckpoints(
        IReadOnlyList<string> targetElements, int targetLength,
        IReadOnlyList<string> inputElements, int inputLength)
    {
        var columns = inputLength + 1;
        var previous = new int[columns];
        var current = new int[columns];
        for (var inputIndex = 1; inputIndex <= inputLength; inputIndex++)
        {
            previous[inputIndex] = inputIndex;
        }

        var checkpointCount = ((targetLength - 1) / AlignmentCheckpointBlockSize) + 1;
        var checkpoints = new List<int[]>(checkpointCount)
        {
            (int[])previous.Clone()
        };
        for (var targetIndex = 1; targetIndex <= targetLength; targetIndex++)
        {
            CalculateFullDistanceRow(
                targetElements, targetIndex, inputElements, inputLength, previous, current);
            (previous, current) = (current, previous);
            if (targetIndex % AlignmentCheckpointBlockSize == 0 && targetIndex < targetLength)
            {
                checkpoints.Add((int[])previous.Clone());
            }
        }

        var blockOperations = new AlignmentOperation[
            checked((AlignmentCheckpointBlockSize + 1) * columns)];
        var steps = new List<AlignmentStep>(Math.Max(targetLength, inputLength));
        var targetCursor = targetLength;
        var inputCursor = inputLength;
        while (targetCursor > 0)
        {
            var blockStart = ((targetCursor - 1) / AlignmentCheckpointBlockSize) *
                AlignmentCheckpointBlockSize;
            var blockHeight = targetCursor - blockStart;
            Array.Copy(
                checkpoints[blockStart / AlignmentCheckpointBlockSize],
                previous,
                inputCursor + 1);

            for (var localTargetIndex = 1; localTargetIndex <= blockHeight; localTargetIndex++)
            {
                CalculateFullDistanceRow(
                    targetElements, blockStart + localTargetIndex,
                    inputElements, inputCursor, previous, current,
                    blockOperations, localTargetIndex, columns);
                (previous, current) = (current, previous);
            }

            while (targetCursor > blockStart)
            {
                var operation = blockOperations[
                    (targetCursor - blockStart) * columns + inputCursor];
                AppendReverseStep(operation, ref targetCursor, ref inputCursor, steps);
            }
        }

        while (inputCursor > 0)
        {
            AppendReverseStep(
                AlignmentOperation.Insert, ref targetCursor, ref inputCursor, steps);
        }

        steps.Reverse();
        return steps;
    }

    private static TypingError ToError(AlignmentStep step, IReadOnlyList<string> targetElements, IReadOnlyList<string> inputElements)
    {
        var actual = step.InputIndex >= 0 && step.InputIndex < inputElements.Count ? inputElements[step.InputIndex] : "";
        var kind = step.Operation switch
        {
            AlignmentOperation.Insert => TypingErrorKind.Insertion,
            AlignmentOperation.Delete => TypingErrorKind.Deletion,
            _ => TypingErrorKind.Substitution
        };
        var expected = kind == TypingErrorKind.Insertion
            ? ""
            : step.TargetIndex >= 0 && step.TargetIndex < targetElements.Count ? targetElements[step.TargetIndex] : "";
        var pattern = step.Operation == AlignmentOperation.Insert
            ? BuildInsertionPattern(targetElements, step.TargetIndex, actual)
            : BuildExpectedPattern(targetElements, step.TargetIndex);

        return new TypingError(Math.Max(0, step.TargetIndex), kind, expected, actual, pattern);
    }

    private static string BuildExpectedPattern(IReadOnlyList<string> targetElements, int index)
    {
        if (targetElements.Count == 0)
        {
            return "";
        }

        if (index >= 0 && index < targetElements.Count - 1)
        {
            return targetElements[index] + targetElements[index + 1];
        }

        if (index > 0 && index < targetElements.Count)
        {
            return targetElements[index - 1] + targetElements[index];
        }

        return index >= 0 && index < targetElements.Count ? targetElements[index] : "";
    }

    private static string BuildInsertionPattern(IReadOnlyList<string> targetElements, int index, string actual)
    {
        if (index > 0 && index <= targetElements.Count)
        {
            return targetElements[index - 1] + actual;
        }

        return actual;
    }

    private static ConsistencyScore CalculateConsistency(IReadOnlyList<int>? wordDurationsMilliseconds)
    {
        var samples = (wordDurationsMilliseconds ?? [])
            .Where(value => value > 0)
            .Take(200)
            .Select(value => (double)value)
            .ToArray();
        if (samples.Length == 0)
        {
            return new ConsistencyScore(100, 0, 0, 0);
        }

        var mean = samples.Average();
        if (samples.Length == 1)
        {
            return new ConsistencyScore(100, 1, mean, 0);
        }

        var variance = samples.Sum(value => Math.Pow(value - mean, 2)) / samples.Length;
        var coefficientOfVariation = mean <= 0 ? 0 : Math.Sqrt(variance) / mean;
        var consistency = Math.Clamp(100d - coefficientOfVariation * 100d, 0d, 100d);
        return new ConsistencyScore(consistency, samples.Length, mean, coefficientOfVariation);
    }

    public string BuildWeaknessText(IReadOnlyCollection<WeaknessObservation> observations, int wordTarget = 60)
    {
        var patterns = observations
            .Where(item => item.Attempts >= 5)
            .OrderByDescending(item => (double)item.Errors / Math.Max(1, item.Attempts))
            .ThenByDescending(item => item.LastSeenAt)
            .Take(5)
            .Select(item => item.Pattern)
            .ToArray();

        var seedWords = GermanWordBank.Words
            .Where(word => patterns.Length == 0 || patterns.Any(pattern => word.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .DefaultIfEmpty("Training")
            .Take(wordTarget)
            .ToArray();

        if (seedWords.Length < wordTarget)
        {
            seedWords = seedWords.Concat(GermanWordBank.Words.Take(wordTarget - seedWords.Length)).ToArray();
        }

        return string.Join(' ', seedWords).Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeText(string value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Trim().Normalize(NormalizationForm.FormC);
    }

    public static IReadOnlyList<string> SplitGraphemes(string value)
    {
        var list = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            list.Add((string)enumerator.Current);
        }

        return list;
    }

    public static string BuildWordTest(int wordCount)
    {
        if (wordCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordCount), "Die Wortzahl muss positiv sein.");
        }

        if (wordCount > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(wordCount), "Die Wortzahl darf maximal 200 betragen.");
        }

        var source = GermanWordBank.WordTestWords;
        var words = new string[wordCount];
        for (var index = 0; index < wordCount; index++)
        {
            words[index] = source[index % source.Length];
        }

        return string.Join(' ', words);
    }

    public static int CountWords(string text)
    {
        return NormalizeText(text).Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private enum AlignmentOperation : byte
    {
        Match,
        Substitute,
        Insert,
        Delete
    }

    private readonly record struct AlignmentStep(AlignmentOperation Operation, int TargetIndex, int InputIndex);

    private readonly record struct ConsistencyScore(double Consistency, int SampleCount, double MeanMilliseconds, double CoefficientOfVariation);
}
