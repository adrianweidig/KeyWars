using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Services;

public sealed record LeaderboardQuery(
    CompetitionBoardKind Board,
    CompetitionPeriod Period,
    TrainingMode Mode,
    Guid? TextId);

public sealed record CompetitionTextOption(Guid Id, string Title, int CharacterCount);

public sealed record LeaderboardBoard(
    CompetitionBoardKind Kind,
    CompetitionPeriod Period,
    string Title,
    string Description,
    string PrimaryMetricLabel,
    IReadOnlyList<LeaderboardEntry> Entries,
    LeaderboardEntry? OwnEntry,
    LeaderboardEntry? NextTarget,
    string EmptyMessage);

public sealed record CompetitionOverview(
    LeaderboardQuery Query,
    bool CurrentProfileVisible,
    string CurrentDivision,
    string PersonalBest,
    IReadOnlyList<CompetitionTextOption> TextOptions,
    LeaderboardBoard Board);

public sealed record LeaderboardEntry
{
    public int Rank { get; init; }
    public Guid UserProfileId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Initials { get; init; } = "";
    public string PrimaryValue { get; init; } = "";
    public string Context { get; init; } = "";
    public string Detail { get; init; } = "";
    public double Score { get; init; }
    public double Wpm { get; init; }
    public double Accuracy { get; init; }
    public double Consistency { get; init; }
    public int Attempts { get; init; }
    public int Wins { get; init; }
    public int Podiums { get; init; }
    public int ArenaRating { get; init; }
    public int RatingDelta { get; init; }
    public int Level { get; init; }
    public int Xp { get; init; }
    public int StreakDays { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public TrainingMode? Mode { get; init; }
    public Guid? TrainingTextId { get; init; }
    public bool IsCurrentUser { get; init; }
    public bool IsPrivatePreview { get; init; }
}

public sealed class CompetitionLeaderboardService(KeyWarsDbContext db, TimeProvider timeProvider)
{
    private const int PublicLimit = 100;

    public async Task<CompetitionOverview> GetAsync(UserProfile currentProfile, LeaderboardQuery query, CancellationToken cancellationToken = default)
    {
        var textOptions = await ReadTextOptionsAsync(cancellationToken);
        var normalized = Normalize(query, textOptions);
        var board = normalized.Board switch
        {
            CompetitionBoardKind.Sprint => await BuildAttemptBoardAsync(currentProfile, normalized, textOptions, false, cancellationToken),
            CompetitionBoardKind.Text => await BuildAttemptBoardAsync(currentProfile, normalized, textOptions, true, cancellationToken),
            CompetitionBoardKind.Challenge => await BuildChallengeBoardAsync(currentProfile, normalized, cancellationToken),
            CompetitionBoardKind.Xp => await BuildXpBoardAsync(currentProfile, normalized, cancellationToken),
            _ => await BuildArenaBoardAsync(currentProfile, normalized, cancellationToken)
        };

        return new CompetitionOverview(
            normalized,
            currentProfile.LeaderboardVisible && !currentProfile.Deleted,
            BuildDivision(currentProfile.ArenaRating),
            await ReadPersonalBestAsync(currentProfile.Id, cancellationToken),
            textOptions,
            board);
    }

    public static IReadOnlyList<LeaderboardEntry> RankEntries(IEnumerable<LeaderboardEntry> entries)
    {
        return entries
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Wpm)
            .ThenByDescending(entry => entry.Accuracy)
            .ThenByDescending(entry => entry.Consistency)
            .ThenBy(entry => entry.FinishedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(entry => entry.DisplayName)
            .ThenBy(entry => entry.UserProfileId)
            .Select((entry, index) => entry with { Rank = index + 1 })
            .ToList();
    }

