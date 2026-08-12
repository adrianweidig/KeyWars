using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace KeyWars.Services;

public sealed record LeaderboardQuery(
    CompetitionBoardKind Board,
    CompetitionPeriod Period,
    TrainingMode Mode,
    Guid? TextId,
    bool OwnDepartmentOnly = false);

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
        var normalized = Normalize(query, textOptions, currentProfile);
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
            ArenaDivision.NameFor(currentProfile.ArenaRating),
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
        var profiles = await VisibleProfiles(currentProfile, query)
            .OrderByDescending(profile => profile.ArenaRating)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .Take(PublicLimit)
            .ToListAsync(cancellationToken);
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
                Context = $"{ArenaDivision.NameFor(profile.ArenaRating)} · {profile.RatedMatchCount:N0} gewertete Rennen",
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
        var candidates = await ReadAttemptCandidatesAsync(query, textBoard, currentProfile, privateProfileId: null, cancellationToken);
        var ranked = RankEntries(candidates.Select(candidate => ToAttemptEntry(candidate, currentProfile.Id, false)));
        LeaderboardEntry? privateEntry = null;
        if (!currentProfile.LeaderboardVisible)
        {
            var privateCandidates = await ReadAttemptCandidatesAsync(query, textBoard, currentProfile, currentProfile.Id, cancellationToken);
            privateEntry = privateCandidates
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
        var candidates = await ReadChallengeCandidatesAsync(query, currentProfile, privateProfileId: null, cancellationToken);
        var ranked = RankEntries(candidates.Select(candidate => ToChallengeEntry(candidate, currentProfile.Id, false)));
        LeaderboardEntry? privateEntry = null;
        if (!currentProfile.LeaderboardVisible)
        {
            var privateCandidates = await ReadChallengeCandidatesAsync(query, currentProfile, currentProfile.Id, cancellationToken);
            privateEntry = privateCandidates
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
        var profiles = await VisibleProfiles(currentProfile, query)
            .OrderByDescending(profile => profile.ExperiencePoints)
            .ThenBy(profile => profile.DisplayName)
            .ThenBy(profile => profile.Id)
            .Take(PublicLimit)
            .ToListAsync(cancellationToken);
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

    private LeaderboardQuery Normalize(LeaderboardQuery query, IReadOnlyList<CompetitionTextOption> textOptions, UserProfile currentProfile)
    {
        var board = Enum.IsDefined(query.Board) ? query.Board : CompetitionBoardKind.ArenaRating;
        var period = Enum.IsDefined(query.Period) ? query.Period : CompetitionPeriod.Day;
        var mode = CompetitionEligibility.IsStandardizedMode(query.Mode) ? query.Mode : TrainingMode.Sprint60;
        var textId = query.TextId;
        if (board == CompetitionBoardKind.Text && (textId is null || textOptions.All(text => text.Id != textId)))
        {
            textId = textOptions.FirstOrDefault()?.Id;
        }

        var ownDepartmentOnly = query.OwnDepartmentOnly && !string.IsNullOrWhiteSpace(currentProfile.Department);
        return new LeaderboardQuery(board, period, mode, textId, ownDepartmentOnly);
    }

    private IQueryable<UserProfile> VisibleProfiles(UserProfile currentProfile, LeaderboardQuery query)
    {
        var profiles = db.UserProfiles.AsNoTracking().Where(profile => profile.LeaderboardVisible && !profile.Deleted);
        return query.OwnDepartmentOnly
            ? profiles.Where(profile => profile.Department == currentProfile.Department)
            : profiles;
    }

    private async Task<IReadOnlyDictionary<Guid, ArenaStat>> ReadArenaStatsAsync(HashSet<Guid> profileIds, CompetitionPeriod period, CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0)
        {
            return new Dictionary<Guid, ArenaStat>();
        }

        var rooms = db.LiveRoomSummaries.AsNoTracking().AsQueryable();
        var start = PeriodStart(period);
        if (start is { } startValue)
        {
            rooms = db.Database.IsSqlite()
                ? db.LiveRoomSummaries.FromSqlInterpolated($"""
                    SELECT *
                    FROM LiveRoomSummaries
                    WHERE FinishedAt IS NOT NULL
                      AND substr(FinishedAt, 1, 19) >= {FormatSqliteDateTimeOffset(startValue)}
                    """).AsNoTracking()
                : rooms.Where(room => room.FinishedAt >= startValue);
        }

        var query =
            from participant in db.LiveRoomParticipantSummaries.AsNoTracking()
            join room in rooms on participant.LiveRoomSummaryId equals room.Id
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
                participant.RatingAfter
            };
        var rows = await query
            .GroupBy(row => row.UserProfileId)
            .Select(group => new
            {
                UserProfileId = group.Key,
                Attempts = group.Count(),
                Wins = group.Count(row => row.Placement == 1),
                Podiums = group.Count(row => row.Placement != null && row.Placement > 0 && row.Placement <= 3),
                AverageWpm = group.Average(row => row.Wpm),
                AverageAccuracy = group.Average(row => row.Accuracy),
                RatingDelta = group.Sum(row => row.RatingAfter - row.RatingBefore)
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.UserProfileId,
            row => new ArenaStat(
                row.Attempts,
                row.Wins,
                row.Podiums,
                row.AverageWpm,
                row.AverageAccuracy,
                row.RatingDelta));
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
            Context = $"{ArenaDivision.NameFor(profile.ArenaRating)} · privat ausgeblendet",
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
        UserProfile currentProfile,
        Guid? privateProfileId,
        CancellationToken cancellationToken)
    {
        IQueryable<TypingAttempt> attemptSource = db.TypingAttempts.AsNoTracking();
        var start = PeriodStart(query.Period);
        if (start is { } startValue)
        {
            attemptSource = db.Database.IsSqlite()
                ? db.TypingAttempts.FromSqlInterpolated($"""
                    SELECT *
                    FROM TypingAttempts
                    WHERE substr(COALESCE(FinishedAt, CreatedAt), 1, 19) >= {FormatSqliteDateTimeOffset(startValue)}
                    """).AsNoTracking()
                : attemptSource.Where(attempt => (attempt.FinishedAt ?? attempt.CreatedAt) >= startValue);
        }

        var visibleProfiles = db.UserProfiles.AsNoTracking().Where(profile => !profile.Deleted);
        if (privateProfileId is { } hiddenProfileId)
        {
            visibleProfiles = visibleProfiles.Where(profile => profile.Id == hiddenProfileId);
        }
        else
        {
            visibleProfiles = visibleProfiles.Where(profile => profile.LeaderboardVisible);
            if (query.OwnDepartmentOnly)
            {
                visibleProfiles = visibleProfiles.Where(profile => profile.Department == currentProfile.Department);
            }
        }

        var attempts =
            from attempt in attemptSource
            join profile in visibleProfiles on attempt.UserProfileId equals profile.Id
            join text in db.TrainingTexts.AsNoTracking() on attempt.TrainingTextId equals text.Id into textJoin
            from text in textJoin.DefaultIfEmpty()
            where attempt.LeaderboardEligible
                && attempt.Official
                && attempt.Completed
                && attempt.Phase == AttemptPhase.Finished
                && attempt.Accuracy >= CompetitionEligibility.MinimumAccuracy
                && !profile.Deleted
            select new
            {
                UserProfileId = profile.Id,
                profile.DisplayName,
                AttemptId = attempt.Id,
                attempt.Mode,
                attempt.TrainingTextId,
                TextTitle = text == null ? null : text.Title,
                TextRatingEligible = text != null && text.RatingEligible,
                attempt.Wpm,
                attempt.Accuracy,
                attempt.Consistency,
                attempt.DurationMilliseconds,
                FinishedAt = attempt.FinishedAt ?? attempt.CreatedAt,
                attempt.CorrectCharacters
            };
        attempts = textBoard
            ? attempts.Where(row => row.Mode == TrainingMode.Text && row.TextRatingEligible && row.TrainingTextId == query.TextId)
            : attempts.Where(row => row.Mode == query.Mode);

        var rows = await attempts
            .Where(row => row.AttemptId == attempts
                .Where(candidate => candidate.UserProfileId == row.UserProfileId)
                .OrderByDescending(candidate => candidate.Wpm)
                .ThenByDescending(candidate => candidate.Accuracy)
                .ThenByDescending(candidate => candidate.Consistency)
                .ThenBy(candidate => candidate.AttemptId)
                .Select(candidate => candidate.AttemptId)
                .First())
            .OrderByDescending(row => row.Wpm)
            .ThenByDescending(row => row.Accuracy)
            .ThenByDescending(row => row.Consistency)
            .ThenBy(row => row.DisplayName)
            .ThenBy(row => row.UserProfileId)
            .Take(privateProfileId is null ? PublicLimit : 1)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new AttemptCandidate(
                row.UserProfileId,
                row.DisplayName,
                row.AttemptId,
                row.Mode,
                row.TrainingTextId,
                row.TextTitle ?? DisplayNames.For(row.Mode),
                row.Wpm,
                row.Accuracy,
                row.Consistency,
                row.DurationMilliseconds,
                row.FinishedAt,
                row.CorrectCharacters))
            .ToList();
    }

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
        UserProfile currentProfile,
        Guid? privateProfileId,
        CancellationToken cancellationToken)
    {
        IQueryable<ChallengeRoundResult> resultSource = db.ChallengeRoundResults.AsNoTracking();
        var start = PeriodStart(query.Period);
        if (start is { } startValue)
        {
            resultSource = db.Database.IsSqlite()
                ? db.ChallengeRoundResults.FromSqlInterpolated($"""
                    SELECT *
                    FROM ChallengeRoundResults
                    WHERE FinishedAt IS NOT NULL
                      AND substr(FinishedAt, 1, 19) >= {FormatSqliteDateTimeOffset(startValue)}
                    """).AsNoTracking()
                : resultSource.Where(result => result.FinishedAt >= startValue);
        }

        var visibleProfiles = db.UserProfiles.AsNoTracking().Where(profile => !profile.Deleted);
        if (privateProfileId is { } hiddenProfileId)
        {
            visibleProfiles = visibleProfiles.Where(profile => profile.Id == hiddenProfileId);
        }
        else
        {
            visibleProfiles = visibleProfiles.Where(profile => profile.LeaderboardVisible);
            if (query.OwnDepartmentOnly)
            {
                visibleProfiles = visibleProfiles.Where(profile => profile.Department == currentProfile.Department);
            }
        }

        var queryRows =
            from result in resultSource
            join round in db.ChallengeRounds.AsNoTracking() on result.ChallengeRoundId equals round.Id
            join challenge in db.Challenges.AsNoTracking() on round.ChallengeId equals challenge.Id
            join profile in visibleProfiles on result.UserProfileId equals profile.Id
            where result.Status == ParticipantStatus.Finished
                && result.FinishedAt != null
                && result.Accuracy >= CompetitionEligibility.MinimumAccuracy
            select new
            {
                UserProfileId = profile.Id,
                profile.DisplayName,
                ChallengeTitle = challenge.Title,
                ResultId = result.Id,
                result.Wpm,
                result.Accuracy,
                result.Consistency,
                result.Placement,
                FinishedAt = result.FinishedAt!.Value
            };
        var rows = await queryRows
            .Where(row => row.ResultId == queryRows
                .Where(candidate => candidate.UserProfileId == row.UserProfileId)
                .OrderByDescending(candidate => candidate.Wpm)
                .ThenByDescending(candidate => candidate.Accuracy)
                .ThenByDescending(candidate => candidate.Consistency)
                .ThenBy(candidate => candidate.ResultId)
                .Select(candidate => candidate.ResultId)
                .First())
            .OrderByDescending(row => row.Wpm)
            .ThenByDescending(row => row.Accuracy)
            .ThenByDescending(row => row.Consistency)
            .ThenBy(row => row.DisplayName)
            .ThenBy(row => row.UserProfileId)
            .Take(privateProfileId is null ? PublicLimit : 1)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ChallengeCandidate(
                row.UserProfileId,
                row.DisplayName,
                row.ChallengeTitle,
                row.Wpm,
                row.Accuracy,
                row.Consistency,
                row.Placement,
                row.FinishedAt))
            .ToList();
    }

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

        IQueryable<RewardLedgerEntry> entries = db.RewardLedgerEntries.AsNoTracking();
        var start = PeriodStart(period);
        if (start is { } startValue)
        {
            entries = db.Database.IsSqlite()
                ? db.RewardLedgerEntries.FromSqlInterpolated($"""
                    SELECT *
                    FROM RewardLedgerEntries
                    WHERE substr(AwardedAt, 1, 19) >= {FormatSqliteDateTimeOffset(startValue)}
                    """).AsNoTracking()
                : entries.Where(entry => entry.AwardedAt >= startValue);
        }

        return await entries
            .Where(entry => profileIds.Contains(entry.UserProfileId))
            .GroupBy(entry => entry.UserProfileId)
            .Select(group => new { UserProfileId = group.Key, Xp = group.Sum(entry => entry.Xp) })
            .ToDictionaryAsync(item => item.UserProfileId, item => item.Xp, cancellationToken);
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

    private static string FormatSqliteDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string BuildInitials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "KW" : string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

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
