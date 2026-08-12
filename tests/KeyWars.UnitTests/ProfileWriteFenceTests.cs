using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.UnitTests;

public sealed class ProfileWriteFenceTests
{
    [Fact]
    public async Task AvailabilityCheckRejectsDeletedProfileAfterFenceAcquisition()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var profile = new UserProfile
        {
            DirectoryObjectGuid = Guid.CreateVersion7().ToString("D"),
            DirectorySid = "S-1-5-21-fence",
            SamAccountName = "fence",
            UserPrincipalName = "fence@tests.local",
            DisplayName = "Fence Test",
            Deleted = true
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        await ProfileWriteFence.AcquireAsync(db, profile.Id, CancellationToken.None);

        Assert.False(await ProfileWriteFence.IsAvailableAsync(db, profile.Id, CancellationToken.None));
    }
}
