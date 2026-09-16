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
    public async Task LoginLimitIsAppliedInEveryTopology(
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
            new TestHostEnvironment(environmentName),
            Options.Create(new AuthOptions { DevelopmentLogin = developmentLogin }),
            limiter);

        Assert.Equal(expectedLimit, limiter.PermitLimit);
    }

    [Theory]
    [InlineData("/anmelden")]
    [InlineData("/anmelden/")]
    public async Task SingleNodeLoginRequestsAreRejectedAfterTheLimit(string path)
    {
        var limiter = new SingleNodeSharedRateLimiter();
        var calls = 0;
        var middleware = new ClusterRateLimitMiddleware(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        for (var index = 0; index < 11; index++)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await middleware.InvokeAsync(
                context,
                new TestHostEnvironment("Production"),
                Options.Create(new AuthOptions()),
                limiter);

            Assert.Equal(index < 10 ? StatusCodes.Status200OK : StatusCodes.Status429TooManyRequests,
                context.Response.StatusCode);
        }

        Assert.Equal(10, calls);
    }

    [Fact]
    public async Task LoginPrefixPathIsNotClassifiedAsLogin()
    {
        var calls = 0;
        var limiter = new CapturingLimiter();
        var middleware = new ClusterRateLimitMiddleware(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/anmelden/extra";

        await middleware.InvokeAsync(
            context,
            new TestHostEnvironment("Production"),
            Options.Create(new AuthOptions()),
            limiter);

        Assert.Equal(1, calls);
        Assert.Equal(0, limiter.Calls);
    }

    private sealed class CapturingLimiter : ISharedRateLimiter
    {
        public int Calls { get; private set; }
        public int PermitLimit { get; private set; }

        public ValueTask<bool> TryAcquireAsync(
            string partition,
            string key,
            int permitLimit,
            TimeSpan window,
            CancellationToken cancellationToken = default)
        {
            Calls++;
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
