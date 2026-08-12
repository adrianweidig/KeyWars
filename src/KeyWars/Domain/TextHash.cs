using System.Security.Cryptography;
using System.Text;

namespace KeyWars.Domain;

public static class TextHash
{
    public static string Compute(string text)
    {
        var normalized = TypingEngine.NormalizeText(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
