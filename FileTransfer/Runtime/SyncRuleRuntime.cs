internal sealed class SyncRuleRuntime : IDisposable
{
    private readonly ILogger _logger;
    private readonly PathTemplateRenderer _templateRenderer;
    private readonly Counter<long> _copiedCounter;
    private readonly Counter<long> _deletedCounter;
    private readonly Counter<long> _reconcileCounter;
    private readonly FileTransferCoordinator _transfers;
    private readonly FileWatcherManager _watchers;
    private readonly ReconciliationManager _reconciliation;
    private readonly TargetHealthMonitor _healthMonitor;
    private readonly StartupInventoryReporter _inventoryReporter;

    private RuntimeState _runtime = RuntimeState.Empty;
    private CancellationToken _stopToken;
    private bool _initialized;
    private bool _disposed;

    public SyncRuleRuntime(
        ILogger logger,
        PathTemplateRenderer templateRenderer,
        Counter<long> copiedCounter,
        Counter<long> deletedCounter,
        Counter<long> reconcileCounter,
        ResolvedTargetPathStateStore resolvedTargetPathStateStore,
        SyncOptions initialOptions)
    {
        _logger = logger;
        _templateRenderer = templateRenderer;
        _copiedCounter = copiedCounter;
        _deletedCounter = deletedCounter;
        _reconcileCounter = reconcileCounter;
        _runtime = RuntimeState.Create(initialOptions, logger, templateRenderer);
        HealthRegistry = new TargetHealthRegistry(logger);

        _transfers = new FileTransferCoordinator(
            logger,
            HealthRegistry,
            () => Current.Options,
            () => Current.PathMapper,
            _copiedCounter,
            _deletedCounter,
            resolvedTargetPathStateStore);

        _watchers = new FileWatcherManager(logger, () => Current.Options, OnChange, OnDelete);

        _reconciliation = new ReconciliationManager(
            logger,
            () => Current.Options,
            _transfers.TryResolveTargetPath,
            _transfers.QueueCopy,
            HealthRegistry,
            () => _reconcileCounter.Add(1));

        _healthMonitor = new TargetHealthMonitor(logger, HealthRegistry, () => Current.Options);
        _inventoryReporter = new StartupInventoryReporter(logger);
    }

    public TargetHealthRegistry HealthRegistry { get; }

    public FileTransferCoordinator Transfers => _transfers;

    public SyncOptions Options => Current.Options;

    public bool RequiresRecreate(SyncOptions prepared)
    {
        var current = Current.Options;
        return current.Queue.CopyCapacity != prepared.Queue.CopyCapacity ||
               current.Queue.DeleteCapacity != prepared.Queue.DeleteCapacity;
    }

    private RuntimeState Current => Volatile.Read(ref _runtime);

    public void Update(SyncOptions prepared, CancellationToken stopToken)
    {
        if (_disposed)
        {
            return;
        }

        _stopToken = stopToken;
        var previous = Current;
        var next = RuntimeState.Create(prepared, _logger, _templateRenderer);
        var resetResolvedTargets = _initialized && previous.PathResolutionChanged(next);

        Volatile.Write(ref _runtime, next);
        HealthRegistry.SyncTargets(prepared.TargetRoots);
        _transfers.UpdateSyncOptions(prepared);

        if (resetResolvedTargets)
        {
            _transfers.ClearResolvedTargetRoots();
        }
        else if (!_initialized)
        {
            _transfers.RestoreResolvedTargetRoots();
        }

        _watchers.Update(prepared);
        _reconciliation.Configure(prepared, _stopToken);
        _healthMonitor.Configure(prepared, _stopToken);
        _initialized = true;

        _logger.LogInformation(
            LogText.Get("RuleApplied"),
            prepared.RuleId,
            prepared.SourceRoot,
            prepared.TargetMode);
        _inventoryReporter.LogIfNeeded(prepared);
    }

    private void OnChange(FileChangeKind watcherEvent, string path)
    {
        var runtime = Current;
        var settings = runtime.Options;

        if (!FileProcessingRules.IsEventEnabled(watcherEvent, settings))
        {
            _logger.LogDebug(LogText.Get("EventDisabled"), settings.RuleId, watcherEvent, path);
            return;
        }

        if (!FileProcessingRules.ShouldProcess(path, runtime.FileExtensions, _logger))
        {
            return;
        }

        if (runtime.PathMapper.IsExcluded(path))
        {
            _logger.LogDebug(LogText.Get("EventIgnoredByMapping"), settings.RuleId, path);
            return;
        }

        _transfers.QueueCopy(path, FileProcessingRules.EventLabel(watcherEvent), _stopToken);
    }

    private void OnDelete(string path)
    {
        var runtime = Current;
        var settings = runtime.Options;

        if (!FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings))
        {
            _logger.LogDebug(LogText.Get("DeleteEventDisabled"), settings.RuleId, path);
            return;
        }

        if (!FileProcessingRules.ShouldProcess(path, runtime.FileExtensions, _logger))
        {
            return;
        }

        if (runtime.PathMapper.IsExcluded(path))
        {
            _logger.LogDebug(LogText.Get("DeleteEventIgnoredByMapping"), settings.RuleId, path);
            return;
        }

        _transfers.ScheduleMirrorDelete(path, _stopToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchers.Dispose();
        _reconciliation.Dispose();
        _healthMonitor.Dispose();
        _transfers.Dispose();
    }
}
