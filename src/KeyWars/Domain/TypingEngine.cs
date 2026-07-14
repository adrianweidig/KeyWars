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
        ("verwaltung", "Verwaltung: sorgfältig erfassen", "In der Verwaltung wechseln Aktenzeichen, Namen, Termine und Zuständigkeiten schnell. Ein Antrag wird geprüft, eine Frist wird gesetzt und eine Rückfrage landet im Postfach der richtigen Stelle. Schon ein verdrehter Buchstabe kann später Zeit kosten. Deshalb übt dieser Beispieltext ruhige Genauigkeit, saubere Großschreibung und eine Sprache, die auch nach mehreren Tagen noch verständlich bleibt. Sorgfältige Eingaben machen Ablagen, Auskünfte und Vertretungen verlässlich, auch wenn mehrere Personen am selben Vorgang arbeiten."),
        ("mil-lagezentrum", "Militär: Lagezentrum im Schichtwechsel", "Im Lagezentrum beginnt die Übergabe mit einem präzisen Lagebild. Der diensthabende Offizier nennt Auftrag, eigene Kräfte, verfügbare Mittel und erkennbare Veränderungen im Raum. Auf der Lagekarte werden Meldepunkte, Versorgungslinien und gesperrte Wege eindeutig markiert. Danach prüft die neue Schicht offene Meldungen, Fristen und Rückfragen. Begriffe wie Führungsfähigkeit, Schwerpunkt, Bereitschaftsgrad und Verbindungslage müssen klar verwendet werden, denn ungenaue Formulierungen erzeugen unnötige Rückfragen. Eine gute Übergabe trennt bestätigte Tatsachen, plausible Annahmen und noch ungeklärte Hinweise voneinander und hält Entscheidungen nachvollziehbar fest."),
        ("mil-logistik", "Militär: Logistik und Nachschubplanung", "Eine belastbare Nachschubplanung verbindet Bedarf, Bestand, Transport und Zeit. Die Logistikgruppe erfasst Betriebsstoff, Verpflegung, Ersatzteile, Sanitätsmaterial und persönliche Ausrüstung getrennt. Für jede Versorgungsklasse werden Meldebestand, Ausgabepunkt und voraussichtliche Reichweite dokumentiert. Ein Konvoi erhält Marschfolge, Zeitfenster, Ausweichroute und verantwortliche Ansprechstelle, ohne dass Sicherheitsabstände oder Ruhezeiten übergangen werden. Im Versorgungspunkt kontrolliert die Materialbewirtschaftung Lieferscheine und Fehlmengen. So bleiben Einsatzbereitschaft und Durchhaltefähigkeit erhalten, während unnötige Fahrten, doppelte Anforderungen und unklare Zuständigkeiten vermieden werden."),
        ("mil-sanitaet", "Militär: Sanitätsdienst und Verwundetentransport", "Der Sanitätsdienst arbeitet nach klaren Prioritäten und sorgfältiger Dokumentation. Am Behandlungsplatz werden Betroffene gesichtet, registriert und entsprechend ihrer Dringlichkeit versorgt. Die Verwundetenkarte begleitet jede Person und enthält Zeitpunkt, Befund, Maßnahmen sowie Transportfähigkeit. Für die Rettungskette stimmen Ersthelfende, Sanitätstrupp, Rettungsstation und aufnehmende Einrichtung ihre Übergaben ab. Funkmeldungen nennen nur notwendige Angaben und schützen persönliche Daten. Begriffe wie Triage, Patientensammelstelle, medizinische Evakuierung und Rückhaltefähigkeit beschreiben feste Aufgaben. Ruhige Kommunikation verhindert Missverständnisse und hilft, begrenzte Mittel verantwortungsvoll einzusetzen."),
        ("mil-funk", "Militär: Funkdisziplin und Meldewesen", "Funkdisziplin bedeutet, eine Verbindung kurz, eindeutig und aufmerksam zu nutzen. Vor dem Senden hört die Funkstelle den Kanal ab, bereitet die Meldung vor und verwendet das zugewiesene Rufzeichen. Wiederholungen, Buchstabiertafel und Rückleseverfahren sichern kritische Angaben wie Koordinaten, Uhrzeiten oder Mengen. Der Sprechfunkverkehr trennt Dringlichkeitsstufen und vermeidet persönliche oder unnötige Informationen. Ein Funkbetriebsbuch dokumentiert Verbindungsaufnahme, Störungen und wichtige Meldungen. Wenn die Primärverbindung ausfällt, greift der vorbereitete Verbindungsplan mit Ersatzfrequenz oder Melder. Gute Funkerinnen und Funker sprechen nicht besonders laut, sondern besonders klar."),
        ("mil-pionier", "Militär: Pioniererkundung und Beweglichkeit", "Pionierkräfte beurteilen Wege, Übergänge und Hindernisse, damit Bewegungen sicher geplant werden können. Ein Erkundungstrupp beschreibt Tragfähigkeit, Breite, Steigung, Untergrund und erkennbare Schäden eines Verkehrswegs. Bei einer Brücke werden Lastenklasse, Zufahrt und Ausweichmöglichkeiten festgehalten. Die Meldung unterscheidet zwischen Beobachtung und fachlicher Bewertung. Begriffe wie Sperre, Übergangsstelle, Räumstreifen, Behelfsbrücke und Pioniererkundung gehören zu einer gemeinsamen Sprache. Zugleich werden Umweltauflagen, zivile Nutzung und Eigenschutz berücksichtigt. Eine genaue Skizze mit Zeitstempel ist oft wertvoller als eine hastige Behauptung ohne überprüfbare Maße."),
        ("mil-aufklaerung", "Militär: Aufklärung und Informationsbewertung", "Aufklärung liefert Grundlagen für Entscheidungen, aber nicht jede Beobachtung ist bereits eine gesicherte Erkenntnis. Die Auswertung prüft Quelle, Aktualität, Plausibilität und mögliche Widersprüche. Eine einzelne Meldung kann einen Hinweis geben; erst der Abgleich mit weiteren Informationen verdichtet das Lagebild. Fachwörter wie Aufklärungsbedarf, Informationsanforderung, Sensor, Beobachtungsraum und Verifikation helfen bei der Ordnung. In der Lagebesprechung wird klar benannt, was bekannt, angenommen oder unbekannt ist. Diese Trennung schützt vor vorschnellen Schlüssen. Verantwortungsvolle Aufklärung beachtet rechtliche Grenzen, den Schutz Unbeteiligter und eine sparsame Weitergabe sensibler Daten."),
        ("mil-kartenkunde", "Militär: Kartenkunde und Orientierung", "Bei der Orientierung im Gelände ergänzen sich Karte, Kompass, Höhenprofil und sichtbare Geländemerkmale. Zuerst wird die Karte eingenordet, danach werden Standort und Marschrichtung geprüft. Gitternetz, Maßstab und Höhenlinien helfen, Entfernungen und Steigungen realistisch einzuschätzen. Eine Koordinate wird vollständig gelesen und zur Sicherheit zurückgelesen. Begriffe wie Nordrichtung, Geländetaufe, Bezugspunkt, Marschkompasszahl und Auffanglinie schaffen Eindeutigkeit. Auch digitale Karten benötigen Plausibilitätskontrollen, weil Akkustand, Empfang oder Datenstand unsicher sein können. Wer regelmäßig Standortbestimmungen vornimmt, erkennt Abweichungen früh und muss später keine große Orientierungsabweichung korrigieren."),
        ("mil-seefahrt", "Militär: Seewache und Brückenroutine", "Auf der Brücke eines Marineschiffs beginnt jede Wache mit einer vollständigen Übergabe. Kurs, Fahrt, Wetter, Verkehrslage und technische Einschränkungen werden gemeinsam geprüft. Die Wachoffizierin gleicht Radarbild, optische Beobachtung und elektronische Kartendarstellung miteinander ab. Ausguck, Rudergänger und Navigation melden Abweichungen mit festgelegten Begriffen. Kollisionsverhütungsregeln, Sicherheitsabstand und Manövrierfähigkeit bestimmen das weitere Vorgehen. Auch bei ruhiger See bleibt die Brückenbesatzung aufmerksam, denn Müdigkeit und Routine können kleine Hinweise verdecken. Ein sauber geführtes Logbuch macht Kursänderungen, besondere Vorkommnisse und Entscheidungen später nachvollziehbar."),
        ("mil-luftlage", "Militär: Luftlage und Flugsicherheit", "Die Luftlageführung verbindet gemeldete Flugbewegungen zu einem geordneten Gesamtbild. Jede Spur erhält Kennung, Höhe, Richtung, Geschwindigkeit und einen aktuellen Bewertungsstatus. Unklare Kontakte werden nicht vorschnell eingeordnet, sondern mit verfügbaren Sensoren und freigegebenen Fluginformationen abgeglichen. Fachbegriffe wie Luftraumordnung, Identifizierung, Flugkorridor, Sicherheitsstaffelung und Luftlagebild strukturieren die Zusammenarbeit. Flugsicherheit hat Vorrang: Zuständigkeiten, Freigaben und Abweichungen müssen eindeutig sein. Bei technischen Störungen wechseln die Beteiligten auf vorbereitete Ersatzverfahren. Sorgfältige Protokolle helfen, Entscheidungen zu prüfen und nach einer Schicht zuverlässig zu übergeben."),
        ("mil-cyberabwehr", "Militär: Cyberabwehr im Führungsnetz", "Ein ungewöhnlicher Anmeldeversuch im Führungsnetz wird zunächst als Sicherheitsereignis behandelt. Das Cyberabwehrteam sichert Protokolle, grenzt betroffene Systeme ein und bewertet die Auswirkung auf Verfügbarkeit, Vertraulichkeit und Integrität. Begriffe wie Lagefeststellung, Indikator, Segmentierung, Wiederanlauf und Meldekette sorgen für ein gemeinsames Verständnis. Die Einsatzleitung erhält regelmäßige, sachliche Updates ohne Spekulation. Beweise werden nachvollziehbar verwahrt, während kritische Dienste nach freigegebenen Notfallplänen weiterlaufen. Nach der Eindämmung folgen Ursachenanalyse und kontrollierte Wiederherstellung. Gute Cyberverteidigung verbindet technische Sorgfalt, klare Zuständigkeiten und geübte Kommunikation unter Zeitdruck."),
        ("mil-stabsarbeit", "Militär: Stabsarbeit und Befehlsgebung", "Im Stab werden Informationen nicht nur gesammelt, sondern in handlungsfähige Vorschläge übersetzt. Die Sachgebiete Personal, Nachrichtenwesen, Führung, Logistik und Planung bringen ihre Fachlage in einen gemeinsamen Führungsprozess ein. Ein Entschluss benennt Ziel, Schwerpunkt, Kräfteansatz, Zeit und wesentliche Koordinierung. Daraus entsteht ein Befehl mit klarer Gliederung: Lage, Auftrag, Durchführung, Einsatzunterstützung sowie Führung und Verbindung. Rückfragen sind ausdrücklich erwünscht, wenn eine Formulierung mehrdeutig bleibt. Eine gute Befehlsausgabe schafft Orientierung und Handlungsspielraum, dokumentiert Annahmen und lässt erkennen, wann eine erneute Lagebeurteilung erforderlich wird."),
        ("mil-winteruebung", "Militär: Winterübung im Gebirge", "Vor einer Winterübung im Gebirge prüft der Zug nicht nur Auftrag und Marschweg, sondern auch Wetterentwicklung, Lawinenlage und Kälteschutz. Die Gruppen kontrollieren Bekleidung, Notausrüstung, Verbindungsmittel und gegenseitige Beobachtung. Marschabstände und Pausen werden an Sicht, Gelände und Leistungsfähigkeit angepasst. Fachbegriffe wie Kälteschutz, Biwak, Sicherungsposten, Marschbereitschaft und Rückfallpunkt erscheinen im Ablaufplan. Niemand soll Erschöpfung oder Erfrierungsanzeichen verbergen. Eine verantwortungsvolle Übungsleitung plant Abbruchkriterien und Rettungskette vor dem Start. Dadurch wird aus Härte keine Leichtsinnigkeit, sondern belastbare Zusammenarbeit unter anspruchsvollen Bedingungen."),
        ("story-leuchtturm", "Geschichte: Die letzte Schicht im Leuchtturm", "Als Mara die letzte Schicht im alten Leuchtturm übernahm, lag der Hafen unter dichtem Nebel. Das neue automatische Feuer sollte am Morgen eingeschaltet werden, doch in dieser Nacht arbeitete noch das schwere Uhrwerk aus Messing. Mara hörte jeden Zahn, jedes Klicken und das regelmäßige Rollen der Linse. Kurz nach Mitternacht verstummte das Geräusch. Sie stieg die enge Treppe hinauf, fand eine gelöste Schraube und reparierte den Antrieb mit ruhigen Händen. Draußen antwortete ein Schiff mit seinem Horn, und der Lichtkegel begann wieder über das schwarze Wasser zu wandern."),
        ("story-nachtzug", "Geschichte: Der Nachtzug nach Norden", "Der Nachtzug verließ den kleinen Bahnhof um 23:17 Uhr. Im Abteil saßen eine Geigerin, ein müder Koch und ein Junge mit einem roten Atlas. Niemand sprach, bis der Zug auf freier Strecke langsamer wurde und schließlich zwischen verschneiten Feldern hielt. Der Schaffner erklärte, ein umgestürzter Ast blockiere das Gleis. Während draußen gearbeitet wurde, öffnete der Koch seinen Proviant, die Geigerin spielte eine leise Melodie und der Junge suchte die Stelle auf seiner Karte. Als der Zug weiterfuhr, kannten alle die Namen der anderen und teilten ein unerwartet warmes Frühstück."),
        ("story-bibliothek", "Geschichte: Das Fenster der alten Bibliothek", "In der Stadtbibliothek gab es ein Fenster, das sich jeden Herbst von selbst beschlug. Auf der Scheibe erschienen dann Buchstaben, obwohl im Lesesaal niemand geschrieben hatte. Die neue Bibliothekarin Lene hielt das zunächst für einen Scherz. Sie notierte die Zeichen und entdeckte einen Katalogcode, der zu einem längst vergessenen Reisebericht führte. Zwischen dessen Seiten lag der Brief einer früheren Mitarbeiterin mit Hinweisen auf ein verstecktes Kinderarchiv. Lene fand keine Geister, aber Hunderte Zeichnungen aus fünf Jahrzehnten. Im Frühjahr zeigte die Bibliothek sie öffentlich, und viele Besucher erkannten ihre eigene kindliche Handschrift."),
        ("story-bergwacht", "Geschichte: Lichtzeichen am Grat", "Kurz vor Sonnenuntergang bemerkte die Bergwacht ein unregelmäßiges Lichtzeichen am nördlichen Grat. Eine Wandergruppe war nicht zur vereinbarten Zeit zurückgekehrt. Einsatzleiter Tom teilte Suchabschnitte ein, prüfte Wetter und letzte Standortmeldung und schickte zwei Teams mit Wärmebildkamera los. Der Wind nahm zu, doch die Meldungen blieben kurz und ruhig. Hinter einer Felsstufe fanden die Retter drei erschöpfte Menschen, die mit einer Taschenlampe signalisiert hatten. Niemand war schwer verletzt. Beim Abstieg erzählte eine Wanderin, wie wichtig die vorher vereinbarte Rückkehrzeit gewesen war. Genau diese kleine Angabe hatte die Suche entscheidend verkürzt."),
        ("story-funkerin", "Geschichte: Die Funkerin von Nordhafen", "Im fiktiven Nordhafen übte eine Hilfseinheit die Versorgung nach einem schweren Sturm. Funkerin Aylin saß in einem provisorischen Lagezelt, während Regen gegen die Plane schlug. Meldungen über gesperrte Straßen, freie Unterkünfte und benötigte Medikamente trafen gleichzeitig ein. Aylin ordnete jede Nachricht nach Ort, Zeit und Dringlichkeit und ließ kritische Zahlen zurücklesen. Als das Hauptnetz ausfiel, wechselte sie ohne Hektik auf die vorbereitete Ersatzverbindung. Stunden später erreichten Fahrzeuge die richtigen Sammelpunkte. Die Übungsleitung lobte nicht ihre Lautstärke, sondern ihre Disziplin: Jede Meldung war knapp, verständlich und zur richtigen Zeit verfügbar."),
        ("story-archiv", "Geschichte: Der Plan im Stadtarchiv", "Restaurator Ben entrollte im Stadtarchiv einen beschädigten Bauplan aus dem Jahr 1912. Zwischen verblassten Linien stand eine handschriftliche Notiz über einen Versorgungstunnel unter dem alten Marktplatz. Weil dort bald gebaut werden sollte, verglich Ben den Plan mit Grundbüchern, Fotografien und modernen Leitungsdaten. Eine Historikerin erkannte das Zeichen einer früheren Wasserleitung. Gemeinsam informierten sie das Bauamt, das eine vorsichtige Untersuchung veranlasste. Der Tunnel existierte tatsächlich, war aber leer und stabil. Durch die gründliche Prüfung blieb die Baustelle sicher, und ein Teil der Stadtgeschichte konnte dokumentiert werden, bevor neue Fundamente entstanden."),
        ("story-orbit", "Geschichte: Drei Minuten über dem Horizont", "Die kleine Forschungsstation empfing den Wettersatelliten nur drei Minuten lang pro Umlauf. In diesem kurzen Fenster mussten Antenne, Empfänger und Speicher fehlerfrei zusammenspielen. Studentin Nika bemerkte, dass die letzten Datenpakete regelmäßig fehlten. Statt sofort die Hardware zu tauschen, verglich sie Zeitstempel, Temperatur und Antennenwinkel. Der Fehler trat nur auf, wenn ein alter Rechner seine Uhr verspätet synchronisierte. Nika korrigierte den Ablauf und wartete auf den nächsten Überflug. Diesmal füllte sich die Anzeige bis zum letzten Paket. Aus den Daten entstand später eine genaue Karte eines herannahenden Sommersturms."),
        ("story-werkstatt", "Geschichte: Das Motorrad aus Kiste sieben", "In Kiste sieben lagen die Teile eines Motorrads, das seit vierzig Jahren niemand gefahren hatte. Werkstattleiterin Sofia wollte es nicht einfach glänzend restaurieren. Sie fotografierte jede Baugruppe, suchte alte Handbücher und prüfte, welche Spuren zur Geschichte der Maschine gehörten. Ein Auszubildender entdeckte unter dem Tank eine eingeritzte Reiseroute. Gemeinsam ersetzten sie nur sicherheitskritische Teile und bewahrten den verblichenen Lack. Beim ersten Start hustete der Motor, dann lief er ruhig. Die Probefahrt endete nach wenigen hundert Metern, aber in der Werkstatt fühlte sie sich wie die Rückkehr einer langen Erinnerung an."),
        ("story-expedition", "Geschichte: Das Messbuch der Expedition", "Die Expedition erreichte das Hochmoor nach zwei Tagen Regen. Geologin Rika führte ein Messbuch, in dem jeder Fundort mit Uhrzeit, Wetter und Koordinate festgehalten wurde. Als ein Sensor plötzlich ungewöhnlich hohe Werte meldete, wollte niemand vorschnell von einer Entdeckung sprechen. Das Team wiederholte die Messung, prüfte Kalibrierung und nahm eine Kontrollprobe. Schließlich zeigte sich, dass eisenreiches Wasser den Ausschlag verursacht hatte. Die vermeintliche Sensation wurde zu einem soliden Datensatz. Am Abend erzählte Rika am Feuer, dass Wissenschaft oft dort beginne, wo eine aufregende Vermutung geduldig überprüft werde."),
        ("sach-rettungsleitstelle", "Sachtext: Koordination in der Rettungsleitstelle", "In einer Rettungsleitstelle treffen Notrufe, Rückmeldungen und Statusänderungen in schneller Folge ein. Die Disponentin erfragt Ort, Situation, Zahl der Betroffenen und erkennbare Gefahren, ohne die anrufende Person mit unnötigen Fragen zu überfordern. Anschließend alarmiert sie passende Kräfte und hält Zufahrtswege frei. Jede Information erhält einen Zeitstempel; neue Erkenntnisse werden als Ergänzung kenntlich gemacht. Wenn mehrere Einsätze gleichzeitig laufen, helfen Prioritäten und klare Zuständigkeiten. Gute Leitstellenarbeit bleibt für Außenstehende oft unsichtbar, verbindet aber Menschen, Fahrzeuge und Fachwissen zu einer verlässlichen Rettungskette."),
        ("sach-energienetz", "Sachtext: Stabilität im Energienetz", "Ein Stromnetz muss Erzeugung und Verbrauch in jedem Augenblick ausgleichen. Leitstellen beobachten Frequenz, Lastflüsse und verfügbare Reserveleistung. Wenn Wind oder Sonne stärker schwanken als erwartet, gleichen flexible Kraftwerke, Speicher und steuerbare Verbraucher die Differenz aus. Wartungsarbeiten werden lange vorbereitet, damit alternative Leitungen genügend Kapazität besitzen. Bei einer Störung schützen automatische Schalter Anlagenteile und begrenzen die Auswirkung. Danach analysieren Fachleute Messwerte und stellen Abschnitte kontrolliert wieder her. Versorgungssicherheit entsteht deshalb nicht durch eine einzelne Maschine, sondern durch Planung, Redundanz, Kommunikation und viele abgestimmte Entscheidungen."),
        ("sach-softwarestoerung", "Sachtext: Eine Softwarestörung geordnet lösen", "Als der Bestelldienst plötzlich langsam reagiert, eröffnet das Betriebsteam einen Störungskanal und benennt eine Einsatzleitung. Zuerst werden Symptome, Beginn und betroffene Funktionen festgehalten. Metriken zeigen eine überlastete Datenbankverbindung, doch das Team prüft weitere Hinweise, bevor es Änderungen vornimmt. Eine begrenzte Entlastungsmaßnahme stabilisiert den Dienst. Danach wird die fehlerhafte Konfiguration zurückgenommen und der Erfolg über mehrere Messfenster beobachtet. Im Nachbericht stehen Zeitlinie, Ursache, Wirkung und konkrete Verbesserungen. Eine gute Störungsbearbeitung sucht nicht nach Schuldigen, sondern nach belastbaren Systemen und verständlichen Entscheidungen."),
        ("sach-orchester", "Sachtext: Zusammenarbeit im Orchester", "Ein Orchester verbindet viele eigenständige Stimmen zu einem gemeinsamen Klang. Vor der Probe stimmen die Musikerinnen und Musiker ihre Instrumente auf denselben Referenzton. Die Dirigentin erläutert Tempo, Dynamik und Übergänge, doch innerhalb jeder Gruppe bleiben Zuhören und Verantwortung entscheidend. Ein Einsatz gelingt, wenn Blickkontakt, Atem und Rhythmus zusammenpassen. Fehler werden markiert und gezielt wiederholt, nicht mit größerer Lautstärke verdeckt. In der Aufführung wirkt das Ergebnis mühelos, obwohl dahinter genaue Vorbereitung steht. So zeigt ein Orchester, wie klare Führung und aufmerksame Zusammenarbeit einander ergänzen können."),
        ("sach-wasserwerk", "Sachtext: Vom Grundwasser bis zum Wasserhahn", "Im Wasserwerk wird Rohwasser gefördert, aufbereitet und kontinuierlich kontrolliert. Je nach Herkunft entfernen Filter Eisen, Mangan oder unerwünschte Trübstoffe. Messgeräte überwachen wichtige Werte, während Laborproben zusätzliche Sicherheit geben. Pumpen fördern das Trinkwasser in Speicher und Leitungsnetz; erhöhte Behälter gleichen Verbrauchsspitzen aus. Bei Arbeiten an einer Leitung werden Abschnitte abgesperrt, gespült und erst nach Prüfung wieder freigegeben. Auch Haushalte tragen Verantwortung, indem sie Installationen warten und Wasser nicht verschwenden. Hinter jedem geöffneten Wasserhahn steht somit eine lange Kette aus Technik, Hygiene und sorgfältiger Dokumentation."),
        ("sach-forschung", "Sachtext: Gute Forschung braucht überprüfbare Schritte", "Eine überzeugende Untersuchung beginnt mit einer klaren Frage. Forschende beschreiben Methode, Material und Auswertung so genau, dass andere den Weg nachvollziehen können. Messwerte werden nicht passend gemacht, sondern mit Unsicherheiten und möglichen Fehlerquellen veröffentlicht. Eine Kontrollgruppe oder Vergleichsmessung hilft, alternative Erklärungen zu prüfen. Auch ein Ergebnis ohne erwarteten Effekt kann wertvoll sein, wenn das Vorgehen sauber war. Vor der Veröffentlichung lesen Fachkolleginnen und Fachkollegen den Text kritisch. Wissenschaftliches Vertrauen entsteht nicht aus Gewissheit, sondern aus Transparenz, Wiederholbarkeit und der Bereitschaft, eine eigene Annahme zu korrigieren.")
    ];
}
