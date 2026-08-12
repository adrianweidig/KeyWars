using System.Security.Claims;
using System.Text.Json;
using KeyWars.Auth;
using KeyWars.Services;

namespace KeyWars.Infrastructure;

public sealed class ProfileAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IProfileAccessGate accessGate)
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
                context.RequestAborted = requestAborted;
            }
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

        var path = context.Request.Path.Value;
        return !string.Equals(path, "/profil/loeschen", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "/profil/statistik-zuruecksetzen", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "/profil/statistikzuruecksetzen", StringComparison.OrdinalIgnoreCase);
    }
}
