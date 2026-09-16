using System.Text.Json;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed class LiveRoomCompletionOutbox(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider)
{
    internal const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal LocalCompletionOutboxAdmission Admit(
        CompletedRoomRecord record,
        int capacity,
        bool acceptForProcessing,
        DateTimeOffset now)
    {
        var payload = JsonSerializer.Serialize(record, SerializerOptions);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        using var transaction = db.Database.BeginTransaction();

        var summaries = db.LiveRoomSummaries
            .AsNoTracking()
            .Where(item => item.Id == record.Id || item.IdempotencyKey == record.IdempotencyKey)
            .Select(item => new { item.Id, item.IdempotencyKey })
            .ToArray();
        if (summaries.Length > 0)
        {
            if (summaries.Any(item => item.Id != record.Id ||
                    !StringComparer.Ordinal.Equals(item.IdempotencyKey, record.IdempotencyKey)))
            {
                throw new InvalidOperationException(
                    "Raum-ID oder Idempotenzschlüssel ist bereits einem anderen Arena-Ergebnis zugeordnet.");
            }

            var stale = db.Set<LiveRoomCompletionOutboxEntry>()
                .SingleOrDefault(item => item.RoomId == record.Id || item.IdempotencyKey == record.IdempotencyKey);
            if (stale is not null)
            {
                db.Remove(stale);
                db.SaveChanges();
            }

            transaction.Commit();
            return new LocalCompletionOutboxAdmission(CompletionState.Persisted, Created: false, Redriven: false);
        }

        var existing = db.Set<LiveRoomCompletionOutboxEntry>()
            .SingleOrDefault(item => item.RoomId == record.Id || item.IdempotencyKey == record.IdempotencyKey);
        if (existing is not null)
        {
            EnsureSameRecord(existing, record, payload);
            var redriven = existing.State == LiveRoomCompletionOutboxState.Failed && acceptForProcessing;
            if (redriven)
            {
                existing.State = LiveRoomCompletionOutboxState.Pending;
                existing.AttemptCount = 0;
                existing.NextAttemptAtUnixMilliseconds = now.ToUnixTimeMilliseconds();
                existing.UpdatedAtUnixMilliseconds = now.ToUnixTimeMilliseconds();
                existing.LastError = null;
                db.SaveChanges();
            }

            transaction.Commit();
            return new LocalCompletionOutboxAdmission(
                existing.State == LiveRoomCompletionOutboxState.Pending
                    ? CompletionState.Pending
                    : CompletionState.Failed,
                Created: false,
                redriven);
        }

        var state = acceptForProcessing && db.Set<LiveRoomCompletionOutboxEntry>().Count() < capacity
            ? LiveRoomCompletionOutboxState.Pending
            : LiveRoomCompletionOutboxState.Failed;
        db.Add(new LiveRoomCompletionOutboxEntry
        {
            RoomId = record.Id,
            IdempotencyKey = record.IdempotencyKey,
            SchemaVersion = CurrentSchemaVersion,
            Payload = payload,
            State = state,
            AttemptCount = 0,
            EnqueuedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            NextAttemptAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            UpdatedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(),
            LastError = state == LiveRoomCompletionOutboxState.Failed
                ? acceptForProcessing
                    ? "capacity"
                    : "shutdown"
                : null
        });
        foreach (var profileId in record.Participants
                     .Select(item => item.UserProfileId)
                     .Distinct()
                     .Order())
        {
            db.Add(new LiveRoomCompletionOutboxProfile
            {
                RoomId = record.Id,
                UserProfileId = profileId
            });
        }

        db.SaveChanges();
        transaction.Commit();
        return new LocalCompletionOutboxAdmission(
            state == LiveRoomCompletionOutboxState.Pending ? CompletionState.Pending : CompletionState.Failed,
            Created: true,
            Redriven: false);
    }

    internal async Task<LocalCompletionOutboxWorkItem?> ReadNextDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var entry = await db.Set<LiveRoomCompletionOutboxEntry>()
            .AsNoTracking()
            .Where(item => item.State == LiveRoomCompletionOutboxState.Pending &&
                item.NextAttemptAtUnixMilliseconds <= now.ToUnixTimeMilliseconds())
            .OrderBy(item => item.NextAttemptAtUnixMilliseconds)
            .ThenBy(item => item.EnqueuedAtUnixMilliseconds)
            .ThenBy(item => item.RoomId)
            .FirstOrDefaultAsync(cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var profileIds = await db.Set<LiveRoomCompletionOutboxProfile>()
            .AsNoTracking()
            .Where(item => item.RoomId == entry.RoomId)
            .OrderBy(item => item.UserProfileId)
            .Select(item => item.UserProfileId)
            .ToArrayAsync(cancellationToken);
        return new LocalCompletionOutboxWorkItem(
            entry.RoomId,
            entry.IdempotencyKey,
            entry.SchemaVersion,
            entry.Payload,
            profileIds,
            entry.AttemptCount,
            DateTimeOffset.FromUnixTimeMilliseconds(entry.EnqueuedAtUnixMilliseconds),
            DateTimeOffset.FromUnixTimeMilliseconds(entry.NextAttemptAtUnixMilliseconds));
    }

    internal async Task RescheduleAsync(
        LocalCompletionOutboxWorkItem item,
        int attemptCount,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(
            item,
            LiveRoomCompletionOutboxState.Pending,
            attemptCount,
            nextAttemptAt,
            error,
            cancellationToken);
    }

    internal async Task FailAsync(
        LocalCompletionOutboxWorkItem item,
        int attemptCount,
        string error,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(
            item,
            LiveRoomCompletionOutboxState.Failed,
            attemptCount,
            DateTimeOffset.MaxValue,
            error,
            cancellationToken);
    }

    internal async Task CompleteAsync(
        LocalCompletionOutboxWorkItem item,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var entry = await db.Set<LiveRoomCompletionOutboxEntry>()
            .SingleOrDefaultAsync(candidate => candidate.RoomId == item.RoomId, cancellationToken);
        if (entry is null)
        {
            return;
        }

        if (!StringComparer.Ordinal.Equals(entry.IdempotencyKey, item.IdempotencyKey))
        {
            throw new InvalidOperationException("Der lokale Completion-Outbox-Auftrag wechselte unerwartet seinen Idempotenzschlüssel.");
        }

        db.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal LocalCompletionOutboxStatus GetStatus(Guid roomId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var state = db.Set<LiveRoomCompletionOutboxEntry>()
            .AsNoTracking()
            .Where(item => item.RoomId == roomId)
            .Select(item => (LiveRoomCompletionOutboxState?)item.State)
            .SingleOrDefault();
        if (state is not null)
        {
            return new LocalCompletionOutboxStatus(
                state == LiveRoomCompletionOutboxState.Pending
                    ? CompletionState.Pending
                    : CompletionState.Failed);
        }

        return new LocalCompletionOutboxStatus(
            db.LiveRoomSummaries.AsNoTracking().Any(item => item.Id == roomId)
                ? CompletionState.Persisted
                : CompletionState.AbortedUnconfirmed);
    }

    internal LocalCompletionOutboxSnapshot GetSnapshot(DateTimeOffset now)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var pending = db.Set<LiveRoomCompletionOutboxEntry>()
            .AsNoTracking()
            .Where(item => item.State == LiveRoomCompletionOutboxState.Pending);
        var oldest = pending
            .OrderBy(item => item.EnqueuedAtUnixMilliseconds)
            .Select(item => (long?)item.EnqueuedAtUnixMilliseconds)
            .FirstOrDefault();
        return new LocalCompletionOutboxSnapshot(
            pending.Count(),
            db.Set<LiveRoomCompletionOutboxEntry>()
                .AsNoTracking()
                .Count(item => item.State == LiveRoomCompletionOutboxState.Failed),
            oldest is null
                ? TimeSpan.Zero
                : now - DateTimeOffset.FromUnixTimeMilliseconds(oldest.Value));
    }

    internal LocalCompletionOutboxProfileSnapshot GetProfileSnapshot(Guid profileId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var states = (
                from profile in db.Set<LiveRoomCompletionOutboxProfile>().AsNoTracking()
                join entry in db.Set<LiveRoomCompletionOutboxEntry>().AsNoTracking()
                    on profile.RoomId equals entry.RoomId
                where profile.UserProfileId == profileId
                select entry.State)
            .ToArray();
        return new LocalCompletionOutboxProfileSnapshot(
            states.Count(item => item == LiveRoomCompletionOutboxState.Pending),
            states.Count(item => item == LiveRoomCompletionOutboxState.Failed));
    }

    internal DateTimeOffset? GetNextAttemptAt()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        return db.Set<LiveRoomCompletionOutboxEntry>()
            .AsNoTracking()
            .Where(item => item.State == LiveRoomCompletionOutboxState.Pending)
            .OrderBy(item => item.NextAttemptAtUnixMilliseconds)
            .Select(item => (long?)item.NextAttemptAtUnixMilliseconds)
            .FirstOrDefault() is { } milliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
    }

    internal static CompletedRoomRecord Deserialize(LocalCompletionOutboxWorkItem item)
    {
        if (item.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Completion-Outbox-Schema {item.SchemaVersion} wird nicht unterstützt.");
        }

        var record = JsonSerializer.Deserialize<CompletedRoomRecord>(item.Payload, SerializerOptions)
            ?? throw new InvalidOperationException("Der lokale Completion-Outbox-Auftrag ist leer.");
        var recordProfiles = record.Participants
            .Select(candidate => candidate.UserProfileId)
            .Distinct()
            .Order()
            .ToArray();
        if (record.Id != item.RoomId ||
            !StringComparer.Ordinal.Equals(record.IdempotencyKey, item.IdempotencyKey) ||
            !recordProfiles.SequenceEqual(item.ProfileIds))
        {
            throw new InvalidOperationException("Der lokale Completion-Outbox-Auftrag ist inkonsistent.");
        }

        return record;
    }

    private async Task UpdateAsync(
        LocalCompletionOutboxWorkItem item,
        LiveRoomCompletionOutboxState state,
        int attemptCount,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var entry = await db.Set<LiveRoomCompletionOutboxEntry>()
            .SingleOrDefaultAsync(candidate => candidate.RoomId == item.RoomId, cancellationToken);
        if (entry is null)
        {
            return;
        }

        if (!StringComparer.Ordinal.Equals(entry.IdempotencyKey, item.IdempotencyKey))
        {
            throw new InvalidOperationException("Der lokale Completion-Outbox-Auftrag wechselte unerwartet seinen Idempotenzschlüssel.");
        }

        entry.State = state;
        entry.AttemptCount = attemptCount;
        entry.NextAttemptAtUnixMilliseconds = nextAttemptAt.ToUnixTimeMilliseconds();
        entry.UpdatedAtUnixMilliseconds = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        entry.LastError = error.Length <= 256 ? error : error[..256];
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureSameRecord(
        LiveRoomCompletionOutboxEntry entry,
        CompletedRoomRecord record,
        string payload)
    {
        if (entry.RoomId != record.Id ||
            !StringComparer.Ordinal.Equals(entry.IdempotencyKey, record.IdempotencyKey) ||
            entry.SchemaVersion != CurrentSchemaVersion ||
            !StringComparer.Ordinal.Equals(entry.Payload, payload))
        {
            throw new InvalidOperationException(
                "Für diesen Arena-Raum existiert bereits ein abweichender Persistenzauftrag.");
        }
    }
}

internal sealed record LocalCompletionOutboxAdmission(
    CompletionState State,
    bool Created,
    bool Redriven);

internal sealed record LocalCompletionOutboxStatus(CompletionState State);

internal sealed record LocalCompletionOutboxSnapshot(
    int PendingCount,
    int FailedCount,
    TimeSpan OldestPendingAge);

internal sealed record LocalCompletionOutboxProfileSnapshot(
    int PendingCount,
    int FailedCount);

internal sealed record LocalCompletionOutboxWorkItem(
    Guid RoomId,
    string IdempotencyKey,
    int SchemaVersion,
    string Payload,
    IReadOnlyList<Guid> ProfileIds,
    int AttemptCount,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset NextAttemptAt);
