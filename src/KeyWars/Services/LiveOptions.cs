namespace KeyWars.Services;

public sealed class LiveOptions
{
    public const int MaximumSafeArenaTargetGraphemes = 2800;
    public const int MaximumSafeArenaTargetUtf8Bytes = 12 * 1024;

    public int MaxParticipantsPerRoom { get; set; } = 64;
    public int MaxConcurrentRooms { get; set; } = 200;
    public int MaxConnectionsPerUser { get; set; } = 3;
    public int ProgressBroadcastHz { get; set; } = 10;
    public int CountdownSeconds { get; set; } = 3;
    public int ReconnectGraceSeconds { get; set; } = 30;
    public int RoomCommandQueueCapacity { get; set; } = 4096;
    public int CompletionQueueCapacity { get; set; } = 4096;
    public int CompletionDrainTimeoutSeconds { get; set; } = 10;
    public int CompletedRoomRetentionMinutes { get; set; } = 60;
    public int LobbyRoomRetentionMinutes { get; set; } = 720;
    public int MaxArenaTargetGraphemes { get; set; } = MaximumSafeArenaTargetGraphemes;
}

public sealed class ChallengeOptions
{
    public int MaxParticipants { get; set; } = 64;
}

public sealed class ContentOptions
{
    public int MaxUploadBytes { get; set; } = 131072;
    public int MaxTextCharacters { get; set; } = 20000;
    public int MaxTextGraphemes { get; set; } = 20000;
    public int MaxTextLines { get; set; } = 400;
}
