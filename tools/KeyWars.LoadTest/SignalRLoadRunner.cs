using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace KeyWars.LoadTesting;

internal sealed class SignalRLoadRunner(LoadTestOptions options)
{
    private readonly MetricRegistry metrics = new(options.MetricCapacity);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OverallTimeout);
        var token = timeout.Token;
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var resources = ResourceProbe.Start(options.TargetProcessId);
        var fanout = new FanoutTracker(metrics);
        var ramp = new RampCoordinator(options.Rooms * options.Participants, options.RampDuration);

        Console.WriteLine(
            $"SignalR-{options.Scenario}: {options.Rooms} Räume x {options.Participants} Teilnehmende, " +
            $"{options.Steps} Schritte, {options.BaseUrls.Count} Knoten");

        var roomTasks = Enumerable.Range(0, options.Rooms)
            .Select(roomIndex => RunRoomSafelyAsync(roomIndex, fanout, ramp, token))
            .ToArray();
        RoomResult[] rooms;
        try
        {
            rooms = await Task.WhenAll(roomTasks);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Der Lasttest überschritt das Gesamtlimit von {options.OverallTimeout}.");
        }

        var health = await ReadHealthAsync(token);
        stopwatch.Stop();
        var operationMetrics = metrics.Snapshot();
        var fanoutSummary = fanout.Snapshot();
        var slo = SloEvaluator.Evaluate(options.Slo, operationMetrics, fanoutSummary, rooms);
        var report = new LoadTestReport(
            startedAt,
            DateTimeOffset.UtcNow,
            options.Scenario.ToString().ToLowerInvariant(),
            options.BaseUrls.Select(uri => uri.ToString()).ToArray(),
            options.ForcedNode,
            CurrentCommit(),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            options.Rooms,
            options.Participants,
            options.Steps,
            options.RampDuration.TotalSeconds,
            Math.Round(1000d / options.StepDelay.TotalMilliseconds, 3),
            options.TypingJitter.TotalMilliseconds,
            options.ReconnectPercent,
            stopwatch.ElapsedMilliseconds,
            options.MetricCapacity,
            rooms,
            operationMetrics,
            fanoutSummary,
            resources.Complete(stopwatch.Elapsed),
            health,
            slo);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        if (!string.IsNullOrWhiteSpace(options.JsonPath))
        {
            var path = Path.GetFullPath(options.JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json + Environment.NewLine, cancellationToken);
            Console.WriteLine($"Bericht: {path}");
        }

        PrintSummary(report);
        return slo.Passed ? 0 : 2;
    }

    private async Task<RoomResult> RunRoomSafelyAsync(
        int roomIndex,
        FanoutTracker fanout,
        RampCoordinator ramp,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunRoomAsync(roomIndex, fanout, ramp, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RoomResult(roomIndex, Guid.Empty, [], 0, 0, 0, 0, ShortError(exception));
        }
    }

    private async Task<RoomResult> RunRoomAsync(
        int roomIndex,
        FanoutTracker fanout,
        RampCoordinator ramp,
        CancellationToken cancellationToken)
    {
        var clients = new List<LoadClient>(options.Participants);
        try
        {
            for (var participantIndex = 0; participantIndex < options.Participants; participantIndex++)
            {
                await ramp.WaitAsync(cancellationToken);
                var nodeIndex = options.NodeFor(roomIndex, participantIndex);
                var client = await LoadClient.LoginAsync(
                    options.BaseUrls[nodeIndex],
                    nodeIndex,
                    roomIndex,
                    participantIndex,
                    metrics,
                    options.OperationTimeout,
                    cancellationToken);
                clients.Add(client);
                if (options.LoginDelay > TimeSpan.Zero)
                {
                    await Task.Delay(options.LoginDelay, cancellationToken);
                }
            }

            var roomId = await metrics.MeasureAsync(
                "room.create.http",
                token => clients[0].CreateRoomAsync($"SignalR Lasttest {roomIndex}", options.Participants, token),
                options.OperationTimeout,
                cancellationToken);

            foreach (var client in clients)
            {
                client.Connection = BuildConnection(client.BaseUrl, client.Cookies);
                var receiverIndex = client.ParticipantIndex;
                client.Connection.On<LiveProgressBatch>("progressChanged", batch =>
                {
                    foreach (var delta in batch.Deltas)
                    {
                        fanout.Observe(receiverIndex, new LiveProgressEnvelope(delta.RoomId, delta.ParticipantId, delta.CorrectCharacters));
                    }
                });
                await metrics.MeasureAsync(
                    "signalr.connect",
                    token => client.Connection.StartAsync(token),
                    options.OperationTimeout,
                    cancellationToken);
                var snapshot = await JoinRoomAsync(client, roomId, "room.join", cancellationToken);
                client.ProfileId = snapshot.Participants
                    .Single(item => item.DisplayName == client.DisplayName)
                    .ProfileId;
            }

            await Task.WhenAll(clients.Select(client => metrics.MeasureAsync(
                "room.ready",
                token => InvokeAsync<LiveRoomSnapshot>(client, "SetReady", [roomId, true], token),
                options.OperationTimeout,
                cancellationToken)));

            await metrics.MeasureAsync(
                "room.start",
                token => InvokeAsync<LiveRoomSnapshot>(clients[0], "Start", [roomId], token),
                options.OperationTimeout,
                cancellationToken);
            var running = await WaitForRunningAsync(clients[0], roomId, cancellationToken);
            var target = running.TargetText;
            if (target.Length < 2)
            {
                throw new InvalidOperationException("Der Trainingstext ist für einen Lasttest zu kurz.");
            }

            var targetGraphemes = TypingEngine.SplitGraphemes(target);
            var expectedIndexes = clients.Select(client => client.ParticipantIndex).ToArray();
            var missingBroadcasts = 0L;
            var reconnects = 0;
            for (var step = 1; step <= options.Steps; step++)
            {
                var length = 1 + ((step - 1) % (targetGraphemes.Count - 1));
                var typedText = string.Concat(targetGraphemes.Take(length));
                var expectedCorrectCharacters = CountExpectedCorrectCharacters(typedText);
                var expectations = new FanoutExpectation[clients.Count];
                for (var index = 0; index < clients.Count; index++)
                {
                    expectations[index] = fanout.Register(
                        roomId,
                        clients[index].ProfileId,
                        expectedCorrectCharacters,
                        expectedIndexes,
                        clients[index].ParticipantIndex);
                }

                await Task.WhenAll(clients.Select((client, index) => metrics.MeasureAsync(
                    "progress.submit",
                    token => InvokeAsync(client, "SubmitProgress", [roomId, step, typedText], token),
                    options.OperationTimeout,
                    cancellationToken)));
                var missing = await Task.WhenAll(expectations.Select(expectation =>
                    fanout.WaitAsync(expectation, options.OperationTimeout, cancellationToken)));
                missingBroadcasts += missing.Sum(value => (long)value);

                if (step == Math.Max(1, options.Steps / 2) && options.ReconnectPercent > 0)
                {
                    reconnects += await ReconnectClientsAsync(clients, roomId, roomIndex, cancellationToken);
                }

                if (step < options.Steps)
                {
                    await Task.Delay(options.DelayForStep(roomIndex, step), cancellationToken);
                }
            }

            await Task.WhenAll(clients.Select(client => metrics.MeasureAsync(
                "room.finish",
                token => InvokeAsync<LiveRoomSnapshot>(client, "Finish", [roomId, target, 0, 0], token),
                options.OperationTimeout,
                cancellationToken)));
            var final = await JoinRoomAsync(clients[0], roomId, "room.final-snapshot", cancellationToken);
            return new RoomResult(
                roomIndex,
                roomId,
                clients.Select(client => client.NodeIndex).ToArray(),
                final.Participants.Count(item => item.Status == ParticipantStatus.Finished),
                final.Participants.Count(item => item.Placement is not null),
                reconnects,
                missingBroadcasts,
                null);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    private async Task<int> ReconnectClientsAsync(
        IReadOnlyList<LoadClient> clients,
        Guid roomId,
        int roomIndex,
        CancellationToken cancellationToken)
    {
        var candidates = clients.Skip(1)
            .Where(client => Math.Abs(HashCode.Combine(options.Seed, roomIndex, client.ParticipantIndex)) % 100 < options.ReconnectPercent)
            .ToArray();
        if (candidates.Length == 0 && clients.Count > 1 && options.ReconnectPercent > 0)
        {
            candidates = [clients[^1]];
        }

        await Task.WhenAll(candidates.Select(async client =>
        {
            await metrics.MeasureAsync(
                "reconnect.stop",
                token => client.Connection!.StopAsync(token),
                options.OperationTimeout,
                cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(50 + (client.ParticipantIndex % 5 * 25)), cancellationToken);
            await metrics.MeasureAsync(
                "reconnect.start",
                token => client.Connection!.StartAsync(token),
                options.OperationTimeout,
                cancellationToken);
            await JoinRoomAsync(client, roomId, "reconnect.join", cancellationToken);
        }));
        return candidates.Length;
    }

    private async Task<LiveRoomSnapshot> WaitForRunningAsync(
        LoadClient client,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + options.OperationTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await JoinRoomAsync(client, roomId, "room.poll-running", cancellationToken);
            if (snapshot.Phase == LiveRoomPhase.Running)
            {
                return snapshot;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("Der Live-Raum erreichte die Running-Phase nicht rechtzeitig.");
    }

    private Task<LiveRoomSnapshot> JoinRoomAsync(
        LoadClient client,
        Guid roomId,
        string operation,
        CancellationToken cancellationToken) => metrics.MeasureAsync(
            operation,
            async token => await InvokeAsync<LiveRoomSnapshot?>(client, "JoinRoom", [roomId], token)
                ?? throw new InvalidOperationException($"Raum {roomId:N} ist auf Knoten {client.NodeIndex} nicht verfügbar."),
            options.OperationTimeout,
            cancellationToken);

    private static Task InvokeAsync(LoadClient client, string method, object?[] arguments, CancellationToken cancellationToken) =>
        client.Connection!.InvokeCoreAsync(method, arguments, cancellationToken);

    private static Task<T> InvokeAsync<T>(LoadClient client, string method, object?[] arguments, CancellationToken cancellationToken) =>
        client.Connection!.InvokeCoreAsync<T>(method, arguments, cancellationToken);

    internal static int CountExpectedCorrectCharacters(string typedText) =>
        TypingEngine.SplitGraphemes(TypingEngine.NormalizeText(typedText)).Count;

    private static HubConnection BuildConnection(Uri baseUrl, CookieContainer cookies) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(baseUrl, "/hubs/arena"), connection =>
            {
                connection.Cookies = cookies;
                connection.Transports = HttpTransportType.WebSockets;
                connection.SkipNegotiation = true;
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)])
            .AddJsonProtocol(protocol => protocol.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private async Task<IReadOnlyList<NodeHealth>> ReadHealthAsync(CancellationToken cancellationToken)
    {
        var results = new List<NodeHealth>(options.BaseUrls.Count);
        for (var index = 0; index < options.BaseUrls.Count; index++)
        {
            var progress = await TryReadHealthAsync(index, options.BaseUrls[index], "/health/arena-progress", "health.arena-progress", cancellationToken);
            var persistence = await TryReadHealthAsync(index, options.BaseUrls[index], "/health/arena-persistence", "health.arena-persistence", cancellationToken);
            results.Add(new NodeHealth(index, options.BaseUrls[index].ToString(), progress.Value, persistence.Value, progress.Error ?? persistence.Error));
        }

        return results;
    }

    private async Task<(JsonElement? Value, string? Error)> TryReadHealthAsync(
        int nodeIndex,
        Uri baseUrl,
        string path,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = baseUrl, Timeout = Timeout.InfiniteTimeSpan };
            var json = await metrics.MeasureAsync(
                $"{operation}.node-{nodeIndex}",
                token => client.GetStringAsync(path, token),
                options.OperationTimeout,
                cancellationToken);
            using var document = JsonDocument.Parse(json);
            return (document.RootElement.Clone(), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, ShortError(exception));
        }
    }

    private static string CurrentCommit()
    {
        try
        {
            var start = new ProcessStartInfo("git", "rev-parse --short=12 HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(2_000) || process.ExitCode != 0)
            {
                return "unknown";
            }

            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ShortError(Exception exception)
    {
        var message = Regex.Replace(exception.GetBaseException().Message, "\\s+", " ").Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    private static void PrintSummary(LoadTestReport report)
    {
        Console.WriteLine(
            $"Dauer: {report.DurationMilliseconds} ms; Räume: {report.RoomResults.Count - report.RoomResults.Count(room => room.Error is not null)}/{report.RoomResults.Count}; " +
            $"Fan-out: {report.Fanout.ObservedDeliveries}/{report.Fanout.ExpectedDeliveries}; fehlend: {report.Fanout.MissingDeliveries}");
        foreach (var metric in report.Operations)
        {
            Console.WriteLine(
                $"{metric.Operation,-34} n={metric.TotalCount,7} err={metric.ErrorCount,5} " +
                $"p50={metric.P50Milliseconds,8:0.000} p95={metric.P95Milliseconds,8:0.000} p99={metric.P99Milliseconds,8:0.000} ms");
        }

        foreach (var room in report.RoomResults.Where(room => room.Error is not null))
        {
            Console.Error.WriteLine($"Raum {room.RoomIndex}: {room.Error}");
        }

        Console.WriteLine(report.Slo.Passed ? "SLO: BESTANDEN" : "SLO: NICHT BESTANDEN (Exitcode 2)");
        foreach (var check in report.Slo.Checks.Where(check => !check.Passed))
        {
            Console.Error.WriteLine($"  {check.Name}: {check.Actual:0.###} > {check.Limit:0.###} {check.Unit}");
        }
    }
}

internal sealed class LoadClient : IAsyncDisposable
{
    private LoadClient(
        Uri baseUrl,
        int nodeIndex,
        int participantIndex,
        string username,
        CookieContainer cookies,
        HttpClient http)
    {
        BaseUrl = baseUrl;
        NodeIndex = nodeIndex;
        ParticipantIndex = participantIndex;
        Username = username;
        DisplayName = LoadToolHtml.ToDisplayName(username);
        Cookies = cookies;
        Http = http;
    }

    public Uri BaseUrl { get; }
    public int NodeIndex { get; }
    public int ParticipantIndex { get; }
    public string Username { get; }
    public string DisplayName { get; }
    public CookieContainer Cookies { get; }
    public HttpClient Http { get; }
    public HubConnection? Connection { get; set; }
    public Guid ProfileId { get; set; }

    public static async Task<LoadClient> LoginAsync(
        Uri baseUrl,
        int nodeIndex,
        int roomIndex,
        int participantIndex,
        MetricRegistry metrics,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var username = $"load.room{roomIndex}.user{participantIndex}";
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
        var http = new HttpClient(handler) { BaseAddress = baseUrl, Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            await metrics.MeasureAsync("login.http", async token =>
            {
                var loginPage = await http.GetStringAsync("/anmelden", token);
                var antiForgeryToken = LoadToolHtml.ExtractAntiForgeryToken(loginPage);
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Input.Username"] = username,
                    ["Input.Password"] = $"load-test-{roomIndex}-{participantIndex}",
                    ["__RequestVerificationToken"] = antiForgeryToken
                });
                using var response = await http.PostAsync("/anmelden", form, token);
                if (response.StatusCode != HttpStatusCode.Redirect)
                {
                    var body = await response.Content.ReadAsStringAsync(token);
                    throw new InvalidOperationException($"Login für {username} fehlgeschlagen: {(int)response.StatusCode} {LoadToolHtml.Brief(body)}");
                }
            }, timeout, cancellationToken);
            return new LoadClient(baseUrl, nodeIndex, participantIndex, username, cookies, http);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public async Task<Guid> CreateRoomAsync(string title, int participants, CancellationToken cancellationToken)
    {
        var page = await Http.GetStringAsync("/arena/neu", cancellationToken);
        var token = LoadToolHtml.ExtractAntiForgeryToken(page);
        var textId = LoadToolHtml.ExtractFirstTrainingTextId(page);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Title"] = title,
            ["Input.TrainingTextId"] = textId,
            ["Input.Visibility"] = LiveRoomVisibility.InternalOpen.ToString(),
            ["Input.MaxParticipants"] = participants.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.Mode"] = LiveRoomMode.Classic.ToString(),
            ["Input.RoundCount"] = "1",
            ["__RequestVerificationToken"] = token
        });
        using var response = await Http.PostAsync("/arena/neu", form, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location is null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Raumerstellung fehlgeschlagen: {(int)response.StatusCode} {LoadToolHtml.Brief(body)}");
        }

        return Guid.Parse(response.Headers.Location.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).Last());
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            try
            {
                await Connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Cleanup darf einen bereits abgeschlossenen Messlauf nicht unbegrenzt blockieren.
            }
        }

        Http.Dispose();
    }
}

