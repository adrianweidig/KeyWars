using KeyWars.Data;

namespace KeyWars.Infrastructure;

public static class StartupValidator
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        var dataDirectory = DataPaths.Resolve(configuration, environment);
        DataPaths.EnsureWritable(dataDirectory);

        var ldapOptions = ConfigurationAliases.GetLdap(configuration);
        var authOptions = ConfigurationAliases.GetAuth(configuration);
        if (environment.IsDevelopment())
        {
            return;
        }

        var urls = ldapOptions.Urls;
        var baseDn = ldapOptions.BaseDn;
        var upnSuffix = ldapOptions.UpnSuffix;
        var developmentAuth = authOptions.DevelopmentLogin;
        var allowStartTls = ldapOptions.AllowStartTls;
        var caCertificatePath = ldapOptions.CaCertificatePath;

        if (string.IsNullOrWhiteSpace(urls))
        {
            throw new InvalidOperationException("KEYWARS__LDAP__URLS ist ausserhalb von Development erforderlich.");
        }

        if (string.IsNullOrWhiteSpace(baseDn))
        {
            throw new InvalidOperationException("KEYWARS__LDAP__BASE_DN ist ausserhalb von Development erforderlich.");
        }

        if (string.IsNullOrWhiteSpace(upnSuffix))
        {
            throw new InvalidOperationException("KEYWARS__LDAP__UPN_SUFFIX ist ausserhalb von Development erforderlich.");
        }

        if (developmentAuth)
        {
            throw new InvalidOperationException("Development-Auth darf ausserhalb von Development nicht aktiviert sein.");
        }

        if (ldapOptions.ConnectTimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("KEYWARS__LDAP__CONNECT_TIMEOUT_SECONDS muss zwischen 1 und 60 liegen.");
        }

        if (ldapOptions.OperationTimeoutSeconds is < 1 or > 120)
        {
            throw new InvalidOperationException("KEYWARS__LDAP__OPERATION_TIMEOUT_SECONDS muss zwischen 1 und 120 liegen.");
        }

        if (!string.IsNullOrWhiteSpace(caCertificatePath) && !File.Exists(caCertificatePath))
        {
            throw new InvalidOperationException($"KEYWARS__LDAP__CA_CERTIFICATE_PATH wurde nicht gefunden: {caCertificatePath}");
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

            throw new InvalidOperationException("KeyWars erlaubt ausserhalb von Development nur ldaps:// oder ldap:// mit KEYWARS__LDAP__ALLOW_STARTTLS=true.");
        }

        logger.LogInformation("Startvalidierung fuer nicht-lokale Umgebung abgeschlossen.");
    }
}
