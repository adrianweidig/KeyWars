using System.Security.Cryptography;
using System.Text;

namespace KeyWars.Auth;

public sealed class DevelopmentDirectoryAuthenticator(ILogger<DevelopmentDirectoryAuthenticator> logger) : ILdapAuthenticator
{
    public Task<AuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(AuthenticationResult.Failure("Benutzername oder Passwort ist ungültig."));
        }

        var normalized = username.Trim();
        var sam = normalized.Contains('@', StringComparison.Ordinal)
            ? normalized[..normalized.IndexOf('@', StringComparison.Ordinal)]
            : normalized.Contains('\\', StringComparison.Ordinal)
                ? normalized[(normalized.IndexOf('\\', StringComparison.Ordinal) + 1)..]
                : normalized;

        var guid = DeterministicGuid(normalized.ToLowerInvariant());
        var displayName = BuildDisplayName(sam);
        logger.LogInformation("Development-Anmeldung für {SamAccountName} provisioniert.", sam);

        return Task.FromResult(AuthenticationResult.Success(new DirectoryIdentity(
            guid.ToString("D"),
            $"S-1-5-21-DEV-{Math.Abs(guid.GetHashCode())}",
            sam,
            normalized.Contains('@', StringComparison.Ordinal) ? normalized : $"{sam}@development.local",
            displayName,
            displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault(),
            $"{sam}@development.local",
            "Entwicklung",
            "Testperson")));
    }

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static string BuildDisplayName(string sam)
    {
        var parts = sam.Replace('.', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "Entwicklung";
        }

        return string.Join(' ', parts.Select(part => string.Create(part.Length, part, static (span, source) =>
        {
            source.AsSpan().ToLowerInvariant(span);
            span[0] = char.ToUpperInvariant(span[0]);
        })));
    }
}
