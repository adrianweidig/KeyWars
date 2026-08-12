using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.IntegrationTests;

public sealed class ContentModerationServiceTests
{
    [Fact]
    public async Task QueueRequiresModeratorClaimAndShowsOnlyForeignModeratableContent()
    {
        await using var context = await ModerationTestContext.CreateAsync();
        var foreignText = context.AddText(context.Owner, "Öffentlicher Text", TrainingTextVisibility.Organization);
        _ = context.AddText(context.Owner, "Privater Text", TrainingTextVisibility.Private);
        _ = context.AddText(context.Moderator, "Eigener Text", TrainingTextVisibility.Organization);
        var quarantinedCollection = context.AddCollection(
            context.Owner,
            "Quarantänisierte Sammlung",
            TrainingTextVisibility.Private,
            isQuarantined: true);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            context.Service.GetQueueAsync(context.Principal(isModerator: false)));

        var page = await context.Service.GetQueueAsync(context.Principal(isModerator: true));

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Items, item => item.TargetId == foreignText.Id);
        Assert.Contains(page.Items, item => item.TargetId == quarantinedCollection.Id && item.IsQuarantined);
        Assert.DoesNotContain(page.Items, item => item.OwnerProfileId == context.Moderator.Id);
    }

    [Fact]
    public async Task UnpublishChangesForeignOrganizationTextAndAppendsCompleteAuditEntry()
    {
        await using var context = await ModerationTestContext.CreateAsync();
        var text = context.AddText(context.Owner, "Prüftext", TrainingTextVisibility.Organization);
        await context.Db.SaveChangesAsync();

        await context.Service.ModerateAsync(
            context.Principal(isModerator: true),
            ContentModerationTargetType.TrainingText,
            text.Id,
            ContentModerationAction.Unpublish,
            "Ungeeigneter organisationsweiter Inhalt",
            CancellationToken.None);

        await context.Db.Entry(text).ReloadAsync();
        var audit = await context.Db.ContentModerationAuditEntries.AsNoTracking().SingleAsync();
        Assert.Equal(TrainingTextVisibility.Private, text.Visibility);
        Assert.False(text.IsQuarantined);
        Assert.Equal(context.Moderator.Id, audit.ActorProfileId);
        Assert.Equal(context.Moderator.DisplayName, audit.ActorDisplayName);
        Assert.Equal(ContentModerationTargetType.TrainingText, audit.TargetType);
        Assert.Equal(text.Id, audit.TargetId);
        Assert.Equal(context.Owner.Id, audit.TargetOwnerProfileId);
        Assert.Equal(text.Title, audit.TargetTitle);
        Assert.Equal(ContentModerationAction.Unpublish, audit.Action);
        Assert.Equal("Ungeeigneter organisationsweiter Inhalt", audit.Reason);
        Assert.Equal(context.Time.GetUtcNow(), audit.CreatedAt);
    }

    [Fact]
    public async Task QuarantineChangesForeignOrganizationCollection()
    {
        await using var context = await ModerationTestContext.CreateAsync();
        var collection = context.AddCollection(
            context.Owner,
            "Unsichere Sammlung",
            TrainingTextVisibility.Organization);
        await context.Db.SaveChangesAsync();

        await context.Service.ModerateAsync(
            context.Principal(isModerator: true),
            ContentModerationTargetType.TextCollection,
            collection.Id,
            ContentModerationAction.Quarantine,
            "Enthält nicht freigegebene Inhalte",
            CancellationToken.None);

        await context.Db.Entry(collection).ReloadAsync();
        var audit = await context.Db.ContentModerationAuditEntries.AsNoTracking().SingleAsync();
        Assert.Equal(TrainingTextVisibility.Private, collection.Visibility);
        Assert.True(collection.IsQuarantined);
        Assert.Equal(ContentModerationAction.Quarantine, audit.Action);
        Assert.Equal(collection.Id, audit.TargetId);
    }

    [Fact]
    public async Task OwnPrivateOrAlreadyModeratedContentCannotBeModeratedAgain()
    {
        await using var context = await ModerationTestContext.CreateAsync();
        var own = context.AddText(context.Moderator, "Eigener Inhalt", TrainingTextVisibility.Organization);
        var foreignPrivate = context.AddText(context.Owner, "Privat", TrainingTextVisibility.Private);
        var quarantined = context.AddText(
            context.Owner,
            "Bereits quarantänisiert",
            TrainingTextVisibility.Private,
            isQuarantined: true);
        await context.Db.SaveChangesAsync();
        var principal = context.Principal(isModerator: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.ModerateAsync(
            principal,
            ContentModerationTargetType.TrainingText,
            own.Id,
            ContentModerationAction.Unpublish,
            "Eigener Inhalt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.ModerateAsync(
            principal,
            ContentModerationTargetType.TrainingText,
            foreignPrivate.Id,
            ContentModerationAction.Unpublish,
            "Privater Inhalt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.ModerateAsync(
            principal,
            ContentModerationTargetType.TrainingText,
            quarantined.Id,
            ContentModerationAction.Quarantine,
            "Doppelte Aktion"));

        Assert.Empty(await context.Db.ContentModerationAuditEntries.ToListAsync());
    }

    [Fact]
    public async Task AuditEntriesCannotBeChangedOrDeletedThroughDbContext()
    {
        await using var context = await ModerationTestContext.CreateAsync();
        var text = context.AddText(context.Owner, "Audittext", TrainingTextVisibility.Organization);
        await context.Db.SaveChangesAsync();
        await context.Service.ModerateAsync(
            context.Principal(isModerator: true),
            ContentModerationTargetType.TrainingText,
            text.Id,
            ContentModerationAction.Unpublish,
            "Audit schützen");
        var audit = await context.Db.ContentModerationAuditEntries.SingleAsync();

        audit.Reason = "Manipuliert";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Db.SaveChangesAsync());
        context.Db.Entry(audit).State = EntityState.Unchanged;
        context.Db.ContentModerationAuditEntries.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Db.SaveChangesAsync());
    }

    private sealed class ModerationTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private ModerationTestContext(
            SqliteConnection connection,
            KeyWarsDbContext db,
            UserProfile moderator,
            UserProfile owner,
            FixedTimeProvider time)
        {
            this.connection = connection;
            Db = db;
            Moderator = moderator;
            Owner = owner;
            Time = time;
            Service = new ContentModerationService(db, new CurrentUser(db), time);
        }

        public KeyWarsDbContext Db { get; }
        public UserProfile Moderator { get; }
        public UserProfile Owner { get; }
        public FixedTimeProvider Time { get; }
        public ContentModerationService Service { get; }

        public static async Task<ModerationTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new KeyWarsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var moderator = CreateProfile("moderator", "Mara Moderation");
            var owner = CreateProfile("owner", "Otto Owner");
            db.UserProfiles.AddRange(moderator, owner);
            await db.SaveChangesAsync();
            return new ModerationTestContext(
                connection,
                db,
                moderator,
                owner,
                new FixedTimeProvider(DateTimeOffset.Parse("2026-08-11T20:00:00Z")));
        }

        public ClaimsPrincipal Principal(bool isModerator)
        {
            var claims = new List<Claim>
            {
                new(KeyWarsClaims.ProfileId, Moderator.Id.ToString("D")),
                new(ClaimTypes.Name, Moderator.DisplayName)
            };
            if (isModerator)
            {
                claims.Add(new Claim(KeyWarsClaims.ContentModerator, "true"));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        public TrainingText AddText(
            UserProfile owner,
            string title,
            TrainingTextVisibility visibility,
            bool isQuarantined = false)
        {
            var text = new TrainingText
            {
                OwnerProfileId = owner.Id,
                Title = title,
                SourceKey = $"test-{Guid.NewGuid():N}",
                Body = "Testinhalt",
                CharacterCount = 10,
                Visibility = visibility,
                IsQuarantined = isQuarantined
            };
            Db.TrainingTexts.Add(text);
            return text;
        }

        public TextCollection AddCollection(
            UserProfile owner,
            string title,
            TrainingTextVisibility visibility,
            bool isQuarantined = false)
        {
            var collection = new TextCollection
            {
                OwnerProfileId = owner.Id,
                Name = title,
                Visibility = visibility,
                IsQuarantined = isQuarantined
            };
            Db.TextCollections.Add(collection);
            return collection;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static UserProfile CreateProfile(string sam, string displayName) => new()
        {
            DirectoryObjectGuid = Guid.NewGuid().ToString("D"),
            DirectorySid = $"S-1-5-21-{Guid.NewGuid():N}",
            SamAccountName = sam,
            UserPrincipalName = $"{sam}@example.local",
            DisplayName = displayName
        };
    }

    public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
