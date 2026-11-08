namespace AccountUsageUI.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\AccountUsageUI.SingleInstance";
    private const string ActivateEventName = @"Local\AccountUsageUI.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
    }

    public bool IsPrimaryInstance { get; }

    public void StartListening(Action onActivateRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivateRequested);
        if (!IsPrimaryInstance || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _activateEvent, _cts.Token.WaitHandle };
            while (!_cts.IsCancellationRequested)
            {
                var signaledIndex = WaitHandle.WaitAny(handles);
                if (signaledIndex == 0)
                {
                    onActivateRequested();
                }
                else
                {
                    break;
                }
            }
        });
    }

    public void SignalPrimaryInstance()
    {
        try
        {
            _activateEvent.Set();
        }
        catch
        {
            // Ignore when primary instance is shutting down.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        try
        {
            if (IsPrimaryInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _activateEvent.Dispose();
        _cts.Dispose();
    }
}
