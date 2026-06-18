using System.Diagnostics;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var participantCounts = args.Length > 0
    ? args.Select(int.Parse).ToArray()
    : [2, 10, 25, 50, 100];

Console.WriteLine("KeyWars Lasttest");
Console.WriteLine($"Zeitpunkt: {DateTimeOffset.UtcNow:O}");
Console.WriteLine($"CPU: {Environment.ProcessorCount}");
Console.WriteLine($"Runtime: {Environment.Version}");

foreach (var count in participantCounts)
{
    var options = Options.Create(new LiveOptions
    {
        MaxParticipantsPerRoom = Math.Max(128, count),
        RoomCommandQueueCapacity = Math.Max(4096, count * 128)
    });
    var manager = new LiveRoomManager(options, TimeProvider.System, new TypingEngine(TimeProvider.System), NullLogger<LiveRoomManager>.Instance);
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
    var timings = new List<double>(count * 30);
    var stopwatch = Stopwatch.StartNew();
    await Parallel.ForEachAsync(manager.Snapshot(snapshot.RoomId).Participants, async (participant, _) =>
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

            await Task.Delay(1);
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
