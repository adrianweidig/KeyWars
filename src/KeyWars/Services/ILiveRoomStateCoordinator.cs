namespace KeyWars.Services;

public interface ILiveRoomStateCoordinator
{
    bool IsAuthoritative { get; }
    string InstanceId { get; }
}

public sealed class SingleNodeLiveRoomStateCoordinator : ILiveRoomStateCoordinator
{
    public bool IsAuthoritative => true;
    public string InstanceId { get; } = Environment.MachineName;
}
