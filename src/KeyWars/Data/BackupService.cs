using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Data;

public sealed class BackupService
{
    private const int ManifestFormatVersion = 1;
    private const int WalCheckpointTimeoutSeconds = 1;
    private const long MaximumManifestSizeBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;
    private readonly ILogger<BackupService> logger;
    private readonly DatabaseRuntimeLock runtimeLock;

    public BackupService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<BackupService> logger)
        : this(configuration, environment, logger, new DatabaseRuntimeLock(configuration, environment))
    {
    }

    public BackupService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<BackupService> logger,
        DatabaseRuntimeLock runtimeLock)
    {
        this.configuration = configuration;
        this.environment = environment;
        this.logger = logger;
        this.runtimeLock = runtimeLock;
    }

    public string DataDirectory => DataPaths.Resolve(configuration, environment);

    public static string GetManifestPath(string backupPath) => $"{backupPath}.manifest.json";

    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        CreateBackupCoreAsync("keywars", cancellationToken);

    public async Task<BackupRetentionResult> ApplyRetentionAsync(
        DateTimeOffset cutoffUtc,
        int minimumPairsPerFamily,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (minimumPairsPerFamily is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPairsPerFamily));
        }

        cutoffUtc = cutoffUtc.ToUniversalTime();
        var backupRoot = Path.GetFullPath(Path.Combine(DataDirectory, "backups"));
        if (!Directory.Exists(backupRoot))
        {
            return new BackupRetentionResult(
                dryRun,
                cutoffUtc,
                minimumPairsPerFamily,
                ValidPairs: 0,
                CandidatePairs: 0,
                DeletedPairs: 0,
                FailedPairs: 0,
                CandidateBytes: 0,
                IgnoredEntries: 0);
        }

        var pairs = new List<BackupPair>();
        var ignoredEntries = 0;
        var discoveredDatabasePaths = Directory
            .EnumerateFiles(backupRoot, "keywars-*.db", SearchOption.TopDirectoryOnly)
            .ToArray();
        foreach (var databasePath in discoveredDatabasePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadAndValidateManifestAsync(databasePath, cancellationToken);
                await ValidateDatabaseFileAsync(databasePath, manifest, cancellationToken);
                var manifestPath = GetManifestPath(databasePath);
                var family = Path.GetFileName(databasePath).StartsWith(
                    "keywars-pre-restore-",
                    StringComparison.Ordinal)
                    ? "pre-restore"
                    : "regular";
                pairs.Add(new BackupPair(
                    databasePath,
                    manifestPath,
                    family,
                    manifest.CreatedAtUtc,
                    checked(new FileInfo(databasePath).Length + new FileInfo(manifestPath).Length)));
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                ignoredEntries++;
                logger.LogWarning(exception, "Ungültiges oder unvollständiges Backup-Paar wird von der Retention nicht verändert: {BackupPath}", databasePath);
            }
        }

        var databasePaths = discoveredDatabasePaths.ToHashSet(StringComparer.Ordinal);
        ignoredEntries += Directory
            .EnumerateFiles(backupRoot, "keywars-*.db.manifest.json", SearchOption.TopDirectoryOnly)
            .Count(manifestPath => !databasePaths.Contains(
                manifestPath[..^".manifest.json".Length]));

        var candidates = pairs
            .GroupBy(pair => pair.Family, StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(pair => pair.CreatedAtUtc)
                .ThenByDescending(pair => pair.DatabasePath, StringComparer.Ordinal)
                .Skip(minimumPairsPerFamily)
                .Where(pair => pair.CreatedAtUtc < cutoffUtc))
            .OrderBy(pair => pair.CreatedAtUtc)
            .ThenBy(pair => pair.DatabasePath, StringComparer.Ordinal)
            .ToList();
        var candidateBytes = candidates.Sum(pair => pair.SizeBytes);
        if (dryRun || candidates.Count == 0)
        {
            return new BackupRetentionResult(
                dryRun,
                cutoffUtc,
                minimumPairsPerFamily,
                pairs.Count,
                candidates.Count,
                0,
                0,
                candidateBytes,
                ignoredEntries);
        }

        var deletedPairs = 0;
        var failedPairs = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeleteBackupPair(candidate);
                deletedPairs++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failedPairs++;
                logger.LogError(exception, "Backup-Paar konnte nicht durch Retention entfernt werden: {BackupPath}", candidate.DatabasePath);
            }
        }

        return new BackupRetentionResult(
            dryRun,
            cutoffUtc,
            minimumPairsPerFamily,
            pairs.Count,
            candidates.Count,
            deletedPairs,
            failedPairs,
            candidateBytes,
            ignoredEntries);
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var dataDirectory = DataDirectory;
        var backupRoot = Path.GetFullPath(Path.Combine(dataDirectory, "backups"));
        Directory.CreateDirectory(backupRoot);
        var fullBackupPath = ResolveRestorePath(backupPath, backupRoot);

        using var restoreLease = runtimeLock.Acquire("restore");
        var manifest = await ReadAndValidateManifestAsync(fullBackupPath, cancellationToken);
        await ValidateDatabaseFileAsync(fullBackupPath, manifest, cancellationToken);

        var targetPath = Path.GetFullPath(DataPaths.DatabasePath(dataDirectory));
        var stagingPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.restore-{Guid.NewGuid():N}.db");

        try
        {
            await CopyFileAsync(fullBackupPath, stagingPath, cancellationToken);
            await ValidateDatabaseFileAsync(stagingPath, manifest, cancellationToken);

            string? preRestoreBackupPath = null;
            if (File.Exists(targetPath))
            {
                preRestoreBackupPath = await CreateBackupCoreAsync("keywars-pre-restore", cancellationToken);
                await CheckpointAndTruncateActiveWalAsync(targetPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDatabaseAtomically(stagingPath, targetPath);
            TryLogInformation(
                "Restore aus {BackupPath} abgeschlossen. Pre-Restore-Backup: {PreRestoreBackupPath}",
                fullBackupPath,
                preRestoreBackupPath ?? "nicht erforderlich");
        }
        finally
        {
            TryDeleteFile(stagingPath);
            TryDeleteFile($"{stagingPath}-wal");
            TryDeleteFile($"{stagingPath}-shm");
            TryDeleteFile($"{stagingPath}-journal");
        }
    }

    private async Task<string> CreateBackupCoreAsync(string filePrefix, CancellationToken cancellationToken)
    {
        var dataDirectory = DataDirectory;
        var backupRoot = Path.GetFullPath(Path.Combine(dataDirectory, "backups"));
        Directory.CreateDirectory(backupRoot);

        var sourcePath = Path.GetFullPath(DataPaths.DatabasePath(dataDirectory));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Die KeyWars-Datenbank wurde nicht gefunden.", sourcePath);
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var targetPath = Path.Combine(
            backupRoot,
            $"{filePrefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{uniqueSuffix}.db");
        var targetManifestPath = GetManifestPath(targetPath);
        var temporaryPath = Path.Combine(backupRoot, $".backup-{Guid.NewGuid():N}.db");
        var temporaryManifestPath = GetManifestPath(temporaryPath);

        try
        {
            await CreateSqliteSnapshotAsync(sourcePath, temporaryPath, cancellationToken);
            await VerifyIntegrityAsync(temporaryPath, cancellationToken);
            var migrations = await ReadMigrationStateAsync(temporaryPath, cancellationToken);
            EnsureCurrentMigrationState(migrations.Expected, migrations.Applied);

            var manifest = new BackupManifest
            {
                FormatVersion = ManifestFormatVersion,
                DatabaseFile = Path.GetFileName(targetPath),
                Sha256 = await ComputeSha256Async(temporaryPath, cancellationToken),
                SizeBytes = new FileInfo(temporaryPath).Length,
                ApplicationVersion = GetApplicationVersion(),
                ExpectedMigrations = migrations.Expected,
                AppliedMigrations = migrations.Applied,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await WriteManifestAsync(temporaryManifestPath, manifest, cancellationToken);

            File.Move(temporaryPath, targetPath);
            try
            {
                File.Move(temporaryManifestPath, targetManifestPath);
            }
            catch
            {
                TryDeleteFile(targetPath);
                throw;
            }

            logger.LogInformation("Backup geschrieben: {BackupPath}", targetPath);
            return targetPath;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            TryDeleteFile(temporaryManifestPath);
            TryDeleteFile($"{temporaryPath}-wal");
            TryDeleteFile($"{temporaryPath}-shm");
            TryDeleteFile($"{temporaryPath}-journal");
        }
    }

    private async Task<BackupManifest> ReadAndValidateManifestAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(backupPath, "Backup");
        var manifestPath = GetManifestPath(backupPath);
        EnsureRegularFile(manifestPath, "Backup-Manifest");
        if (new FileInfo(manifestPath).Length > MaximumManifestSizeBytes)
        {
            throw new InvalidOperationException("Das Backup-Manifest ist unzulässig groß.");
        }

        BackupManifest manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                    stream,
                    ManifestJsonOptions,
                    cancellationToken)
                ?? throw new InvalidOperationException("Das Backup-Manifest ist leer.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Das Backup-Manifest ist ungültig.", exception);
        }

        if (manifest.FormatVersion != ManifestFormatVersion ||
            !string.Equals(manifest.DatabaseFile, Path.GetFileName(backupPath), StringComparison.Ordinal) ||
            manifest.SizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(manifest.ApplicationVersion) ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero ||
            manifest.ExpectedMigrations is null ||
            manifest.AppliedMigrations is null ||
            !IsSha256(manifest.Sha256))
        {
            throw new InvalidOperationException("Das Backup-Manifest enthält ungültige oder unpassende Metadaten.");
        }

        return manifest;
    }

    private static async Task ValidateDatabaseFileAsync(
        string databasePath,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(databasePath, "Backup");
        var fileInfo = new FileInfo(databasePath);
        if (fileInfo.Length != manifest.SizeBytes)
        {
            throw new InvalidOperationException("Die Backup-Größe stimmt nicht mit dem Manifest überein.");
        }

        var sha256 = await ComputeSha256Async(databasePath, cancellationToken);
        if (!string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Der SHA256-Hash des Backups stimmt nicht mit dem Manifest überein.");
        }

        await VerifyIntegrityAsync(databasePath, cancellationToken);
        var migrations = await ReadMigrationStateAsync(databasePath, cancellationToken);
        if (!manifest.ExpectedMigrations.SequenceEqual(manifest.AppliedMigrations, StringComparer.Ordinal) ||
            !manifest.AppliedMigrations.SequenceEqual(migrations.Applied, StringComparer.Ordinal) ||
            !migrations.Expected.SequenceEqual(migrations.Applied, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Der Migrationsstand des Backups ist unvollständig oder nicht mit dieser KeyWars-Version kompatibel.");
        }
    }

    private static void EnsureCurrentMigrationState(string[] expected, string[] applied)
    {
        if (!expected.SequenceEqual(applied, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Die Datenbank ist nicht auf dem erwarteten EF-Migrationsstand und kann nicht gesichert werden.");
        }
    }

    private static async Task CreateSqliteSnapshotAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var source = new SqliteConnection(BuildConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
        await using var target = new SqliteConnection(BuildConnectionString(targetPath, SqliteOpenMode.ReadWriteCreate));
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
    }

    private static async Task CheckpointAndTruncateActiveWalAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var connection = new SqliteConnection(
                BuildConnectionString(databasePath, SqliteOpenMode.ReadWrite)))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                command.CommandTimeout = WalCheckpointTimeoutSeconds;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.FieldCount < 3)
                {
                    throw new InvalidOperationException("Der WAL-Checkpoint der aktiven Datenbank lieferte kein gültiges Ergebnis.");
                }

                var busy = reader.GetInt64(0);
                var logFrames = reader.GetInt64(1);
                var checkpointedFrames = reader.GetInt64(2);
                var noWal = logFrames == -1 && checkpointedFrames == -1;
                var fullyCheckpointed = logFrames >= 0 &&
                    checkpointedFrames >= 0 &&
                    logFrames == checkpointedFrames;
                if (busy != 0 || (!noWal && !fullyCheckpointed))
                {
                    throw new InvalidOperationException(
                        $"Der WAL-Checkpoint der aktiven Datenbank konnte nicht vollständig abgeschlossen werden " +
                        $"(busy={busy}, log={logFrames}, checkpointed={checkpointedFrames}).");
                }
            }

            var walPath = $"{databasePath}-wal";
            if (File.Exists(walPath) && new FileInfo(walPath).Length != 0)
            {
                throw new InvalidOperationException("Das WAL der aktiven Datenbank wurde nach dem Checkpoint nicht vollständig geleert.");
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Der WAL-Checkpoint der aktiven Datenbank ist fehlgeschlagen; der Restore wurde vor dem Datenbankaustausch abgebrochen.",
                exception);
        }
    }

    private void ReplaceDatabaseAtomically(string stagingPath, string targetPath)
    {
        var rollbackSuffix = Guid.NewGuid().ToString("N");
        var rollbackPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.rollback-{rollbackSuffix}.db");
        var movedSidecars = new List<(string Original, string Rollback)>();

        try
        {
            MoveSidecarIfPresent($"{targetPath}-wal", $"{rollbackPath}-wal", movedSidecars);
            MoveSidecarIfPresent($"{targetPath}-shm", $"{rollbackPath}-shm", movedSidecars);
            MoveSidecarIfPresent($"{targetPath}-journal", $"{rollbackPath}-journal", movedSidecars);

            if (File.Exists(targetPath))
            {
                File.Replace(stagingPath, targetPath, rollbackPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stagingPath, targetPath);
            }
        }
        catch (Exception replacementException)
        {
            var rollbackExceptions = RestoreSidecars(movedSidecars);
            if (rollbackExceptions.Count > 0)
            {
                rollbackExceptions.Insert(0, replacementException);
                throw new AggregateException(
                    "Der atomare Datenbankaustausch ist fehlgeschlagen und mindestens eine SQLite-Sidecar-Datei konnte nicht zurückgesetzt werden.",
                    rollbackExceptions);
            }

            throw;
        }

        TryDeleteFile(rollbackPath);
        foreach (var (_, sidecarRollbackPath) in movedSidecars)
        {
            TryDeleteFile(sidecarRollbackPath);
        }
    }

    private static void MoveSidecarIfPresent(
        string originalPath,
        string rollbackPath,
        List<(string Original, string Rollback)> movedSidecars)
    {
        if (!File.Exists(originalPath))
        {
            return;
        }

        File.Move(originalPath, rollbackPath);
        movedSidecars.Add((originalPath, rollbackPath));
    }

    private static List<Exception> RestoreSidecars(List<(string Original, string Rollback)> movedSidecars)
    {
        var exceptions = new List<Exception>();
        for (var index = movedSidecars.Count - 1; index >= 0; index--)
        {
            var (originalPath, rollbackPath) = movedSidecars[index];
            try
            {
                if (File.Exists(rollbackPath))
                {
                    File.Move(rollbackPath, originalPath);
                }
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        return exceptions;
    }

    private static async Task WriteManifestAsync(
        string manifestPath,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task VerifyIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Das Backup hat die SQLite-Integritätsprüfung nicht bestanden.");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidOperationException(
                "Das Backup ist keine lesbare, integre SQLite-Datenbank.",
                exception);
        }
    }

    private static async Task<MigrationState> ReadMigrationStateAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>()
            .UseSqlite(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly))
            .Options;
        await using var db = new KeyWarsDbContext(options);
        var expected = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        return new MigrationState(expected, applied);
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static string ResolveRestorePath(string backupPath, string backupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullBackupPath = Path.GetFullPath(backupPath);
        var relativePath = Path.GetRelativePath(backupRoot, fullBackupPath);
        if (relativePath == "." ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Restore ist nur aus dem KeyWars-Backup-Verzeichnis erlaubt.");
        }

        return fullBackupPath;
    }

    private static void EnsureRegularFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} wurde nicht gefunden.", path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} darf kein symbolischer Link sein.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string GetApplicationVersion()
    {
        var assembly = typeof(BackupService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string BuildConnectionString(string databasePath, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false
        };
        return builder.ToString();
    }

    private void DeleteBackupPair(BackupPair pair)
    {
        EnsureRegularFile(pair.DatabasePath, "Backup");
        EnsureRegularFile(pair.ManifestPath, "Backup-Manifest");
        var suffix = Guid.NewGuid().ToString("N");
        var stagedDatabasePath = $"{pair.DatabasePath}.retention-{suffix}";
        var stagedManifestPath = $"{pair.ManifestPath}.retention-{suffix}";

        File.Move(pair.DatabasePath, stagedDatabasePath);
        try
        {
            File.Move(pair.ManifestPath, stagedManifestPath);
        }
        catch (Exception stagingException)
        {
            try
            {
                File.Move(stagedDatabasePath, pair.DatabasePath);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Das Backup-Manifest konnte nicht vorgemerkt und die Datenbank nicht zurückgesetzt werden.",
                    stagingException,
                    rollbackException);
            }

            throw;
        }

        try
        {
            File.Delete(stagedDatabasePath);
        }
        catch (Exception deletionException)
        {
            var restoreExceptions = new List<Exception>();
            try
            {
                File.Move(stagedDatabasePath, pair.DatabasePath);
            }
            catch (Exception exception)
            {
                restoreExceptions.Add(exception);
            }

            try
            {
                File.Move(stagedManifestPath, pair.ManifestPath);
            }
            catch (Exception exception)
            {
                restoreExceptions.Add(exception);
            }

            if (restoreExceptions.Count > 0)
            {
                restoreExceptions.Insert(0, deletionException);
                throw new AggregateException(
                    "Ein nicht löschbares Backup-Paar konnte nicht vollständig zurückgesetzt werden.",
                    restoreExceptions);
            }

            throw;
        }

        try
        {
            File.Delete(stagedManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Das Backup wurde gelöscht, aber sein vorgemerktes Manifest konnte nicht entfernt werden: {ManifestPath}",
                stagedManifestPath);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            try
            {
                logger.LogWarning(exception, "Temporäre Backup-Datei konnte nicht entfernt werden: {Path}", path);
            }
            catch
            {
                // Cleanup darf einen bereits erfolgreichen atomaren Austausch nicht nachträglich fehlschlagen lassen.
            }
        }
    }

    private void TryLogInformation(string message, params object?[] arguments)
    {
        try
        {
            logger.LogInformation(message, arguments);
        }
        catch
        {
            // Nach dem atomaren Austausch darf ein Loggerfehler den Restore nicht als fehlgeschlagen melden.
        }
    }

    private sealed record MigrationState(string[] Expected, string[] Applied);

    private sealed record BackupPair(
        string DatabasePath,
        string ManifestPath,
        string Family,
        DateTimeOffset CreatedAtUtc,
        long SizeBytes);
}

public sealed record BackupRetentionResult(
    bool DryRun,
    DateTimeOffset CutoffUtc,
    int MinimumPairsPerFamily,
    int ValidPairs,
    int CandidatePairs,
    int DeletedPairs,
    int FailedPairs,
    long CandidateBytes,
    int IgnoredEntries,
    bool Applicable = true,
    string? SkippedReason = null)
{
    public static BackupRetentionResult NotApplicable(
        bool dryRun,
        DateTimeOffset cutoffUtc,
        int minimumPairsPerFamily,
        string reason) =>
        new(
            dryRun,
            cutoffUtc,
            minimumPairsPerFamily,
            ValidPairs: 0,
            CandidatePairs: 0,
            DeletedPairs: 0,
            FailedPairs: 0,
            CandidateBytes: 0,
            IgnoredEntries: 0,
            Applicable: false,
            SkippedReason: reason);
}

public sealed record BackupManifest
{
    public required int FormatVersion { get; init; }
    public required string DatabaseFile { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string[] ExpectedMigrations { get; init; }
    public required string[] AppliedMigrations { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
