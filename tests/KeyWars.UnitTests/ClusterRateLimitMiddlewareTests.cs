using System.Net;
using KeyWars.Auth;
using KeyWars.Infrastructure.Cluster;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class ClusterRateLimitMiddlewareTests
{
    [Theory]
    [InlineData("Development", true, 200)]
    [InlineData("Development", false, 10)]
    [InlineData("Production", true, 10)]
    public async Task ClusterLoginLimitMatchesTheLocalLoginPolicy(
        string environmentName,
        bool developmentLogin,
        int expectedLimit)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/anmelden";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var limiter = new CapturingLimiter();
        var middleware = new ClusterRateLimitMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(
            context,
            ClusterTopology,
            new TestHostEnvironment(environmentName),
            Options.Create(new AuthOptions { DevelopmentLogin = developmentLogin }),
            limiter);

        Assert.Equal(expectedLimit, limiter.PermitLimit);
    }

    private static readonly RuntimeTopology ClusterTopology = new(
        RuntimeRole.Web,
        KeyWarsDatabaseProvider.PostgreSql,
        "Host=postgres;Database=keywars",
        "redis:6379",
        "KeyWars");

    private sealed class CapturingLimiter : ISharedRateLimiter
    {
        public int PermitLimit { get; private set; }

        public ValueTask<bool> TryAcquireAsync(
            string partition,
            string key,
            int permitLimit,
            TimeSpan window,
            CancellationToken cancellationToken = default)
        {
            PermitLimit = permitLimit;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "KeyWars.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
