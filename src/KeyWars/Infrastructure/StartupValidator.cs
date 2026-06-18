using KeyWars.Data;

namespace KeyWars.Infrastructure;

public static class StartupValidator
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var dataDirectory = DataPaths.Resolve(configuration, environment);
        DataPaths.EnsureWritable(dataDirectory);

        if (!environment.IsProduction())
        {
            return;
        }

        var ldapOptions = ConfigurationAliases.GetLdap(configuration);
        var authOptions = ConfigurationAliases.GetAuth(configuration);
        var urls = ldapOptions.Urls;
        var baseDn = ldapOptions.BaseDn;
        var upnSuffix = ldapOptions.UpnSuffix;
        var developmentAuth = authOptions.DevelopmentLogin;
        var allowStartTls = ldapOptions.AllowStartTls;

        if (string.IsNullOrWhiteSpace(urls))
        {
            throw new InvalidOperationException("KEYWARS__LDAP__URLS ist in Production erforderlich.");
        }

        if (string.IsNullOrWhiteSpace(baseDn))
        {
            throw new InvalidOperationException("KEYWARS__LDAP__BASE_DN ist in Production erforderlich.");
        }

        if (string.IsNullOrWhiteSpace(upnSuffix))
        {
            throw new InvalidOperationException("KEYWARS__LDAP__UPN_SUFFIX ist in Production erforderlich.");
        }

        if (developmentAuth)
        {
            throw new InvalidOperationException("Development-Auth darf in Production nicht aktiviert sein.");
        }

        foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"Ungültige LDAP-URL: {value}");
            }

            if (uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (uri.Scheme.Equals("ldap", StringComparison.OrdinalIgnoreCase) && allowStartTls)
            {
                continue;
            }

            throw new InvalidOperationException("Production erlaubt nur ldaps:// oder ldap:// mit KEYWARS__LDAP__ALLOW_STARTTLS=true.");
        }

        logger.LogInformation("Production-Startvalidierung abgeschlossen.");
    }
}
