using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Infrastructure;

public sealed class ProfileAccessHubFilter(ProfileAccessGate accessGate) : IHubFilter
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

        using var lease = accessGate.Acquire(parsedProfileId);
        return await next(invocationContext);
    }
}
