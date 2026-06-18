using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KeyWars.Domain;

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
    bool Completed);

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
        bool timeMode = false)
    {
        var targetElements = SplitGraphemes(NormalizeText(target));
        var inputElements = SplitGraphemes(NormalizeText(input));
        var correct = 0;
        var incorrect = 0;
        var comparable = inputElements.Count;

        for (var index = 0; index < comparable; index++)
        {
            if (index >= targetElements.Count)
            {
                incorrect++;
                continue;
            }

            if (StringComparer.Ordinal.Equals(targetElements[index], inputElements[index]))
            {
                correct++;
            }
            else
            {
                incorrect++;
            }
        }

        var totalInput = inputElements.Count;
        var minutes = Math.Max(duration.TotalMinutes, 1d / 60d);
        var accuracy = totalInput == 0 ? 0 : (double)correct / totalInput * 100d;
        var wpm = correct / 5d / minutes;
        var rawWpm = totalInput / 5d / minutes;
        var cpm = correct / minutes;
        var penalty = (incorrect * 3d) + backspaces + (focusLosses * 2d);
        var consistency = Math.Clamp(100d - penalty / Math.Max(1, targetElements.Count) * 100d, 0d, 100d);
        var completed = targetElements.Count == inputElements.Count && incorrect == 0;

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
            Math.Round(consistency, 2),
            completed);
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

        var words = new string[wordCount];
        for (var index = 0; index < wordCount; index++)
        {
            words[index] = GermanWordBank.Words[index % GermanWordBank.Words.Length];
        }

        return string.Join(' ', words);
    }

    public static int CountWords(string text)
    {
        return NormalizeText(text).Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

public static class GermanWordBank
{
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
        ("standard-kurz", "Deutscher Kurztext", "Heute üben wir klare Sätze, ruhige Hände und genaue Anschläge. Jede Runde zählt, aber Genauigkeit bleibt wichtiger als Hektik."),
        ("umlaute", "Umlaute und ß", "Äpfel, Öl, Übermut und die große Straße gehören fest zum deutschen Schreiben. Übe ä, ö, ü, Ä, Ö, Ü und ß bewusst."),
        ("zahlen", "Zahlen und Sonderzeichen", "Im Jahr 2026 prüfen wir 15 Werte: 3, 7, 21 und 42. E-Mail, Pfad C:\\Daten und 100 % Genauigkeit sind erlaubt."),
        ("arena", "Arena-Standard", "Alle Teilnehmenden tippen denselben Text. Der Start erfolgt gemeinsam, der Fortschritt wird live angezeigt und die Platzierung entsteht erst nach dem Zieleinlauf.")
    ];
}
