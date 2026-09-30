using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Data.Common;

namespace KeyWars.IntegrationTests;

public sealed class DatabaseReadModelContractTests
{
    [Fact]
    public async Task CurrentMigrationAppliesWithScaleIndexesAndRematchConstraint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        AssertIndex<TypingAttempt>(db, nameof(TypingAttempt.UserProfileId), nameof(TypingAttempt.Phase), nameof(TypingAttempt.Completed), nameof(TypingAttempt.CreatedAt), nameof(TypingAttempt.Id));
        AssertIndex<RewardLedgerEntry>(db, nameof(RewardLedgerEntry.UserProfileId), nameof(RewardLedgerEntry.AwardedAt));
        AssertIndex<Achievement>(db, nameof(Achievement.UserProfileId), nameof(Achievement.UnlockedAt));
        AssertIndex<GamificationEvent>(db, nameof(GamificationEvent.UserProfileId), nameof(GamificationEvent.CreatedAt), nameof(GamificationEvent.Id));
        AssertIndex<LiveRoomSummary>(db, nameof(LiveRoomSummary.TargetTextHash), nameof(LiveRoomSummary.Mode), nameof(LiveRoomSummary.AbortedByServer));

        var challengeType = db.Model.FindEntityType(typeof(Challenge))!;
        var rematchIndex = challengeType.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(Challenge.RematchOfChallengeId)]));
        Assert.True(rematchIndex.IsUnique);
        var rematchForeignKey = challengeType.GetForeignKeys().Single(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([nameof(Challenge.RematchOfChallengeId)]));
        Assert.Equal(DeleteBehavior.Restrict, rematchForeignKey.DeleteBehavior);
    }

    [Fact]
    public async Task IntegrityMigrationDefaultsLegacyRowsAndOldWriterInsertsToFalse()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260813093634_AddArenaPersonalBestIndex");

        var profileId = Guid.CreateVersion7();
        var textId = Guid.CreateVersion7();
        var attemptId = Guid.CreateVersion7();
        var roomId = Guid.CreateVersion7();
        var summaryId = Guid.CreateVersion7();
        var challengeId = Guid.CreateVersion7();
        var roundId = Guid.CreateVersion7();
        var resultId = Guid.CreateVersion7();
        await InsertLegacyRowAsync(connection, "UserProfiles", new Dictionary<string, object>()
        {
            ["Id"] = profileId,
            ["DirectoryObjectGuid"] = profileId.ToString("N"),
            ["DirectorySid"] = $"S-1-5-21-{profileId:N}",
            ["SamAccountName"] = $"legacy-{profileId:N}",
            ["UserPrincipalName"] = $"legacy-{profileId:N}@example.local",
            ["DisplayName"] = "Legacy"
        });
        await InsertLegacyRowAsync(connection, "TrainingTexts", new Dictionary<string, object>()
        {
            ["Id"] = textId,
            ["SourceKey"] = $"legacy-{textId:N}",
            ["Title"] = "Legacy",
            ["Body"] = "Legacy"
        });
        await InsertLegacyRowAsync(connection, "TypingAttempts", new Dictionary<string, object>()
        {
            ["Id"] = attemptId,
            ["UserProfileId"] = profileId,
            ["TrainingTextId"] = textId,
            ["Nonce"] = $"legacy-{attemptId:N}"[..32]
        });
        await InsertLegacyRowAsync(connection, "LiveRoomSummaries", new Dictionary<string, object>()
        {
            ["Id"] = roomId,
            ["CreatorProfileId"] = profileId,
            ["IdempotencyKey"] = $"legacy-{roomId:N}",
            ["RoomCode"] = "LEGACY01"
        });
        await InsertLegacyRowAsync(connection, "LiveRoomParticipantSummaries", new Dictionary<string, object>()
        {
            ["Id"] = summaryId,
            ["LiveRoomSummaryId"] = roomId,
            ["UserProfileId"] = profileId
        });
        await InsertLegacyRowAsync(connection, "Challenges", new Dictionary<string, object>()
        {
            ["Id"] = challengeId,
            ["CreatorProfileId"] = profileId,
            ["TrainingTextId"] = textId,
            ["Title"] = "Legacy"
        });
        await InsertLegacyRowAsync(connection, "ChallengeRounds", new Dictionary<string, object>()
        {
            ["Id"] = roundId,
            ["ChallengeId"] = challengeId,
            ["RoundNumber"] = 1
        });
        await InsertLegacyRowAsync(connection, "ChallengeRoundResults", new Dictionary<string, object>()
        {
            ["Id"] = resultId,
            ["ChallengeRoundId"] = roundId,
            ["UserProfileId"] = profileId
        });

        await migrator.MigrateAsync();

        Assert.Equal(0, await ScalarAsync(connection, "SELECT CompetitionIntegrityEligible FROM TypingAttempts WHERE Id = $id", attemptId));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT CompetitionEligible FROM LiveRoomParticipantSummaries WHERE Id = $id", summaryId));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT CompetitionEligible FROM ChallengeRoundResults WHERE Id = $id", resultId));

        await ReinsertWithoutColumnAsync(connection, "TypingAttempts", attemptId, "CompetitionIntegrityEligible");
        await ReinsertWithoutColumnAsync(connection, "LiveRoomParticipantSummaries", summaryId, "CompetitionEligible");
        await ReinsertWithoutColumnAsync(connection, "ChallengeRoundResults", resultId, "CompetitionEligible");
        Assert.Equal(0, await ScalarAsync(connection, "SELECT CompetitionIntegrityEligible FROM TypingAttempts WHERE Id = $id", attemptId));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT CompetitionEligible FROM LiveRoomParticipantSummaries WHERE Id = $id", summaryId));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT CompetitionEligible FROM ChallengeRoundResults WHERE Id = $id", resultId));
    }

    [Fact]
    public async Task ModerationAuditEntriesAreAppendOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<KeyWarsDbContext>().UseSqlite(connection).Options;
        await using var db = new KeyWarsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var entry = new ContentModerationAuditEntry
        {
            ActorProfileId = Guid.CreateVersion7(),
            ActorDisplayName = "Admin Test",
            TargetType = ContentModerationTargetType.TrainingText,
            TargetId = Guid.CreateVersion7(),
            TargetOwnerProfileId = Guid.CreateVersion7(),
            TargetTitle = "Prüftext",
            Action = ContentModerationAction.Quarantine,
            Reason = "Nachvollziehbarer Testgrund",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ContentModerationAuditEntries.Add(entry);
        await db.SaveChangesAsync();

        entry.Reason = "Nachträgliche Manipulation";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("unveränderlich", exception.Message);
    }

    [Fact]
    public void PostgreSqlUsesAnIndependentProviderNativeInitialMigration()
    {
        var options = new DbContextOptionsBuilder<PostgresKeyWarsDbContext>()
            .UseNpgsql("Host=localhost;Database=keywars_contract;Username=keywars")
            .Options;
        using var db = new PostgresKeyWarsDbContext(options);

        var migrations = db.Database.GetMigrations().ToArray();
        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Collection(
            migrations,
            migration => Assert.EndsWith("_InitialPostgresV05", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddSeasonScoring", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddArenaPersonalBestIndex", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddCompetitionIntegrityEligibility", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddChallengeTargetSnapshot", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddLocalCompletionOutbox", migration, StringComparison.Ordinal));
        Assert.Contains("CREATE TABLE \"UserProfiles\"", script, StringComparison.Ordinal);
        Assert.Contains("timestamp with time zone", script, StringComparison.Ordinal);
        Assert.Contains("uuid", script, StringComparison.Ordinal);
        Assert.Contains("\"CompetitionIntegrityEligible\" boolean NOT NULL DEFAULT FALSE", script, StringComparison.Ordinal);
        Assert.Contains("\"CompetitionEligible\" boolean NOT NULL DEFAULT FALSE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlite_master", script, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertIndex<TEntity>(KeyWarsDbContext db, params string[] propertyNames)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity))!;
        Assert.Contains(entityType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static async Task InsertLegacyRowAsync(
        DbConnection connection,
        string table,
        IReadOnlyDictionary<string, object> values)
    {
        var columns = await ReadColumnsAsync(connection, table);
        var included = columns
            .Where(column => values.ContainsKey(column.Name) || (column.Required && column.DefaultValue is null))
            .ToArray();
        await using var command = connection.CreateCommand();
        var names = new List<string>(included.Length);
        var parameters = new List<string>(included.Length);
        for (var index = 0; index < included.Length; index++)
        {
            var column = included[index];
            names.Add(QuoteIdentifier(column.Name));
            var parameterName = $"$p{index}";
            parameters.Add(parameterName);
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = values.TryGetValue(column.Name, out var value)
                ? DatabaseValue(value)
                : FallbackValue(column);
            command.Parameters.Add(parameter);
        }

        command.CommandText = $"INSERT INTO {QuoteIdentifier(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameters)});";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReinsertWithoutColumnAsync(
        DbConnection connection,
        string table,
        Guid id,
        string excludedColumn)
    {
        var columns = (await ReadColumnsAsync(connection, table))
            .Where(column => column.Name != excludedColumn)
            .Select(column => QuoteIdentifier(column.Name))
            .ToArray();
        var projection = string.Join(", ", columns);
        var temporary = QuoteIdentifier($"integrity_copy_{table}");
        await ExecuteAsync(connection, $"CREATE TEMP TABLE {temporary} AS SELECT {projection} FROM {QuoteIdentifier(table)} WHERE Id = $id;", id);
        await ExecuteAsync(connection, $"DELETE FROM {QuoteIdentifier(table)} WHERE Id = $id;", id);
        await ExecuteAsync(connection, $"INSERT INTO {QuoteIdentifier(table)} ({projection}) SELECT {projection} FROM {temporary};");
        await ExecuteAsync(connection, $"DROP TABLE {temporary};");
    }

    private static async Task<IReadOnlyList<TableColumn>> ReadColumnsAsync(DbConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<TableColumn>();
        while (await reader.ReadAsync())
        {
            columns.Add(new TableColumn(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0 || reader.GetInt64(5) != 0,
                reader.IsDBNull(4) ? null : reader.GetValue(4)));
        }

        return columns;
    }

    private static async Task<int> ScalarAsync(DbConnection connection, string sql, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = id.ToString();
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, Guid? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is { } value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = value.ToString();
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static object DatabaseValue(object value) => value switch
    {
        Guid guid => guid.ToString(),
        DateTimeOffset timestamp => timestamp.ToString("O"),
        _ => value
    };

    private static object FallbackValue(TableColumn column)
    {
        if (column.Type.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (column.Type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
            column.Type.Contains("DOUBLE", StringComparison.OrdinalIgnoreCase) ||
            column.Type.Contains("FLOAT", StringComparison.OrdinalIgnoreCase))
        {
            return 0d;
        }

        return column.Name.EndsWith("At", StringComparison.Ordinal)
            ? "2026-08-13T00:00:00.0000000+00:00"
            : column.Name switch
            {
                "Status" => ParticipantStatus.Finished.ToString(),
                "Phase" => AttemptPhase.Finished.ToString(),
                "Mode" => TrainingMode.Text.ToString(),
                "Visibility" => TrainingTextVisibility.Organization.ToString(),
                _ => "legacy"
            };
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record TableColumn(string Name, string Type, bool Required, object? DefaultValue);
}
