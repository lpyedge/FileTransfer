internal sealed class TransferConcurrencyGate : IDisposable
{
    private readonly ConcurrentDictionary<SemaphoreSlim, int> _semaphoreUsage = new();
    private readonly ConcurrentDictionary<SemaphoreSlim, byte> _retiredSemaphores = new();
    private SemaphoreSlim _semaphore;
    private int _currentLimit;

    public TransferConcurrencyGate(int initialLimit)
    {
        _currentLimit = Math.Max(1, initialLimit);
        _semaphore = new SemaphoreSlim(_currentLimit);
    }

    public void UpdateLimit(int newLimit)
    {
        newLimit = Math.Max(1, newLimit);
        if (newLimit == _currentLimit)
        {
            return;
        }

        var next = new SemaphoreSlim(newLimit);
        var previous = Interlocked.Exchange(ref _semaphore, next);
        _currentLimit = newLimit;
        Retire(previous);
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var semaphore = _semaphore;
        RegisterUsage(semaphore);
        try
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, semaphore);
        }
        catch
        {
            ReleaseUsage(semaphore);
            throw;
        }
    }

    public void Dispose()
    {
        Retire(_semaphore);
    }

    private void RegisterUsage(SemaphoreSlim semaphore)
    {
        _semaphoreUsage.AddOrUpdate(semaphore, 1, static (_, count) => count + 1);
    }

    private void ReleaseUsage(SemaphoreSlim semaphore)
    {
        var remaining = _semaphoreUsage.AddOrUpdate(semaphore, 0, static (_, count) => Math.Max(0, count - 1));
        if (remaining == 0)
        {
            _semaphoreUsage.TryRemove(semaphore, out _);
            if (_retiredSemaphores.TryRemove(semaphore, out _))
            {
                semaphore.Dispose();
            }
        }
    }

    private void Retire(SemaphoreSlim? semaphore)
    {
        if (semaphore is null)
        {
            return;
        }

        if (!_semaphoreUsage.TryGetValue(semaphore, out var usage) || usage == 0)
        {
            semaphore.Dispose();
            return;
        }

        _retiredSemaphores[semaphore] = 0;
    }

    private sealed class Lease : IDisposable
    {
        private readonly TransferConcurrencyGate _owner;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public Lease(TransferConcurrencyGate owner, SemaphoreSlim semaphore)
        {
            _owner = owner;
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _semaphore.Release();
            _owner.ReleaseUsage(_semaphore);
        }
    }
}
