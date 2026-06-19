using KeyWars.Auth;
using KeyWars.Services;

namespace KeyWars.Infrastructure;

public static class ConfigurationAliases
{
    public static void BindLdap(IConfiguration configuration, LdapOptions options)
    {
        var section = configuration.GetSection("KEYWARS:LDAP");
        section.Bind(options);
        SetString(section, "URLS", value => options.Urls = value);
        SetString(section, "BASE_DN", value => options.BaseDn = value);
        SetString(section, "UPN_SUFFIX", value => options.UpnSuffix = value);
        SetString(section, "NETBIOS_DOMAIN", value => options.NetbiosDomain = value);
        SetString(section, "USER_BASE_DN", value => options.UserBaseDn = value);
        SetString(section, "CA_CERTIFICATE_PATH", value => options.CaCertificatePath = value);
        SetInt(section, "CONNECT_TIMEOUT_SECONDS", value => options.ConnectTimeoutSeconds = value);
        SetInt(section, "OPERATION_TIMEOUT_SECONDS", value => options.OperationTimeoutSeconds = value);
        SetBool(section, "ALLOW_STARTTLS", value => options.AllowStartTls = value);
    }

    public static void BindAuth(IConfiguration configuration, AuthOptions options)
    {
        var section = configuration.GetSection("KEYWARS:AUTH");
        section.Bind(options);
        SetInt(section, "COOKIE_LIFETIME_HOURS", value => options.CookieLifetimeHours = value);
        SetBool(section, "DEVELOPMENT_LOGIN", value => options.DevelopmentLogin = value);
    }

    public static AuthOptions GetAuth(IConfiguration configuration)
    {
        var options = new AuthOptions();
        BindAuth(configuration, options);
        return options;
    }

    public static LdapOptions GetLdap(IConfiguration configuration)
    {
        var options = new LdapOptions();
        BindLdap(configuration, options);
        return options;
    }

    public static void BindLive(IConfiguration configuration, LiveOptions options)
    {
        var section = configuration.GetSection("KEYWARS:LIVE");
        section.Bind(options);
        SetInt(section, "MAX_PARTICIPANTS_PER_ROOM", value => options.MaxParticipantsPerRoom = value);
        SetInt(section, "MAX_SPECTATORS_PER_ROOM", value => options.MaxSpectatorsPerRoom = value);
        SetInt(section, "MAX_CONCURRENT_ROOMS", value => options.MaxConcurrentRooms = value);
        SetInt(section, "MAX_CONNECTIONS_PER_USER", value => options.MaxConnectionsPerUser = value);
        SetInt(section, "PROGRESS_BROADCAST_HZ", value => options.ProgressBroadcastHz = value);
        SetInt(section, "COUNTDOWN_SECONDS", value => options.CountdownSeconds = value);
        SetInt(section, "RECONNECT_GRACE_SECONDS", value => options.ReconnectGraceSeconds = value);
        SetInt(section, "ROOM_COMMAND_QUEUE_CAPACITY", value => options.RoomCommandQueueCapacity = value);
        SetInt(section, "COMPLETION_QUEUE_CAPACITY", value => options.CompletionQueueCapacity = value);
        SetInt(section, "COMPLETED_ROOM_RETENTION_MINUTES", value => options.CompletedRoomRetentionMinutes = value);
        SetInt(section, "LOBBY_ROOM_RETENTION_MINUTES", value => options.LobbyRoomRetentionMinutes = value);
    }

    public static void BindChallenges(IConfiguration configuration, ChallengeOptions options)
    {
        var section = configuration.GetSection("KEYWARS:CHALLENGES");
        section.Bind(options);
        SetInt(section, "MAX_PARTICIPANTS", value => options.MaxParticipants = value);
    }

    public static void BindContent(IConfiguration configuration, ContentOptions options)
    {
        var section = configuration.GetSection("KEYWARS:CONTENT");
        section.Bind(options);
        SetInt(section, "MAX_UPLOAD_BYTES", value => options.MaxUploadBytes = value);
        SetInt(section, "MAX_TEXT_CHARACTERS", value => options.MaxTextCharacters = value);
        SetInt(section, "MAX_TEXT_GRAPHEMES", value => options.MaxTextGraphemes = value);
        SetInt(section, "MAX_TEXT_LINES", value => options.MaxTextLines = value);
    }

    private static void SetString(IConfiguration section, string key, Action<string> set)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            set(value);
        }
    }

    private static void SetInt(IConfiguration section, string key, Action<int> set)
    {
        if (int.TryParse(section[key], out var value))
        {
            set(value);
        }
    }

    private static void SetBool(IConfiguration section, string key, Action<bool> set)
    {
        if (bool.TryParse(section[key], out var value))
        {
            set(value);
        }
    }
}
