internal delegate bool TryResolveTargetPathDelegate(string sourcePath, string targetRoot, out string targetPath);

internal sealed class ReconciliationManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<SyncOptions> _getSyncOptions;
    private readonly TryResolveTargetPathDelegate _tryResolveTargetPath;
    private readonly Action<string, string, CancellationToken> _queueCopy;
    private readonly TargetHealthRegistry _healthRegistry;
    private readonly Action _onRunCompleted;

    private System.Threading.Timer? _timer;
    private CancellationToken _stopToken;
    private int _running;
    private bool _skipNextRun;

    public ReconciliationManager(
        ILogger logger,
        Func<SyncOptions> getSyncOptions,
        TryResolveTargetPathDelegate tryResolveTargetPath,
        Action<string, string, CancellationToken> queueCopy,
        TargetHealthRegistry healthRegistry,
        Action onRunCompleted)
    {
        _logger = logger;
        _getSyncOptions = getSyncOptions;
        _tryResolveTargetPath = tryResolveTargetPath;
        _queueCopy = queueCopy;
        _healthRegistry = healthRegistry;
        _onRunCompleted = onRunCompleted;
    }

    public void Configure(SyncOptions settings, CancellationToken stopToken)
    {
        _stopToken = stopToken;
        var isFirstConfigure = _timer is null;
        _timer?.Dispose();

        if (isFirstConfigure)
        {
            _skipNextRun = settings.SkipInitialScan;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, settings.ReconciliationIntervalMs));
        _timer = new System.Threading.Timer(_ => Trigger(), null, interval, interval);
    }

    private void Trigger()
    {
        if (_stopToken.IsCancellationRequested || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(_stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LogText.Get("ReconciliationFailed"));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }, _stopToken);
    }

    private Task RunAsync(CancellationToken ct)
    {
        if (_skipNextRun)
        {
            _skipNextRun = false;
            _logger.LogInformation(LogText.Get("ReconciliationInitialSkipped"));
            _onRunCompleted();
            return Task.CompletedTask;
        }

        var settings = _getSyncOptions();
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (_healthRegistry.GetPreferredTarget(settings.TargetRoots) is null)
        {
            _logger.LogWarning(LogText.Get("ReconciliationNoHealthyTargets"));
        }
        else if ((settings.WatchEvents.Created || settings.WatchEvents.Changed) && Directory.Exists(settings.SourceRoot))
        {
            ReconcileMissingCopies(settings, ct);
        }

        _onRunCompleted();
        return Task.CompletedTask;
    }

    private void ReconcileMissingCopies(SyncOptions settings, CancellationToken ct)
    {
        var extensions = FileProcessingRules.NormalizeFileExtensions(settings.FileExtensions);
        var scanned = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.MaxReconciliationDurationMs));
        var maxFiles = Math.Max(1, settings.MaxReconciliationFilesPerRun);

        foreach (var file in EnumerateSourceFiles(settings, ct))
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (++scanned > maxFiles || DateTime.UtcNow >= deadline)
            {
                _logger.LogInformation(
                    LogText.Get("ReconciliationResourceLimit"),
                    settings.RuleId,
                    scanned - 1,
                    maxFiles);
                return;
            }

            if (!FileProcessingRules.ShouldProcess(file, extensions, _logger))
            {
                continue;
            }

            if (ShouldQueueMissingCopy(settings, file, ct))
            {
                _logger.LogInformation(LogText.Get("ReconciliationMismatchFound"), settings.RuleId, file);
                _queueCopy(file, "Reconcile", ct);
            }
        }
    }


    private bool ShouldQueueMissingCopy(SyncOptions settings, string file, CancellationToken ct)
    {
        FileInfo sourceInfo;
        try
        {
            sourceInfo = new FileInfo(file);
            if (!sourceInfo.Exists)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("ReconciliationSourceInfoFailed"), settings.RuleId, file);
            return false;
        }

        var hasResolvableTarget = false;
        var currentInAnyTarget = false;
        var missingOrStaleHealthyTarget = false;

        foreach (var targetRoot in settings.TargetRoots)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            if (!_tryResolveTargetPath(file, targetRoot, out var candidate))
            {
                continue;
            }

            hasResolvableTarget = true;
            var healthy = _healthRegistry.IsHealthy(targetRoot);
            var current = IsTargetCurrent(sourceInfo, candidate, settings, ct);
            currentInAnyTarget |= current;

            if (!current && healthy)
            {
                missingOrStaleHealthyTarget = true;
            }
        }

        if (!hasResolvableTarget)
        {
            _logger.LogDebug(LogText.Get("ReconciliationNoTargetPath"), settings.RuleId, file);
            return false;
        }

        return settings.TargetMode == TargetMode.FirstAvailable
            ? !currentInAnyTarget
            : missingOrStaleHealthyTarget;
    }

    private bool IsTargetCurrent(FileInfo sourceInfo, string targetPath, SyncOptions settings, CancellationToken ct)
    {
        try
        {
            var destInfo = new FileInfo(targetPath);
            if (!destInfo.Exists || destInfo.Length != sourceInfo.Length || destInfo.LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc)
            {
                return false;
            }

            if (settings.ComparisonMode != ComparisonMode.Hash)
            {
                return true;
            }

            if (ct.IsCancellationRequested)
            {
                return false;
            }

            return string.Equals(ComputeHash(sourceInfo.FullName), ComputeHash(destInfo.FullName), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("ReconciliationTargetInfoFailed"), settings.RuleId, targetPath);
            return false;
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 262144, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private IEnumerable<string> EnumerateSourceFiles(SyncOptions settings, CancellationToken ct)
    {
        if (!Directory.Exists(settings.SourceRoot))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.IncludeSubdirectories,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(settings.SourceRoot, "*", options).GetEnumerator();
            while (!ct.IsCancellationRequested)
            {
                string file;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    file = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, LogText.Get("ReconciliationEnumerationFailed"), settings.SourceRoot);
                    yield break;
                }

                yield return file;
            }
        }
        finally
        {
            enumerator?.Dispose();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
