internal delegate bool TryResolveTargetPathDelegate(string sourcePath, string targetRoot, out string targetPath);

internal sealed class ReconciliationManager : IDisposable
{
    /// <summary>Delay before a catch-up run after watcher events were lost (lets the burst settle).</summary>
    internal static readonly TimeSpan LostEventsRunDelay = TimeSpan.FromSeconds(2);

    /// <summary>Delay between batches while a catch-up pass is still pending.</summary>
    internal static readonly TimeSpan CatchUpContinuationDelay = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly Func<SyncOptions> _getSyncOptions;
    private readonly TryResolveTargetPathDelegate _tryResolveTargetPath;
    private readonly Action<string, SourceFileStamp, string, CancellationToken> _observeSourceVersion;
    private readonly Action<string, string, CancellationToken> _requestCopy;
    private readonly TargetHealthRegistry _healthRegistry;
    private readonly InitialScanSkipStateStore _initialScanSkipStateStore;
    private readonly Action _onRunCompleted;

    private System.Threading.Timer? _timer;
    private System.Threading.Timer? _followUpTimer;
    private CancellationToken _stopToken;
    private int _running;
    private int _followUpScheduled;
    private int _disposed;
    private bool _skipNextRun;

    // Resume point so that a run stopped by maxFiles/maxDuration continues where it left off
    // instead of rescanning the same leading files forever.
    private string? _resumeAfterPath;
    private string? _cursorSourceRoot;

    // Every lost-event notification bumps _lostEventsGeneration. A full pass that started at or
    // after a given generation covers it; until then catch-up batches run back to back.
    private long _lostEventsGeneration;
    private long _coveredGeneration;
    private long _passStartGeneration;

    public ReconciliationManager(
        ILogger logger,
        Func<SyncOptions> getSyncOptions,
        TryResolveTargetPathDelegate tryResolveTargetPath,
        Action<string, SourceFileStamp, string, CancellationToken> observeSourceVersion,
        Action<string, string, CancellationToken> requestCopy,
        TargetHealthRegistry healthRegistry,
        InitialScanSkipStateStore initialScanSkipStateStore,
        Action onRunCompleted)
    {
        _logger = logger;
        _getSyncOptions = getSyncOptions;
        _tryResolveTargetPath = tryResolveTargetPath;
        _observeSourceVersion = observeSourceVersion;
        _requestCopy = requestCopy;
        _healthRegistry = healthRegistry;
        _initialScanSkipStateStore = initialScanSkipStateStore;
        _onRunCompleted = onRunCompleted;
    }

    public ReconciliationManager(
        ILogger logger,
        Func<SyncOptions> getSyncOptions,
        TryResolveTargetPathDelegate tryResolveTargetPath,
        Action<string, string, CancellationToken> queueCopy,
        TargetHealthRegistry healthRegistry,
        Action onRunCompleted)
        : this(logger, getSyncOptions, tryResolveTargetPath, (_, _, _, _) => { }, queueCopy, healthRegistry, new InitialScanSkipStateStore(logger), onRunCompleted)
    {
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

        if (!PathKeyComparer.Comparer.Equals(_cursorSourceRoot ?? string.Empty, settings.SourceRoot ?? string.Empty))
        {
            _cursorSourceRoot = settings.SourceRoot;
            _resumeAfterPath = null;
        }

        _followUpTimer ??= new System.Threading.Timer(_ =>
        {
            Interlocked.Exchange(ref _followUpScheduled, 0);
            Trigger();
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, settings.ReconciliationIntervalMs));
        _timer = new System.Threading.Timer(_ => Trigger(), null, interval, interval);
    }

