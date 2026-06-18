using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace KeyWars.Data;

public sealed class BackupService(IConfiguration configuration, IHostEnvironment environment, ILogger<BackupService> logger)
{
    public string DataDirectory => DataPaths.Resolve(configuration, environment);

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var dataDirectory = DataDirectory;
        Directory.CreateDirectory(Path.Combine(dataDirectory, "backups"));

        var sourcePath = DataPaths.DatabasePath(dataDirectory);
        var targetPath = Path.Combine(dataDirectory, "backups", $"keywars-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");

        await using var source = new SqliteConnection($"Data Source={sourcePath}");
        await using var target = new SqliteConnection($"Data Source={targetPath}");
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        logger.LogInformation("Backup geschrieben: {BackupPath}", targetPath);
        return targetPath;
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var dataDirectory = DataDirectory;
        var fullBackupPath = Path.GetFullPath(backupPath);
        var backupRoot = Path.GetFullPath(Path.Combine(dataDirectory, "backups"));
        var relative = Path.GetRelativePath(backupRoot, fullBackupPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Restore ist nur aus /data/backups erlaubt.");
        }

        if (!File.Exists(fullBackupPath))
        {
            throw new FileNotFoundException("Backup wurde nicht gefunden.", fullBackupPath);
        }

        await VerifyIntegrityAsync(fullBackupPath, cancellationToken);
        var targetPath = DataPaths.DatabasePath(dataDirectory);
        await using var source = new SqliteConnection($"Data Source={fullBackupPath}");
        await using var target = new SqliteConnection($"Data Source={targetPath}");
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        logger.LogInformation("Restore aus {BackupPath} abgeschlossen.", fullBackupPath);
    }

    private static async Task VerifyIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Das Backup hat die SQLite-Integritätsprüfung nicht bestanden.");
        }
    }
}
