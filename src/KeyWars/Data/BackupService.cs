using Microsoft.Data.Sqlite;

namespace KeyWars.Data;

public sealed class BackupService(IConfiguration configuration, ILogger<BackupService> logger)
{
    public string DataDirectory => configuration["KEYWARS:DATA:DIRECTORY"] ?? "/data";

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
        if (!fullBackupPath.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Restore ist nur aus /data/backups erlaubt.");
        }

        var targetPath = DataPaths.DatabasePath(dataDirectory);
        await using var source = new SqliteConnection($"Data Source={fullBackupPath}");
        await using var target = new SqliteConnection($"Data Source={targetPath}");
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        logger.LogInformation("Restore aus {BackupPath} abgeschlossen.", fullBackupPath);
    }
}
