using System.Security.Claims;
using System.Text.Json;
using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace KeyWars.Infrastructure;

public sealed class ProfileAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IProfileAccessGate accessGate,
        ProfileRequestContext profileContext)
    {
        if (!ShouldLease(context) ||
            !Guid.TryParse(context.User.FindFirstValue(KeyWarsClaims.ProfileId), out var profileId))
        {
            await next(context);
            return;
        }

        try
        {
            var requestAborted = context.RequestAborted;
            await using var lease = await accessGate.AcquireAsync(profileId, requestAborted);
            profileContext.Begin(profileId);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                requestAborted,
                lease.LeaseLost);
            context.RequestAborted = operationCancellation.Token;
            try
            {
                lease.ThrowIfLost();
                await next(context);
                lease.ThrowIfLost();
            }
            catch (Exception exception) when (
                lease.LeaseLost.IsCancellationRequested &&
                !requestAborted.IsCancellationRequested &&
                exception is OperationCanceledException or InvalidOperationException)
            {
                if (context.Response.HasStarted)
                {
                    throw;
                }

                await WriteProblemAsync(
                    context,
                    "Der Profilzugriff wurde während der Anfrage unterbrochen.",
                    "profile_access_lost",
                    CancellationToken.None);
            }
            finally
            {
                profileContext.Clear();
                context.RequestAborted = requestAborted;
            }
        }
        catch (ProfileOperationException exception) when (
            !context.Response.HasStarted &&
            string.Equals(exception.Code, "profile_deleted", StringComparison.Ordinal))
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            await next(context);
        }
        catch (ProfileOperationException exception) when (!context.Response.HasStarted)
        {
            await WriteProblemAsync(context, exception.Message, exception.Code, context.RequestAborted);
        }
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        string title,
        string code,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                type = "about:blank",
                title,
                status = StatusCodes.Status409Conflict,
                code
            },
            cancellationToken: cancellationToken);
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

        var path = context.Request.Path;
        return !MatchesPathOrTrailingSlash(path, "/profil/loeschen") &&
            !MatchesPathOrTrailingSlash(path, "/profil/statistik-zuruecksetzen") &&
            !MatchesPathOrTrailingSlash(path, "/profil/statistikzuruecksetzen");
    }

    private static bool MatchesPathOrTrailingSlash(PathString path, string basePath) =>
        path.Equals(basePath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(basePath + "/", StringComparison.OrdinalIgnoreCase);
}
