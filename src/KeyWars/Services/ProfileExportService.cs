using System.Text.Json;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record ProfileExportRange(
    DateOnly? From,
    DateOnly? To,
    DateTimeOffset? FromInclusive,
    DateTimeOffset? UntilExclusive,
    DateOnly? UntilDateExclusive)
{
    public bool IsFiltered => From is not null || To is not null;

    public static ProfileExportRange Create(DateOnly? from, DateOnly? to)
    {
        if (from is not null && to is not null && from > to)
        {
            throw new ProfileExportValidationException("Das Von-Datum darf nicht nach dem Bis-Datum liegen.");
        }

        if (to == DateOnly.MaxValue)
        {
            throw new ProfileExportValidationException("Das Bis-Datum liegt außerhalb des unterstützten Bereichs.");
        }

        DateTimeOffset? fromInclusive = from is null
            ? null
            : new DateTimeOffset(from.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var untilDateExclusive = to?.AddDays(1);
        DateTimeOffset? untilExclusive = untilDateExclusive is null
            ? null
            : new DateTimeOffset(untilDateExclusive.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return new ProfileExportRange(from, to, fromInclusive, untilExclusive, untilDateExclusive);
    }
}

public sealed class ProfileExportValidationException(string message) : ArgumentException(message);

public sealed record ProfileExportPreview(
    int SchemaVersion,
    DateOnly? From,
    DateOnly? To,
    long Attempts,
    long ActivityRecords,
    long OwnedContentRecords,
    long ChallengeRecords,
    long ArenaRecords,
    long TotalRecords);

public sealed record ProfileExportRangeMetadata(bool Filtered, DateOnly? From, DateOnly? To, string CalendarTimeZone);

public sealed class ProfileExportService(KeyWarsDbContext db, TimeProvider timeProvider)
{
    public const int SchemaVersion = 3;
    private const int FlushInterval = 128;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task<ProfileExportPreview> GetPreviewAsync(
        Guid profileId,
        ProfileExportRange range,
        CancellationToken cancellationToken = default)
    {
        await EnsureProfileExistsAsync(profileId, cancellationToken);
        var queries = BuildQueries(profileId, range);
        var attempts = await queries.Attempts.CountAsync(cancellationToken);
        var activityRecords = await queries.AttemptErrors.CountAsync(cancellationToken)
            + await queries.RewardLedger.CountAsync(cancellationToken)
            + await queries.Missions.CountAsync(cancellationToken)
            + await queries.Achievements.CountAsync(cancellationToken)
            + await queries.GamificationEvents.CountAsync(cancellationToken)
            + await queries.WeaknessObservations.CountAsync(cancellationToken)
            + await queries.ContentModerationAuditEntries.CountAsync(cancellationToken);
        var ownedContentRecords = await queries.OwnedTexts.CountAsync(cancellationToken)
            + await queries.OwnedCollections.CountAsync(cancellationToken)
            + await queries.OwnedCollectionItems.CountAsync(cancellationToken);
        var challengeRecords = await queries.CreatedChallenges.CountAsync(cancellationToken)
            + await queries.ChallengeRounds.CountAsync(cancellationToken)
            + await queries.ChallengeParticipations.CountAsync(cancellationToken)
            + await queries.ChallengeRoundResults.CountAsync(cancellationToken)
            + await queries.ChallengeAttemptBindings.CountAsync(cancellationToken);
        var arenaRecords = await queries.CreatedLiveRooms.CountAsync(cancellationToken)
            + await queries.LiveRoomResults.CountAsync(cancellationToken);

        return new ProfileExportPreview(
            SchemaVersion,
            range.From,
            range.To,
            attempts,
            activityRecords,
            ownedContentRecords,
            challengeRecords,
            arenaRecords,
            1 + attempts + activityRecords + ownedContentRecords + challengeRecords + arenaRecords);
    }

    public IActionResult CreateDownload(Guid profileId, ProfileExportRange range)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var fileName = $"keywars-profile-export-{generatedAt:yyyyMMdd-HHmmss}.json";
        return new ProfileExportDownloadResult(this, profileId, range, generatedAt, fileName);
    }

    public async Task WriteAsync(
        Guid profileId,
        ProfileExportRange range,
        DateTimeOffset generatedAt,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == profileId && !item.Deleted, cancellationToken);
        var queries = BuildQueries(profileId, range);

        await using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("Version", SchemaVersion);
        writer.WriteString("GeneratedAt", generatedAt);
        writer.WritePropertyName("Range");
        JsonSerializer.Serialize(
            writer,
            new ProfileExportRangeMetadata(range.IsFiltered, range.From, range.To, "UTC"),
            SerializerOptions);
        writer.WritePropertyName("Profile");
        JsonSerializer.Serialize(writer, profile, SerializerOptions);

        await WriteArrayAsync(writer, "Attempts", queries.Attempts, cancellationToken);
        await WriteArrayAsync(writer, "AttemptErrors", queries.AttemptErrors, cancellationToken);
        await WriteArrayAsync(writer, "RewardLedger", queries.RewardLedger, cancellationToken);
        await WriteArrayAsync(writer, "Missions", queries.Missions, cancellationToken);
        await WriteArrayAsync(writer, "Achievements", queries.Achievements, cancellationToken);
        await WriteArrayAsync(writer, "GamificationEvents", queries.GamificationEvents, cancellationToken);
        await WriteArrayAsync(writer, "WeaknessObservations", queries.WeaknessObservations, cancellationToken);
        await WriteArrayAsync(writer, "OwnedTexts", queries.OwnedTexts, cancellationToken);
        await WriteArrayAsync(writer, "OwnedCollections", queries.OwnedCollections, cancellationToken);
        await WriteArrayAsync(writer, "OwnedCollectionItems", queries.OwnedCollectionItems, cancellationToken);
        await WriteArrayAsync(writer, "ContentModerationAuditEntries", queries.ContentModerationAuditEntries, cancellationToken);
        await WriteArrayAsync(writer, "CreatedChallenges", queries.CreatedChallenges, cancellationToken);
        await WriteArrayAsync(writer, "ChallengeRounds", queries.ChallengeRounds, cancellationToken);
        await WriteArrayAsync(writer, "ChallengeParticipations", queries.ChallengeParticipations, cancellationToken);
        await WriteArrayAsync(writer, "ChallengeRoundResults", queries.ChallengeRoundResults, cancellationToken);
        await WriteArrayAsync(writer, "ChallengeAttemptBindings", queries.ChallengeAttemptBindings, cancellationToken);
        await WriteArrayAsync(writer, "CreatedLiveRooms", queries.CreatedLiveRooms, cancellationToken);
        await WriteArrayAsync(writer, "LiveRoomResults", queries.LiveRoomResults, cancellationToken);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    private async Task EnsureProfileExistsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (!await db.UserProfiles.AsNoTracking().AnyAsync(item => item.Id == profileId && !item.Deleted, cancellationToken))
        {
            throw new InvalidOperationException("Das Profil ist nicht mehr verfügbar.");
        }
    }

    private ExportQueries BuildQueries(Guid profileId, ProfileExportRange range)
    {
        var includeAll = !range.IsFiltered;
        bool IncludesTimestamp(DateTimeOffset value) =>
            (range.FromInclusive is null || value >= range.FromInclusive.Value) &&
            (range.UntilExclusive is null || value < range.UntilExclusive.Value);
        bool IncludesOptionalTimestamp(DateTimeOffset? value) =>
            includeAll || value is { } timestamp && IncludesTimestamp(timestamp);
        bool IncludesDate(DateOnly value) =>
            (range.From is null || value >= range.From.Value) &&
            (range.UntilDateExclusive is null || value < range.UntilDateExclusive.Value);

        var attempts = db.TypingAttempts.AsNoTracking()
            .Where(item => item.UserProfileId == profileId)
            .OrderBy(item => item.Id)
            .Select(item => new TypingAttemptExport(
                item.Id,
                item.UserProfileId,
                item.TrainingTextId,
                item.Mode,
                item.Phase,
                item.StandardTextKey,
                item.TextHash,
                item.PreparedAt,
                item.StartedAt,
                item.FinishedAt,
                item.DurationMilliseconds,
                item.ClientDurationMilliseconds,
                item.CorrectCharacters,
                item.IncorrectCharacters,
                item.Backspaces,
                item.FocusLosses,
                item.TotalCharacters,
                item.Wpm,
                item.RawWpm,
                item.CharactersPerMinute,
                item.Accuracy,
                item.Consistency,
                item.ConsistencySampleCount,
                item.MeanWordMilliseconds,
                item.WordTimingVariation,
                item.Completed,
                item.Official,
                item.LeaderboardEligible,
                item.ExperienceAwarded,
                item.CreatedAt));
        var createdChallenges = db.Challenges.AsNoTracking()
            .Where(item => item.CreatorProfileId == profileId);
        var challengeParticipations = db.ChallengeParticipants.AsNoTracking()
            .Where(item => item.UserProfileId == profileId);
        var challengeRoundResults = db.ChallengeRoundResults.AsNoTracking()
            .Where(item => item.UserProfileId == profileId);
        var challengeAttemptBindings = db.ChallengeAttemptBindings.AsNoTracking()
            .Where(item => item.UserProfileId == profileId);
        var relatedChallengeIds = createdChallenges.Select(item => item.Id)
            .Union(challengeParticipations.Select(item => item.ChallengeId))
            .Union(challengeAttemptBindings.Select(item => item.ChallengeId));
        var relatedChallengeRoundIds = challengeRoundResults.Select(item => item.ChallengeRoundId)
            .Union(challengeAttemptBindings.Select(item => item.ChallengeRoundId));
        var datedLiveResults = db.LiveRoomParticipantSummaries.AsNoTracking()
            .Where(item => item.UserProfileId == profileId)
            .Join(
                db.LiveRoomSummaries.AsNoTracking(),
                result => result.LiveRoomSummaryId,
                room => room.Id,
                (result, room) => new DatedLiveRoomResult(result, room.CreatedAt));

        return new ExportQueries(
            ExportSequence<TypingAttemptExport>.FromQuery(attempts, item => IncludesTimestamp(item.CreatedAt), includeAll),
            ExportSequence<TypingAttemptError>.FromQuery(
                db.TypingAttemptErrors.AsNoTracking()
                    .Where(item => item.UserProfileId == profileId)
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<RewardLedgerEntry>.FromQuery(
                db.RewardLedgerEntries.AsNoTracking()
                    .Where(item => item.UserProfileId == profileId)
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.AwardedAt),
                includeAll),
            ExportSequence<Mission>.FromQuery(
                db.Missions.AsNoTracking()
                    .Where(item => item.UserProfileId == profileId)
                    .OrderBy(item => item.MissionDate).ThenBy(item => item.Id),
                item => IncludesDate(item.MissionDate),
                includeAll),
            ExportSequence<Achievement>.FromQuery(
                db.Achievements.AsNoTracking()
                    .Where(item => item.UserProfileId == profileId)
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.UnlockedAt),
                includeAll),
            ExportSequence<GamificationEvent>.FromQuery(
                db.GamificationEvents.AsNoTracking()
                    .Where(item => item.UserProfileId == profileId)
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<WeaknessObservation>.FromQuery(
                db.WeaknessObservations.AsNoTracking()
                    .Where(item => item.UserProfileId == profileId)
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.LastSeenAt),
                includeAll),
            ExportSequence<TrainingText>.FromQuery(
                db.TrainingTexts.AsNoTracking()
                    .Where(item => item.OwnerProfileId == profileId)
                    .OrderBy(item => item.Id)),
            ExportSequence<TextCollection>.FromQuery(
                db.TextCollections.AsNoTracking()
                    .Where(item => item.OwnerProfileId == profileId)
                    .OrderBy(item => item.Id)),
            ExportSequence<TextCollectionItem>.FromQuery(
                db.TextCollectionItems.AsNoTracking()
                    .Where(item => db.TextCollections.Any(collection =>
                        collection.Id == item.TextCollectionId && collection.OwnerProfileId == profileId))
                    .OrderBy(item => item.TextCollectionId).ThenBy(item => item.SortOrder).ThenBy(item => item.TrainingTextId)),
            ExportSequence<ContentModerationAuditEntry>.FromQuery(
                db.ContentModerationAuditEntries.AsNoTracking()
                    .Where(item => item.ActorProfileId == profileId || item.TargetOwnerProfileId == profileId)
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<Challenge>.FromQuery(
                createdChallenges.OrderBy(item => item.Id),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<ChallengeRound>.FromQuery(
                db.ChallengeRounds.AsNoTracking()
                    .Where(item => relatedChallengeIds.Contains(item.ChallengeId) || relatedChallengeRoundIds.Contains(item.Id))
                    .OrderBy(item => item.Id),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<ChallengeParticipant>.FromQuery(
                challengeParticipations.OrderBy(item => item.ChallengeId),
                item => IncludesTimestamp(item.InvitedAt),
                includeAll),
            ExportSequence<ChallengeRoundResult>.FromQuery(
                challengeRoundResults.OrderBy(item => item.Id),
                item => IncludesOptionalTimestamp(item.FinishedAt),
                includeAll),
            ExportSequence<ChallengeAttemptBindingExport>.FromQuery(
                challengeAttemptBindings
                    .OrderBy(item => item.Id)
                    .Select(item => new ChallengeAttemptBindingExport(
                        item.Id,
                        item.ChallengeId,
                        item.ChallengeRoundId,
                        item.UserProfileId,
                        item.TypingAttemptId,
                        item.TextSnapshotHash,
                        item.Mode,
                        item.Consumed,
                        item.CreatedAt,
                        item.ConsumedAt)),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<LiveRoomSummaryExport>.FromQuery(
                db.LiveRoomSummaries.AsNoTracking()
                    .Where(item => item.CreatorProfileId == profileId)
                    .OrderBy(item => item.Id)
                    .Select(item => new LiveRoomSummaryExport(
                        item.Id,
                        item.RoundNumber,
                        item.RoundVersion,
                        item.CreatorProfileId,
                        item.RoomCode,
                        item.Mode,
                        item.Visibility,
                        item.RoundCount,
                        item.CreatedAt,
                        item.StartedAt,
                        item.FinishedAt,
                        item.AbortedByServer)),
                item => IncludesTimestamp(item.CreatedAt),
                includeAll),
            ExportSequence<LiveRoomParticipantSummary>.FromQuery(
                datedLiveResults,
                item => IncludesTimestamp(item.OccurredAt),
                item => item.Result,
                includeAll));
    }

    private static async Task WriteArrayAsync<T>(
        Utf8JsonWriter writer,
        string propertyName,
        ExportSequence<T> sequence,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        var pendingSinceFlush = 0;
        await foreach (var item in sequence.ReadAsync(cancellationToken))
        {
            JsonSerializer.Serialize(writer, item, SerializerOptions);
            if (++pendingSinceFlush >= FlushInterval)
            {
                await writer.FlushAsync(cancellationToken);
                pendingSinceFlush = 0;
            }
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken);
    }

    private sealed record ExportQueries(
        ExportSequence<TypingAttemptExport> Attempts,
        ExportSequence<TypingAttemptError> AttemptErrors,
        ExportSequence<RewardLedgerEntry> RewardLedger,
        ExportSequence<Mission> Missions,
        ExportSequence<Achievement> Achievements,
        ExportSequence<GamificationEvent> GamificationEvents,
        ExportSequence<WeaknessObservation> WeaknessObservations,
        ExportSequence<TrainingText> OwnedTexts,
        ExportSequence<TextCollection> OwnedCollections,
        ExportSequence<TextCollectionItem> OwnedCollectionItems,
        ExportSequence<ContentModerationAuditEntry> ContentModerationAuditEntries,
        ExportSequence<Challenge> CreatedChallenges,
        ExportSequence<ChallengeRound> ChallengeRounds,
        ExportSequence<ChallengeParticipant> ChallengeParticipations,
        ExportSequence<ChallengeRoundResult> ChallengeRoundResults,
        ExportSequence<ChallengeAttemptBindingExport> ChallengeAttemptBindings,
        ExportSequence<LiveRoomSummaryExport> CreatedLiveRooms,
        ExportSequence<LiveRoomParticipantSummary> LiveRoomResults);

    private sealed record DatedLiveRoomResult(LiveRoomParticipantSummary Result, DateTimeOffset OccurredAt);

    private sealed class ExportSequence<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> reader,
        Func<CancellationToken, Task<long>> counter)
    {
        public IAsyncEnumerable<T> ReadAsync(CancellationToken cancellationToken) => reader(cancellationToken);

        public Task<long> CountAsync(CancellationToken cancellationToken) => counter(cancellationToken);

        public static ExportSequence<T> FromQuery(IQueryable<T> query) =>
            FromQuery(query, static _ => true, includeAll: true);

        public static ExportSequence<T> FromQuery(
            IQueryable<T> query,
            Func<T, bool> include,
            bool includeAll) => new(
                cancellationToken => ReadQueryAsync(query, include, cancellationToken),
                includeAll
                    ? cancellationToken => query.LongCountAsync(cancellationToken)
                    : cancellationToken => CountQueryAsync(query, include, cancellationToken));

        public static ExportSequence<T> FromQuery<TSource>(
            IQueryable<TSource> query,
            Func<TSource, bool> include,
            Func<TSource, T> select,
            bool includeAll) => new(
                cancellationToken => ReadQueryAsync(query, include, select, cancellationToken),
                includeAll
                    ? cancellationToken => query.LongCountAsync(cancellationToken)
                    : cancellationToken => CountQueryAsync(query, include, cancellationToken));

        private static async IAsyncEnumerable<T> ReadQueryAsync(
            IQueryable<T> query,
            Func<T, bool> include,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                if (include(item))
                {
                    yield return item;
                }
            }
        }

        private static async IAsyncEnumerable<T> ReadQueryAsync<TSource>(
            IQueryable<TSource> query,
            Func<TSource, bool> include,
            Func<TSource, T> select,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                if (include(item))
                {
                    yield return select(item);
                }
            }
        }

        private static async Task<long> CountQueryAsync<TSource>(
            IQueryable<TSource> query,
            Func<TSource, bool> include,
            CancellationToken cancellationToken)
        {
            long count = 0;
            await foreach (var item in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                if (include(item))
                {
                    count++;
                }
            }

            return count;
        }
    }
}

internal sealed class ProfileExportDownloadResult(
    ProfileExportService service,
    Guid profileId,
    ProfileExportRange range,
    DateTimeOffset generatedAt,
    string fileName) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        await response.StartAsync(context.HttpContext.RequestAborted);
        await service.WriteAsync(profileId, range, generatedAt, response.Body, context.HttpContext.RequestAborted);
    }
}
