using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record RivalGhostOption(
    Guid RivalProfileId,
    string DisplayName,
    double Wpm,
    double Accuracy);

public sealed record RivalGhostSelection(
    Guid SourceAttemptId,
    Guid TrainingTextId,
    double OwnWpm,
    double OwnAccuracy,
    IReadOnlyList<RivalGhostOption> Rivals,
    RivalGhostOption? SelectedRival,
    bool FallbackApplied);

public sealed class RivalGhostService(KeyWarsDbContext db)
{
    private const int CandidateWindow = 16;
    private const int MaximumOptions = 12;

    public async Task<RivalGhostSelection?> GetAsync(
        Guid currentProfileId,
        Guid sourceAttemptId,
        Guid? requestedRivalProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var source = await (
            from attempt in db.TypingAttempts.AsNoTracking()
            join text in db.TrainingTexts.AsNoTracking() on attempt.TrainingTextId equals text.Id
            where attempt.Id == sourceAttemptId
                && attempt.UserProfileId == currentProfileId
                && attempt.Completed
                && attempt.Phase == AttemptPhase.Finished
                && attempt.FinishedAt != null
                && attempt.Official
                && attempt.CompetitionIntegrityEligible
                && !text.IsQuarantined
                && (text.IsStandard ||
                    text.Visibility == TrainingTextVisibility.Organization ||
                    text.OwnerProfileId == currentProfileId)
            select new
            {
                attempt.Id,
                TrainingTextId = text.Id,
                attempt.Wpm,
                attempt.Accuracy
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return null;
        }

        var parameters = new List<object>();
        string Parameter(object value)
        {
            var index = parameters.Count;
            parameters.Add(value);
            return $"{{{index}}}";
        }

        var currentProfile = Parameter(DatabaseValue(currentProfileId));
        var textId = Parameter(DatabaseValue(source.TrainingTextId));
        var textMode = Parameter(TrainingMode.Text.ToString());
        var eligible = Parameter(true);
        var integrityEligible = Parameter(true);
        var official = Parameter(true);
        var completed = Parameter(true);
        var finished = Parameter(AttemptPhase.Finished.ToString());
        var minimumAccuracy = Parameter(CompetitionEligibility.MinimumAccuracy);
        var ratingEligible = Parameter(true);
        var quarantined = Parameter(false);
        var visible = Parameter(true);
        var sharingEnabled = Parameter(true);
        var deleted = Parameter(false);
        var sourceWpm = Parameter(source.Wpm);
        var candidateWindow = Parameter(CandidateWindow);
        var sql = $$"""
            WITH "RankedAttempts" AS (
                SELECT profile."Id" AS "RivalProfileId",
                       profile."DisplayName",
                       attempt."Wpm",
                       attempt."Accuracy",
                       attempt."Consistency",
                       ROW_NUMBER() OVER (
                           PARTITION BY profile."Id"
                           ORDER BY attempt."Wpm" DESC,
                                    attempt."Accuracy" DESC,
                                    attempt."Consistency" DESC,
                                    attempt."Id"
                       ) AS "ProfileRank"
                FROM "TypingAttempts" AS attempt
                INNER JOIN "UserProfiles" AS profile ON attempt."UserProfileId" = profile."Id"
                INNER JOIN "TrainingTexts" AS text ON attempt."TrainingTextId" = text."Id"
                WHERE attempt."UserProfileId" <> {{currentProfile}}
                  AND attempt."TrainingTextId" = {{textId}}
                  AND attempt."Mode" = {{textMode}}
                  AND attempt."LeaderboardEligible" = {{eligible}}
                  AND attempt."CompetitionIntegrityEligible" = {{integrityEligible}}
                  AND attempt."Official" = {{official}}
                  AND attempt."Completed" = {{completed}}
                  AND attempt."Phase" = {{finished}}
                  AND attempt."FinishedAt" IS NOT NULL
                  AND attempt."Accuracy" >= {{minimumAccuracy}}
                  AND text."RatingEligible" = {{ratingEligible}}
                  AND text."IsQuarantined" = {{quarantined}}
                  AND profile."LeaderboardVisible" = {{visible}}
                  AND profile."GhostSharingEnabled" = {{sharingEnabled}}
                  AND profile."Deleted" = {{deleted}}
            ),
            "BestPerProfile" AS (
                SELECT "RivalProfileId", "DisplayName", "Wpm", "Accuracy", "Consistency"
                FROM "RankedAttempts"
                WHERE "ProfileRank" = 1
            ),
            "CandidateWindow" AS (
                SELECT "RivalProfileId", "DisplayName", "Wpm", "Accuracy",
                       ROW_NUMBER() OVER (
                           PARTITION BY CASE WHEN "Wpm" >= {{sourceWpm}} THEN 1 ELSE 0 END
                           ORDER BY CASE WHEN "Wpm" >= {{sourceWpm}} THEN "Wpm" END,
                                    CASE WHEN "Wpm" < {{sourceWpm}} THEN "Wpm" END DESC,
                                    "Accuracy" DESC,
                                    "DisplayName",
                                    "RivalProfileId"
                       ) AS "CandidateRank"
                FROM "BestPerProfile"
            )
            SELECT "RivalProfileId", "DisplayName", "Wpm", "Accuracy"
            FROM "CandidateWindow"
            WHERE "CandidateRank" <= {{candidateWindow}}
            """;
        var rows = await db.Database
            .SqlQueryRaw<RivalCandidateRow>(sql, parameters.ToArray())
            .ToListAsync(cancellationToken);

        var rivals = rows
            .OrderBy(row => Math.Abs(row.Wpm - source.Wpm))
            .ThenByDescending(row => row.Wpm)
            .ThenByDescending(row => row.Accuracy)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RivalProfileId)
            .Take(MaximumOptions)
            .Select(row => new RivalGhostOption(
                row.RivalProfileId,
                string.IsNullOrWhiteSpace(row.DisplayName) ? "Rivale" : row.DisplayName.Trim(),
                row.Wpm,
                row.Accuracy))
            .ToArray();

        RivalGhostOption? selected = null;
        var fallbackApplied = false;
        if (requestedRivalProfileId is { } requestedId)
        {
            selected = rivals.SingleOrDefault(rival => rival.RivalProfileId == requestedId);
            if (selected is null)
            {
                selected = rivals.FirstOrDefault();
                fallbackApplied = true;
            }
        }

        return new RivalGhostSelection(
            source.Id,
            source.TrainingTextId,
            source.Wpm,
            source.Accuracy,
            rivals,
            selected,
            fallbackApplied);
    }

    private object DatabaseValue(Guid value) =>
        db.Database.IsSqlite() ? value.ToString().ToUpperInvariant() : value;

    private sealed class RivalCandidateRow
    {
        public Guid RivalProfileId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public double Wpm { get; set; }
        public double Accuracy { get; set; }
    }
}