    private async Task<LeaderboardBoard> BuildArenaBoardAsync(UserProfile currentProfile, LeaderboardQuery query, CancellationToken cancellationToken)
    {
        var profiles = await VisibleProfiles().ToListAsync(cancellationToken);
        var profileIds = profiles.Select(profile => profile.Id).ToHashSet();
        var stats = await ReadArenaStatsAsync(profileIds, query.Period, cancellationToken);
        var ranked = RankEntries(profiles.Select(profile =>
        {
            var stat = stats.GetValueOrDefault(profile.Id);
            return new LeaderboardEntry
            {
                UserProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                Initials = BuildInitials(profile.DisplayName),
                PrimaryValue = profile.ArenaRating.ToString("N0"),
                Context = $"{BuildDivision(profile.ArenaRating)} · {profile.RatedMatchCount:N0} gewertete Rennen",
                Detail = stat is null ? "Noch keine Rennen im Zeitraum" : $"{stat.Attempts:N0} Rennen · {stat.Wins:N0} Siege · Ø {stat.AverageWpm:0.0} WPM",
                Score = profile.ArenaRating,
                Wpm = stat?.AverageWpm ?? 0,
                Accuracy = stat?.AverageAccuracy ?? 0,
                Attempts = stat?.Attempts ?? 0,
                Wins = stat?.Wins ?? 0,
                Podiums = stat?.Podiums ?? 0,
                ArenaRating = profile.ArenaRating,
                RatingDelta = stat?.RatingDelta ?? 0,
                IsCurrentUser = profile.Id == currentProfile.Id
            };
        }));

        var privateEntry = currentProfile.LeaderboardVisible ? null : await BuildPrivateArenaEntryAsync(currentProfile, query.Period, cancellationToken);
        return BuildBoard(
            CompetitionBoardKind.ArenaRating,
            query.Period,
            "Arena-Rating",
            "Rating, Siege und Podien aus abgeschlossenen Live-Rennen.",
            "Rating",
            ranked,
            currentProfile,
            privateEntry,
            "Noch keine sichtbaren Arena-Ergebnisse.");
    }

    private async Task<LeaderboardBoard> BuildAttemptBoardAsync(
        UserProfile currentProfile,
        LeaderboardQuery query,
        IReadOnlyList<CompetitionTextOption> textOptions,
        bool textBoard,
        CancellationToken cancellationToken)
    {
        var candidates = await ReadAttemptCandidatesAsync(query, textBoard, includeHiddenCurrentProfileId: null, cancellationToken);
        var best = BestAttemptPerProfile(candidates);
        var ranked = RankEntries(best.Select(candidate => ToAttemptEntry(candidate, currentProfile.Id, false)));
        LeaderboardEntry? privateEntry = null;
        if (!currentProfile.LeaderboardVisible)
        {
            var privateCandidates = await ReadAttemptCandidatesAsync(query, textBoard, currentProfile.Id, cancellationToken);
            privateEntry = BestAttemptPerProfile(privateCandidates)
                .Select(candidate => ToAttemptEntry(candidate, currentProfile.Id, true))
                .FirstOrDefault();
        }

        var textTitle = textBoard
            ? textOptions.FirstOrDefault(text => text.Id == query.TextId)?.Title ?? "Text-Bestleistungen"
            : DisplayNames.For(query.Mode);
        return BuildBoard(
            textBoard ? CompetitionBoardKind.Text : CompetitionBoardKind.Sprint,
            query.Period,
            textBoard ? textTitle : $"Bestwerte: {DisplayNames.For(query.Mode)}",
            textBoard ? "Gleicher Text, bestes gültiges Ergebnis pro Person." : "Standardisierter Modus, bestes gültiges Ergebnis pro Person.",
            "WPM",
            ranked,
            currentProfile,
            privateEntry,
            textBoard ? "Für diesen Text gibt es noch keine sichtbaren Bestleistungen." : "Für diesen Modus gibt es noch keine sichtbaren Bestleistungen.");
    }

