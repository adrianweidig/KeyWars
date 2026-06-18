using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Services;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Infrastructure;

public static class ApiEndpoints
{
    public static void MapKeyWarsApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization();

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

        api.MapPost("/spielen/abschliessen", async (FinishAttemptRequest request, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var attempt = await attempts.FinishAsync(profile.Id, request, cancellationToken);
            return Results.Ok(new
            {
                attempt.Id,
                attempt.Wpm,
                attempt.RawWpm,
                attempt.CharactersPerMinute,
                attempt.Accuracy,
                attempt.Consistency,
                attempt.CorrectCharacters,
                attempt.IncorrectCharacters,
                attempt.Completed
            });
        });

        api.MapPost("/herausforderungen/{id:guid}/abschliessen", async (Guid id, FinishAttemptRequest request, CurrentUser currentUser, HttpContext httpContext, AttemptService attempts, ChallengeService challenges, CancellationToken cancellationToken) =>
        {
            var profile = await currentUser.RequireProfileAsync(httpContext.User, cancellationToken);
            var attempt = await attempts.FinishAsync(profile.Id, request, cancellationToken);
            await challenges.FinishRoundAsync(id, profile.Id, attempt, cancellationToken);
            return Results.Ok(new
            {
                attempt.Id,
                attempt.Wpm,
                attempt.RawWpm,
                attempt.CharactersPerMinute,
                attempt.Accuracy,
                attempt.Consistency,
                attempt.CorrectCharacters,
                attempt.IncorrectCharacters,
                attempt.Completed
            });
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
}
