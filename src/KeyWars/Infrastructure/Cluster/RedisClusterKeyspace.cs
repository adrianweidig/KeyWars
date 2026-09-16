using System.Security.Cryptography;

namespace KeyWars.Infrastructure.Cluster;

internal static class RedisClusterKeyspace
{
    public const int BucketCount = 256;

    internal static byte GetBucket(Guid id)
    {
        Span<byte> canonicalBytes = stackalloc byte[16];
        if (!id.TryWriteBytes(canonicalBytes, bigEndian: true, out var bytesWritten) || bytesWritten != canonicalBytes.Length)
        {
            throw new InvalidOperationException("Die Guid konnte nicht kanonisch serialisiert werden.");
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(canonicalBytes, digest);
        return digest[0];
    }

    internal static string GetBucketHex(Guid id) => GetBucket(id).ToString("x2");

    internal static string GetHashTag(string subsystem, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        if (subsystem.IndexOfAny(['{', '}']) >= 0)
        {
            throw new ArgumentException("Das Redis-Subsystem darf keine Hash-Tag-Klammern enthalten.", nameof(subsystem));
        }

        return $"{{{subsystem}-b{GetBucketHex(id)}}}";
    }
}