    private async Task<LeaderboardBoard> BuildChallengeBoardAsync(UserProfile currentProfile, LeaderboardQuery query, CancellationToken cancellationToken)
    {
        var candidates = await ReadChallengeCandidatesAsync(query, includeHiddenCurrentProfileId: null, cancellationToken);
        var ranked = RankEntries(BestChallengePerProfile(candidates).Select(candidate => ToChallengeEntry(candidate, currentProfile.Id, false)));
        LeaderboardEntry? privateEntry = null;
        if (!currentProfile.LeaderboardVisible)
        {
            var privateCandidates = await ReadChallengeCandidatesAsync(query, currentProfile.Id, cancellationToken);
            privateEntry = BestChallengePerProfile(privateCandidates)
                .Select(candidate => ToChallengeEntry(candidate, currentProfile.Id, true))
                .FirstOrDefault();
        }

        return BuildBoard(
            CompetitionBoardKind.Challenge,
            query.Period,
            "Challenge-Bestleistungen",
            "Beste abgeschlossene Gruppenherausforderungen mit Platzierung.",
            "WPM",
            ranked,
            currentProfile,
            privateEntry,
            "Noch keine sichtbaren Challenge-Ergebnisse.");
    }

    private async Task<LeaderboardBoard> BuildXpBoardAsync(UserProfile currentProfile, LeaderboardQuery query, CancellationToken cancellationToken)
    {
        var profiles = await VisibleProfiles().ToListAsync(cancellationToken);
        var profileIds = profiles.Select(profile => profile.Id).ToHashSet();
        var missionCounts = await db.Missions
            .AsNoTracking()
            .Where(mission => profileIds.Contains(mission.UserProfileId) && mission.Completed)
            .GroupBy(mission => mission.UserProfileId)
            .Select(group => new { UserProfileId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserProfileId, item => item.Count, cancellationToken);
        var recentXp = await ReadRecentXpAsync(profileIds, query.Period, cancellationToken);

        var ranked = RankEntries(profiles.Select(profile =>
        {
            var periodXp = recentXp.GetValueOrDefault(profile.Id);
            return new LeaderboardEntry
            {
                UserProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                Initials = BuildInitials(profile.DisplayName),
                PrimaryValue = $"Level {profile.Level:N0}",
                Context = $"{profile.ExperiencePoints:N0} XP · {profile.CurrentStreakDays:N0} Tage Serie",
                Detail = $"{missionCounts.GetValueOrDefault(profile.Id):N0} Ziele · +{periodXp:N0} XP im Zeitraum",
                Score = profile.ExperiencePoints,
                Level = profile.Level,
                Xp = profile.ExperiencePoints,
                StreakDays = profile.CurrentStreakDays,
                Attempts = missionCounts.GetValueOrDefault(profile.Id),
                IsCurrentUser = profile.Id == currentProfile.Id
            };
        }));

        LeaderboardEntry? privateEntry = null;
        if (!currentProfile.LeaderboardVisible)
        {
            var currentXp = await ReadRecentXpAsync([currentProfile.Id], query.Period, cancellationToken);
            var missions = await db.Missions.AsNoTracking().CountAsync(item => item.UserProfileId == currentProfile.Id && item.Completed, cancellationToken);
            privateEntry = new LeaderboardEntry
            {
                UserProfileId = currentProfile.Id,
                DisplayName = currentProfile.DisplayName,
                Initials = BuildInitials(currentProfile.DisplayName),
                PrimaryValue = $"Level {currentProfile.Level:N0}",
                Context = $"{currentProfile.ExperiencePoints:N0} XP · {currentProfile.CurrentStreakDays:N0} Tage Serie",
                Detail = $"{missions:N0} Ziele · +{currentXp.GetValueOrDefault(currentProfile.Id):N0} XP im Zeitraum",
                Score = currentProfile.ExperiencePoints,
                Level = currentProfile.Level,
                Xp = currentProfile.ExperiencePoints,
                StreakDays = currentProfile.CurrentStreakDays,
                Attempts = missions,
                IsCurrentUser = true,
                IsPrivatePreview = true
            };
        }

        return BuildBoard(
            CompetitionBoardKind.Xp,
            query.Period,
            "Level und XP",
            "Motivation, Streaks und abgeschlossene Ziele.",
            "Level",
            ranked,
            currentProfile,
            privateEntry,
            "Noch keine sichtbaren XP-Daten.");
    }

    private LeaderboardBoard BuildBoard(
        CompetitionBoardKind kind,
        CompetitionPeriod period,
        string title,
        string description,
        string primaryMetricLabel,
        IReadOnlyList<LeaderboardEntry> ranked,
        UserProfile currentProfile,
        LeaderboardEntry? privateEntry,
        string emptyMessage)
    {
        var top = ranked.Take(PublicLimit).ToList();
        var ownEntry = currentProfile.LeaderboardVisible
            ? ranked.FirstOrDefault(entry => entry.UserProfileId == currentProfile.Id)
            : privateEntry;
        var nextTarget = ownEntry is { Rank: > 1 } ? ranked.FirstOrDefault(entry => entry.Rank == ownEntry.Rank - 1) : null;
        return new LeaderboardBoard(kind, period, title, description, primaryMetricLabel, top, ownEntry, nextTarget, emptyMessage);
    }

    private async Task<IReadOnlyList<CompetitionTextOption>> ReadTextOptionsAsync(CancellationToken cancellationToken)
    {
        return await db.TrainingTexts
            .AsNoTracking()
            .Where(text => text.RatingEligible && (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization))
            .OrderBy(text => text.Title)
            .ThenBy(text => text.Id)
            .Select(text => new CompetitionTextOption(text.Id, text.Title, text.CharacterCount))
            .ToListAsync(cancellationToken);
    }

    private LeaderboardQuery Normalize(LeaderboardQuery query, IReadOnlyList<CompetitionTextOption> textOptions)
    {
        var board = Enum.IsDefined(query.Board) ? query.Board : CompetitionBoardKind.ArenaRating;
        var period = Enum.IsDefined(query.Period) ? query.Period : CompetitionPeriod.Day;
        var mode = CompetitionEligibility.IsStandardizedMode(query.Mode) ? query.Mode : TrainingMode.Sprint60;
        var textId = query.TextId;
        if (board == CompetitionBoardKind.Text && (textId is null || textOptions.All(text => text.Id != textId)))
        {
            textId = textOptions.FirstOrDefault()?.Id;
        }

        return new LeaderboardQuery(board, period, mode, textId);
    }

    private IQueryable<UserProfile> VisibleProfiles() =>
        db.UserProfiles.AsNoTracking().Where(profile => profile.LeaderboardVisible && !profile.Deleted);

    private async Task<IReadOnlyDictionary<Guid, ArenaStat>> ReadArenaStatsAsync(HashSet<Guid> profileIds, CompetitionPeriod period, CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0)
        {
            return new Dictionary<Guid, ArenaStat>();
        }

        var start = PeriodStart(period);
        var query =
            from participant in db.LiveRoomParticipantSummaries.AsNoTracking()
            join room in db.LiveRoomSummaries.AsNoTracking() on participant.LiveRoomSummaryId equals room.Id
            where profileIds.Contains(participant.UserProfileId)
                && participant.Status == ParticipantStatus.Finished
                && room.FinishedAt != null
                && !room.AbortedByServer
            select new
            {
                participant.UserProfileId,
                participant.Placement,
                participant.Wpm,
                participant.Accuracy,
                participant.RatingBefore,
                participant.RatingAfter,
                room.FinishedAt
            };
        var rows = await query
            .Select(row => new ArenaResultRow(
                row.UserProfileId,
                row.Placement,
                row.Wpm,
                row.Accuracy,
                row.RatingAfter - row.RatingBefore,
                row.FinishedAt!.Value))
            .ToListAsync(cancellationToken);
        if (start is { } startValue)
        {
            rows = rows.Where(row => row.FinishedAt >= startValue).ToList();
        }

        return rows
            .GroupBy(row => row.UserProfileId)
            .ToDictionary(
                group => group.Key,
                group => new ArenaStat(
                    group.Count(),
                    group.Count(row => row.Placement == 1),
                    group.Count(row => row.Placement is > 0 and <= 3),
                    group.Average(row => row.Wpm),
                    group.Average(row => row.Accuracy),
                    (int)Math.Round(group.Sum(row => row.RatingDelta))));
    }

