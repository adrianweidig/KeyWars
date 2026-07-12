using System.Security.Cryptography;
using System.Text.Json;
using KeyWars.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeyWars.IntegrationTests;

public sealed class BackupServiceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"keywars-backup;Mode=ReadOnly-{Guid.NewGuid():N}");

    [Fact]
    public async Task BackupAndRestoreHandlePathsWithConnectionStringSeparators()
    {
        await InitializeDatabaseAsync(dataDirectory, "before");
        var service = CreateService(dataDirectory);

        var backupPath = await service.CreateBackupAsync();
        var manifest = await ReadManifestAsync(backupPath);
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "after");

        await service.RestoreAsync(backupPath);

        Assert.Equal("before", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(Path.GetFileName(backupPath), manifest.DatabaseFile);
        Assert.Equal(new FileInfo(backupPath).Length, manifest.SizeBytes);
        Assert.Equal(await ComputeSha256Async(backupPath), manifest.Sha256);
        Assert.False(string.IsNullOrWhiteSpace(manifest.ApplicationVersion));
        Assert.Equal(TimeSpan.Zero, manifest.CreatedAtUtc.Offset);
        Assert.NotEmpty(manifest.ExpectedMigrations);
        Assert.Equal(manifest.ExpectedMigrations, manifest.AppliedMigrations);

        var preRestoreBackup = Assert.Single(Directory.GetFiles(
            Path.Combine(dataDirectory, "backups"),
            "keywars-pre-restore-*.db"));
        Assert.Equal("after", await ReadSampleValueAsync(preRestoreBackup));
        Assert.True(File.Exists(BackupService.GetManifestPath(preRestoreBackup)));
        Assert.Empty(Directory.GetFiles(dataDirectory, ".keywars.db.restore-*.db"));
    }

    [Fact]
    public async Task RestoreCanPopulateAnEmptyDataDirectory()
    {
        var sourceDirectory = Path.Combine(dataDirectory, "source");
        var targetDirectory = Path.Combine(dataDirectory, "target");
        await InitializeDatabaseAsync(sourceDirectory, "from-backup");
        var sourceService = CreateService(sourceDirectory);
        var sourceBackupPath = await sourceService.CreateBackupAsync();

        var targetBackupRoot = Path.Combine(targetDirectory, "backups");
        Directory.CreateDirectory(targetBackupRoot);
        var targetBackupPath = Path.Combine(targetBackupRoot, Path.GetFileName(sourceBackupPath));
        File.Copy(sourceBackupPath, targetBackupPath);
        File.Copy(
            BackupService.GetManifestPath(sourceBackupPath),
            BackupService.GetManifestPath(targetBackupPath));

        await CreateService(targetDirectory).RestoreAsync(targetBackupPath);

        Assert.Equal("from-backup", await ReadSampleValueAsync(DataPaths.DatabasePath(targetDirectory)));
        Assert.Empty(Directory.GetFiles(targetBackupRoot, "keywars-pre-restore-*.db"));
    }

    [Fact]
    public async Task RestoreRejectsAPathOutsideTheBackupRootWithoutChangingTheDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "active");
        var service = CreateService(dataDirectory);
        var outsidePath = Path.Combine(dataDirectory, "outside.db");
        File.Copy(DataPaths.DatabasePath(dataDirectory), outsidePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(outsidePath));

        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreRejectsMissingManifestWithoutChangingTheDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var service = CreateService(dataDirectory);
        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "active");
        File.Delete(BackupService.GetManifestPath(backupPath));

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.RestoreAsync(backupPath));

        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreRejectsMalformedManifestWithoutChangingTheDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var service = CreateService(dataDirectory);
        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "active");
        await File.WriteAllTextAsync(BackupService.GetManifestPath(backupPath), "{");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backupPath));

        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreRejectsWrongHashWithoutChangingTheDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var service = CreateService(dataDirectory);
        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "active");
        var manifest = await ReadManifestAsync(backupPath);
        await WriteManifestAsync(backupPath, manifest with { Sha256 = new string('0', 64) });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backupPath));

        Assert.Contains("SHA256", exception.Message, StringComparison.Ordinal);
        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreRejectsCorruptDatabaseEvenWhenManifestHashMatches()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var service = CreateService(dataDirectory);
        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "active");
        var corruptBytes = new byte[checked((int)new FileInfo(backupPath).Length)];
        await File.WriteAllBytesAsync(backupPath, corruptBytes);
        var manifest = await ReadManifestAsync(backupPath);
        await WriteManifestAsync(
            backupPath,
            manifest with
            {
                Sha256 = await ComputeSha256Async(backupPath),
                SizeBytes = corruptBytes.LongLength
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backupPath));

        Assert.Contains("SQLite", exception.Message, StringComparison.Ordinal);
        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreRejectsIncompatibleMigrationManifestWithoutChangingTheDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var service = CreateService(dataDirectory);
        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "active");
        var manifest = await ReadManifestAsync(backupPath);
        await WriteManifestAsync(
            backupPath,
            manifest with
            {
                ExpectedMigrations = [.. manifest.ExpectedMigrations, "20990101000000_UnknownMigration"]
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backupPath));

        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreIsRejectedWhileTheWebRuntimeLockIsHeld()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var configuration = Configuration(dataDirectory);
        var environment = new TestEnvironment(dataDirectory);
        var runtimeLock = new DatabaseRuntimeLock(configuration, environment);
        var service = new BackupService(
            configuration,
            environment,
            NullLogger<BackupService>.Instance,
            runtimeLock);
        var backupPath = await service.CreateBackupAsync();
        await WriteSampleValueAsync(DataPaths.DatabasePath(dataDirectory), "active");

        using var runtimeLease = runtimeLock.Acquire("webhost");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(backupPath));

        Assert.Equal("active", await ReadSampleValueAsync(DataPaths.DatabasePath(dataDirectory)));
    }

    [Fact]
    public async Task RestoreRejectsBusyWalCheckpointWithoutChangingTheActiveDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var backupPath = await CreateService(dataDirectory).CreateBackupAsync();
        var databasePath = DataPaths.DatabasePath(dataDirectory);
        await CreatePendingWalStateAsync(databasePath, "active-wal");
        SqliteConnection? blockerConnection = null;
        SqliteTransaction? blockerTransaction = null;
        var logger = new OneShotCallbackLogger<BackupService>(() =>
        {
            blockerConnection = new SqliteConnection(BuildConnectionString(databasePath));
            blockerConnection.Open();
            blockerTransaction = blockerConnection.BeginTransaction();
            using var command = blockerConnection.CreateCommand();
            command.Transaction = blockerTransaction;
            command.CommandText = "UPDATE Sample SET Value = 'uncommitted';";
            command.ExecuteNonQuery();
        });

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService(dataDirectory, logger).RestoreAsync(backupPath));

            Assert.Contains("WAL-Checkpoint", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            blockerTransaction?.Dispose();
            blockerConnection?.Dispose();
        }

        Assert.Equal("active-wal", await ReadSampleValueAsync(databasePath));
        Assert.Empty(Directory.GetFiles(dataDirectory, ".keywars.db.restore-*.db"));
        Assert.Empty(Directory.GetFiles(dataDirectory, ".keywars.db.rollback-*.db"));
    }

    [Fact]
    public async Task RestoreExchangeFailureKeepsCheckpointedWalDataInTheActiveDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var backupPath = await CreateService(dataDirectory).CreateBackupAsync();
        var databasePath = DataPaths.DatabasePath(dataDirectory);
        await CreatePendingWalStateAsync(databasePath, "active-wal");
        var logger = new OneShotCallbackLogger<BackupService>(() =>
        {
            var stagingPath = Assert.Single(Directory.GetFiles(dataDirectory, ".keywars.db.restore-*.db"));
            File.Delete(stagingPath);
        });

        await Assert.ThrowsAnyAsync<IOException>(() => CreateService(dataDirectory, logger).RestoreAsync(backupPath));

        Assert.Equal("active-wal", await ReadSampleValueAsync(databasePath));
        Assert.False(File.Exists($"{databasePath}-wal") && new FileInfo($"{databasePath}-wal").Length > 0);
        var standaloneMainPath = Path.Combine(dataDirectory, "checkpointed-active.db");
        File.Copy(databasePath, standaloneMainPath);
        Assert.Equal("active-wal", await ReadSampleValueAsync(standaloneMainPath));
        Assert.Empty(Directory.GetFiles(dataDirectory, ".keywars.db.restore-*.db"));
        Assert.Empty(Directory.GetFiles(dataDirectory, ".keywars.db.rollback-*.db"));
    }

    [Fact]
    public async Task RestoreCheckpointsPendingWalDataBeforeReplacingTheDatabase()
    {
        await InitializeDatabaseAsync(dataDirectory, "backup");
        var backupPath = await CreateService(dataDirectory).CreateBackupAsync();
        var databasePath = DataPaths.DatabasePath(dataDirectory);
        await CreatePendingWalStateAsync(databasePath, "active-wal");
        Assert.True(new FileInfo($"{databasePath}-wal").Length > 0);

        await CreateService(dataDirectory).RestoreAsync(backupPath);

        Assert.False(File.Exists($"{databasePath}-wal"));
        Assert.False(File.Exists($"{databasePath}-shm"));
        Assert.False(File.Exists($"{databasePath}-journal"));
        Assert.Equal("backup", await ReadSampleValueAsync(databasePath));
        var preRestoreBackup = Assert.Single(Directory.GetFiles(
            Path.Combine(dataDirectory, "backups"),
            "keywars-pre-restore-*.db"));
        Assert.Equal("active-wal", await ReadSampleValueAsync(preRestoreBackup));
    }

    [Fact]
    public async Task RuntimeLockCanBeReacquiredAfterItsLeaseIsDisposed()
    {
        var configuration = Configuration(dataDirectory);
        var runtimeLock = new DatabaseRuntimeLock(configuration, new TestEnvironment(dataDirectory));

        using (runtimeLock.Acquire("webhost"))
        {
            Assert.True(File.Exists(runtimeLock.LockPath));
        }

        using var secondLease = runtimeLock.Acquire("restore");
        Assert.True(File.Exists(runtimeLock.LockPath));
        await Task.CompletedTask;
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

    private static BackupService CreateService(
        string directory,
        ILogger<BackupService>? logger = null)
    {
        var configuration = Configuration(directory);
        var environment = new TestEnvironment(directory);
        return new BackupService(
            configuration,
            environment,
            logger ?? NullLogger<BackupService>.Instance,
            new DatabaseRuntimeLock(configuration, environment));
    }

    private static IConfiguration Configuration(string directory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KEYWARS:DATA:DIRECTORY"] = directory
            })
            .Build();

    private static async Task InitializeDatabaseAsync(string directory, string value)
    {
        Directory.CreateDirectory(Path.Combine(directory, "backups"));
        var databasePath = DataPaths.DatabasePath(directory);
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(BuildConnectionString(databasePath))
            .Options;
        await using (var db = new KeyWarsDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await WriteSampleValueAsync(databasePath, value);
    }

    private static async Task CreatePendingWalStateAsync(string databasePath, string value)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mainSnapshotPath = $"{databasePath}.wal-main-{suffix}";
        var walSnapshotPath = $"{databasePath}.wal-sidecar-{suffix}";
        var shmSnapshotPath = $"{databasePath}.shm-sidecar-{suffix}";
        var walPath = $"{databasePath}-wal";
        var shmPath = $"{databasePath}-shm";

        try
        {
            await using (var connection = new SqliteConnection(BuildConnectionString(databasePath)))
            {
                await connection.OpenAsync();
                await using (var journalMode = connection.CreateCommand())
                {
                    journalMode.CommandText = "PRAGMA journal_mode = WAL;";
                    Assert.Equal(
                        "wal",
                        ((await journalMode.ExecuteScalarAsync())?.ToString() ?? "").ToLowerInvariant());
                }

                await using (var disableAutoCheckpoint = connection.CreateCommand())
                {
                    disableAutoCheckpoint.CommandText = "PRAGMA wal_autocheckpoint = 0;";
                    await disableAutoCheckpoint.ExecuteNonQueryAsync();
                }

                await using (var update = connection.CreateCommand())
                {
                    update.CommandText = "UPDATE Sample SET Value = $value;";
                    update.Parameters.AddWithValue("$value", value);
                    await update.ExecuteNonQueryAsync();
                }

                Assert.True(File.Exists(walPath));
                Assert.True(new FileInfo(walPath).Length > 0);
                Assert.True(File.Exists(shmPath));
                File.Copy(databasePath, mainSnapshotPath);
                File.Copy(walPath, walSnapshotPath);
                File.Copy(shmPath, shmSnapshotPath);
            }

            File.Copy(mainSnapshotPath, databasePath, overwrite: true);
            File.Copy(walSnapshotPath, walPath, overwrite: true);
            File.Copy(shmSnapshotPath, shmPath, overwrite: true);
        }
        finally
        {
            File.Delete(mainSnapshotPath);
            File.Delete(walSnapshotPath);
            File.Delete(shmSnapshotPath);
        }

        Assert.True(File.Exists(walPath));
        Assert.True(new FileInfo(walPath).Length > 0);
    }

    private static async Task WriteSampleValueAsync(string databasePath, string value)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(databasePath));
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
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Sample LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<BackupManifest> ReadManifestAsync(string backupPath)
    {
        await using var stream = File.OpenRead(BackupService.GetManifestPath(backupPath));
        return (await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions))!;
    }

    private static async Task WriteManifestAsync(string backupPath, BackupManifest manifest)
    {
        await using var stream = new FileStream(
            BackupService.GetManifestPath(backupPath),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions);
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        };
        return builder.ToString();
    }

    private sealed class TestEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "KeyWars.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class OneShotCallbackLogger<T>(Action callback) : ILogger<T>
    {
        private int invoked;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.Exchange(ref invoked, 1) == 0)
            {
                callback();
            }
        }
    }
}
