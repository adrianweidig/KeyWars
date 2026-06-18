namespace KeyWars.Auth;

public sealed class LdapOptions
{
    public string Urls { get; set; } = "";
    public string BaseDn { get; set; } = "";
    public string UpnSuffix { get; set; } = "";
    public string? NetbiosDomain { get; set; }
    public string? UserBaseDn { get; set; }
    public string? CaCertificatePath { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int OperationTimeoutSeconds { get; set; } = 10;
    public bool AllowStartTls { get; set; }
}

public sealed class AuthOptions
{
    public int CookieLifetimeHours { get; set; } = 8;
    public bool DevelopmentLogin { get; set; }
}