    private async Task<LeaderboardEntry?> BuildPrivateArenaEntryAsync(UserProfile profile, CompetitionPeriod period, CancellationToken cancellationToken)
    {
        var stats = await ReadArenaStatsAsync([profile.Id], period, cancellationToken);
        var stat = stats.GetValueOrDefault(profile.Id);
        return new LeaderboardEntry
        {
            UserProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            Initials = BuildInitials(profile.DisplayName),
            PrimaryValue = profile.ArenaRating.ToString("N0"),
            Context = $"{BuildDivision(profile.ArenaRating)} · privat ausgeblendet",
            Detail = stat is null ? "Noch keine Rennen im Zeitraum" : $"{stat.Attempts:N0} Rennen · {stat.Wins:N0} Siege · Ø {stat.AverageWpm:0.0} WPM",
            Score = profile.ArenaRating,
            Wpm = stat?.AverageWpm ?? 0,
            Accuracy = stat?.AverageAccuracy ?? 0,
            Attempts = stat?.Attempts ?? 0,
            Wins = stat?.Wins ?? 0,
            Podiums = stat?.Podiums ?? 0,
            ArenaRating = profile.ArenaRating,
            IsCurrentUser = true,
            IsPrivatePreview = true
        };
    }

    private async Task<IReadOnlyList<AttemptCandidate>> ReadAttemptCandidatesAsync(
        LeaderboardQuery query,
        bool textBoard,
        Guid? includeHiddenCurrentProfileId,
        CancellationToken cancellationToken)
    {
        var start = PeriodStart(query.Period);
        var visibleProfiles = db.UserProfiles.AsNoTracking().Where(profile => !profile.Deleted && (profile.LeaderboardVisible || profile.Id == includeHiddenCurrentProfileId));
        var attempts =
            from attempt in db.TypingAttempts.AsNoTracking()
            join profile in visibleProfiles on attempt.UserProfileId equals profile.Id
            join text in db.TrainingTexts.AsNoTracking() on attempt.TrainingTextId equals text.Id into textJoin
            from text in textJoin.DefaultIfEmpty()
            where attempt.LeaderboardEligible
                && attempt.Official
                && attempt.Completed
                && attempt.Phase == AttemptPhase.Finished
                && attempt.Accuracy >= CompetitionEligibility.MinimumAccuracy
                && !profile.Deleted
            select new { attempt, profile, text };
        attempts = textBoard
            ? attempts.Where(row => row.attempt.Mode == TrainingMode.Text && row.text != null && row.text.RatingEligible && row.attempt.TrainingTextId == query.TextId)
            : attempts.Where(row => row.attempt.Mode == query.Mode);

        var rows = await attempts.ToListAsync(cancellationToken);
        if (start is { } startValue)
        {
            rows = rows.Where(row => (row.attempt.FinishedAt ?? row.attempt.CreatedAt) >= startValue).ToList();
        }

        return rows.Select(row => new AttemptCandidate(
                row.profile.Id,
                row.profile.DisplayName,
                row.attempt.Id,
                row.attempt.Mode,
                row.attempt.TrainingTextId,
                row.text?.Title ?? DisplayNames.For(row.attempt.Mode),
                row.attempt.Wpm,
                row.attempt.Accuracy,
                row.attempt.Consistency,
                row.attempt.DurationMilliseconds,
                row.attempt.FinishedAt ?? row.attempt.CreatedAt,
                row.attempt.CorrectCharacters))
            .ToList();
    }

