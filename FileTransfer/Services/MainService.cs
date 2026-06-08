internal sealed class MainService : BackgroundService
{
    private static readonly Meter ServiceMeter = new("FileTransfer.MainService");

    private readonly ILogger<MainService> _logger;
    private readonly ISyncOptionsProvider _settingsProvider;
    private readonly AppPathsOptions _appPaths;
    private readonly PathTemplateRenderer _templateRenderer = new();
    private readonly Dictionary<string, SyncRuleRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _applySync = new();

    private FileTransferCoordinator? _transfers;
    private TargetHealthRegistry? _healthRegistry;
    private CancellationToken _stopToken;
    private bool _subscribed;

    private readonly Counter<long> _copiedCounter = ServiceMeter.CreateCounter<long>("files_copied");
    private readonly Counter<long> _deletedCounter = ServiceMeter.CreateCounter<long>("files_moved_to_trash");
    private readonly Counter<long> _reconcileCounter = ServiceMeter.CreateCounter<long>("reconcile_runs");

    public MainService(ILogger<MainService> logger, ISyncOptionsProvider settingsProvider, AppPathsOptions appPaths)
    {
        _logger = logger;
        _settingsProvider = settingsProvider;
        _appPaths = appPaths;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _settingsProvider.OptionsChanged += OnOptionsChanged;
        _subscribed = true;
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopToken = stoppingToken;
        if (ApplySyncOptions(_settingsProvider.Current))
        {
            _logger.LogInformation(LogText.Get("ServiceStarted"));
        }
        else
        {
            _logger.LogError(LogText.Get("ServiceStartConfigFailed"));
        }

        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void OnOptionsChanged(IReadOnlyList<SyncOptions> updated)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (ApplySyncOptions(updated))
                {
                    _logger.LogInformation(LogText.Get("ConfigReloadApplied"), updated.Count);
                }
                else
                {
                    _logger.LogWarning(LogText.Get("ConfigReloadRejected"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LogText.Get("ConfigReloadFailed"));
            }
        }, _stopToken);
    }

    private bool ApplySyncOptions(IReadOnlyList<SyncOptions> candidates)
    {
        lock (_applySync)
        {
            var preparedRules = new List<SyncOptions>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (!candidate.TryPrepare(_logger, _templateRenderer, out var prepared))
                {
                    return false;
                }

                if (!seenKeys.Add(prepared.RuntimeKey))
                {
                    _logger.LogError(LogText.Get("DuplicateRuntimeId"), prepared.RuntimeKey);
                    return false;
                }

                preparedRules.Add(prepared);
            }

            if (preparedRules.Count == 0)
            {
                _logger.LogError(LogText.Get("NoRules"));
                return false;
            }

            var desiredKeys = new HashSet<string>(preparedRules.Select(rule => rule.RuntimeKey), StringComparer.OrdinalIgnoreCase);
            foreach (var existingKey in _runtimes.Keys.ToArray())
            {
                if (desiredKeys.Contains(existingKey))
                {
                    continue;
                }

                _logger.LogInformation(LogText.Get("RuntimeStopped"), existingKey);
                _runtimes[existingKey].Dispose();
                _runtimes.Remove(existingKey);
            }

            foreach (var prepared in preparedRules)
            {
                if (_runtimes.TryGetValue(prepared.RuntimeKey, out var existingRuntime) && existingRuntime.RequiresRecreate(prepared))
                {
                    _logger.LogInformation(LogText.Get("RuntimeQueueChanged"), prepared.RuntimeKey);
                    existingRuntime.Dispose();
                    _runtimes.Remove(prepared.RuntimeKey);
                }

                if (!_runtimes.TryGetValue(prepared.RuntimeKey, out var runtime))
                {
                    var resolvedTargetPathStateStore = new ResolvedTargetPathStateStore(
                        _logger,
                        _appPaths.ResolvedTargetPathStatePath(prepared.RuleId));
                    var initialScanSkipStateStore = new InitialScanSkipStateStore(
                        _logger,
                        _appPaths.InitialScanSkipStatePath(prepared.RuleId));

                    runtime = new SyncRuleRuntime(
                        _logger,
                        _templateRenderer,
                        _copiedCounter,
                        _deletedCounter,
                        _reconcileCounter,
                        resolvedTargetPathStateStore,
                        initialScanSkipStateStore,
                        prepared);
                    _runtimes.Add(prepared.RuntimeKey, runtime);
                    _logger.LogInformation(LogText.Get("RuntimeStarted"), prepared.RuntimeKey);
                }

                runtime.Update(prepared, _stopToken);
            }

            var firstRuntime = _runtimes.Values.FirstOrDefault();
            _transfers = firstRuntime?.Transfers;
            _healthRegistry = firstRuntime?.HealthRegistry;
            return true;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeComponents();
        _logger.LogInformation(LogText.Get("ServiceStopped"));
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        DisposeComponents();
        base.Dispose();
    }

    private void DisposeComponents()
    {
        if (_subscribed)
        {
            _settingsProvider.OptionsChanged -= OnOptionsChanged;
            _subscribed = false;
        }

        lock (_applySync)
        {
            foreach (var runtime in _runtimes.Values)
            {
                runtime.Dispose();
            }

            _runtimes.Clear();
            _transfers = null;
            _healthRegistry = null;
        }
    }
}
