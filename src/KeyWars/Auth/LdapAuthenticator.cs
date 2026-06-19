using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace KeyWars.Auth;

public sealed class LdapAuthenticator(IOptions<LdapOptions> options, ILogger<LdapAuthenticator> logger) : ILdapAuthenticator
{
    private static readonly string[] Attributes =
    [
        "objectGUID",
        "objectSid",
        "sAMAccountName",
        "userPrincipalName",
        "displayName",
        "givenName",
        "sn",
        "mail",
        "department",
        "title",
        "userAccountControl"
    ];

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return AuthenticationResult.Failure("Benutzername oder Passwort ist ungültig.");
        }

        var ldapOptions = options.Value;
        var bindName = NormalizeBindName(username, ldapOptions);
        var userSearchName = ExtractSearchName(username, ldapOptions);
        var urls = ldapOptions.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0)
        {
            return AuthenticationResult.Failure("Die Anmeldung ist aktuell nicht konfiguriert.");
        }

        foreach (var urlValue in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var url))
            {
                logger.LogWarning("Ungültige LDAP-URL ignoriert.");
                continue;
            }

            try
            {
                var identity = await AuthenticateAgainstServerAsync(url, bindName, password, userSearchName, ldapOptions, cancellationToken);
                if (identity is not null)
                {
                    return AuthenticationResult.Success(identity);
                }
            }
            catch (LdapException ex)
            {
                logger.LogWarning(ex, "LDAP-Anmeldung gegen {Host} ist fehlgeschlagen.", url.Host);
            }
            catch (DirectoryOperationException ex)
            {
                logger.LogWarning(ex, "LDAP-Suche gegen {Host} ist fehlgeschlagen.", url.Host);
            }
        }

        return AuthenticationResult.Failure("Anmeldung fehlgeschlagen. Bitte prüfe Benutzername und Passwort.");
    }

    private static async Task<DirectoryIdentity?> AuthenticateAgainstServerAsync(
        Uri url,
        string bindName,
        string password,
        string userSearchName,
        LdapOptions ldapOptions,
        CancellationToken cancellationToken)
    {
        var port = url.Port > 0 ? url.Port : url.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389;
        var identifier = new LdapDirectoryIdentifier(url.Host, port, fullyQualifiedDnsHostName: false, connectionless: false);
        using var caCertificate = ConfigureCertificateValidation(ldapOptions.CaCertificatePath);
        using var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindName, password),
            Timeout = TimeSpan.FromSeconds(ldapOptions.ConnectTimeoutSeconds)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = url.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (caCertificate is not null)
        {
            connection.SessionOptions.VerifyServerCertificate = (_, certificate) =>
                VerifyServerCertificate(url.Host, certificate, caCertificate);
        }

        if (url.Scheme.Equals("ldap", StringComparison.OrdinalIgnoreCase))
        {
            if (!ldapOptions.AllowStartTls)
            {
                throw new InvalidOperationException("LDAP ohne StartTLS ist nicht erlaubt.");
            }

            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        await Task.Run(connection.Bind, cancellationToken);

        var searchBase = string.IsNullOrWhiteSpace(ldapOptions.UserBaseDn) ? ldapOptions.BaseDn : ldapOptions.UserBaseDn;
        var filter = $"(&(|(userPrincipalName={EscapeFilter(userSearchName)})(sAMAccountName={EscapeFilter(userSearchName)}))(objectClass=user))";
        var request = new SearchRequest(searchBase, filter, SearchScope.Subtree, Attributes)
        {
            SizeLimit = 2,
            TimeLimit = TimeSpan.FromSeconds(ldapOptions.OperationTimeoutSeconds)
        };

        var response = (SearchResponse)await Task.Run(() => connection.SendRequest(request), cancellationToken);
        if (response.Entries.Count != 1)
        {
            return null;
        }

        var entry = response.Entries[0];
        if (IsDisabled(entry))
        {
            return null;
        }

        var sam = GetString(entry, "sAMAccountName") ?? userSearchName;
        var upn = GetString(entry, "userPrincipalName") ?? $"{sam}@{ldapOptions.UpnSuffix}";
        var given = GetString(entry, "givenName");
        var surname = GetString(entry, "sn");
        var nameFromParts = string.Join(' ', new[] { given, surname }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var display = GetString(entry, "displayName");
        if (string.IsNullOrWhiteSpace(display))
        {
            display = string.IsNullOrWhiteSpace(nameFromParts) ? sam : nameFromParts;
        }

        return new DirectoryIdentity(
            GetGuid(entry, "objectGUID"),
            GetSid(entry, "objectSid"),
            sam,
            upn,
            display,
            given,
            surname,
            GetString(entry, "mail"),
            GetString(entry, "department"),
            GetString(entry, "title"));
    }

    private static bool IsDisabled(SearchResultEntry entry)
    {
        var userAccountControl = GetString(entry, "userAccountControl");
        return int.TryParse(userAccountControl, out var flags) && (flags & 0x0002) != 0;
    }

    private static string NormalizeBindName(string username, LdapOptions options)
    {
        var trimmed = username.Trim();
        if (trimmed.Contains('@', StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return $"{trimmed}@{options.UpnSuffix}";
    }

    private static string ExtractSearchName(string username, LdapOptions options)
    {
        var trimmed = username.Trim();
        if (trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return trimmed[(trimmed.IndexOf('\\', StringComparison.Ordinal) + 1)..];
        }

        if (trimmed.EndsWith($"@{options.UpnSuffix}", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed;
    }

    private static string EscapeFilter(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static string? GetString(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName) || entry.Attributes[attributeName].Count == 0)
        {
            return null;
        }

        return entry.Attributes[attributeName][0]?.ToString();
    }

    private static string GetGuid(SearchResultEntry entry, string attributeName)
    {
        var values = entry.Attributes[attributeName].GetValues(typeof(byte[]));
        return values.Length > 0 && values[0] is byte[] bytes ? new Guid(bytes).ToString("D") : throw new InvalidOperationException("objectGUID fehlt.");
    }

    private static string GetSid(SearchResultEntry entry, string attributeName)
    {
        var values = entry.Attributes[attributeName].GetValues(typeof(byte[]));
        return values.Length > 0 && values[0] is byte[] bytes ? ConvertSidToString(bytes) : "";
    }

    private static string ConvertSidToString(byte[] sid)
    {
        if (sid.Length < 8)
        {
            return "";
        }

        var revision = sid[0];
        var subAuthorityCount = sid[1];
        var identifierAuthority = 0L;
        for (var index = 2; index < 8; index++)
        {
            identifierAuthority = (identifierAuthority << 8) | sid[index];
        }

        var parts = new List<string> { $"S-{revision}-{identifierAuthority}" };
        var offset = 8;
        for (var index = 0; index < subAuthorityCount && offset + 4 <= sid.Length; index++, offset += 4)
        {
            var value = BitConverter.ToUInt32(sid, offset);
            parts.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join('-', parts);
    }

    private static X509Certificate2? LoadCaCertificate(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : X509CertificateLoader.LoadCertificateFromFile(path);
    }

    private static X509Certificate2? ConfigureCertificateValidation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("LDAPTLS_CACERT", path);
            return null;
        }

        return LoadCaCertificate(path);
    }

    private static bool VerifyServerCertificate(string host, X509Certificate certificate, X509Certificate2 caCertificate)
    {
        using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        using var chain = new X509Chain();
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(serverCertificate) && CertificateMatchesHost(serverCertificate, host);
    }

    private static bool CertificateMatchesHost(X509Certificate2 certificate, string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.');
        var dnsName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return HostMatchesPattern(dnsName, normalizedHost) || HostMatchesPattern(simpleName, normalizedHost);
    }

    private static bool HostMatchesPattern(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedPattern = pattern.Trim().TrimEnd('.');
        if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalizedPattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Count(character => character == '.') == suffix.Count(character => character == '.');
        }

        return string.Equals(normalizedPattern, host, StringComparison.OrdinalIgnoreCase);
    }
}
