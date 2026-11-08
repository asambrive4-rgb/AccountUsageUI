namespace AccountUsageUI.Core.Profiles;

public sealed class ProfileOperationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBusy => _gate.CurrentCount == 0;

    public async Task<IDisposable?> TryEnterAsync(
        CancellationToken cancellationToken)
    {
        var entered = await _gate.WaitAsync(
            millisecondsTimeout: 0,
            cancellationToken);
        return entered
            ? new OperationLease(_gate)
            : null;
    }

    private sealed class OperationLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public OperationLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