    /// <summary>
    /// Requests a complete reconciliation pass as soon as possible, e.g. because the file
    /// watcher dropped notifications. Cheap and safe to call from watcher threads.
    /// </summary>
    public void RequestRun(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0 || _stopToken.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _lostEventsGeneration);
        if (ScheduleFollowUp(LostEventsRunDelay))
        {
            _logger.LogWarning(LogText.Get("ReconciliationRequested"), _getSyncOptions().RuleId, reason, generation);
        }
    }

    private bool CatchUpPending => Interlocked.Read(ref _lostEventsGeneration) > Interlocked.Read(ref _coveredGeneration);

    private bool ScheduleFollowUp(TimeSpan delay)
    {
        var timer = _followUpTimer;
        if (timer is null || Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _followUpScheduled, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            timer.Change(delay, Timeout.InfiniteTimeSpan);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Trigger()
    {
        if (_stopToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _running, 1) == 1)
        {
            // A busy run re-checks CatchUpPending when it finishes, so no request is lost.
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
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LogText.Get("ReconciliationFailed"));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (CatchUpPending && !_stopToken.IsCancellationRequested)
                {
                    ScheduleFollowUp(CatchUpContinuationDelay);
                }
            }
        }, _stopToken);
    }

    private Task RunAsync(CancellationToken ct)
    {
        if (_skipNextRun && !CatchUpPending)
        {
            _skipNextRun = false;
            _logger.LogInformation(LogText.Get("ReconciliationInitialSkipped"));
            _onRunCompleted();
            return Task.CompletedTask;
        }

        // Files recorded by the initial-scan skip state stay excluded inside the scan itself,
        // so a catch-up run may safely replace the skipped initial run.
        _skipNextRun = false;

        var settings = _getSyncOptions();
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if ((settings.WatchEvents.Created || settings.WatchEvents.Changed) && Directory.Exists(settings.SourceRoot))
        {
            ReconcileMissingCopies(settings, ct);
        }
        else
        {
            // Nothing to reconcile for this rule; treat pending catch-up requests as satisfied.
            Interlocked.Exchange(ref _coveredGeneration, Interlocked.Read(ref _lostEventsGeneration));
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

        var resumeAfter = _resumeAfterPath;
        if (resumeAfter is null)
        {
            _passStartGeneration = Interlocked.Read(ref _lostEventsGeneration);
        }

        string? lastFile = null;
        var enumerationCompleted = false;

        foreach (var file in EnumerateSourceFiles(settings, ct, () => enumerationCompleted = true))
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (resumeAfter is not null)
            {
                // Fast-forward past files already handled in this pass (names only, no IO).
                if (PathKeyComparer.Comparer.Equals(file, resumeAfter))
                {
                    resumeAfter = null;
                }

                continue;
            }

            if (++scanned > maxFiles || DateTime.UtcNow >= deadline)
            {
                _resumeAfterPath = lastFile ?? _resumeAfterPath;
                _logger.LogInformation(
                    LogText.Get("ReconciliationResourceLimit"),
                    settings.RuleId,
                    scanned - 1,
                    maxFiles);
                return;
            }

            lastFile = file;

            if (_initialScanSkipStateStore.IsSkipped(settings.RuntimeId, file))
            {
                continue;
            }

            if (!FileProcessingRules.ShouldProcess(file, extensions, _logger))
            {
                continue;
            }

            if (!TryGetSourceStamp(settings, file, out var stamp))
            {
                continue;
            }

            _observeSourceVersion(file, stamp, "Reconcile", ct);

            if (ShouldQueueMissingCopy(settings, file, ct))
            {
                _logger.LogInformation(LogText.Get("ReconciliationMismatchFound"), settings.RuleId, file);
                _requestCopy(file, "Reconcile", ct);
            }
        }

        if (!enumerationCompleted || ct.IsCancellationRequested)
        {
            // Enumeration failed or was cancelled; keep the cursor and retry later.
            if (lastFile is not null)
            {
                _resumeAfterPath = lastFile;
            }

            return;
        }

        _resumeAfterPath = null;
        if (resumeAfter is not null)
        {
            // The resume file disappeared; the next run starts a fresh pass from the beginning.
            return;
        }

        var covered = Interlocked.Read(ref _coveredGeneration);
        if (_passStartGeneration > covered)
        {
            _logger.LogInformation(LogText.Get("ReconciliationCatchUpCompleted"), settings.RuleId);
            Interlocked.Exchange(ref _coveredGeneration, _passStartGeneration);
        }
    }

    private bool TryGetSourceStamp(SyncOptions settings, string file, out SourceFileStamp stamp)
    {
        stamp = default;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return false;
            }

            stamp = new SourceFileStamp(info.Length, info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("ReconciliationSourceInfoFailed"), settings.RuleId, file);
            return false;
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
            var canAttempt = _healthRegistry.CanAttempt(targetRoot, DateTimeOffset.UtcNow);
            var current = IsTargetCurrent(sourceInfo, candidate, settings, ct);
            currentInAnyTarget |= current;

            if (!current && canAttempt)
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

    private IEnumerable<string> EnumerateSourceFiles(SyncOptions settings, CancellationToken ct, Action onCompleted)
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
                        onCompleted();
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
        Interlocked.Exchange(ref _disposed, 1);
        _timer?.Dispose();
        _timer = null;
        _followUpTimer?.Dispose();
        _followUpTimer = null;
    }
}
