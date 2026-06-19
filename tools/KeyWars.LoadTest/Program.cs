using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Any(arg => string.Equals(arg, "--signalr", StringComparison.OrdinalIgnoreCase)))
{
    await RunSignalRLoadAsync(SignalRLoadOptions.Parse(args));
    return;
}

RunInMemoryLoad(args);

static void RunInMemoryLoad(string[] args)
{
    var participantCounts = args.Length > 0
        ? args.Select(int.Parse).ToArray()
        : [2, 10, 25, 50, 100];

    Console.WriteLine("KeyWars Lasttest");
    Console.WriteLine($"Zeitpunkt: {DateTimeOffset.UtcNow:O}");
    Console.WriteLine($"CPU: {Environment.ProcessorCount}");
    Console.WriteLine($"Runtime: {Environment.Version}");

    foreach (var count in participantCounts)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = Options.Create(new LiveOptions
        {
            MaxParticipantsPerRoom = Math.Max(128, count),
            RoomCommandQueueCapacity = Math.Max(4096, count * 128),
            CountdownSeconds = 1
        });
        var manager = new LiveRoomManager(options, time, new TypingEngine(time), NullLogger<LiveRoomManager>.Instance);
        var creator = Guid.CreateVersion7();
        var targetText = TypingEngine.BuildWordTest(100);
        var snapshot = manager.CreateRoom(new CreateLiveRoomRequest(creator, "Person 0", $"Lasttest {count}", targetText, LiveRoomMode.Classic, LiveRoomVisibility.InternalOpen, 1, count));
        var participants = Enumerable.Range(1, count - 1).Select(index => (Id: Guid.CreateVersion7(), Name: $"Person {index}")).ToArray();
        foreach (var participant in participants)
        {
            manager.Join(snapshot.RoomId, participant.Id, participant.Name);
        }

        foreach (var participant in manager.Snapshot(snapshot.RoomId).Participants)
        {
            manager.SetReady(snapshot.RoomId, participant.ProfileId, true);
        }

        manager.Start(snapshot.RoomId, creator);
        time.Advance(TimeSpan.FromSeconds(1));
        var timings = new List<double>(count * 30);
        var stopwatch = Stopwatch.StartNew();
        Parallel.ForEach(manager.Snapshot(snapshot.RoomId).Participants, participant =>
        {
            for (var sequence = 1; sequence <= 30; sequence++)
            {
                var iteration = Stopwatch.StartNew();
                var length = Math.Min(targetText.Length, sequence * 3);
                manager.SubmitProgress(snapshot.RoomId, participant.ProfileId, sequence, targetText[..length]);
                iteration.Stop();
                lock (timings)
                {
                    timings.Add(iteration.Elapsed.TotalMilliseconds);
                }

                Thread.Sleep(1);
            }

            manager.Finish(snapshot.RoomId, participant.ProfileId, targetText, 0, 0);
        });
        stopwatch.Stop();

        var ordered = timings.OrderBy(value => value).ToArray();
        var p95 = ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1)];
        var final = manager.Snapshot(snapshot.RoomId);
        var finished = final.Participants.Count(item => item.Status == ParticipantStatus.Finished);

        Console.WriteLine($"Teilnehmende={count}; Fertig={finished}; DauerMs={stopwatch.ElapsedMilliseconds}; ProgressP95Ms={p95:0.000}; Platzierungen={final.Participants.Count(item => item.Placement is not null)}");
    }
}

static async Task RunSignalRLoadAsync(SignalRLoadOptions options)
{
    var stopwatch = Stopwatch.StartNew();
    var commandLatencies = new ConcurrentBag<double>();
    var broadcastLatencies = new ConcurrentBag<double>();
    var pendingBroadcasts = new ConcurrentDictionary<string, long>();
    var errors = 0;

    var roomRuns = await Task.WhenAll(Enumerable.Range(0, options.Rooms).Select(async roomIndex =>
    {
        try
        {
            return await RunSignalRRoomAsync(options, roomIndex, commandLatencies, broadcastLatencies, pendingBroadcasts);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref errors);
            return new SignalRRoomResult(roomIndex, Guid.Empty, 0, 0, ex.Message);
        }
    }));

    stopwatch.Stop();
    var progressHealth = await TryReadHealthAsync(options.BaseUrl, "/health/arena-progress");
    var persistenceHealth = await TryReadHealthAsync(options.BaseUrl, "/health/arena-persistence");
    var report = new SignalRLoadReport(
        DateTimeOffset.UtcNow,
        options.BaseUrl.ToString(),
        CurrentCommit(),
        Environment.MachineName,
        Environment.ProcessorCount,
        Environment.Version.ToString(),
        options.Rooms,
        options.Participants,
        options.Steps,
        stopwatch.ElapsedMilliseconds,
        roomRuns,
        LatencyStats.From(commandLatencies),
        LatencyStats.From(broadcastLatencies),
        errors,
        progressHealth,
        persistenceHealth);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (!string.IsNullOrWhiteSpace(options.JsonPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.JsonPath))!);
        await File.WriteAllTextAsync(options.JsonPath, json + Environment.NewLine);
    }

    Console.WriteLine(json);
    if (errors > 0 || roomRuns.Any(room => room.Error is not null))
    {
        Environment.ExitCode = 1;
    }
}