internal static class LoadToolHtml
{
    public static string ExtractAntiForgeryToken(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(html, "value=\"(?<value>[^\"]+)\"[^>]*name=\"__RequestVerificationToken\"", RegexOptions.IgnoreCase);
        }

        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value)
            : throw new InvalidOperationException("Anti-Forgery-Token wurde nicht gefunden.");
    }

    public static string ExtractFirstTrainingTextId(string html)
    {
        var match = Regex.Match(html, "<option\\s+value=\"(?<value>[0-9a-fA-F-]{36})\"", RegexOptions.IgnoreCase);
        return match.Success
            ? match.Groups["value"].Value
            : throw new InvalidOperationException("Kein Trainingstext für den Live-Raum gefunden.");
    }

    public static string ToDisplayName(string username)
    {
        var parts = username.Replace('.', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? "Load"
            : string.Join(' ', parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    public static string Brief(string value)
    {
        var normalized = Regex.Replace(value, "\\s+", " ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}

internal sealed class RampCoordinator(int totalClients, TimeSpan duration)
{
    private readonly long started = Stopwatch.GetTimestamp();
    private int ordinal = -1;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || totalClients <= 1)
        {
            return;
        }

        var current = Interlocked.Increment(ref ordinal);
        var target = TimeSpan.FromTicks((long)(duration.Ticks * (current / (double)(totalClients - 1))));
        var remaining = target - Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }
}

internal sealed record LoadTestReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Scenario,
    IReadOnlyList<string> BaseUrls,
    int? ForcedNode,
    string Commit,
    string Hostname,
    int ProcessorCount,
    string RuntimeVersion,
    int Rooms,
    int ParticipantsPerRoom,
    int Steps,
    double RampSeconds,
    double TypingCharactersPerSecond,
    double TypingJitterMilliseconds,
    int ReconnectPercent,
    long DurationMilliseconds,
    int MetricCapacityPerOperation,
    IReadOnlyList<RoomResult> RoomResults,
    IReadOnlyList<OperationMetric> Operations,
    FanoutSummary Fanout,
    ResourceReport Resources,
    IReadOnlyList<NodeHealth> NodeHealth,
    SloReport Slo);

internal sealed record RoomResult(
    int RoomIndex,
    Guid RoomId,
    IReadOnlyList<int> NodeAssignments,
    int FinishedParticipants,
    int Placements,
    int Reconnects,
    long MissingBroadcasts,
    string? Error);

internal sealed record NodeHealth(
    int NodeIndex,
    string BaseUrl,
    JsonElement? ArenaProgress,
    JsonElement? ArenaPersistence,
    string? Error);
