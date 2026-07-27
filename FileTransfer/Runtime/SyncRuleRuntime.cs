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
    private readonly StartupInventoryReporter _inventoryReporter;
    private readonly InitialScanSkipStateStore _initialScanSkipStateStore;
    private readonly StagingFileCleaner _stagingFileCleaner;
    private readonly HashSet<string> _cleanedTargetRoots = new(PathKeyComparer.Comparer);

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
        InitialScanSkipStateStore initialScanSkipStateStore,
        SyncOptions initialOptions)
    {
        _logger = logger;
        _templateRenderer = templateRenderer;
        _copiedCounter = copiedCounter;
        _deletedCounter = deletedCounter;
        _reconcileCounter = reconcileCounter;
        _initialScanSkipStateStore = initialScanSkipStateStore;
        _stagingFileCleaner = new StagingFileCleaner(logger);
        _runtime = RuntimeState.Create(initialOptions, logger, templateRenderer);
        HealthRegistry = new TargetHealthRegistry(logger);

        _transfers = new FileTransferCoordinator(
            logger,
            HealthRegistry,
            () => Current.Options,
            () => Current.PathMapper,
            _copiedCounter,
            _deletedCounter,
            resolvedTargetPathStateStore,
            initialScanSkipStateStore);

        _watchers = new FileWatcherManager(logger, () => Current.Options, OnChange, OnDelete);

        _reconciliation = new ReconciliationManager(
            logger,
            () => Current.Options,
            _transfers.TryResolveTargetPath,
            _transfers.ObserveSourceVersion,
            _transfers.RequestCopy,
            HealthRegistry,
            _initialScanSkipStateStore,
            () => _reconcileCounter.Add(1));

        _inventoryReporter = new StartupInventoryReporter(logger);
    }

    public TargetHealthRegistry HealthRegistry { get; }

    public FileTransferCoordinator Transfers => _transfers;

    public SyncOptions Options => Current.Options;

    public bool RequiresRecreate(SyncOptions prepared)
    {
        var current = Current.Options;
        return current.MaxParallelTransfers != prepared.MaxParallelTransfers;
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

        if (!_initialized && prepared.SkipInitialScan)
        {
            _initialScanSkipStateStore.SeedFromSourceRoot(prepared);
        }

        _watchers.Update(prepared);
        var newTargetRoots = prepared.TargetRoots.Where(root => _cleanedTargetRoots.Add(root)).ToArray();
        if (newTargetRoots.Length > 0)
        {
            _stagingFileCleaner.Clean(newTargetRoots, prepared.OperationTimeoutMs);
        }
        _reconciliation.Configure(prepared, _stopToken);
        _initialized = true;

        _logger.LogInformation(
            LogText.Get("RuleApplied"),
            prepared.RuleId,
            prepared.SourceRoot,
            prepared.TargetMode);
        _inventoryReporter.LogIfNeeded(prepared);
    }

    private void OnChange(FileChangeKind watcherEvent, string path, string? oldPath)
    {
        if (watcherEvent == FileChangeKind.Renamed)
        {
            HandleRename(path, oldPath);
            return;
        }

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

        if (_transfers.TrySkipInitialScan(path, LogText.Get("CopyContext")))
        {
            return;
        }

        _transfers.QueueCopy(path, FileProcessingRules.EventLabel(watcherEvent), _stopToken);
    }

    private void HandleRename(string newPath, string? oldPath)
    {
        // A rename represents two independent operations.  In particular, a new
        // path that is excluded from copying must not prevent the old path from
        // being considered for mirroring deletion.
        QueueRenamedNewPath(newPath);

        if (!string.IsNullOrWhiteSpace(oldPath))
        {
            ScheduleRenamedOldPath(oldPath);
        }
    }

    private void QueueRenamedNewPath(string path)
    {
        var runtime = Current;
        var settings = runtime.Options;

        if (!FileProcessingRules.IsEventEnabled(FileChangeKind.Renamed, settings))
        {
            _logger.LogDebug(LogText.Get("EventDisabled"), settings.RuleId, FileChangeKind.Renamed, path);
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

        if (_transfers.TrySkipInitialScan(path, LogText.Get("CopyContext")))
        {
            return;
        }

        _transfers.QueueCopy(path, FileProcessingRules.EventLabel(FileChangeKind.Renamed), _stopToken);
    }

    private void ScheduleRenamedOldPath(string path)
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

        if (_transfers.TrySkipInitialScan(path, LogText.Get("DeleteSyncContext")))
        {
            return;
        }

        _transfers.ScheduleMirrorDelete(path, _stopToken);
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

        if (_transfers.TrySkipInitialScan(path, LogText.Get("DeleteSyncContext")))
        {
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
        _transfers.Dispose();
        _initialScanSkipStateStore.Dispose();
    }
}
