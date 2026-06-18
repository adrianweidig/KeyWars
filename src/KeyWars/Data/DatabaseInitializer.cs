using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Data;

public sealed class DatabaseInitializer(
    IServiceProvider services,
    ILogger<DatabaseInitializer> logger,
    IHostEnvironment environment)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);
        await SeedStandardTextsAsync(db, cancellationToken);
        logger.LogInformation("KeyWars-Datenbank ist bereit ({Environment}).", environment.EnvironmentName);
    }

    private static async Task SeedStandardTextsAsync(KeyWarsDbContext db, CancellationToken cancellationToken)
    {
        foreach (var standardText in GermanWordBank.StandardTexts)
        {
            var exists = await db.TrainingTexts.AnyAsync(text => text.SourceKey == standardText.Key, cancellationToken);
            if (exists)
            {
                continue;
            }

            db.TrainingTexts.Add(new TrainingText
            {
                Title = standardText.Title,
                SourceKey = standardText.Key,
                Body = TypingEngine.NormalizeText(standardText.Body),
                CharacterCount = TypingEngine.SplitGraphemes(standardText.Body).Count,
                IsStandard = true,
                RatingEligible = true,
                Visibility = TrainingTextVisibility.Organization
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
