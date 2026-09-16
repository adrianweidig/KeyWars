using KeyWars.Services;

namespace KeyWars.UnitTests;

public sealed class SeasonServiceTests
{
    [Theory]
    [InlineData("2026-01-01T00:00:00Z", 1, "2026-01", "2026-01-01T00:00:00+00:00", "2026-02-01T00:00:00+00:00")]
    [InlineData("2026-06-30T23:59:59Z", 3, "2026-04_2026-06", "2026-04-01T00:00:00+00:00", "2026-07-01T00:00:00+00:00")]
    [InlineData("2026-12-31T23:59:59Z", 12, "2026-01_2026-12", "2026-01-01T00:00:00+00:00", "2027-01-01T00:00:00+00:00")]
    public void ResolveWindowUsesStableUtcBoundaries(
        string instant,
        int months,
        string key,
        string expectedStart,
        string expectedEnd)
    {
        var window = SeasonService.ResolveWindow(DateTimeOffset.Parse(instant), months);

        Assert.Equal(key, window.Key);
        Assert.Equal(DateTimeOffset.Parse(expectedStart), window.StartsAt);
        Assert.Equal(DateTimeOffset.Parse(expectedEnd), window.EndsAt);
    }

    [Fact]
    public void OptionsRejectPeriodsThatDoNotPartitionTheYear()
    {
        var options = new SeasonOptions { MonthsPerSeason = 5 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ProductSeasonWritersUseProviderAwareFenceOrdering()
    {
        var fenceSource = File.ReadAllText(FindSource("Services/SeasonWriteFence.cs"));
        Assert.Contains("db.Database.IsNpgsql()", fenceSource, StringComparison.Ordinal);
        Assert.Contains("db.Database.CurrentTransaction is null", fenceSource, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_xact_lock", fenceSource, StringComparison.Ordinal);

        AssertFenceOrder(
            File.ReadAllText(FindSource("Services/AttemptService.cs")),
            "public async Task<AttemptCompletion> FinishAsync");
        AssertFenceOrder(
            File.ReadAllText(FindSource("Services/ChallengeService.cs")),
            "public async Task<AttemptCompletion> FinishAttemptAsync");
        AssertFenceOrder(
            File.ReadAllText(FindSource("Services/LiveRoomCompletionQueue.cs")),
            "private async Task PersistOnceAsync");
        AssertFenceOrder(
            File.ReadAllText(FindSource("Services/SeasonService.cs")),
            "public async Task<Season> EnsureCurrentAsync");
    }

    private static void AssertFenceOrder(string source, string methodMarker)
    {
        var methodStart = source.IndexOf(methodMarker, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Methode nicht gefunden: {methodMarker}");
        var sqliteFence = source.IndexOf("sqlite", methodStart, StringComparison.OrdinalIgnoreCase);
        var transaction = source.IndexOf("BeginTransactionAsync", methodStart, StringComparison.Ordinal);
        var postgresFence = source.IndexOf("postgres", transaction, StringComparison.OrdinalIgnoreCase);
        Assert.True(sqliteFence >= methodStart && sqliteFence < transaction);
        Assert.True(transaction >= 0 && transaction < postgresFence);
    }

    private static string FindSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
    {
        var workingTreeCandidate = Path.Combine(
            Directory.GetCurrentDirectory(),
            "src",
            "KeyWars",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(workingTreeCandidate))
        {
            return workingTreeCandidate;
        }

        var current = new DirectoryInfo(
            Path.GetDirectoryName(callerFile) ?? AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "KeyWars",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
