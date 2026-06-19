using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Pages.Texte;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class TextLibraryServiceTests
{
    [Fact]
    public async Task UploadRejectsInvalidUtf8()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        var bytes = new byte[] { 0x54, 0x65, 0xC3, 0x28, 0x74 };
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "upload", "invalid.txt");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateFromUploadAsync(context.HttpContext, file, TrainingTextVisibility.Private, CancellationToken.None));

        Assert.Contains("UTF-8", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.Db.TrainingTexts);
    }

    [Fact]
    public async Task CreateNormalizesNfcAndRejectsInvisibleOrControlCharacters()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();

        var text = await context.Service.CreateAsync(context.Profile.Id, "Umlaute", "A\u0308rger mit Öl", TrainingTextVisibility.Private);
        var invisible = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateAsync(context.Profile.Id, "Unsichtbar", "Hallo\u200bWelt", TrainingTextVisibility.Private));
        var control = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateAsync(context.Profile.Id, "Steuerzeichen", "Hallo\u0001Welt", TrainingTextVisibility.Private));

        Assert.StartsWith("Ärger", text.Body, StringComparison.Ordinal);
        Assert.Equal(TypingEngine.SplitGraphemes(text.Body).Count, text.CharacterCount);
        Assert.Contains("unsichtbares", invisible.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steuerzeichen", control.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAndDeleteRequireOwnerAndUnusedText()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        var other = TextLibraryTestContext.CreateProfile("other");
        context.Db.UserProfiles.Add(other);
        var own = await context.Service.CreateAsync(context.Profile.Id, "Entwurf", "Ein kurzer Entwurf.", TrainingTextVisibility.Private);
        var otherText = new TrainingText
        {
            OwnerProfileId = other.Id,
            Title = "Fremd",
            SourceKey = "other-text",
            Body = "Nicht dein Text",
            CharacterCount = 15,
            Visibility = TrainingTextVisibility.Private
        };
        context.Db.TrainingTexts.Add(otherText);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.UpdateAsync(context.Profile.Id, otherText.Id, "Manipulation", "Text", TrainingTextVisibility.Private));
        var updated = await context.Service.UpdateAsync(context.Profile.Id, own.Id, "Überarbeitet", "Ein überarbeiteter Entwurf.", TrainingTextVisibility.Organization);
        await context.Service.DeleteAsync(context.Profile.Id, updated.Id);

        Assert.Equal("Überarbeitet", updated.Title);
        Assert.Equal(TrainingTextVisibility.Organization, updated.Visibility);
        Assert.Empty(await context.Db.TrainingTexts.Where(item => item.Id == own.Id).ToListAsync());
    }

    [Fact]
    public async Task UpdateRejectsReferencedTextToKeepExistingSnapshotsStable()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        var text = await context.Service.CreateAsync(context.Profile.Id, "Verwendet", "Dieser Text wurde trainiert.", TrainingTextVisibility.Private);
        context.Db.TypingAttempts.Add(new TypingAttempt
        {
            UserProfileId = context.Profile.Id,
            TrainingTextId = text.Id,
            Mode = TrainingMode.Text,
            Phase = AttemptPhase.Finished,
            PreparedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Completed = true,
            Official = true
        });
        await context.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.UpdateAsync(context.Profile.Id, text.Id, "Neu", "Geändert", TrainingTextVisibility.Private));

        Assert.Contains("bereits verwendet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRemovesOwnedTextAndCollectionLinks()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        var text = await context.Service.CreateAsync(context.Profile.Id, "Sammlungstext", "Text für Sammlung", TrainingTextVisibility.Private);
        var collection = await context.Service.CreateCollectionAsync(context.Profile.Id, "Privat", null, TrainingTextVisibility.Private, [text.Id]);

        await context.Service.DeleteAsync(context.Profile.Id, text.Id);

        Assert.Empty(await context.Db.TrainingTexts.Where(item => item.Id == text.Id).ToListAsync());
        Assert.Empty(await context.Db.TextCollectionItems.Where(item => item.TextCollectionId == collection.Id).ToListAsync());
    }

    [Fact]
    public async Task CollectionRejectsEmptyDuplicateAndInvisibleSelections()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        var privateText = await context.Service.CreateAsync(context.Profile.Id, "Privat", "Privater Text", TrainingTextVisibility.Private);
        var other = TextLibraryTestContext.CreateProfile("other");
        context.Db.UserProfiles.Add(other);
        var hidden = new TrainingText
        {
            OwnerProfileId = other.Id,
            Title = "Versteckt",
            SourceKey = "hidden-text",
            Body = "Fremd",
            CharacterCount = 5,
            Visibility = TrainingTextVisibility.Private
        };
        context.Db.TrainingTexts.Add(hidden);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateCollectionAsync(context.Profile.Id, "Leer", null, TrainingTextVisibility.Private, []));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateCollectionAsync(context.Profile.Id, "Doppelt", null, TrainingTextVisibility.Private, [privateText.Id, privateText.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateCollectionAsync(context.Profile.Id, "Unsichtbar", null, TrainingTextVisibility.Private, [hidden.Id]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.CreateCollectionAsync(context.Profile.Id, "Leak", null, TrainingTextVisibility.Organization, [privateText.Id]));
    }

    [Fact]
    public async Task CopyPageGetDoesNotMutateAndPostCreatesPrivateCopy()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        var original = await context.Service.CreateAsync(context.Profile.Id, "Vorlage", "Kopierbarer Text", TrainingTextVisibility.Organization);
        var page = new KopierenModel(new CurrentUser(context.Db), context.Service)
        {
            PageContext = new PageContext { HttpContext = context.HttpContext }
        };

        await page.OnGetAsync(original.Id, CancellationToken.None);
        Assert.Single(await context.Db.TrainingTexts.ToListAsync());

        await page.OnPostAsync(original.Id, CancellationToken.None);
        var texts = await context.Db.TrainingTexts.OrderBy(item => item.Title).ToListAsync();
        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, item => item.Title == "Vorlage Kopie" && item.Visibility == TrainingTextVisibility.Private && item.OwnerProfileId == context.Profile.Id);
    }

    [Fact]
    public async Task ListVisibleSupportsSearchVisibilityAndPaging()
    {
        await using var context = await TextLibraryTestContext.CreateAsync();
        await context.Service.CreateAsync(context.Profile.Id, "Alpha", "eins", TrainingTextVisibility.Private);
        await context.Service.CreateAsync(context.Profile.Id, "Beta", "zwei", TrainingTextVisibility.Private);
        await context.Service.CreateAsync(context.Profile.Id, "Gamma", "drei", TrainingTextVisibility.Organization);

        var search = await context.Service.ListVisibleAsync(context.Profile.Id, "amm");
        var privatePage = await context.Service.ListVisibleAsync(context.Profile.Id, visibility: TrainingTextVisibility.Private, page: 2, pageSize: 1);

        Assert.Single(search);
        Assert.Equal("Gamma", search[0].Title);
        Assert.Single(privatePage);
    }

    private sealed class TextLibraryTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TextLibraryTestContext(SqliteConnection connection, KeyWarsDbContext db, UserProfile profile)
        {
            this.connection = connection;
            Db = db;
            Profile = profile;
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(KeyWarsClaims.ProfileId, profile.Id.ToString())
                ], "test"))
            };
            Service = new TextLibraryService(Db, new CurrentUser(Db), Options.Create(new ContentOptions
            {
                MaxUploadBytes = 4096,
                MaxTextCharacters = 200,
                MaxTextGraphemes = 200,
                MaxTextLines = 8
            }));
        }

        public KeyWarsDbContext Db { get; }
        public UserProfile Profile { get; }
        public HttpContext HttpContext { get; }
        public TextLibraryService Service { get; }

        public static async Task<TextLibraryTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var profile = CreateProfile("uupload");
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
            return new TextLibraryTestContext(connection, db, profile);
        }

        public static UserProfile CreateProfile(string account) => new()
        {
            DisplayName = account,
            SamAccountName = account,
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = $"S-1-5-21-{account}"
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
