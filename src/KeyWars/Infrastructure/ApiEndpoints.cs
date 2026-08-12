using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace KeyWars.Infrastructure;

public static class ApiEndpoints
{
    public static void MapKeyWarsApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api")
            .RequireAuthorization()
            .RequireRateLimiting("keywars-api");
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                var request = context.HttpContext.Request;
                if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsDelete(request.Method))
                {
                    if (!IsJsonRequest(request))
                    {
                        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                    }

                    if (!IsSameOrigin(request))
                    {
                        return Results.Forbid();
                    }
                }

                return await next(context);
            }
            catch (AttemptLifecycleException exception)
            {
                return AttemptProblem(exception);
            }
            catch (ChallengeLifecycleException exception)
            {
                return Results.Problem(
                    title: exception.Message,
                    statusCode: exception.StatusCode,
                    extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
            }
            catch (ProfileOperationException exception)
            {
                return Results.Problem(
                    title: exception.Message,
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
            }
        });

        api.MapGet("/personen/suche", async (string? q, string? department, string? purpose, int? page, int? pageSize, CurrentUser currentUser, HttpContext httpContext, KeyWarsDbContext db, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var normalized = (q ?? string.Empty).Trim();
            var normalizedDepartment = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
            if (normalizedDepartment?.Length > 160)
            {
                return Results.BadRequest(new { code = "department_too_long", message = "Die Abteilung darf höchstens 160 Zeichen enthalten." });
            }

            var normalizedPurpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim().ToLowerInvariant();
            if (normalizedPurpose is not null and not "arena" and not "challenge")
            {
                return Results.BadRequest(new { code = "invalid_purpose", message = "purpose muss 'arena' oder 'challenge' sein." });
            }

            var requestedPage = Math.Max(1, page.GetValueOrDefault(1));
            var boundedPageSize = Math.Clamp(pageSize.GetValueOrDefault(20), 1, 20);
            var directoryPeople = db.UserProfiles
                .AsNoTracking()
                .Where(person => !person.Deleted && person.Id != profile.Id);
            if (normalizedPurpose == "challenge")
            {
                directoryPeople = directoryPeople.Where(person => person.ChallengesEnabled);
            }

            if (normalizedDepartment is not null)
            {
                directoryPeople = directoryPeople.Where(person => person.Department == normalizedDepartment);
            }

            var people = directoryPeople;
            if (normalized.Length > 0)
            {
                people = people.Where(person =>
                    person.DisplayName.Contains(normalized) ||
                    person.SamAccountName.Contains(normalized) ||
                    person.UserPrincipalName.Contains(normalized));
            }

            var totalCount = await people.CountAsync(cancellationToken);
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)boundedPageSize));
            var boundedPage = Math.Min(requestedPage, totalPages);
            var pageItems = await people
                .OrderBy(person => person.DisplayName)
                .ThenBy(person => person.SamAccountName)
                .Skip((boundedPage - 1) * boundedPageSize)
                .Take(boundedPageSize)
                .Select(person => new { person.Id, person.DisplayName, person.SamAccountName, person.Department })
                .ToListAsync(cancellationToken);
            var pageNames = pageItems.Select(person => person.DisplayName).Distinct().ToArray();
            var duplicateNames = pageNames.Length == 0
                ? []
                : await directoryPeople
                    .Where(person => pageNames.Contains(person.DisplayName))
                    .GroupBy(person => person.DisplayName)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArrayAsync(cancellationToken);
            var ambiguousNames = duplicateNames.ToHashSet(StringComparer.Ordinal);
            var items = pageItems.Select(person => new
            {
                person.Id,
                person.DisplayName,
                person.SamAccountName,
                person.Department,
                Label = ambiguousNames.Contains(person.DisplayName)
                    ? $"{person.DisplayName} ({person.SamAccountName})"
                    : person.DisplayName
            });

            return Results.Ok(new { items, page = boundedPage, pageSize = boundedPageSize, totalCount, totalPages });
        });

        api.MapPost("/spielen/start", async (StartAttemptRequest request, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var session = await attempts.StartAsync(profile.Id, request, cancellationToken);
            return Results.Ok(session);
        });

        api.MapPost("/spielen/begin", async (BeginAttemptRequest request, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var start = await attempts.BeginAsync(profile.Id, request, cancellationToken);
            return Results.Ok(start);
        });

        api.MapPost("/spielen/abschliessen", async (FinishAttemptRequest request, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var completion = await attempts.FinishAsync(profile.Id, request, cancellationToken);
            return Results.Ok(BuildAttemptResult(completion.Attempt, profile, completion.Motivation));
        });

        api.MapPost("/herausforderungen/{id:guid}/start", async (Guid id, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, ChallengeService challenges, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var session = await challenges.StartAttemptAsync(id, profile.Id, attempts, cancellationToken);
            return Results.Ok(session);
        });

        api.MapPost("/herausforderungen/{id:guid}/abschliessen", async (Guid id, FinishAttemptRequest request, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, ChallengeService challenges, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var completion = await challenges.FinishAttemptAsync(id, profile.Id, request, attempts, cancellationToken);
            return Results.Ok(BuildAttemptResult(completion.Attempt, profile, completion.Motivation));
        });

        api.MapGet("/motivation/recent", async (int? take, CurrentUser currentUser, HttpContext httpContext, KeyWarsDbContext db, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var count = Math.Clamp(take.GetValueOrDefault(10), 1, 25);
            var events = db.Database.IsSqlite()
                ? await db.GamificationEvents
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM GamificationEvents
                        WHERE UserProfileId = {profile.Id.ToString().ToUpperInvariant()}
                        ORDER BY substr(CreatedAt, 1, 19) DESC, Id DESC
                        LIMIT {count}
                        """)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken)
                : await db.GamificationEvents
                    .AsNoTracking()
                    .Where(item => item.UserProfileId == profile.Id)
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Id)
                    .Take(count)
                    .ToListAsync(cancellationToken);
            return Results.Ok(events.Select(BuildMotivationEvent));
        });

        api.MapGet("/profil/kurz", async (CurrentUser currentUser, HttpContext httpContext, KeyWarsDbContext db, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var profileKey = profile.Id.ToString().ToUpperInvariant();
            var last = db.Database.IsSqlite()
                ? await db.Database.SqlQuery<ShortProfileAttemptRow>($"""
                        SELECT Wpm, Accuracy, CreatedAt
                        FROM TypingAttempts
                        WHERE UserProfileId = {profileKey}
                        ORDER BY CreatedAt DESC, Id DESC
                        LIMIT 5
                        """)
                    .ToListAsync(cancellationToken)
                : await db.TypingAttempts
                    .AsNoTracking()
                    .Where(item => item.UserProfileId == profile.Id)
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Id)
                    .Take(5)
                    .Select(item => new ShortProfileAttemptRow
                    {
                        Wpm = item.Wpm,
                        Accuracy = item.Accuracy,
                        CreatedAt = item.CreatedAt
                    })
                    .ToListAsync(cancellationToken);
            return Results.Ok(new { profile.DisplayName, profile.Level, profile.ExperiencePoints, profile.ArenaRating, LastAttempts = last });
        });

        api.MapGet("/arena/{roomId:guid}/speicherstatus", async (Guid roomId, CurrentUser currentUser, HttpContext httpContext, KeyWarsDbContext db, ILiveRoomCompletionSink completions, CancellationToken cancellationToken) =>
        {
            await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var persisted = await db.LiveRoomSummaries.AsNoTracking().AnyAsync(room => room.Id == roomId, cancellationToken);
            var state = persisted ? CompletionState.Persisted : completions.GetStatus(roomId).State;
            return Results.Ok(new { State = state.ToString() });
        });
    }

    private static IResult AttemptProblem(AttemptLifecycleException exception)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = exception.Code };
        if (exception.RetryAfterMs is { } retryAfterMs)
        {
            extensions["retryAfterMs"] = retryAfterMs;
        }

        return Results.Problem(
            title: exception.Message,
            statusCode: exception.StatusCode,
            extensions: extensions);
    }

    private static object BuildAttemptResult(TypingAttempt attempt, UserProfile profile, MotivationOutcome motivation)
    {
        var progress = MotivationService.GetLevelProgress(profile.ExperiencePoints);
        return new
        {
            attempt.Id,
            attempt.Wpm,
            attempt.RawWpm,
            attempt.CharactersPerMinute,
            attempt.DurationMilliseconds,
            attempt.Accuracy,
            attempt.Consistency,
            attempt.ConsistencySampleCount,
            attempt.CorrectCharacters,
            attempt.IncorrectCharacters,
            attempt.Completed,
            TargetCompleted = attempt.CorrectCharacters == attempt.TotalCharacters && attempt.IncorrectCharacters == 0,
            profile.Level,
            profile.ExperiencePoints,
            progress.NextLevelXp,
            progress.RemainingXp,
            progress.ProgressPercent,
            Motivation = new
            {
                motivation.XpDelta,
                motivation.LevelBefore,
                motivation.LevelAfter,
                motivation.ProgressPercent,
                Events = motivation.Events.Select(BuildMotivationEvent)
            }
        };
    }

    private static object BuildMotivationEvent(GamificationEvent item)
    {
        var visual = MotivationVisuals.ForEvent(item);
        return new
        {
            item.Id,
            Type = item.Type.ToString(),
            item.Title,
            item.Description,
            Rarity = item.Rarity.ToString(),
            item.XpDelta,
            item.LevelBefore,
            item.LevelAfter,
            item.CreatedAt,
            visual.VisualKey,
            visual.Accent
        };
    }

    private static bool IsJsonRequest(HttpRequest request)
    {
        return request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        return string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ShortProfileAttemptRow
    {
        public double Wpm { get; set; }
        public double Accuracy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}