static async Task<SignalRRoomResult> RunSignalRRoomAsync(
    SignalRLoadOptions options,
    int roomIndex,
    ConcurrentBag<double> commandLatencies,
    ConcurrentBag<double> broadcastLatencies,
    ConcurrentDictionary<string, long> pendingBroadcasts)
{
    var clients = new List<LoadClient>(options.Participants);
    for (var index = 0; index < options.Participants; index++)
    {
        clients.Add(await LoadClient.LoginAsync(options.BaseUrl, $"load.room{roomIndex}.user{index}", roomIndex, index));
        if (options.LoginDelayMs > 0)
        {
            await Task.Delay(options.LoginDelayMs);
        }
    }

    try
    {
        var roomId = await clients[0].CreateRoomAsync($"SignalR Lasttest {roomIndex}", options.Participants);
        foreach (var client in clients)
        {
            client.Connection = BuildConnection(options.BaseUrl, client.Cookies);
            client.Connection.On<LiveProgressBatch>("progressChanged", batch =>
            {
                foreach (var delta in batch.Deltas)
                {
                    var key = ProgressKey(batch.RoomId, delta.ParticipantId, delta.CorrectCharacters);
                    if (pendingBroadcasts.TryRemove(key, out var startedAt))
                    {
                        broadcastLatencies.Add(ElapsedMilliseconds(startedAt));
                    }
                }
            });
            await MeasureVoidAsync(commandLatencies, () => client.Connection.StartAsync());
            var snapshot = await MeasureAsync(commandLatencies, () => client.Connection.InvokeAsync<LiveRoomSnapshot>("JoinRoom", roomId));
            client.ProfileId = snapshot.Participants.Single(item => item.DisplayName == client.DisplayName).ProfileId;
        }

        foreach (var client in clients)
        {
            await MeasureAsync(commandLatencies, () => client.Connection!.InvokeAsync<LiveRoomSnapshot>("SetReady", roomId, true));
        }

        await MeasureAsync(commandLatencies, () => clients[0].Connection!.InvokeAsync<LiveRoomSnapshot>("Start", roomId));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var running = await MeasureAsync(commandLatencies, () => clients[0].Connection!.InvokeAsync<LiveRoomSnapshot>("JoinRoom", roomId));
        var target = running.TargetText;
        var progressStep = Math.Max(1, (int)Math.Ceiling(target.Length / (double)options.Steps));

        for (var sequence = 1; sequence <= options.Steps; sequence++)
        {
            await Task.WhenAll(clients.Select(async client =>
            {
                var length = Math.Min(target.Length, sequence * progressStep);
                pendingBroadcasts[ProgressKey(roomId, client.ProfileId, length)] = Stopwatch.GetTimestamp();
                await MeasureVoidAsync(commandLatencies, () => client.Connection!.InvokeAsync("SubmitProgress", roomId, sequence, target[..length]));
            }));
            await Task.Delay(options.StepDelayMs);
        }

        await Task.WhenAll(clients.Select(client =>
            MeasureAsync(commandLatencies, () => client.Connection!.InvokeAsync<LiveRoomSnapshot>("Finish", roomId, target, 0, 0))));
        await Task.Delay(250);
        var final = await MeasureAsync(commandLatencies, () => clients[0].Connection!.InvokeAsync<LiveRoomSnapshot>("JoinRoom", roomId));
        return new SignalRRoomResult(
            roomIndex,
            roomId,
            final.Participants.Count(item => item.Status == ParticipantStatus.Finished),
            final.Participants.Count(item => item.Placement is not null),
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

static HubConnection BuildConnection(Uri baseUrl, CookieContainer cookies)
{
    return new HubConnectionBuilder()
        .WithUrl(new Uri(baseUrl, "/hubs/arena"), options => options.Cookies = cookies)
        .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)])
        .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
        .Build();
}

static async Task<T> MeasureAsync<T>(ConcurrentBag<double> latencies, Func<Task<T>> action)
{
    var started = Stopwatch.GetTimestamp();
    var result = await action();
    latencies.Add(ElapsedMilliseconds(started));
    return result;
}

static async Task MeasureVoidAsync(ConcurrentBag<double> latencies, Func<Task> action)
{
    var started = Stopwatch.GetTimestamp();
    await action();
    latencies.Add(ElapsedMilliseconds(started));
}

static double ElapsedMilliseconds(long started) =>
    Stopwatch.GetElapsedTime(started).TotalMilliseconds;

static string ProgressKey(Guid roomId, Guid participantId, int correctCharacters) =>
    $"{roomId:N}:{participantId:N}:{correctCharacters}";

