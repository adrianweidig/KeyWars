using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KeyWars.E2ETests;

public sealed class SecurityConfigurationTests
{
    [Fact]
    public async Task ProductionCookiesUseHostPrefixAndSecureContract()
    {
        var factory = new ProductionSecurityWebFactory();
        try
        {
            var authentication = factory.Services
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var antiforgery = factory.Services
                .GetRequiredService<IOptions<AntiforgeryOptions>>()
                .Value;

            AssertCookie(
                authentication.Cookie,
                "__Host-KeyWars.Auth",
                authentication.ExpireTimeSpan,
                TimeSpan.FromHours(8));
            AssertCookie(antiforgery.Cookie, "__Host-KeyWars.AntiForgery");
        }
        finally
        {
            await factory.DisposeAsync();
            factory.TryDeleteDataDirectory();
        }
    }

    private static void AssertCookie(
        CookieBuilder cookie,
        string expectedName,
        TimeSpan? actualLifetime = null,
        TimeSpan? expectedLifetime = null)
    {
        Assert.Equal(expectedName, cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, cookie.SecurePolicy);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, cookie.SameSite);
        Assert.Equal("/", cookie.Path);
        Assert.Null(cookie.Domain);
        Assert.Equal(expectedLifetime, actualLifetime);
    }
}

internal sealed class ProductionSecurityWebFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"keywars-production-security-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("KEYWARS:DATA:DIRECTORY", dataDirectory);
        builder.UseSetting("KEYWARS:LDAP:URLS", "ldaps://dc01.example.local:636");
        builder.UseSetting("KEYWARS:LDAP:BASE_DN", "DC=example,DC=local");
        builder.UseSetting("KEYWARS:LDAP:UPN_SUFFIX", "example.local");
    }

    public void TryDeleteDataDirectory()
    {
        try
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
