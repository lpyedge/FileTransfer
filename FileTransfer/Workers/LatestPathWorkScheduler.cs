internal enum DesiredSourceState
{
    Present,
    Absent
}

internal readonly record struct PathWorkItem(
    string SourcePath,
    long Generation,
    DesiredSourceState DesiredState,
    string Label);

internal sealed class LatestPathWorkScheduler : IDisposable
{
    private sealed class WorkState
    {
        public object SyncRoot { get; } = new();
        public long Generation;
        public DesiredSourceState DesiredState;
        public string Label = string.Empty;
        public SourceFileStamp? LastObservedStamp;
        public bool Queued;
        public bool Running;
        public bool RerunRequested;
        public bool Retired;
        public CancellationTokenSource? ActiveCts;
    }

    private readonly ILogger _logger;
    private readonly Func<PathWorkItem, CancellationToken, Task> _handler;
    private readonly ConcurrentDictionary<string, WorkState> _states = new(PathKeyComparer.Comparer);
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task[] _workers;
    private int _disposed;

    internal int StateCount => _states.Count;
    internal Action<string>? BeforeRetiredStateRemoval { get; set; }
    internal Action<string>? AfterSignalStateLookup { get; set; }
    internal Func<string, Task> BeforeWorkItemSnapshotAsync { get; set; } = static _ => Task.CompletedTask;

