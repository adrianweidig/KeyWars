using System.Text.Json;

namespace KeyWars.Data;

public sealed class DatabaseRuntimeLock(IConfiguration configuration, IHostEnvironment environment)
{
    public const string LockFileName = ".keywars-runtime.lock";

    public string LockPath => Path.Combine(DataPaths.Resolve(configuration, environment), LockFileName);

    public DatabaseRuntimeLockLease Acquire(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, new RuntimeLockMetadata(owner, Environment.ProcessId, DateTimeOffset.UtcNow));
            stream.Flush(flushToDisk: true);
            return new DatabaseRuntimeLockLease(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new InvalidOperationException(
                $"Der exklusive KeyWars-Datenbank-Lock konnte nicht erworben werden: {LockPath}",
                exception);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private sealed record RuntimeLockMetadata(string Owner, int ProcessId, DateTimeOffset AcquiredAtUtc);
}

public sealed class DatabaseRuntimeLockLease : IDisposable
{
    private FileStream? stream;

    internal DatabaseRuntimeLockLease(FileStream stream)
    {
        this.stream = stream;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref stream, null)?.Dispose();
    }
}
