using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;

namespace KeyWars.Services;

public sealed record TextQuality(int Bytes, int Characters, int Graphemes, int Lines, int Words, int EstimatedSeconds, bool ContainsUmlautsOrSymbols);

public sealed class TextLibraryService(
    KeyWarsDbContext db,
    CurrentUser currentUser,
    IOptions<ContentOptions> options)
{
    public Task<IReadOnlyList<TrainingText>> ListVisibleAsync(Guid ownerProfileId, CancellationToken cancellationToken = default) =>
        ListVisibleAsync(ownerProfileId, null, null, 1, 48, cancellationToken);

    public async Task<IReadOnlyList<TrainingText>> ListVisibleAsync(
        Guid ownerProfileId,
        string? query = null,
        TrainingTextVisibility? visibility = null,
        int page = 1,
        int pageSize = 48,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        var boundedPage = Math.Max(1, page);
        var boundedPageSize = Math.Clamp(pageSize, 1, 100);
        var texts = db.TrainingTexts
            .Where(text => text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == ownerProfileId);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            texts = texts.Where(text => text.Title.Contains(normalizedQuery) || text.Body.Contains(normalizedQuery));
        }

        if (visibility is { } requestedVisibility)
        {
            texts = texts.Where(text => !text.IsStandard && text.Visibility == requestedVisibility);
        }

        return await texts
            .OrderByDescending(text => text.IsStandard)
            .ThenBy(text => text.Title)
            .Skip((boundedPage - 1) * boundedPageSize)
            .Take(boundedPageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<TrainingText> GetVisibleAsync(Guid ownerProfileId, Guid textId, CancellationToken cancellationToken = default)
    {
        return await db.TrainingTexts
            .SingleAsync(text => text.Id == textId && (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == ownerProfileId), cancellationToken);
    }

    public async Task<TrainingText> GetOwnedEditableAsync(Guid ownerProfileId, Guid textId, CancellationToken cancellationToken = default)
    {
        return await db.TrainingTexts
            .SingleAsync(text => text.Id == textId && text.OwnerProfileId == ownerProfileId && !text.IsStandard, cancellationToken);
    }

    public async Task<TrainingText> CreateAsync(
        Guid ownerProfileId,
        string title,
        string body,
        TrainingTextVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidateText(title, body);
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

    public async Task<TrainingText> CopyAsync(Guid ownerProfileId, Guid sourceTextId, CancellationToken cancellationToken = default)
    {
        var original = await GetVisibleAsync(ownerProfileId, sourceTextId, cancellationToken);
        return await CreateAsync(ownerProfileId, $"{original.Title} Kopie", original.Body, TrainingTextVisibility.Private, cancellationToken);
    }

    public async Task<TrainingText> UpdateAsync(
        Guid ownerProfileId,
        Guid textId,
        string title,
        string body,
        TrainingTextVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        var text = await GetOwnedEditableAsync(ownerProfileId, textId, cancellationToken);
        await EnsureTextCanChangeAsync(text.Id, cancellationToken);
        var normalized = NormalizeAndValidateText(title, body);
        text.Title = title.Trim();
        text.Body = normalized;
        text.Visibility = visibility;
        text.CharacterCount = TypingEngine.SplitGraphemes(normalized).Count;
        text.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return text;
    }

    public async Task DeleteAsync(Guid ownerProfileId, Guid textId, CancellationToken cancellationToken = default)
    {
        var text = await GetOwnedEditableAsync(ownerProfileId, textId, cancellationToken);
        await EnsureTextCanChangeAsync(text.Id, cancellationToken);
        var links = await db.TextCollectionItems
            .Where(item => item.TrainingTextId == text.Id)
            .ToListAsync(cancellationToken);
        db.TextCollectionItems.RemoveRange(links);
        db.TrainingTexts.Remove(text);
        await db.SaveChangesAsync(cancellationToken);
    }

    public TextQuality AnalyzeText(string body)
    {
        var normalized = TypingEngine.NormalizeText(body);
        var graphemes = TypingEngine.SplitGraphemes(normalized);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        var words = TypingEngine.CountWords(normalized);
        var lines = normalized.Length == 0 ? 0 : normalized.Split('\n').Length;
        var containsUmlautsOrSymbols = normalized.Any(character => "ÄÖÜäöüß".Contains(character, StringComparison.Ordinal)) ||
            normalized.Any(character => char.IsSymbol(character) || char.IsPunctuation(character));
        var estimatedSeconds = Math.Max(5, (int)Math.Ceiling(words / 45d * 60d));
        return new TextQuality(byteCount, normalized.Length, graphemes.Count, lines, words, estimatedSeconds, containsUmlautsOrSymbols);
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

        var distinctTextIds = textIds.Distinct().ToArray();
        if (distinctTextIds.Length == 0)
        {
            throw new InvalidOperationException("Eine Sammlung braucht mindestens einen Text.");
        }

        if (distinctTextIds.Length != textIds.Count)
        {
            throw new InvalidOperationException("Ein Text darf nur einmal in derselben Sammlung enthalten sein.");
        }

        var visibleTexts = await db.TrainingTexts
            .Where(text => distinctTextIds.Contains(text.Id) && (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == ownerProfileId))
            .ToListAsync(cancellationToken);
        if (visibleTexts.Count != distinctTextIds.Length)
        {
            throw new InvalidOperationException("Mindestens ein ausgewählter Text ist nicht sichtbar.");
        }

        if (visibility == TrainingTextVisibility.Organization && visibleTexts.Any(text => !text.IsStandard && text.Visibility != TrainingTextVisibility.Organization))
        {
            throw new InvalidOperationException("Organisationsweite Sammlungen dürfen keine privaten Texte enthalten.");
        }

        var collection = new TextCollection
        {
            OwnerProfileId = ownerProfileId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Visibility = visibility
        };
        db.TextCollections.Add(collection);

        var order = 0;
        foreach (var textId in distinctTextIds)
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

        string body;
        try
        {
            using var reader = new StreamReader(
                upload.OpenReadStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            body = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Die Datei muss als gültiges UTF-8 gespeichert sein.", ex);
        }

        var title = Path.GetFileNameWithoutExtension(upload.FileName);
        return await CreateAsync(profile.Id, title, body, visibility, cancellationToken);
    }

    private async Task EnsureTextCanChangeAsync(Guid textId, CancellationToken cancellationToken)
    {
        var usedByAttempt = await db.TypingAttempts.AnyAsync(attempt => attempt.TrainingTextId == textId, cancellationToken);
        var usedByChallenge = await db.Challenges.AnyAsync(challenge => challenge.TrainingTextId == textId, cancellationToken);
        if (usedByAttempt || usedByChallenge)
        {
            throw new InvalidOperationException("Dieser Text wurde bereits verwendet. Erstelle eine Kopie, damit bestehende Ergebnisse und Herausforderungen unverändert bleiben.");
        }
    }

    private string NormalizeAndValidateText(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Der Titel darf nicht leer sein.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Der Text darf nicht leer sein.");
        }

        var normalized = TypingEngine.NormalizeText(body);
        var byteCount = Encoding.UTF8.GetByteCount(normalized);
        var graphemes = TypingEngine.SplitGraphemes(normalized);
        var lines = normalized.Split('\n').Length;
        if (byteCount > options.Value.MaxUploadBytes)
        {
            throw new InvalidOperationException($"Der Text überschreitet {options.Value.MaxUploadBytes} Byte.");
        }

        if (normalized.Length > options.Value.MaxTextCharacters)
        {
            throw new InvalidOperationException($"Der Text überschreitet {options.Value.MaxTextCharacters} Zeichen.");
        }

        if (graphemes.Count > options.Value.MaxTextGraphemes)
        {
            throw new InvalidOperationException($"Der Text überschreitet {options.Value.MaxTextGraphemes} Grapheme.");
        }

        if (lines > options.Value.MaxTextLines)
        {
            throw new InvalidOperationException($"Der Text überschreitet {options.Value.MaxTextLines} Zeilen.");
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control && rune.Value is not '\n' and not '\t')
            {
                throw new InvalidOperationException($"Der Text enthält ein unzulässiges Steuerzeichen U+{rune.Value:X4}.");
            }

            if (category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate)
            {
                throw new InvalidOperationException($"Der Text enthält ein unsichtbares oder nicht unterstütztes Zeichen U+{rune.Value:X4}.");
            }
        }

        return normalized;
    }
}
