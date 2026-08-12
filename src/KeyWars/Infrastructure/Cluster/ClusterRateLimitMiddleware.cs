using System.Security.Claims;
using System.Text.Json;
using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.Extensions.Options;

namespace KeyWars.Infrastructure.Cluster;

public sealed class ClusterRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        RuntimeTopology topology,
        IHostEnvironment environment,
        IOptions<AuthOptions> authOptions,
        ISharedRateLimiter limiter)
    {
        if (!topology.IsCluster)
        {
            await next(context);
            return;
        }

        var request = context.Request;
        string? partition = null;
        string? key = null;
        var limit = 0;
        if (HttpMethods.IsPost(request.Method) && request.Path.Equals("/anmelden"))
        {
            partition = "login";
            key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            limit = environment.IsDevelopment() && authOptions.Value.DevelopmentLogin ? 200 : 10;
        }
        else if (request.Path.StartsWithSegments("/api"))
        {
            partition = "api";
            key = context.User.FindFirstValue(KeyWarsClaims.ProfileId)
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown";
            limit = 180;
        }

        if (partition is null ||
            await limiter.TryAcquireAsync(
                partition,
                key!,
                limit,
                TimeSpan.FromMinutes(1),
                context.RequestAborted))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = "60";
        if (request.Path.StartsWithSegments("/api"))
        {
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new
                {
                    type = "about:blank",
                    title = "Zu viele Anfragen. Bitte warte kurz und versuche es erneut.",
                    status = StatusCodes.Status429TooManyRequests,
                    code = "rate_limit_exceeded"
                },
                cancellationToken: context.RequestAborted);
        }
    }
}
