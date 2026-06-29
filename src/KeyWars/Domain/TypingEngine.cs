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
        var targetCount = targetElements.Count;
        var inputCount = inputElements.Count;
        var distance = new int[targetCount + 1, inputCount + 1];
        var operation = new AlignmentOperation[targetCount + 1, inputCount + 1];

        for (var index = 1; index <= targetCount; index++)
        {
            distance[index, 0] = index;
            operation[index, 0] = AlignmentOperation.Delete;
        }

        for (var index = 1; index <= inputCount; index++)
        {
            distance[0, index] = index;
            operation[0, index] = AlignmentOperation.Insert;
        }

        for (var targetIndex = 1; targetIndex <= targetCount; targetIndex++)
        {
            for (var inputIndex = 1; inputIndex <= inputCount; inputIndex++)
            {
                var matches = StringComparer.Ordinal.Equals(targetElements[targetIndex - 1], inputElements[inputIndex - 1]);
                var substituteCost = distance[targetIndex - 1, inputIndex - 1] + (matches ? 0 : 1);
                var deleteCost = distance[targetIndex - 1, inputIndex] + 1;
                var insertCost = distance[targetIndex, inputIndex - 1] + 1;

                var bestCost = substituteCost;
                var bestOperation = matches ? AlignmentOperation.Match : AlignmentOperation.Substitute;
                if (deleteCost < bestCost)
                {
                    bestCost = deleteCost;
                    bestOperation = AlignmentOperation.Delete;
                }

                if (insertCost < bestCost)
                {
                    bestCost = insertCost;
                    bestOperation = AlignmentOperation.Insert;
                }

                distance[targetIndex, inputIndex] = bestCost;
                operation[targetIndex, inputIndex] = bestOperation;
            }
        }

        var steps = new List<AlignmentStep>();
        var targetCursor = targetCount;
        var inputCursor = inputCount;
        while (targetCursor > 0 || inputCursor > 0)
        {
            var current = operation[targetCursor, inputCursor];
            switch (current)
            {
                case AlignmentOperation.Match:
                case AlignmentOperation.Substitute:
                    targetCursor--;
                    inputCursor--;
                    steps.Add(new AlignmentStep(current, targetCursor, inputCursor));
                    break;
                case AlignmentOperation.Delete:
                    targetCursor--;
                    steps.Add(new AlignmentStep(current, targetCursor, -1));
                    break;
                case AlignmentOperation.Insert:
                    inputCursor--;
                    steps.Add(new AlignmentStep(current, targetCursor, inputCursor));
                    break;
            }
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

    private enum AlignmentOperation
    {
        Match,
        Substitute,
        Insert,
        Delete
    }

    private readonly record struct AlignmentStep(AlignmentOperation Operation, int TargetIndex, int InputIndex);

    private readonly record struct ConsistencyScore(double Consistency, int SampleCount, double MeanMilliseconds, double CoefficientOfVariation);
}

public static class GermanWordBank
{
    private const string WordTestCorpus = "Der frühe Morgen beginnt ruhig, doch im Team wartet bereits die nächste Aufgabe. " +
        "Eine Kollegin prüft den Kalender, ein Kollege sortiert die wichtigsten Nachrichten und alle schreiben mit klarem Rhythmus. " +
        "Gute Tipptechnik entsteht nicht durch Hast, sondern durch sichere Bewegungen, kurze Pausen und einen Blick für Fehler. " +
        "Wer den Text aufmerksam verfolgt, erkennt Namen, Zahlen und Satzzeichen rechtzeitig und bleibt auch unter Zeitdruck präzise. " +
        "Im Wettbewerb zählt das beste Ergebnis, aber im Training zählt jede saubere Wiederholung. " +
        "Nach einigen Minuten fühlt sich die Tastatur vertrauter an, die Finger finden schneller ihren Weg und der Kopf bleibt frei für den Inhalt. " +
        "So wird aus einer einzelnen Runde ein sichtbarer Fortschritt, der zum nächsten Versuch motiviert.";

    public static readonly string[] WordTestWords = WordTestCorpus.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static readonly string[] Words =
    [
        "aber", "achten", "Änderung", "Arbeit", "Aufgabe", "Büro", "Chance", "Code", "denken", "direkt",
        "ehrlich", "einfach", "Fähigkeit", "Frage", "genau", "größer", "heute", "intern", "Jahr", "Küche",
        "lernen", "lösen", "Mensch", "Minute", "nächste", "öffentlich", "prüfen", "Qualität", "Räume",
        "schnell", "Schlüssel", "schreiben", "Straße", "üben", "Verlauf", "Wörter", "Ziel", "ß", "Zahl",
        "Team", "Signal", "Profil", "Runde", "Serie", "Text", "Tempo", "Fehler", "Fokus", "Gewinn",
        "Druck", "Stärke", "Woche", "Monat", "Start", "Ende", "Punkt", "Fenster", "Gerät", "Satz"
    ];