    private static IReadOnlyList<AttemptCandidate> BestAttemptPerProfile(IEnumerable<AttemptCandidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.UserProfileId)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Wpm)
                .ThenByDescending(candidate => candidate.Accuracy)
                .ThenByDescending(candidate => candidate.Consistency)
                .ThenBy(candidate => candidate.FinishedAt)
                .ThenBy(candidate => candidate.DisplayName)
                .First())
            .ToList();

    private static LeaderboardEntry ToAttemptEntry(AttemptCandidate candidate, Guid currentProfileId, bool privatePreview) => new()
    {
        UserProfileId = candidate.UserProfileId,
        DisplayName = candidate.DisplayName,
        Initials = BuildInitials(candidate.DisplayName),
        PrimaryValue = $"{candidate.Wpm:0.0}",
        Context = candidate.Context,
        Detail = $"{candidate.Accuracy:0.0} % Genauigkeit · {candidate.Consistency:0.0} % Rhythmus",
        Score = candidate.Wpm,
        Wpm = candidate.Wpm,
        Accuracy = candidate.Accuracy,
        Consistency = candidate.Consistency,
        Attempts = 1,
        FinishedAt = candidate.FinishedAt,
        Mode = candidate.Mode,
        TrainingTextId = candidate.TrainingTextId,
        IsCurrentUser = candidate.UserProfileId == currentProfileId,
        IsPrivatePreview = privatePreview
    };

    private async Task<IReadOnlyList<ChallengeCandidate>> ReadChallengeCandidatesAsync(
        LeaderboardQuery query,
        Guid? includeHiddenCurrentProfileId,
        CancellationToken cancellationToken)
    {
        var start = PeriodStart(query.Period);
        var visibleProfiles = db.UserProfiles.AsNoTracking().Where(profile => !profile.Deleted && (profile.LeaderboardVisible || profile.Id == includeHiddenCurrentProfileId));
        var queryRows =
            from result in db.ChallengeRoundResults.AsNoTracking()
            join round in db.ChallengeRounds.AsNoTracking() on result.ChallengeRoundId equals round.Id
            join challenge in db.Challenges.AsNoTracking() on round.ChallengeId equals challenge.Id
            join profile in visibleProfiles on result.UserProfileId equals profile.Id
            where result.Status == ParticipantStatus.Finished
                && result.FinishedAt != null
                && result.Accuracy >= CompetitionEligibility.MinimumAccuracy
            select new { result, challenge, profile };
        var rows = await queryRows
            .Select(row => new ChallengeCandidate(
                row.profile.Id,
                row.profile.DisplayName,
                row.challenge.Title,
                row.result.Wpm,
                row.result.Accuracy,
                row.result.Consistency,
                row.result.Placement,
                row.result.FinishedAt!.Value))
            .ToListAsync(cancellationToken);
        if (start is { } startValue)
        {
            rows = rows.Where(row => row.FinishedAt >= startValue).ToList();
        }

        return rows;
    }

    private static IReadOnlyList<ChallengeCandidate> BestChallengePerProfile(IEnumerable<ChallengeCandidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.UserProfileId)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Wpm)
                .ThenByDescending(candidate => candidate.Accuracy)
                .ThenByDescending(candidate => candidate.Consistency)
                .ThenBy(candidate => candidate.FinishedAt)
                .ThenBy(candidate => candidate.DisplayName)
                .First())
            .ToList();

    private static LeaderboardEntry ToChallengeEntry(ChallengeCandidate candidate, Guid currentProfileId, bool privatePreview) => new()
    {
        UserProfileId = candidate.UserProfileId,
        DisplayName = candidate.DisplayName,
        Initials = BuildInitials(candidate.DisplayName),
        PrimaryValue = $"{candidate.Wpm:0.0}",
        Context = candidate.ChallengeTitle,
        Detail = $"{candidate.Accuracy:0.0} % Genauigkeit · {FormatChallengePlacement(candidate.Placement)}",
        Score = candidate.Wpm,
        Wpm = candidate.Wpm,
        Accuracy = candidate.Accuracy,
        Consistency = candidate.Consistency,
        FinishedAt = candidate.FinishedAt,
        IsCurrentUser = candidate.UserProfileId == currentProfileId,
        IsPrivatePreview = privatePreview
    };

    private static string FormatChallengePlacement(int? placement) =>
        placement is null ? "Platz offen" : $"Platz {placement.Value:N0}";

    private async Task<IReadOnlyDictionary<Guid, int>> ReadRecentXpAsync(HashSet<Guid> profileIds, CompetitionPeriod period, CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var start = PeriodStart(period);
        var rows = await db.RewardLedgerEntries
            .AsNoTracking()
            .Where(entry => profileIds.Contains(entry.UserProfileId))
            .ToListAsync(cancellationToken);
        if (start is { } startValue)
        {
            rows = rows.Where(entry => entry.AwardedAt >= startValue).ToList();
        }

        return rows
            .GroupBy(entry => entry.UserProfileId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Xp));
    }

    private async Task<string> ReadPersonalBestAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var standardizedModes = CompetitionEligibility.StandardizedModes;
        var best = await db.TypingAttempts
            .AsNoTracking()
            .Where(attempt =>
                attempt.UserProfileId == profileId &&
                attempt.LeaderboardEligible &&
                attempt.Official &&
                attempt.Completed &&
                attempt.Phase == AttemptPhase.Finished &&
                attempt.Accuracy >= CompetitionEligibility.MinimumAccuracy &&
                (standardizedModes.Contains(attempt.Mode) || attempt.Mode == TrainingMode.Text))
            .OrderByDescending(attempt => attempt.Wpm)
            .Select(attempt => (double?)attempt.Wpm)
            .FirstOrDefaultAsync(cancellationToken);
        return best is null ? "-" : $"{best:0.0} WPM";
    }

    private DateTimeOffset? PeriodStart(CompetitionPeriod period)
    {
        var now = timeProvider.GetUtcNow();
        return period switch
        {
            CompetitionPeriod.Day => now.AddDays(-1),
            CompetitionPeriod.Week => now.AddDays(-7),
            CompetitionPeriod.Month => now.AddDays(-30),
            _ => null
        };
    }

    private static string BuildInitials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "KW" : string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    private static string BuildDivision(int rating) => rating switch
    {
        >= 1300 => "Diamant",
        >= 1200 => "Platin",
        >= 1100 => "Gold",
        >= 1050 => "Silber",
        _ => "Bronze"
    };

    private sealed record ArenaResultRow(Guid UserProfileId, int? Placement, double Wpm, double Accuracy, double RatingDelta, DateTimeOffset FinishedAt);

    private sealed record ArenaStat(int Attempts, int Wins, int Podiums, double AverageWpm, double AverageAccuracy, int RatingDelta);

    private sealed record AttemptCandidate(
        Guid UserProfileId,
        string DisplayName,
        Guid AttemptId,
        TrainingMode Mode,
        Guid? TrainingTextId,
        string Context,
        double Wpm,
        double Accuracy,
        double Consistency,
        int DurationMilliseconds,
        DateTimeOffset FinishedAt,
        int CorrectCharacters);

    private sealed record ChallengeCandidate(
        Guid UserProfileId,
        string DisplayName,
        string ChallengeTitle,
        double Wpm,
        double Accuracy,
        double Consistency,
        int? Placement,
        DateTimeOffset FinishedAt);
}
