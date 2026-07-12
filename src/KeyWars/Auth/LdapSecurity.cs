using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace KeyWars.Auth;

public enum LdapFailureKind
{
    InvalidCredentials,
    Unavailable,
    DirectoryOperation,
    Configuration,
    Unknown
}

public static class LdapSecurity
{
    public static string NormalizeBindName(string username, LdapOptions options)
    {
        var trimmed = username.Trim();
        return trimmed.Contains('@', StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal)
            ? trimmed
            : $"{trimmed}@{options.UpnSuffix}";
    }

    public static string ExtractSearchName(string username)
    {
        var trimmed = username.Trim();
        if (trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return trimmed[(trimmed.IndexOf('\\', StringComparison.Ordinal) + 1)..];
        }

        return trimmed;
    }

    public static string EscapeFilter(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    public static bool HostMatchesPattern(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedPattern = pattern.Trim().TrimEnd('.');
        var normalizedHost = host.Trim().TrimEnd('.');
        if (normalizedPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalizedPattern[1..];
            return normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && normalizedHost.Count(character => character == '.') == suffix.Count(character => character == '.');
        }

        return string.Equals(normalizedPattern, normalizedHost, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CertificateMatchesHost(X509Certificate2 certificate, string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.');
        if (normalizedHost.StartsWith("[", StringComparison.Ordinal) &&
            normalizedHost.EndsWith("]", StringComparison.Ordinal))
        {
            normalizedHost = normalizedHost[1..^1];
        }

        var subjectAlternativeName = certificate.Extensions
            .FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17");
        if (subjectAlternativeName is not null)
        {
            var alternativeNames = new X509SubjectAlternativeNameExtension(
                subjectAlternativeName.RawData,
                subjectAlternativeName.Critical);
            if (IPAddress.TryParse(normalizedHost, out var address))
            {
                return alternativeNames.EnumerateIPAddresses().Any(candidate => candidate.Equals(address));
            }

            return alternativeNames.EnumerateDnsNames()
                .Any(candidate => HostMatchesPattern(candidate, normalizedHost));
        }

        if (IPAddress.TryParse(normalizedHost, out _))
        {
            return false;
        }

        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return HostMatchesPattern(commonName, normalizedHost);
    }

    public static LdapFailureKind ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            LdapException { ErrorCode: 49 } => LdapFailureKind.InvalidCredentials,
            LdapException { ErrorCode: 81 or 85 or 91 } => LdapFailureKind.Unavailable,
            DirectoryOperationException => LdapFailureKind.DirectoryOperation,
            InvalidOperationException => LdapFailureKind.Configuration,
            _ => LdapFailureKind.Unknown
        };
    }
}