static async Task<JsonElement?> TryReadHealthAsync(Uri baseUrl, string path)
{
    try
    {
        using var client = new HttpClient { BaseAddress = baseUrl };
        var json = await client.GetStringAsync(path);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
    catch
    {
        return null;
    }
}

static string CurrentCommit()
{
    try
    {
        var start = new ProcessStartInfo("git", "rev-parse --short=12 HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return "unknown";
        }

        process.WaitForExit(2000);
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "unknown";
    }
    catch
    {
        return "unknown";
    }
}

internal sealed class LoadClient : IAsyncDisposable
{
    private LoadClient(Uri baseUrl, string username, CookieContainer cookies, HttpClient http)
    {
        BaseUrl = baseUrl;
        Username = username;
        DisplayName = LoadToolHtml.ToDisplayName(username);
        Cookies = cookies;
        Http = http;
    }

    public Uri BaseUrl { get; }
    public string Username { get; }
    public string DisplayName { get; }
    public CookieContainer Cookies { get; }
    public HttpClient Http { get; }
    public HubConnection? Connection { get; set; }
    public Guid ProfileId { get; set; }

    public static async Task<LoadClient> LoginAsync(Uri baseUrl, string username, int roomIndex, int userIndex)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false
        };
        var http = new HttpClient(handler) { BaseAddress = baseUrl };
        var loginPage = await http.GetStringAsync("/anmelden");
        var token = LoadToolHtml.ExtractAntiForgeryToken(loginPage);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = username,
            ["Input.Password"] = $"load-test-{roomIndex}-{userIndex}",
            ["__RequestVerificationToken"] = token
        });
        var response = await http.PostAsync("/anmelden", form);
        if (response.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Login für {username} fehlgeschlagen: {(int)response.StatusCode} {body}");
        }

        return new LoadClient(baseUrl, username, cookies, http);
    }

    public async Task<Guid> CreateRoomAsync(string title, int participants)
    {
        var page = await Http.GetStringAsync("/arena/neu");
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
        var response = await Http.PostAsync("/arena/neu", form);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location is null)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Raumerstellung fehlgeschlagen: {(int)response.StatusCode} {body}");
        }

        var id = response.Headers.Location.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        return Guid.Parse(id);
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
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

        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : throw new InvalidOperationException("Anti-Forgery-Token wurde nicht gefunden.");
    }

    public static string ExtractFirstTrainingTextId(string html)
    {
        var match = Regex.Match(html, "<option\\s+value=\"(?<value>[0-9a-fA-F-]{36})\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : throw new InvalidOperationException("Kein Trainingstext für den Live-Raum gefunden.");
    }

    public static string ToDisplayName(string username)
    {
        var parts = username.Replace('.', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? "Load"
            : string.Join(' ', parts.Select(part => string.Create(part.Length, part, static (span, source) =>
            {
                source.AsSpan().ToLowerInvariant(span);
                span[0] = char.ToUpperInvariant(span[0]);
            })));
    }
}

internal sealed record SignalRLoadOptions(
    Uri BaseUrl,
    int Rooms,
    int Participants,
    int Steps,
    int StepDelayMs,
    int LoginDelayMs,
    string? JsonPath)
{
    public static SignalRLoadOptions Parse(string[] args)
    {
        return new SignalRLoadOptions(
            new Uri(Value(args, "--base-url", "http://127.0.0.1:5187")),
            IntValue(args, "--rooms", 1),
            IntValue(args, "--participants", 2),
            IntValue(args, "--steps", 12),
            IntValue(args, "--step-delay-ms", 25),
            IntValue(args, "--login-delay-ms", 0),
            OptionalValue(args, "--json"));
    }

    private static string Value(string[] args, string name, string fallback) =>
        OptionalValue(args, name) ?? fallback;

    private static string? OptionalValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int IntValue(string[] args, string name, int fallback) =>
        int.TryParse(OptionalValue(args, name), out var value) ? value : fallback;
}

internal sealed record SignalRLoadReport(
    DateTimeOffset GeneratedAt,
    string BaseUrl,
    string Commit,
    string Hostname,
    int ProcessorCount,
    string RuntimeVersion,
    int Rooms,
    int ParticipantsPerRoom,
    int Steps,
    long DurationMilliseconds,
    IReadOnlyList<SignalRRoomResult> RoomResults,
    LatencyStats CommandLatencyMilliseconds,
    LatencyStats BroadcastLatencyMilliseconds,
    int Errors,
    JsonElement? ProgressHealth,
    JsonElement? PersistenceHealth);

internal sealed record SignalRRoomResult(
    int RoomIndex,
    Guid RoomId,
    int FinishedParticipants,
    int Placements,
    string? Error);

internal sealed record LatencyStats(int Count, double P50, double P95, double P99, double Max)
{
    public static LatencyStats From(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return new LatencyStats(0, 0, 0, 0, 0);
        }

        return new LatencyStats(
            ordered.Length,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1]);
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1);
        return Math.Round(ordered[index], 3);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow += duration;
}
