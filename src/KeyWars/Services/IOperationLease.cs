namespace KeyWars.Services;

public interface IOperationLease : IAsyncDisposable
{
    CancellationToken LeaseLost { get; }
    void ThrowIfLost();
}
