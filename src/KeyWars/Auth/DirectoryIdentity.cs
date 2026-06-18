namespace KeyWars.Auth;

public sealed record DirectoryIdentity(
    string ObjectGuid,
    string ObjectSid,
    string SamAccountName,
    string UserPrincipalName,
    string DisplayName,
    string? GivenName,
    string? Surname,
    string? Email,
    string? Department,
    string? Title);

public sealed record AuthenticationResult(bool Succeeded, DirectoryIdentity? Identity, string? FailureMessage)
{
    public static AuthenticationResult Success(DirectoryIdentity identity) => new(true, identity, null);
    public static AuthenticationResult Failure(string message) => new(false, null, message);
}

public interface ILdapAuthenticator
{
    Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
}
