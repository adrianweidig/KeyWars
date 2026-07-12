using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KeyWars.Auth;
using Microsoft.Extensions.Logging;

namespace KeyWars.UnitTests;

public sealed class LdapSecurityTests
{
    private static readonly LdapOptions Options = new() { UpnSuffix = "top.secret" };

    [Theory]
    [InlineData("max", "max@top.secret")]
    [InlineData(" max@top.secret ", "max@top.secret")]
    [InlineData("TOP\\max", "TOP\\max")]
    public void NormalizeBindNamePreservesExplicitDirectoryNames(string input, string expected)
    {
        Assert.Equal(expected, LdapSecurity.NormalizeBindName(input, Options));
    }

    [Fact]
    public void EscapeFilterEscapesAllRfc4515ControlCharacters()
    {
        Assert.Equal("a\\2a\\28b\\29\\5c\\00", LdapSecurity.EscapeFilter("a*(b)\\\0"));
    }

    [Theory]
    [InlineData("dc01.top.secret", "dc01.top.secret", true)]
    [InlineData("DC01.TOP.SECRET.", "dc01.top.secret", true)]
    [InlineData("*.top.secret", "dc01.top.secret", true)]
    [InlineData("*.top.secret", "nested.dc01.top.secret", false)]
    [InlineData("*.top.secret", "top.secret", false)]
    [InlineData("dc01.top.secret", "dc02.top.secret", false)]
    public void HostPatternOnlyAllowsExactOrSingleLabelWildcard(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, LdapSecurity.HostMatchesPattern(pattern, host));
    }

    [Fact]
    public void CertificateHostnameMatchesAnyDnsSubjectAlternativeName()
    {
        using var certificate = CreateCertificate(
            "legacy.top.secret",
            dnsNames: ["dc01.top.secret", "dc02.top.secret"]);

        Assert.True(LdapSecurity.CertificateMatchesHost(certificate, "dc02.top.secret"));
    }

    [Fact]
    public void CertificateHostnameDoesNotFallBackToCommonNameWhenSanExists()
    {
        using var certificate = CreateCertificate(
            "dc01.top.secret",
            dnsNames: ["other.top.secret"]);

        Assert.False(LdapSecurity.CertificateMatchesHost(certificate, "dc01.top.secret"));
    }

    [Fact]
    public void CertificateHostnameOnlyMatchesIpAddressesFromIpSubjectAlternativeNames()
    {
        using var certificate = CreateCertificate(
            "10.0.0.1",
            ipAddresses: [IPAddress.Parse("10.0.0.2")]);

        Assert.True(LdapSecurity.CertificateMatchesHost(certificate, "10.0.0.2"));
        Assert.False(LdapSecurity.CertificateMatchesHost(certificate, "10.0.0.1"));
    }

    [Fact]
    public void CertificateHostnameFallsBackToCommonNameWithoutSan()
    {
        using var certificate = CreateCertificate("dc01.top.secret");

        Assert.True(LdapSecurity.CertificateMatchesHost(certificate, "dc01.top.secret"));
    }

    [Theory]
    [InlineData(49, LdapFailureKind.InvalidCredentials)]
    [InlineData(81, LdapFailureKind.Unavailable)]
    [InlineData(85, LdapFailureKind.Unavailable)]
    public void LdapErrorsAreClassifiedWithoutCredentials(int errorCode, LdapFailureKind expected)
    {
        Assert.Equal(expected, LdapSecurity.ClassifyFailure(new LdapException(errorCode, "Test")));
    }

    [Fact]
    public async Task ConnectionFailureLogOmitsExceptionAndCredentials()
    {
        const string username = "sensitive-user";
        const string password = "sensitive-password";
        var logger = new CapturingLogger<LdapAuthenticator>();
        var authenticator = new LdapAuthenticator(
            Microsoft.Extensions.Options.Options.Create(new LdapOptions
            {
                Urls = "ldaps://127.0.0.1:1",
                BaseDn = "DC=top,DC=secret",
                UpnSuffix = "top.secret",
                ConnectTimeoutSeconds = 1,
                OperationTimeoutSeconds = 1
            }),
            logger);

        var result = await authenticator.AuthenticateAsync(username, password, CancellationToken.None);

        Assert.False(result.Succeeded);
        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(username, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, entry.Message, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("fehlgeschlagen (", entry.Message, StringComparison.Ordinal);
    }

    private static X509Certificate2 CreateCertificate(
        string commonName,
        string[]? dnsNames = null,
        IPAddress[]? ipAddresses = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        if (dnsNames is { Length: > 0 } || ipAddresses is { Length: > 0 })
        {
            var alternativeNames = new SubjectAlternativeNameBuilder();
            foreach (var dnsName in dnsNames ?? [])
            {
                alternativeNames.AddDnsName(dnsName);
            }

            foreach (var ipAddress in ipAddresses ?? [])
            {
                alternativeNames.AddIpAddress(ipAddress);
            }

            request.CertificateExtensions.Add(alternativeNames.Build());
        }

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(string Message, Exception? Exception);
}
