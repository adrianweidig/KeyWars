namespace KeyWars.Services;

public sealed class LiveOptions
{
    public int MaxParticipantsPerRoom { get; set; } = 64;
    public int MaxSpectatorsPerRoom { get; set; } = 128;
    public int MaxConcurrentRooms { get; set; } = 200;
    public int MaxConnectionsPerUser { get; set; } = 3;
    public int ProgressBroadcastHz { get; set; } = 10;
    public int ReconnectGraceSeconds { get; set; } = 30;
    public int RoomCommandQueueCapacity { get; set; } = 4096;
}

public sealed class ChallengeOptions
{
    public int MaxParticipants { get; set; } = 64;
}

public sealed class ContentOptions
{
    public int MaxUploadBytes { get; set; } = 131072;
}
