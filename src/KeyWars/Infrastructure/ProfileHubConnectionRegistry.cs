using System.Collections.Concurrent;
using KeyWars.Hubs;
using KeyWars.Services;
using Microsoft.AspNetCore.SignalR;

namespace KeyWars.Infrastructure;

public sealed record ProfileHubConnectionRegistration(
    Guid ProfileId,
    string ConnectionId,
    string OwnerId,
    string Epoch,
    long Generation,
    Guid? RoomId);

public sealed record ProfileHubRevocation(
    string Epoch,
    long Generation,
    IReadOnlyList<ProfileHubConnectionRegistration> Connections);

public interface IProfileHubConnectionStateStore
{
    ValueTask<ProfileHubConnectionRegistration> RegisterAsync(
        Guid profileId,
        string connectionId,
        string ownerId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RefreshAsync(
        ProfileHubConnectionRegistration registration,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SetRoomAsync(
        ProfileHubConnectionRegistration registration,
        Guid? roomId,
        CancellationToken cancellationToken = default);

    ValueTask UnregisterAsync(
        ProfileHubConnectionRegistration registration,
        CancellationToken cancellationToken = default);

    ValueTask<ProfileHubRevocation> RevokeAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask<(string Epoch, long Generation)> ReadVersionAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    ValueTask PublishRevocationAsync(
        Guid profileId,
        string epoch,
        long generation,
        CancellationToken cancellationToken = default);
}

public sealed class LocalProfileHubConnectionStateStore : IProfileHubConnectionStateStore
{
    private const int MaximumConnectionsPerProfile = 64;
    private readonly object gate = new();
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private readonly Dictionary<Guid, long> generations = [];
    private readonly Dictionary<string, ProfileHubConnectionRegistration> connections =
        new(StringComparer.Ordinal);

    public ValueTask<ProfileHubConnectionRegistration> RegisterAsync(
        Guid profileId,
        string connectionId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var activeCount = connections.Values.Count(item => item.ProfileId == profileId);
            if (!connections.ContainsKey(connectionId) && activeCount >= MaximumConnectionsPerProfile)
            {
                throw new InvalidOperationException(
                    $"Es sind maximal {MaximumConnectionsPerProfile} aktive Arena-Verbindungen pro Person erlaubt.");
            }

            var generation = generations.GetValueOrDefault(profileId);
            var registration = new ProfileHubConnectionRegistration(
                profileId,
                connectionId,
                ownerId,
                epoch,
                generation,
                null);
            connections[connectionId] = registration;
            return ValueTask.FromResult(registration);
        }
    }

    public ValueTask<bool> RefreshAsync(
        ProfileHubConnectionRegistration registration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult(
                registration.Epoch == epoch &&
                generations.GetValueOrDefault(registration.ProfileId) == registration.Generation &&
                connections.ContainsKey(registration.ConnectionId));
        }
    }

