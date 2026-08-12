namespace KeyWars.Data;

public sealed class RetentionOptions
{
    public const int MinimumStaleAttemptHours = 2;

    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int BatchSize { get; set; } = 250;
    public int MaxBatchesPerRun { get; set; } = 20;
    public int StaleAttemptHours { get; set; } = MinimumStaleAttemptHours;
    public int AbandonedAttemptRetentionDays { get; set; } = 90;
    public int SeenGamificationEventRetentionDays { get; set; } = 180;
    public int BackupRetentionDays { get; set; } = 30;
    public int MinimumBackupPairs { get; set; } = 3;

    public void Validate()
    {
        ValidateRange(IntervalHours, 1, 24 * 7, nameof(IntervalHours));
        ValidateRange(BatchSize, 1, 1_000, nameof(BatchSize));
        ValidateRange(MaxBatchesPerRun, 1, 100, nameof(MaxBatchesPerRun));
        ValidateRange(StaleAttemptHours, MinimumStaleAttemptHours, 24 * 30, nameof(StaleAttemptHours));
        ValidateRange(AbandonedAttemptRetentionDays, 7, 3650, nameof(AbandonedAttemptRetentionDays));
        ValidateRange(SeenGamificationEventRetentionDays, 30, 3650, nameof(SeenGamificationEventRetentionDays));
        ValidateRange(BackupRetentionDays, 1, 3650, nameof(BackupRetentionDays));
        ValidateRange(MinimumBackupPairs, 1, 100, nameof(MinimumBackupPairs));
    }

    private static void ValidateRange(int value, int minimum, int maximum, string propertyName)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"KEYWARS__RETENTION__{ToEnvironmentKey(propertyName)} muss zwischen {minimum} und {maximum} liegen.");
        }
    }

    private static string ToEnvironmentKey(string propertyName)
    {
        var result = new System.Text.StringBuilder(propertyName.Length + 4);
        for (var index = 0; index < propertyName.Length; index++)
        {
            var character = propertyName[index];
            if (index > 0 && char.IsUpper(character))
            {
                result.Append('_');
            }

            result.Append(char.ToUpperInvariant(character));
        }

        return result.ToString();
    }
}

public sealed record RetentionStepResult(
    string Name,
    DateTimeOffset CutoffUtc,
    long Candidates,
    long Affected,
    long Remaining,
    bool BatchLimitReached);

public sealed record DataRetentionReport(
    bool DryRun,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    RetentionStepResult StaleAttempts,
    RetentionStepResult ExpiredChallenges,
    RetentionStepResult AbandonedAttempts,
    RetentionStepResult SeenGamificationEvents,
    BackupRetentionResult BackupPairs,
    IReadOnlyList<string> ProtectedDataSets);