    public LatestPathWorkScheduler(
        ILogger logger,
        int workerCount,
        Func<PathWorkItem, CancellationToken, Task> handler)
    {
        _logger = logger;
        _handler = handler;
        _workers = Enumerable.Range(0, Math.Max(1, workerCount))
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    public void SignalPresent(string normalizedSourcePath, string label, CancellationToken stopToken)
    {
        SourceFileStamp? observedStamp = null;
        try
        {
            var info = new FileInfo(normalizedSourcePath);
            if (info.Exists)
            {
                observedStamp = new SourceFileStamp(info.Length, info.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not observe source metadata while signaling Present: {SourcePath}.", normalizedSourcePath);
        }

        SignalState(normalizedSourcePath, DesiredSourceState.Present, label, stopToken, observedStamp);
    }

    public void SignalAbsent(string normalizedSourcePath, string label, CancellationToken stopToken) =>
        SignalState(normalizedSourcePath, DesiredSourceState.Absent, label, stopToken, observedStamp: null);

    public void ObservePresent(string normalizedSourcePath, SourceFileStamp stamp, string label, CancellationToken stopToken)
    {
        if (stopToken.IsCancellationRequested || IsDisposed())
        {
            return;
        }

        while (true)
        {
            var state = _states.GetOrAdd(normalizedSourcePath, _ => new WorkState
            {
                DesiredState = DesiredSourceState.Present,
                Label = label
            });
            CancellationTokenSource? activeCts = null;
            var enqueue = false;
            var retry = false;

            lock (state.SyncRoot)
            {
                if (state.Retired)
                {
                    retry = true;
                }
                else if (state.LastObservedStamp is null && state.DesiredState == DesiredSourceState.Present)
                {
                    state.LastObservedStamp = stamp;
                    state.Label = label;
                    return;
                }
                else if (state.DesiredState == DesiredSourceState.Present && state.LastObservedStamp == stamp)
                {
                    state.Label = label;
                    return;
                }
                else
                {
                    state.Generation++;
                    state.DesiredState = DesiredSourceState.Present;
                    state.Label = label;
                    state.LastObservedStamp = stamp;
                    if (state.Running)
                    {
                        state.RerunRequested = true;
                        activeCts = state.ActiveCts;
                    }
                    else if (!state.Queued)
                    {
                        state.Queued = true;
                        enqueue = true;
                    }
                }
            }

            if (retry)
            {
                continue;
            }

            CancelActive(activeCts);
            if (enqueue)
            {
                Enqueue(normalizedSourcePath);
            }

            return;
        }
    }

    public void RequestCopy(string normalizedSourcePath, string label, CancellationToken stopToken)
    {
        if (stopToken.IsCancellationRequested || IsDisposed())
        {
            return;
        }

        while (true)
        {
            var state = _states.GetOrAdd(normalizedSourcePath, _ => new WorkState
            {
                DesiredState = DesiredSourceState.Present,
                Label = label
            });
            var enqueue = false;
            var retry = false;
            lock (state.SyncRoot)
            {
                if (state.Retired)
                {
                    retry = true;
                }
                else if (state.DesiredState == DesiredSourceState.Absent)
                {
                    return;
                }
                else
                {
                    state.Label = label;
                    if (!state.Running && !state.Queued)
                    {
                        state.Queued = true;
                        enqueue = true;
                    }
                }
            }

            if (retry)
            {
                continue;
            }

            if (enqueue)
            {
                Enqueue(normalizedSourcePath);
            }

            return;
        }
    }

    public bool IsCurrent(string normalizedSourcePath, long generation, DesiredSourceState desiredState)
    {
        while (_states.TryGetValue(normalizedSourcePath, out var state))
        {
            lock (state.SyncRoot)
            {
                if (state.Retired)
                {
                    continue;
                }

                return state.Generation == generation && state.DesiredState == desiredState;
            }
        }

        return false;
    }

    public void RecordCommittedStamp(string normalizedSourcePath, long generation, SourceFileStamp stamp)
    {
        while (_states.TryGetValue(normalizedSourcePath, out var state))
        {
            lock (state.SyncRoot)
            {
                if (state.Retired)
                {
                    continue;
                }

                if (state.Generation == generation && state.DesiredState == DesiredSourceState.Present)
                {
                    state.LastObservedStamp = stamp;
                }

                return;
            }
        }
    }

    public void MarkSourceChangedDetected(
        string normalizedSourcePath,
        long expectedGeneration,
        SourceFileStamp? observedStamp,
        string label)
    {
        if (IsDisposed())
        {
            return;
        }

        while (_states.TryGetValue(normalizedSourcePath, out var state))
        {
            CancellationTokenSource? activeCts = null;
            var enqueue = false;
            var retry = false;
            lock (state.SyncRoot)
            {
                if (state.Retired)
                {
                    retry = true;
                }
                else if (state.Generation != expectedGeneration || state.DesiredState != DesiredSourceState.Present)
                {
                    return;
                }
                else
                {
                    state.Generation++;
                    state.Label = label;
                    state.LastObservedStamp = observedStamp;
                    if (state.Running)
                    {
                        state.RerunRequested = true;
                        activeCts = state.ActiveCts;
                    }
                    else if (!state.Queued)
                    {
                        state.Queued = true;
                        enqueue = true;
                    }
                }
            }

            if (retry)
            {
                continue;
            }

            CancelActive(activeCts);
            if (enqueue)
            {
                Enqueue(normalizedSourcePath);
            }

            return;
        }
    }

    private void SignalState(
        string normalizedSourcePath,
        DesiredSourceState desiredState,
        string label,
        CancellationToken stopToken,
        SourceFileStamp? observedStamp)
    {
        if (stopToken.IsCancellationRequested || IsDisposed())
        {
            return;
        }

        while (true)
        {
            var state = _states.GetOrAdd(normalizedSourcePath, _ => new WorkState());
            AfterSignalStateLookup?.Invoke(normalizedSourcePath);
            CancellationTokenSource? activeCts = null;
            var enqueue = false;
            var retry = false;
            lock (state.SyncRoot)
            {
                if (state.Retired)
                {
                    retry = true;
                }
                else
                {
                    state.Generation++;
                    state.DesiredState = desiredState;
                    state.Label = label;
                    if (desiredState == DesiredSourceState.Present && observedStamp is SourceFileStamp presentStamp)
                    {
                        state.LastObservedStamp = presentStamp;
                    }
                    if (state.Running)
                    {
                        state.RerunRequested = true;
                        activeCts = state.ActiveCts;
                    }
                    else if (!state.Queued)
                    {
                        state.Queued = true;
                        enqueue = true;
                    }
                }
            }

            if (retry)
            {
                continue;
            }

            CancelActive(activeCts);
            if (enqueue)
            {
                Enqueue(normalizedSourcePath);
            }

            return;
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var sourcePath in _channel.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                await BeforeWorkItemSnapshotAsync(sourcePath).ConfigureAwait(false);
                if (!_states.TryGetValue(sourcePath, out var state))
                {
                    continue;
                }

                PathWorkItem item;
                CancellationTokenSource activeCts;
                lock (state.SyncRoot)
                {
                    if (state.Retired)
                    {
                        continue;
                    }

                    state.Queued = false;
                    if (state.Running)
                    {
                        continue;
                    }

                    state.Running = true;
                    state.RerunRequested = false;
                    item = new PathWorkItem(sourcePath, state.Generation, state.DesiredState, state.Label);
                    activeCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                    state.ActiveCts = activeCts;
                }

                try
                {
                    await _handler(item, activeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activeCts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Latest-path work handler failed for {SourcePath}.", sourcePath);
                }
                finally
                {
                    var enqueue = false;
                    lock (state.SyncRoot)
                    {
                        state.ActiveCts = null;
                        state.Running = false;
                        if ((state.Generation != item.Generation || state.DesiredState != item.DesiredState || state.RerunRequested) && !state.Queued)
                        {
                            state.Queued = true;
                            enqueue = true;
                        }

                        if (!enqueue &&
                            item.DesiredState == DesiredSourceState.Absent &&
                            state.Generation == item.Generation &&
                            state.DesiredState == DesiredSourceState.Absent &&
                            !state.RerunRequested &&
                            !state.Queued)
                        {
                            state.Retired = true;
                            BeforeRetiredStateRemoval?.Invoke(sourcePath);
                            TryRemoveExact(sourcePath, state);
                        }
                    }

                    activeCts.Dispose();
                    if (enqueue)
                    {
                        Enqueue(sourcePath);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
    }

    private void Enqueue(string sourcePath)
    {
        _channel.Writer.TryWrite(sourcePath);
    }

    private bool TryRemoveExact(string sourcePath, WorkState state) =>
        ((ICollection<KeyValuePair<string, WorkState>>)_states)
            .Remove(new KeyValuePair<string, WorkState>(sourcePath, state));

    private static void CancelActive(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return; }
    }

    private bool IsDisposed() => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var activeTokens = new List<CancellationTokenSource>();
        foreach (var state in _states.Values)
        {
            lock (state.SyncRoot)
            {
                if (state.ActiveCts is not null)
                {
                    activeTokens.Add(state.ActiveCts);
                }
            }
        }

        _disposeCts.Cancel();
        foreach (var activeToken in activeTokens)
        {
            try
            {
                activeToken.Cancel();
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("An active scheduler cancellation token was already disposed.");
            }
        }

        _channel.Writer.TryComplete();
        try
        {
            Task.WaitAll(_workers, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            _logger.LogDebug("Scheduler workers did not complete cleanly during disposal.");
        }
        finally
        {
            _disposeCts.Dispose();
        }
    }
}
