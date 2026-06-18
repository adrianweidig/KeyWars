using System.Security.Claims;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class TextLibraryServiceTests
{
    [Fact]
    public async Task UploadRejectsInvalidUtf8()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DisplayName = "Uta Upload",
            SamAccountName = "uupload",
            DirectoryObjectGuid = Guid.CreateVersion7().ToString(),
            DirectorySid = "S-1-5-21-upload"
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(KeyWarsClaims.ProfileId, profile.Id.ToString())
            ], "test"))
        };
        var bytes = new byte[] { 0x54, 0x65, 0xC3, 0x28, 0x74 };
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "upload", "invalid.txt");
        var service = new TextLibraryService(db, new CurrentUser(db), Options.Create(new ContentOptions()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateFromUploadAsync(httpContext, file, TrainingTextVisibility.Private, CancellationToken.None));

        Assert.Contains("UTF-8", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.TrainingTexts);
    }
}
