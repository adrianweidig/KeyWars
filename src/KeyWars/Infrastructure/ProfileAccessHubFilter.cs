using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Infrastructure;

public sealed class ProfileAccessHubFilter(
    IProfileAccessGate accessGate,
    ISharedRateLimiter rateLimiter) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var profileId = invocationContext.Context.User?.FindFirst(KeyWarsClaims.ProfileId)?.Value;
        if (!Guid.TryParse(profileId, out var parsedProfileId))
        {
            return await next(invocationContext);
        }

        if (!await rateLimiter.TryAcquireAsync(
                "hub",
                parsedProfileId.ToString("N"),
                900,
                TimeSpan.FromMinutes(1),
                invocationContext.Context.ConnectionAborted))
        {
            throw new HubException("Zu viele Arena-Aktionen. Bitte warte kurz.");
        }

        await using var lease = await accessGate.AcquireAsync(
            parsedProfileId,
            invocationContext.Context.ConnectionAborted);
        return await next(invocationContext);
    }
}
