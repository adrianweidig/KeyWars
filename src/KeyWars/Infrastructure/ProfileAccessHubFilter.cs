using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Infrastructure;

public sealed class ProfileAccessHubFilter(
    IProfileAccessGate accessGate,
    ISharedRateLimiter rateLimiter,
    IServiceScopeFactory scopeFactory,
    ILogger<ProfileAccessHubFilter> logger) : IHubFilter
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

        await using var lease = await AcquireLeaseAsync(invocationContext.Context, parsedProfileId);
        using var leaseLostRegistration = lease.LeaseLost.Register(
            invocationContext.Context.Abort);
        lease.ThrowIfLost();
        if (lease is not IAuthoritativeProfileValidationLease validationLease ||
            validationLease.RequiresAuthoritativeValidation)
        {
            await EnsureAuthoritativeProfileAsync(invocationContext.Context, parsedProfileId);
            if (lease is IAuthoritativeProfileValidationLease redisValidationLease)
            {
                await MarkAuthoritativelyValidatedAsync(
                    invocationContext.Context,
                    redisValidationLease);
            }
        }
        lease.ThrowIfLost();
        var result = await next(invocationContext);
        lease.ThrowIfLost();
        return result;
    }

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        var profileId = context.Context.User?.FindFirst(KeyWarsClaims.ProfileId)?.Value;
        if (!Guid.TryParse(profileId, out var parsedProfileId))
        {
            await next(context);
            return;
        }

        await using var lease = await AcquireLeaseAsync(context.Context, parsedProfileId);
        using var leaseLostRegistration = lease.LeaseLost.Register(context.Context.Abort);
        lease.ThrowIfLost();
        if (lease is not IAuthoritativeProfileValidationLease validationLease ||
            validationLease.RequiresAuthoritativeValidation)
        {
            await EnsureAuthoritativeProfileAsync(context.Context, parsedProfileId);
            if (lease is IAuthoritativeProfileValidationLease redisValidationLease)
            {
                await MarkAuthoritativelyValidatedAsync(context.Context, redisValidationLease);
            }
        }
        lease.ThrowIfLost();
        await next(context);
        lease.ThrowIfLost();
    }

    private async ValueTask<IOperationLease> AcquireLeaseAsync(
        HubCallerContext context,
        Guid profileId)
    {
        try
        {
            return await accessGate.AcquireAsync(profileId, context.ConnectionAborted);
        }
        catch (ProfileOperationException exception) when (exception.Code == "profile_deleted")
        {
            context.Abort();
            throw new HubException("Die Profilsitzung ist nicht mehr gültig.");
        }
    }

    private static async ValueTask MarkAuthoritativelyValidatedAsync(
        HubCallerContext context,
        IAuthoritativeProfileValidationLease validationLease)
    {
        try
        {
            await validationLease.MarkAuthoritativelyValidatedAsync(context.ConnectionAborted);
        }
        catch (ProfileOperationException exception) when (exception.Code == "profile_deleted")
        {
            context.Abort();
            throw new HubException("Die Profilsitzung ist nicht mehr gültig.");
        }
    }

    private async Task EnsureAuthoritativeProfileAsync(
        HubCallerContext context,
        Guid profileId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KeyWarsDbContext>();
        var exists = await db.UserProfiles
            .AsNoTracking()
            .AnyAsync(
                profile => profile.Id == profileId && !profile.Deleted,
                context.ConnectionAborted);
        if (!exists)
        {
            if (accessGate is IAuthoritativeProfileDeletionMarker deletionMarker)
            {
                try
                {
                    await deletionMarker.MarkDeletedAuthoritativelyAsync(
                        profileId,
                        context.ConnectionAborted);
                }
                catch (OperationCanceledException) when (context.ConnectionAborted.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception markerException)
                {
                    logger.LogWarning(
                        markerException,
                        "Der dauerhafte Löschmarker für Profil {ProfileId} konnte nicht nachgefüllt werden.",
                        profileId);
                }
            }

            context.Abort();
            throw new HubException("Die Profilsitzung ist nicht mehr gültig.");
        }
    }
}
