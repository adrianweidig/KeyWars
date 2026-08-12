using System.Globalization;

namespace KeyWars.LoadTesting;

internal enum LoadScenario
{
    Smoke,
    Ramp,
    Steady,
    Soak,
    Spike
}

internal sealed record SloOptions(
    double OperationP95Milliseconds,
    double OperationP99Milliseconds,
    double FanoutP95Milliseconds,
    double MaximumErrorRatePercent,
    long MaximumMissingBroadcasts);

internal sealed record LoadTestOptions(
    IReadOnlyList<Uri> BaseUrls,
    int? ForcedNode,
    LoadScenario Scenario,
    int Rooms,
    int Participants,
    int Steps,
    TimeSpan RampDuration,
    TimeSpan StepDelay,
    TimeSpan TypingJitter,
    TimeSpan LoginDelay,
    int ReconnectPercent,
    TimeSpan OperationTimeout,
    TimeSpan OverallTimeout,
    int MetricCapacity,
    int? TargetProcessId,
    int Seed,
    string? JsonPath,
    SloOptions Slo)
{
    public static string Usage => """
        KeyWars-Lasttest

          Schnell:  dotnet run --project tools/KeyWars.LoadTest -- 2 25 64
          Netzwerk: dotnet run --project tools/KeyWars.LoadTest -- --signalr [Optionen]
          Selbsttest: dotnet run --project tools/KeyWars.LoadTest -- --self-test

        Netzwerkoptionen:
          --base-url URL              wiederholbar; Clients werden auf Knoten verteilt
          --base-urls URL1,URL2       alternative Mehrfachangabe
          --forced-node INDEX         alle Clients gezielt an einen Knoten binden
          --scenario NAME             smoke|ramp|steady|soak|spike
          --rooms N                   parallele Räume
          --participants N            Teilnehmende je Raum
          --steps N                   Tippereignisse je Teilnehmendem
          --typing-cps N              mittlere Anschläge pro Sekunde
          --jitter-ms N               zufälliger Tipp-Jitter (+/-)
          --ramp-seconds N            Anmeldungen/Verbindungen zeitlich verteilen
          --login-delay-ms N          zusätzliche Pause nach jedem Login
          --reconnect-percent N       Anteil mit Stop/Start/Join zur Laufmitte
          --operation-timeout-ms N    Zeitlimit einzelner HTTP-/Hub-Operationen
          --timeout-seconds N         hartes Gesamtlaufzeitlimit
          --metric-capacity N         maximale Latenzstichproben je Operation
          --target-process-id N       lokale Zielprozess-CPU/RSS/Threads messen
          --json PATH                 vollständigen JSON-Bericht schreiben
          --slo-p95-ms N              p95-Grenze je Operation
          --slo-p99-ms N              p99-Grenze je Operation
          --slo-fanout-p95-ms N       p95-Grenze für jede Empfängerzustellung
          --slo-error-rate-percent N  maximale Fehlerrate über alle Operationen
          --slo-missing-broadcasts N  maximal fehlende Empfängerzustellungen
        """;

    public static LoadTestOptions Parse(string[] args)
    {
        var reader = new ArgumentReader(args);
        var scenario = ParseScenario(reader.Optional("--scenario") ?? "smoke");
        var preset = ScenarioPreset.For(scenario);
        var baseUrls = ParseBaseUrls(reader.All("--base-url"), reader.All("--base-urls"));
        var typingCps = reader.Double("--typing-cps", preset.TypingCharactersPerSecond, 0.1, 60);
        var stepDelayMs = reader.OptionalInt("--step-delay-ms")
            ?? (int)Math.Round(1000d / typingCps, MidpointRounding.AwayFromZero);

        var options = new LoadTestOptions(
            baseUrls,
            reader.OptionalInt("--forced-node"),
            scenario,
            reader.Int("--rooms", preset.Rooms, 1, 10_000),
            reader.Int("--participants", preset.Participants, 2, 100_000),
            reader.Int("--steps", preset.Steps, 1, 10_000_000),
            TimeSpan.FromSeconds(reader.Double("--ramp-seconds", preset.RampSeconds, 0, 86_400)),
            TimeSpan.FromMilliseconds(Math.Clamp(stepDelayMs, 1, 60_000)),
            TimeSpan.FromMilliseconds(reader.Int("--jitter-ms", preset.JitterMilliseconds, 0, 30_000)),
            TimeSpan.FromMilliseconds(reader.Int("--login-delay-ms", 0, 0, 60_000)),
            reader.Int("--reconnect-percent", preset.ReconnectPercent, 0, 100),
            TimeSpan.FromMilliseconds(reader.Int("--operation-timeout-ms", 10_000, 100, 600_000)),
            TimeSpan.FromSeconds(reader.Int("--timeout-seconds", preset.TimeoutSeconds, 1, 604_800)),
            reader.Int("--metric-capacity", 50_000, 100, 1_000_000),
            reader.OptionalInt("--target-process-id"),
            reader.Int("--seed", 17_031, 0, int.MaxValue),
            reader.Optional("--json"),
            new SloOptions(
                reader.Double("--slo-p95-ms", 2_000, 0.1, 3_600_000),
                reader.Double("--slo-p99-ms", 5_000, 0.1, 3_600_000),
                reader.Double("--slo-fanout-p95-ms", 3_000, 0.1, 3_600_000),
                reader.Double("--slo-error-rate-percent", 0, 0, 100),
                reader.Long("--slo-missing-broadcasts", 0, 0, long.MaxValue)));

        if (options.ForcedNode is < 0 || options.ForcedNode >= options.BaseUrls.Count)
        {
            throw new ArgumentException($"--forced-node muss zwischen 0 und {options.BaseUrls.Count - 1} liegen.");
        }

        return options;
    }

    public int NodeFor(int roomIndex, int participantIndex) =>
        ForcedNode ?? ((roomIndex * Participants) + participantIndex) % BaseUrls.Count;

    public TimeSpan DelayForStep(int roomIndex, int step)
    {
        if (TypingJitter == TimeSpan.Zero)
        {
            return StepDelay;
        }

        var random = new Random(HashCode.Combine(Seed, roomIndex, step));
        var jitter = random.NextDouble() * 2d - 1d;
        return TimeSpan.FromMilliseconds(Math.Max(1, StepDelay.TotalMilliseconds + (TypingJitter.TotalMilliseconds * jitter)));
    }

    private static IReadOnlyList<Uri> ParseBaseUrls(IEnumerable<string> repeated, IEnumerable<string> grouped)
    {
        var values = repeated.Concat(grouped)
            .SelectMany(value => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .DefaultIfEmpty("http://127.0.0.1:5187")
            .Select(value => new Uri(value, UriKind.Absolute))
            .Distinct()
            .ToArray();
        if (values.Any(value => value.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("Base-URLs müssen http oder https verwenden.");
        }

        return values;
    }

    private static LoadScenario ParseScenario(string value) =>
        Enum.TryParse<LoadScenario>(value, true, out var scenario)
            ? scenario
            : throw new ArgumentException($"Unbekanntes Szenario '{value}'. Erlaubt: smoke, ramp, steady, soak, spike.");
}

internal sealed record ScenarioPreset(
    int Rooms,
    int Participants,
    int Steps,
    double TypingCharactersPerSecond,
    int JitterMilliseconds,
    double RampSeconds,
    int ReconnectPercent,
    int TimeoutSeconds)
{
    public static ScenarioPreset For(LoadScenario scenario) => scenario switch
    {
        LoadScenario.Smoke => new(1, 2, 8, 5, 30, 0, 0, 120),
        LoadScenario.Ramp => new(5, 10, 40, 5, 40, 30, 2, 300),
        LoadScenario.Steady => new(10, 20, 300, 6, 45, 60, 2, 900),
        LoadScenario.Soak => new(20, 25, 7_200, 4, 60, 300, 5, 7_200),
        LoadScenario.Spike => new(20, 25, 40, 8, 20, 0, 10, 600),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };
}

internal sealed class ArgumentReader(string[] args)
{
    public string? Optional(string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public IEnumerable<string> All(string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                yield return args[index + 1];
            }
        }
    }

    public int? OptionalInt(string name)
    {
        var value = Optional(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} erwartet eine Ganzzahl.");
    }

    public int Int(string name, int fallback, int minimum, int maximum)
    {
        var value = OptionalInt(name) ?? fallback;
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentException($"{name} muss zwischen {minimum} und {maximum} liegen.");
    }

    public long Long(string name, long fallback, long minimum, long maximum)
    {
        var raw = Optional(name);
        var value = raw is null
            ? fallback
            : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"{name} erwartet eine Ganzzahl.");
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentException($"{name} muss zwischen {minimum} und {maximum} liegen.");
    }

    public double Double(string name, double fallback, double minimum, double maximum)
    {
        var raw = Optional(name);
        var value = raw is null
            ? fallback
            : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"{name} erwartet eine Zahl mit Punkt als Dezimaltrennzeichen.");
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentException($"{name} muss zwischen {minimum} und {maximum} liegen.");
    }
}