    public static readonly (string Key, string Title, string Body)[] StandardTexts =
    [
        ("standard-kurz", "Büroalltag mit klarem Fokus", "Im Büro beginnt die ruhige Runde mit einem klaren Auftrag: zuerst die Nachricht lesen, dann die Fakten ordnen und erst danach schreiben. Wer konzentriert bleibt, vermeidet doppelte Rückfragen, falsche Namen und unnötige Korrekturen. Genaues Tippen ist kein Selbstzweck, sondern spart im Team jeden Tag Minuten, die sonst in kleinen Missverständnissen verschwinden. Die beste Geschwindigkeit entsteht aus sicheren Bewegungen und sauberem Rhythmus."),
        ("umlaute", "Deutsch mit Umlauten und präzisen Namen", "Umlaute gehören zu vielen echten Namen, Orten und Vorgängen. Eine Anfrage aus Köln, ein Vertrag aus München oder ein Gerät in der Straße Am Ölberg soll ohne Ausweichen korrekt erfasst werden. Wer Ä, Ö, Ü und ß sicher trifft, schreibt nicht nur schneller, sondern auch respektvoller und genauer. Deshalb trainiert dieser Text bewusst Wörter mit Länge, Klang und kleinen Stolperstellen."),
        ("zahlen", "IT-Support mit Zahlen, Pfaden und Zeichen", "Im Support zählt jedes Zeichen: Ticket 2026-0417 betrifft den Pfad C:\\Daten\\Tickets\\2026, eine E-Mail an support@example.local und einen Fehler um 08:30 Uhr. Die Rückmeldung nennt 3 betroffene Geräte, 21 gesicherte Dateien und 100 % abgeschlossene Prüfung. Saubere Zahlen, Doppelpunkte, Bindestriche und Backslashes verhindern, dass aus einer schnellen Notiz ein neuer Fehler wird. Wenn danach eine Seriennummer wie KW-77A-19 geprüft wird, muss sie exakt so im Protokoll stehen."),
        ("arena", "Live-Arena Teamrennen", "In der Live-Arena tippen alle denselben Zieltext und starten erst, wenn der Countdown abgelaufen ist. Während des Rennens zeigt das Textboard, welche Stellen schon korrekt sind, wo Fehler stehen und an welcher Position die anderen gerade arbeiten. Dadurch fühlt sich der Wettbewerb wie ein gemeinsamer Raum an: konzentriert, fair und nachvollziehbar bis zum Zieleinlauf. Wer kurz zurückfällt, sieht trotzdem sofort, wo die eigene nächste Taste liegt."),
        ("ausbildung", "Ausbildung: konzentriert dokumentieren", "In der Ausbildung hilft ein sauberer Bericht mehr als ein hastiger Satz. Beobachtungen werden vollständig notiert, Arbeitsschritte nachvollziehbar beschrieben und offene Fragen klar markiert. Wer beim Tippen ruhig bleibt, kann Prüfprotokolle, Lernnotizen und kurze Übergaben ohne Chaos schreiben. Der Text trainiert längere Satzfolgen, damit Tempo und Verständlichkeit gemeinsam wachsen. Am Ende zählt nicht der schnellste Entwurf, sondern eine Notiz, die Ausbilderinnen und Kollegen zuverlässig weiterverwenden können."),
        ("verwaltung", "Verwaltung: sorgfältig erfassen", "In der Verwaltung wechseln Aktenzeichen, Namen, Termine und Zuständigkeiten schnell. Ein Antrag wird geprüft, eine Frist wird gesetzt und eine Rückfrage landet im Postfach der richtigen Stelle. Schon ein verdrehter Buchstabe kann später Zeit kosten. Deshalb übt dieser Beispieltext ruhige Genauigkeit, saubere Großschreibung und eine Sprache, die auch nach mehreren Tagen noch verständlich bleibt. Sorgfältige Eingaben machen Ablagen, Auskünfte und Vertretungen verlässlich, auch wenn mehrere Personen am selben Vorgang arbeiten.")
    ];
}
