using System.Net;
using KeyWars.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class ProxySecurityTests
{
    [Theory]
    [InlineData("10.0.0.10", "https")]
    [InlineData("10.0.0.11", "http")]
    public async Task ForwardedProtoIsOnlyAcceptedFromConfiguredProxy(string remoteAddress, string expectedScheme)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["KEYWARS:PROXY:KNOWN_PROXIES"] = "10.0.0.10"
        });
        var options = ConfigurationAliases.GetForwardedHeaders(configuration);
        var observedScheme = "";
        var middleware = new ForwardedHeadersMiddleware(
            context =>
            {
                observedScheme = context.Request.Scheme;
                return Task.CompletedTask;
            },
            NullLoggerFactory.Instance,
            Options.Create(options));
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        await middleware.Invoke(context);

        Assert.Equal(expectedScheme, observedScheme);
    }

    [Theory]
    [InlineData("10.0.0.10", true, "wss://keywars.test")]
    [InlineData("10.0.0.11", false, "ws://keywars.test")]
    public async Task SecurityHeadersUseHttpsOnlyAfterTrustedForwardedProto(
        string remoteAddress,
        bool expectHsts,
        string expectedWebSocketSource)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["KEYWARS:PROXY:KNOWN_PROXIES"] = "10.0.0.10"
        });
        var options = ConfigurationAliases.GetForwardedHeaders(configuration);
        var responseFeature = new TestResponseFeature();
        var securityHeaders = new SecurityHeadersMiddleware(_ => responseFeature.FireOnStartingAsync());
        var forwardedHeaders = new ForwardedHeadersMiddleware(
            securityHeaders.InvokeAsync,
            NullLoggerFactory.Instance,
            Options.Create(options));
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("keywars.test");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        await forwardedHeaders.Invoke(context);

        Assert.Equal(expectHsts, context.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.Contains(
            expectedWebSocketSource,
            context.Response.Headers.ContentSecurityPolicy.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("KEYWARS:PROXY:KNOWN_PROXIES", "not-an-ip", "IP-Adresse")]
    [InlineData("KEYWARS:PROXY:KNOWN_NETWORKS", "10.0.0.0/not-a-prefix", "CIDR-Netz")]
    public void InvalidProxyTrustEntryFailsClosed(string key, string value, string expectedMessage)
    {
        var configuration = Configuration(new Dictionary<string, string?> { [key] = value });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfigurationAliases.GetForwardedHeaders(configuration));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitProxyConfigurationReplacesFrameworkLoopbackDefaults()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["KEYWARS:PROXY:KNOWN_PROXIES"] = "10.0.0.10;10.0.0.11",
            ["KEYWARS:PROXY:KNOWN_NETWORKS"] = "10.1.0.0/16"
        });

        var options = ConfigurationAliases.GetForwardedHeaders(configuration);

        Assert.Equal([IPAddress.Parse("10.0.0.10"), IPAddress.Parse("10.0.0.11")], options.KnownProxies);
        var network = Assert.Single(options.KnownIPNetworks);
        Assert.Equal(IPAddress.Parse("10.1.0.0"), network.BaseAddress);
        Assert.Equal(16, network.PrefixLength);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TestResponseFeature : IHttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> onStarting = [];
        private readonly Stack<(Func<object, Task> Callback, object State)> onCompleted = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => onStarting.Push((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state) => onCompleted.Push((callback, state));

        public async Task FireOnStartingAsync()
        {
            while (onStarting.TryPop(out var registration))
            {
                await registration.Callback(registration.State);
            }

            HasStarted = true;
        }
    }
}
