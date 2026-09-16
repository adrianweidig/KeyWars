namespace KeyWars.Services;

public interface IProfileHubConnectionRevoker
{
    Task RevokeProfileAsync(
        Guid profileId,
        string reason,
        CancellationToken cancellationToken = default);
}
