using System.Collections.Concurrent;

namespace KeyWars.Services;

public sealed class AttemptSessionStore
{
    private readonly ConcurrentDictionary<Guid, AttemptSession> sessions = new();

    public void Add(AttemptSession session) => sessions[session.Id] = session;

    public bool TryGet(Guid id, out AttemptSession? session) => sessions.TryGetValue(id, out session);

    public bool TryUpdate(AttemptSession current, AttemptSession updated) => sessions.TryUpdate(current.Id, updated, current);

    public bool TryRemove(Guid id, out AttemptSession? session) => sessions.TryRemove(id, out session);

    public IReadOnlyList<AttemptSession> RemoveExpired(DateTimeOffset now, TimeSpan lifetime)
    {
        var expired = new List<AttemptSession>();
        foreach (var item in sessions)
        {
            var reference = item.Value.StartedAt ?? item.Value.PreparedAt;
            if (now - reference > lifetime && sessions.TryRemove(item.Key, out var session))
            {
                expired.Add(session);
            }
        }

        return expired;
    }
}
