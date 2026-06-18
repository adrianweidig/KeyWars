using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.Services;

public sealed class TextLibraryService(
    KeyWarsDbContext db,
    CurrentUser currentUser,
    IOptions<ContentOptions> options)
{
    public async Task<IReadOnlyList<TrainingText>> ListVisibleAsync(Guid ownerProfileId, CancellationToken cancellationToken = default)
    {
        return await db.TrainingTexts
            .Where(text => text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == ownerProfileId)
            .OrderByDescending(text => text.IsStandard)
            .ThenBy(text => text.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<TrainingText> GetVisibleAsync(Guid ownerProfileId, Guid textId, CancellationToken cancellationToken = default)
    {
        return await db.TrainingTexts
            .SingleAsync(text => text.Id == textId && (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == ownerProfileId), cancellationToken);
    }

    public async Task<TrainingText> CreateAsync(
        Guid ownerProfileId,
        string title,
        string body,
        TrainingTextVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        var normalized = TypingEngine.NormalizeText(body);
        ValidateText(title, normalized);
        var text = new TrainingText
        {
            OwnerProfileId = ownerProfileId,
            Title = title.Trim(),
            Body = normalized,
            SourceKey = $"user-{Guid.CreateVersion7():N}",
            Visibility = visibility,
            IsStandard = false,
            RatingEligible = false,
            CharacterCount = TypingEngine.SplitGraphemes(normalized).Count
        };

        db.TrainingTexts.Add(text);
        await db.SaveChangesAsync(cancellationToken);
        return text;
    }

    public async Task<TextCollection> CreateCollectionAsync(
        Guid ownerProfileId,
        string name,
        string? description,
        TrainingTextVisibility visibility,
        IReadOnlyCollection<Guid> textIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Der Sammlungsname darf nicht leer sein.");
        }

        var visibleIds = await db.TrainingTexts
            .Where(text => textIds.Contains(text.Id) && (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == ownerProfileId))
            .Select(text => text.Id)
            .ToListAsync(cancellationToken);

        var collection = new TextCollection
        {
            OwnerProfileId = ownerProfileId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Visibility = visibility
        };
        db.TextCollections.Add(collection);

        var order = 0;
        foreach (var textId in visibleIds.Distinct())
        {
            db.TextCollectionItems.Add(new TextCollectionItem
            {
                TextCollectionId = collection.Id,
                TrainingTextId = textId,
                SortOrder = order++
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public async Task<IReadOnlyList<UserProfile>> SearchPeopleAsync(Guid currentProfileId, string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        var normalized = (query ?? string.Empty).Trim();
        var candidates = db.UserProfiles.Where(profile => !profile.Deleted && profile.Id != currentProfileId);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            candidates = candidates.Where(profile =>
                profile.DisplayName.Contains(normalized) ||
                profile.SamAccountName.Contains(normalized) ||
                profile.UserPrincipalName.Contains(normalized));
        }

        return await candidates
            .OrderBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.SamAccountName)
            .Take(Math.Clamp(take, 1, 50))
            .ToListAsync(cancellationToken);
    }

    public async Task<TrainingText> CreateFromUploadAsync(HttpContext httpContext, IFormFile upload, TrainingTextVisibility visibility, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
        if (upload.Length <= 0 || upload.Length > options.Value.MaxUploadBytes)
        {
            throw new InvalidOperationException($"Die Datei muss zwischen 1 Byte und {options.Value.MaxUploadBytes} Byte groß sein.");
        }

        if (!Path.GetExtension(upload.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Nur .txt-Dateien sind erlaubt.");
        }

        using var reader = new StreamReader(upload.OpenReadStream());
        var body = await reader.ReadToEndAsync(cancellationToken);
        var title = Path.GetFileNameWithoutExtension(upload.FileName);
        return await CreateAsync(profile.Id, title, body, visibility, cancellationToken);
    }

    private void ValidateText(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Der Titel darf nicht leer sein.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Der Text darf nicht leer sein.");
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(body);
        if (byteCount > options.Value.MaxUploadBytes)
        {
            throw new InvalidOperationException($"Der Text überschreitet {options.Value.MaxUploadBytes} Byte.");
        }
    }
}
