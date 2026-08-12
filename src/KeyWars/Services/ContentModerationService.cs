using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record ContentModerationQueueItem(
    ContentModerationTargetType TargetType,
    Guid TargetId,
    string Title,
    Guid OwnerProfileId,
    string OwnerDisplayName,
    TrainingTextVisibility Visibility,
    bool IsQuarantined,
    DateTimeOffset CreatedAt);

public sealed record ContentModerationQueuePage(
    IReadOnlyList<ContentModerationQueueItem> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages)
{
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}

public sealed record ContentModerationAuditPage(
    IReadOnlyList<ContentModerationAuditEntry> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages)
{
    public bool HasPreviousPage => Page > 1;
    public bool HasNextPage => Page < TotalPages;
}

public sealed class ContentModerationService(
    KeyWarsDbContext db,
    CurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<ContentModerationQueuePage> GetQueueAsync(
        ClaimsPrincipal principal,
        string? query = null,
        ContentModerationTargetType? targetType = null,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireModeratorAsync(principal, cancellationToken);
        if (targetType is not null && !Enum.IsDefined(targetType.Value))
        {
            throw new InvalidOperationException("Der Moderationsfilter ist ungültig.");
        }

        var normalizedQuery = (query ?? string.Empty).Trim();
        var boundedPageSize = Math.Clamp(pageSize, 1, 100);

        var texts =
            from text in db.TrainingTexts.AsNoTracking()
            join owner in db.UserProfiles.AsNoTracking() on text.OwnerProfileId equals (Guid?)owner.Id
            where owner.Id != actor.Id &&
                  !text.IsStandard &&
                  (text.Visibility == TrainingTextVisibility.Organization || text.IsQuarantined)
            select new
            {
                TargetType = ContentModerationTargetType.TrainingText,
                TargetId = text.Id,
                Title = text.Title,
                OwnerProfileId = owner.Id,
                OwnerDisplayName = owner.DisplayName,
                Visibility = text.Visibility,
                text.IsQuarantined,
                text.CreatedAt
            };

        var collections =
            from collection in db.TextCollections.AsNoTracking()
            join owner in db.UserProfiles.AsNoTracking() on collection.OwnerProfileId equals owner.Id
            where owner.Id != actor.Id &&
                  (collection.Visibility == TrainingTextVisibility.Organization || collection.IsQuarantined)
            select new
            {
                TargetType = ContentModerationTargetType.TextCollection,
                TargetId = collection.Id,
                Title = collection.Name,
                OwnerProfileId = owner.Id,
                OwnerDisplayName = owner.DisplayName,
                Visibility = collection.Visibility,
                collection.IsQuarantined,
                collection.CreatedAt
            };

        var items = targetType switch
        {
            ContentModerationTargetType.TrainingText => texts,
            ContentModerationTargetType.TextCollection => collections,
            _ => texts.Concat(collections)
        };

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            items = items.Where(item =>
                item.Title.Contains(normalizedQuery) ||
                item.OwnerDisplayName.Contains(normalizedQuery));
        }

        var totalCount = await items.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 1 : ((totalCount - 1) / boundedPageSize) + 1;
        var boundedPage = Math.Clamp(page, 1, totalPages);
        var pageRows = await items
            .OrderByDescending(item => item.IsQuarantined)
            .ThenBy(item => item.Title)
            .ThenBy(item => item.TargetId)
            .Skip((boundedPage - 1) * boundedPageSize)
            .Take(boundedPageSize)
            .ToListAsync(cancellationToken);
        var pageItems = pageRows
            .Select(item => new ContentModerationQueueItem(
                item.TargetType,
                item.TargetId,
                item.Title,
                item.OwnerProfileId,
                item.OwnerDisplayName,
                item.Visibility,
                item.IsQuarantined,
                item.CreatedAt))
            .ToArray();

        return new ContentModerationQueuePage(
            pageItems,
            totalCount,
            boundedPage,
            boundedPageSize,
            totalPages);
    }

    public async Task<ContentModerationAuditPage> GetAuditAsync(
        ClaimsPrincipal principal,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireModeratorAsync(principal, cancellationToken);
        var boundedPageSize = Math.Clamp(pageSize, 1, 100);
        var audit = db.Set<ContentModerationAuditEntry>().AsNoTracking();
        var totalCount = await audit.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 1 : ((totalCount - 1) / boundedPageSize) + 1;
        var boundedPage = Math.Clamp(page, 1, totalPages);
        var items = await audit
            .OrderByDescending(entry => entry.Id)
            .Skip((boundedPage - 1) * boundedPageSize)
            .Take(boundedPageSize)
            .ToListAsync(cancellationToken);

        return new ContentModerationAuditPage(
            items,
            totalCount,
            boundedPage,
            boundedPageSize,
            totalPages);
    }

    public async Task ModerateAsync(
        ClaimsPrincipal principal,
        ContentModerationTargetType targetType,
        Guid targetId,
        ContentModerationAction action,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var actor = await RequireModeratorAsync(principal, cancellationToken);
        if (!Enum.IsDefined(targetType) || !Enum.IsDefined(action))
        {
            throw new InvalidOperationException("Die Moderationsaktion ist ungültig.");
        }

        var normalizedReason = NormalizeReason(reason);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var target = targetType switch
        {
            ContentModerationTargetType.TrainingText => await ModerateTextAsync(
                actor.Id,
                targetId,
                action,
                now,
                cancellationToken),
            ContentModerationTargetType.TextCollection => await ModerateCollectionAsync(
                actor.Id,
                targetId,
                action,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(targetType))
        };

        db.Set<ContentModerationAuditEntry>().Add(new ContentModerationAuditEntry
        {
            ActorProfileId = actor.Id,
            ActorDisplayName = actor.DisplayName,
            TargetType = targetType,
            TargetId = targetId,
            TargetOwnerProfileId = target.OwnerProfileId,
            TargetTitle = target.Title,
            Action = action,
            Reason = normalizedReason,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<ModeratedTarget> ModerateTextAsync(
        Guid actorProfileId,
        Guid targetId,
        ContentModerationAction action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var target = await db.TrainingTexts
            .AsNoTracking()
            .Where(text => text.Id == targetId && text.OwnerProfileId != null && !text.IsStandard)
            .Select(text => new { text.OwnerProfileId, text.Title })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw InvalidTarget();
        EnsureForeignOwner(actorProfileId, target.OwnerProfileId!.Value);

        var candidates = db.TrainingTexts.Where(text =>
            text.Id == targetId &&
            text.Visibility == TrainingTextVisibility.Organization &&
            !text.IsQuarantined);
        var changed = action switch
        {
            ContentModerationAction.Unpublish => await candidates.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(text => text.Visibility, TrainingTextVisibility.Private)
                    .SetProperty(text => text.UpdatedAt, now),
                cancellationToken),
            ContentModerationAction.Quarantine => await candidates.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(text => text.Visibility, TrainingTextVisibility.Private)
                    .SetProperty(text => text.IsQuarantined, true)
                    .SetProperty(text => text.UpdatedAt, now),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        if (changed != 1)
        {
            throw InvalidTarget();
        }

        return new ModeratedTarget(target.Title, target.OwnerProfileId.Value);
    }

    private async Task<ModeratedTarget> ModerateCollectionAsync(
        Guid actorProfileId,
        Guid targetId,
        ContentModerationAction action,
        CancellationToken cancellationToken)
    {
        var target = await db.TextCollections
            .AsNoTracking()
            .Where(collection => collection.Id == targetId)
            .Select(collection => new { collection.OwnerProfileId, collection.Name })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw InvalidTarget();
        EnsureForeignOwner(actorProfileId, target.OwnerProfileId);

        var candidates = db.TextCollections.Where(collection =>
            collection.Id == targetId &&
            collection.Visibility == TrainingTextVisibility.Organization &&
            !collection.IsQuarantined);
        var changed = action switch
        {
            ContentModerationAction.Unpublish => await candidates.ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    collection => collection.Visibility,
                    TrainingTextVisibility.Private),
                cancellationToken),
            ContentModerationAction.Quarantine => await candidates.ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(collection => collection.Visibility, TrainingTextVisibility.Private)
                    .SetProperty(collection => collection.IsQuarantined, true),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        if (changed != 1)
        {
            throw InvalidTarget();
        }

        return new ModeratedTarget(target.Name, target.OwnerProfileId);
    }

    private async Task<UserProfile> RequireModeratorAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!ContentModeratorClaims.IsModerator(principal))
        {
            throw new UnauthorizedAccessException("Für die Inhaltsmoderation fehlt die LDAP-Gruppenfreigabe.");
        }

        return await currentUser.RequireProfileAsync(principal, cancellationToken);
    }

    private static void EnsureForeignOwner(Guid actorProfileId, Guid ownerProfileId)
    {
        if (actorProfileId == ownerProfileId)
        {
            throw InvalidTarget();
        }
    }

    private static string NormalizeReason(string reason)
    {
        var normalized = (reason ?? string.Empty).Trim();
        if (normalized.Length is < 3 or > 500)
        {
            throw new InvalidOperationException("Die Begründung muss zwischen 3 und 500 Zeichen lang sein.");
        }

        return normalized;
    }

    private static InvalidOperationException InvalidTarget() =>
        new("Der Inhalt ist nicht organisationsweit, gehört dir selbst oder wurde bereits moderiert.");

    private sealed record ModeratedTarget(string Title, Guid OwnerProfileId);
}
