using System.Diagnostics;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.LoadTesting;

internal static class InMemoryLoadRunner
{
    public static void Run(string[] args)
    {
        var participantCounts = args.Length > 0
            ? args.Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : [2, 10, 25, 50, 100];

        Console.WriteLine("KeyWars In-Memory-Lasttest (kein Netzwerkpfad)");
        Console.WriteLine($"Zeitpunkt: {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"CPU: {Environment.ProcessorCount}; Runtime: {Environment.Version}");

        foreach (var count in participantCounts)
        {
            if (count < 2)
            {
                throw new ArgumentException("Teilnehmendenzahlen müssen mindestens 2 sein.");
            }

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
            var snapshot = manager.CreateRoom(new CreateLiveRoomRequest(
                creator,
                "Person 0",
                $"Lasttest {count}",
                targetText,
                LiveRoomMode.Classic,
                LiveRoomVisibility.InternalOpen,
                1,
                count));
            var participants = Enumerable.Range(1, count - 1)
                .Select(index => (Id: Guid.CreateVersion7(), Name: $"Person {index}"))
                .ToArray();
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
            var timings = new BoundedMetricSeries(20_000);
            var stopwatch = Stopwatch.StartNew();
            Parallel.ForEach(manager.Snapshot(snapshot.RoomId).Participants, participant =>
            {
                for (var sequence = 1; sequence <= 30; sequence++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var length = Math.Min(targetText.Length, sequence * 3);
                    manager.SubmitProgress(snapshot.RoomId, participant.ProfileId, sequence, targetText[..length]);
                    timings.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, false);
                    Thread.Sleep(1);
                }

                manager.Finish(snapshot.RoomId, participant.ProfileId, targetText, 0, 0);
            });
            stopwatch.Stop();

            var metric = timings.Snapshot("progress.in-memory");
            var final = manager.Snapshot(snapshot.RoomId);
            Console.WriteLine(
                $"Teilnehmende={count}; Fertig={final.Participants.Count(item => item.Status == ParticipantStatus.Finished)}; " +
                $"DauerMs={stopwatch.ElapsedMilliseconds}; ProgressP95Ms={metric.P95Milliseconds:0.000}; " +
                $"Platzierungen={final.Participants.Count(item => item.Placement is not null)}");
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow += duration;
}
