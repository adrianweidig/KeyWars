using System.Security.Claims;
using System.Text.Json;
using KeyWars.Auth;
using KeyWars.Services;

namespace KeyWars.Infrastructure;

public sealed class ProfileAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ProfileAccessGate accessGate)
    {
        if (!ShouldLease(context) ||
            !Guid.TryParse(context.User.FindFirstValue(KeyWarsClaims.ProfileId), out var profileId))
        {
            await next(context);
            return;
        }

        try
        {
            using var lease = accessGate.Acquire(profileId);
            await next(context);
        }
        catch (ProfileOperationException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new
                {
                    type = "about:blank",
                    title = exception.Message,
                    status = StatusCodes.Status409Conflict,
                    code = exception.Code
                },
                cancellationToken: context.RequestAborted);
        }
    }

    private static bool ShouldLease(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            context.Request.Path.StartsWithSegments("/hubs/arena"))
        {
            return false;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return true;
        }

        var path = context.Request.Path.Value;
        return !string.Equals(path, "/profil/loeschen", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "/profil/statistik-zuruecksetzen", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "/profil/statistikzuruecksetzen", StringComparison.OrdinalIgnoreCase);
    }
}
