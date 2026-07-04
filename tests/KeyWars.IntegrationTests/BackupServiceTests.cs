using KeyWars.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeyWars.IntegrationTests;

public sealed class BackupServiceTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"keywars-backup;Mode=ReadOnly-{Guid.NewGuid():N}");

    [Fact]
    public async Task BackupAndRestoreHandlePathsWithConnectionStringSeparators()
    {
        Directory.CreateDirectory(Path.Combine(dataDirectory, "backups"));
        var databasePath = DataPaths.DatabasePath(dataDirectory);
        await WriteSampleValueAsync(databasePath, "before");
        var service = new BackupService(Configuration(), new TestEnvironment(dataDirectory), NullLogger<BackupService>.Instance);

        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(databasePath, "after");

        await service.RestoreAsync(backupPath);

        Assert.Equal("before", await ReadSampleValueAsync(databasePath));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:DATA:DIRECTORY"] = dataDirectory
            })
            .Build();

    private static async Task WriteSampleValueAsync(string databasePath, string value)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS Sample;
            CREATE TABLE Sample (Value TEXT NOT NULL);
            INSERT INTO Sample (Value) VALUES ($value);
            """;
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadSampleValueAsync(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Sample LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private sealed class TestEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "KeyWars.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
