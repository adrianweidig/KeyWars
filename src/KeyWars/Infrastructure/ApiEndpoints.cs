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
        });

        api.MapGet("/personen/suche", async (string? q, CurrentUser currentUser, HttpContext httpContext, TextLibraryService texts, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var people = await texts.SearchPeopleAsync(profile.Id, q, cancellationToken: cancellationToken);
            return Results.Ok(people.Select(person => new
            {
                person.Id,
                person.DisplayName,
                person.SamAccountName,
                Label = people.Count(other => other.DisplayName == person.DisplayName) > 1
                    ? $"{person.DisplayName} ({person.SamAccountName})"
                    : person.DisplayName
            }));
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
            var attempt = await attempts.FinishAsync(profile.Id, request, cancellationToken);
            return Results.Ok(BuildAttemptResult(attempt, profile));
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
            var attempt = await attempts.FinishAsync(profile.Id, request, cancellationToken);
            await challenges.FinishRoundAsync(id, profile.Id, attempt, cancellationToken);
            return Results.Ok(BuildAttemptResult(attempt, profile));
        });

        api.MapGet("/profil/kurz", async (CurrentUser currentUser, HttpContext httpContext, KeyWarsDbContext db, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var last = (await db.TypingAttempts
                .Where(item => item.UserProfileId == profile.Id)
                .ToListAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .Take(5)
                .Select(item => new { item.Wpm, item.Accuracy, item.CreatedAt })
                .ToList();
            return Results.Ok(new { profile.DisplayName, profile.Level, profile.ExperiencePoints, profile.ArenaRating, LastAttempts = last });
        });
    }

    private static object BuildAttemptResult(TypingAttempt attempt, UserProfile profile)
    {
        var progress = MotivationService.GetLevelProgress(profile.ExperiencePoints);
        return new
        {
            attempt.Id,
            attempt.Wpm,
            attempt.RawWpm,
            attempt.CharactersPerMinute,
            attempt.Accuracy,
            attempt.Consistency,
            attempt.CorrectCharacters,
            attempt.IncorrectCharacters,
            attempt.Completed,
            profile.Level,
            profile.ExperiencePoints,
            progress.NextLevelXp,
            progress.RemainingXp,
            progress.ProgressPercent
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
}