    public ValueTask<bool> SetRoomAsync(
        ProfileHubConnectionRegistration registration,
        Guid? roomId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (registration.Epoch != epoch ||
                generations.GetValueOrDefault(registration.ProfileId) != registration.Generation ||
                !connections.ContainsKey(registration.ConnectionId))
            {
                return ValueTask.FromResult(false);
            }

            connections[registration.ConnectionId] = registration with { RoomId = roomId };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask UnregisterAsync(
        ProfileHubConnectionRegistration registration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            connections.Remove(registration.ConnectionId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ProfileHubRevocation> RevokeAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var generation = checked(generations.GetValueOrDefault(profileId) + 1);
            generations[profileId] = generation;
            var snapshot = connections.Values
                .Where(item => item.ProfileId == profileId && item.Generation < generation)
                .ToArray();
            return ValueTask.FromResult(new ProfileHubRevocation(epoch, generation, snapshot));
        }
    }

    public ValueTask<(string Epoch, long Generation)> ReadVersionAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return ValueTask.FromResult((epoch, generations.GetValueOrDefault(profileId)));
        }
    }

    public ValueTask PublishRevocationAsync(
        Guid profileId,
        string epoch,
        long generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class ProfileHubConnectionRegistry(
    IProfileHubConnectionStateStore stateStore,
    IHubContext<ArenaHub> hubContext,
    ILogger<ProfileHubConnectionRegistry> logger) : BackgroundService, IProfileHubConnectionRevoker
{
    private const int MaximumRevocationEntries = 256;
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, LocalConnection> localConnections =
        new(StringComparer.Ordinal);
    private readonly string ownerId = Guid.NewGuid().ToString("N");

    internal async ValueTask RegisterAsync(
        Guid profileId,
        HubCallerContext context,
        CancellationToken cancellationToken)
    {
        var registration = await stateStore.RegisterAsync(
            profileId,
            context.ConnectionId,
            ownerId,
            cancellationToken);
        var local = new LocalConnection(context, registration);
        if (!localConnections.TryAdd(context.ConnectionId, local))
        {
            await stateStore.UnregisterAsync(registration, CancellationToken.None);
            throw new InvalidOperationException("Die Arena-Verbindung ist bereits registriert.");
        }
    }

    internal async ValueTask SetRoomAsync(
        string connectionId,
        Guid? roomId,
        CancellationToken cancellationToken)
    {
        if (!localConnections.TryGetValue(connectionId, out var local))
        {
            throw new InvalidOperationException("Die Arena-Verbindung ist nicht registriert.");
        }

        await local.Mutation.WaitAsync(cancellationToken);
        try
        {
            if (local.IsRevoking)
            {
                local.Context.Abort();
                throw new HubException("Die Profilsitzung ist nicht mehr gültig.");
            }

            var current = local.Registration;
            if (!await stateStore.SetRoomAsync(current, roomId, cancellationToken))
            {
                local.Context.Abort();
                throw new HubException("Die Profilsitzung ist nicht mehr gültig.");
            }

            local.Registration = current with { RoomId = roomId };
        }
        finally
        {
            local.Mutation.Release();
        }
    }

    internal async ValueTask UnregisterAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (localConnections.TryRemove(connectionId, out var local))
        {
            await stateStore.UnregisterAsync(local.Registration, cancellationToken);
        }
    }

    public async Task RevokeProfileAsync(
        Guid profileId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var revocation = await stateStore.RevokeAsync(profileId, cancellationToken);
        if (revocation.Connections.Count > MaximumRevocationEntries)
        {
            throw new InvalidOperationException("Das Profil-Verbindungsverzeichnis überschreitet die sichere Obergrenze.");
        }

        foreach (var registration in revocation.Connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registration.RoomId is { } roomId)
            {
                await hubContext.Groups.RemoveFromGroupAsync(
                    registration.ConnectionId,
                    roomId.ToString("N"),
                    cancellationToken);
            }
        }

        await stateStore.PublishRevocationAsync(
            profileId,
            revocation.Epoch,
            revocation.Generation,
            cancellationToken);
        await ApplyRevocationAsync(
            profileId,
            revocation.Epoch,
            revocation.Generation,
            reason,
            cancellationToken);
    }

    internal async Task ApplyRevocationAsync(
        Guid profileId,
        string epoch,
        long generation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var targets = localConnections.Values
            .Where(item => item.Registration.ProfileId == profileId &&
                (item.Registration.Epoch != epoch || item.Registration.Generation < generation))
            .ToArray();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyConnectionRevocationAsync(target, reason, cancellationToken);
        }
    }

    internal async Task ApplyPublishedRevocationAsync(
        Guid profileId,
        string epoch,
        long generation,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var current = await stateStore.ReadVersionAsync(profileId, cancellationToken);
        if (!StringComparer.Ordinal.Equals(current.Epoch, epoch) || current.Generation < generation)
        {
            return;
        }

        await ApplyRevocationAsync(
            profileId,
            current.Epoch,
            current.Generation,
            reason,
            cancellationToken);
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var profiles = localConnections.Values
            .GroupBy(item => item.Registration.ProfileId)
            .ToArray();
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileProfileAsync(profile.Key, profile.ToArray(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Arena-Verbindungswiderrufe konnten für Profil {ProfileId} nicht abgeglichen werden.",
                    profile.Key);
            }
        }
    }

    private async Task ReconcileProfileAsync(
        Guid profileId,
        IReadOnlyList<LocalConnection> connections,
        CancellationToken cancellationToken)
    {
        var version = await stateStore.ReadVersionAsync(profileId, cancellationToken);
        foreach (var item in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Registration.Epoch != version.Epoch ||
                item.Registration.Generation < version.Generation)
            {
                await ApplyConnectionRevocationAsync(
                    item,
                    "Deine Arena-Sitzung wurde widerrufen.",
                    cancellationToken);
                continue;
            }

            await item.Mutation.WaitAsync(cancellationToken);
            try
            {
                if (item.IsRevoking)
                {
                    continue;
                }

                var registration = item.Registration;
                if (registration.Epoch != version.Epoch ||
                    registration.Generation < version.Generation ||
                    !await stateStore.RefreshAsync(registration, cancellationToken))
                {
                    item.MarkRevocationRequired();
                }
            }
            finally
            {
                item.Mutation.Release();
            }

            if (item.ConsumeRevocationRequired())
            {
                await ApplyConnectionRevocationAsync(
                    item,
                    "Deine Arena-Sitzung wurde widerrufen.",
                    cancellationToken);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Arena-Verbindungswiderrufe konnten nicht vollständig abgeglichen werden.");
            }

            try
            {
                await Task.Delay(ReconciliationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ApplyConnectionRevocationAsync(
        LocalConnection target,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!target.TryBeginRevocation())
        {
            return;
        }

        await target.Mutation.WaitAsync(cancellationToken);
        ProfileHubConnectionRegistration registration;
        try
        {
            registration = target.Registration;
        }
        finally
        {
            target.Mutation.Release();
        }

        try
        {
            if (registration.RoomId is { } roomId)
            {
                await hubContext.Groups.RemoveFromGroupAsync(
                    registration.ConnectionId,
                    roomId.ToString("N"),
                    cancellationToken);
            }

            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCancellation.CancelAfter(SendTimeout);
            try
            {
                await hubContext.Clients.Client(registration.ConnectionId).SendAsync(
                    "profileRevoked",
                    reason,
                    sendCancellation.Token);
            }
            catch (OperationCanceledException) when (sendCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Die Widerrufsnachricht konnte nicht an die Arena-Verbindung gesendet werden.");
            }
        }
        finally
        {
            target.Context.Abort();
            localConnections.TryRemove(registration.ConnectionId, out _);
            try
            {
                await stateStore.UnregisterAsync(registration, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Die widerrufene Arena-Verbindung konnte nicht aus dem Verzeichnis entfernt werden.");
            }
        }
    }

    private sealed class LocalConnection(
        HubCallerContext context,
        ProfileHubConnectionRegistration registration)
    {
        private int revocationStarted;
        private int revocationRequired;

        public HubCallerContext Context { get; } = context;
        public ProfileHubConnectionRegistration Registration { get; set; } = registration;
        public SemaphoreSlim Mutation { get; } = new(1, 1);
        public bool IsRevoking => Volatile.Read(ref revocationStarted) != 0;

        public bool TryBeginRevocation() =>
            Interlocked.CompareExchange(ref revocationStarted, 1, 0) == 0;

        public void MarkRevocationRequired() =>
            Interlocked.Exchange(ref revocationRequired, 1);

        public bool ConsumeRevocationRequired() =>
            Interlocked.Exchange(ref revocationRequired, 0) != 0;
    }
}
