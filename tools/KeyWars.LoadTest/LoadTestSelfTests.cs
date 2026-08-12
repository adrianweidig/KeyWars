namespace KeyWars.LoadTesting;

internal static class LoadTestSelfTests
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("bounded metrics", TestBoundedMetricsAsync),
            ("multi-node options", TestMultiNodeOptionsAsync),
            ("normalized fan-out key", TestNormalizedFanoutKeyAsync),
            ("complete fan-out", () => TestFanoutAsync(cancellationToken)),
            ("missing fan-out", () => TestMissingFanoutAsync(cancellationToken)),
            ("SLO failure", TestSloAsync)
        };

        foreach (var test in tests)
        {
            await test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }

        Console.WriteLine($"{tests.Length} Lasttest-Selbsttests bestanden.");
        return 0;
    }

    private static Task TestBoundedMetricsAsync()
    {
        var series = new BoundedMetricSeries(5);
        for (var index = 1; index <= 100; index++)
        {
            series.Record(index, index == 100);
        }

        var metric = series.Snapshot("bounded");
        Require(metric.TotalCount == 100, "Gesamtzähler ist falsch.");
        Require(metric.SampledCount == 5, "Metrikspeicher ist nicht begrenzt.");
        Require(metric.ErrorCount == 1, "Fehlerzähler ist falsch.");
        Require(metric.P99Milliseconds == 100, "Ringpuffer-Quantil ist falsch.");
        return Task.CompletedTask;
    }

    private static Task TestMultiNodeOptionsAsync()
    {
        var options = LoadTestOptions.Parse([
            "--signalr",
            "--base-url", "http://node-a:5000",
            "--base-url", "http://node-b:5000",
            "--rooms", "2",
            "--participants", "3"
        ]);
        Require(options.BaseUrls.Count == 2, "Mehrere Base-URLs wurden nicht gelesen.");
        Require(options.NodeFor(0, 0) == 0 && options.NodeFor(0, 1) == 1, "Round-Robin-Knotenzuordnung ist falsch.");
        return Task.CompletedTask;
    }

    private static Task TestNormalizedFanoutKeyAsync()
    {
        Require(
            SignalRLoadRunner.CountExpectedCorrectCharacters("In ") == 2,
            "Ein abschließendes Leerzeichen darf keinen nicht sendbaren Fan-out-Schlüssel erzeugen.");
        Require(
            SignalRLoadRunner.CountExpectedCorrectCharacters("Grüße") == 5,
            "Fan-out-Schlüssel müssen Grapheme statt UTF-16-Codeeinheiten zählen.");
        return Task.CompletedTask;
    }

    private static async Task TestFanoutAsync(CancellationToken cancellationToken)
    {
        var metrics = new MetricRegistry(100);
        var tracker = new FanoutTracker(metrics);
        var room = Guid.CreateVersion7();
        var participant = Guid.CreateVersion7();
        var expectation = tracker.Register(room, participant, 7, [0, 1, 2]);
        tracker.Observe(0, new LiveProgressEnvelope(room, participant, 7));
        tracker.Observe(1, new LiveProgressEnvelope(room, participant, 7));
        tracker.Observe(2, new LiveProgressEnvelope(room, participant, 7));
        var missing = await tracker.WaitAsync(expectation, TimeSpan.FromSeconds(1), cancellationToken);
        Require(missing == 0, "Vollständiger Fan-out wurde als fehlend markiert.");
        Require(tracker.Snapshot() == new FanoutSummary(3, 3, 0), "Fan-out-Zähler sind falsch.");
    }

    private static async Task TestMissingFanoutAsync(CancellationToken cancellationToken)
    {
        var metrics = new MetricRegistry(100);
        var tracker = new FanoutTracker(metrics);
        var room = Guid.CreateVersion7();
        var participant = Guid.CreateVersion7();
        var expectation = tracker.Register(room, participant, 9, [0, 1]);
        tracker.Observe(0, new LiveProgressEnvelope(room, participant, 9));
        var missing = await tracker.WaitAsync(expectation, TimeSpan.FromMilliseconds(5), cancellationToken);
        Require(missing == 1, "Fehlende Empfängerzustellung wurde nicht erkannt.");
        Require(tracker.Snapshot() == new FanoutSummary(2, 1, 1), "Fehlender Fan-out wurde falsch gezählt.");
    }

    private static Task TestSloAsync()
    {
        var metrics = new[]
        {
            new OperationMetric("progress.submit", 10, 10, 1, 10, 10, 50, 100, 100),
            new OperationMetric("progress.fanout-recipient", 10, 10, 1, 10, 10, 50, 100, 100)
        };
        var report = SloEvaluator.Evaluate(
            new SloOptions(40, 90, 40, 0, 0),
            metrics,
            new FanoutSummary(10, 9, 1),
            [new RoomResult(0, Guid.Empty, [], 0, 0, 0, 1, null)]);
        Require(!report.Passed, "SLO-Verletzungen ergeben fälschlich Erfolg.");
        Require(report.Checks.Any(check => check.Name == "progress.missing-broadcasts" && !check.Passed), "Broadcast-SLO fehlt.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
