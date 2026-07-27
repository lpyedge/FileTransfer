internal sealed class FileTransferCoordinator : IDisposable
{
    private readonly ILogger _logger;
    private readonly TargetHealthRegistry _targetHealthRegistry;
    private readonly Func<SyncOptions> _getSyncOptions;
    private readonly Func<PathMapper?> _getPathMapper;
    private readonly InitialScanSkipStateStore _initialScanSkipStateStore;
    private readonly Counter<long> _copiedCounter;
    private readonly Counter<long> _deletedCounter;
    private readonly ResolvedTargetPathCache _resolvedTargets;
    private readonly LatestPathWorkScheduler _scheduler;
    private readonly IStagingFileCopier _stagingFileCopier;
    private int _disposed;

    public FileTransferCoordinator(
        ILogger logger,
        TargetHealthRegistry targetHealthRegistry,
        Func<SyncOptions> getSyncOptions,
        Func<PathMapper?> getPathMapper,
        Counter<long> copiedCounter,
        Counter<long> deletedCounter,
        ResolvedTargetPathStateStore resolvedTargetPathStateStore,
        InitialScanSkipStateStore initialScanSkipStateStore,
        IStagingFileCopier? stagingFileCopier = null)
    {
        _logger = logger;
        _targetHealthRegistry = targetHealthRegistry;
        _getSyncOptions = getSyncOptions;
        _getPathMapper = getPathMapper;
        _initialScanSkipStateStore = initialScanSkipStateStore;
        _copiedCounter = copiedCounter;
        _deletedCounter = deletedCounter;
        var initialSettings = getSyncOptions();
        _stagingFileCopier = stagingFileCopier ?? new StagingFileCopier(logger);
        FileHashProvider = ComputeFileHashAsync;
        _scheduler = new LatestPathWorkScheduler(logger, initialSettings.MaxParallelTransfers, ProcessWorkItemAsync);
        _resolvedTargets = new ResolvedTargetPathCache(logger, resolvedTargetPathStateStore, initialSettings.RuntimeId, ownsStore: true);
    }

    public ConcurrentDictionary<string, FileTransferFingerprint> Fingerprints { get; } = new(PathKeyComparer.Comparer);

    internal Func<PathWorkItem, string, Task> BeforeTrashMoveAsync { get; set; } = static (_, _) => Task.CompletedTask;
    internal Func<PathWorkItem, string, Task> AfterTrashMoveAsync { get; set; } = static (_, _) => Task.CompletedTask;
    internal Func<PathWorkItem, string, Task> BeforeDeleteStateForgetAsync { get; set; } = static (_, _) => Task.CompletedTask;
    internal Func<PathWorkItem, string, Task> BeforeCommitAsync { get; set; } = static (_, _) => Task.CompletedTask;
    internal Func<PathWorkItem, string, Task> BeforeSourceDeleteAsync { get; set; } = static (_, _) => Task.CompletedTask;
    internal Func<PathWorkItem, Task> AfterDeleteProcessingAsync { get; set; } = static _ => Task.CompletedTask;
    internal Func<string, SourcePresence> SourcePresenceProbe { get; set; } = ProbeSourcePresence;
    internal Func<string, SyncOptions, CancellationToken, Task<string?>> FileHashProvider { get; set; }
    internal Func<PathWorkItem, string, bool, Task> AfterExistingTargetComparisonAsync { get; set; } =
        static (_, _, _) => Task.CompletedTask;
    internal Func<string, SourceFileStamp> ReadyFileInfoProvider { get; set; } = ReadReadyFileInfo;

    public void UpdateSyncOptions(SyncOptions settings)
    {
        _resolvedTargets.UpdateRuntimeId(settings.RuntimeId);

        if (!FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings))
        {
            Fingerprints.Clear();
        }
    }

    public void RestoreResolvedTargetRoots() => _resolvedTargets.Restore();

    public void ClearResolvedTargetRoots() => _resolvedTargets.Clear();

    public bool TrySkipInitialScan(string sourcePath, string context)
    {
        var settings = _getSyncOptions();
        if (!_initialScanSkipStateStore.IsSkipped(settings.RuntimeId, sourcePath))
        {
            return false;
        }

        _logger.LogDebug("Skipped {Context} because it was recorded in the initial-scan skip state: {Path}", context, sourcePath);
        return true;
    }


    public bool TryResolveTargetPath(string sourcePath, string targetRoot, out string targetPath)
    {
        targetPath = string.Empty;

        var mapper = _getPathMapper();
        if (mapper is null)
        {
            return false;
        }

        return TryGetOrCreateResolvedTargetPath(sourcePath, targetRoot, mapper, out targetPath);
    }

    public void QueueCopy(string sourcePath, string label, CancellationToken stopToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || stopToken.IsCancellationRequested)
        {
            return;
        }

        if (!TryNormalizeSourcePath(sourcePath, out var normalized))
        {
            return;
        }

        _scheduler.SignalPresent(normalized, label, stopToken);
        _logger.LogDebug(LogText.Get("CopyQueued"), label, sourcePath);
    }

    public void ScheduleMirrorDelete(string sourcePath, CancellationToken stopToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || stopToken.IsCancellationRequested)
        {
            return;
        }

        if (!TryNormalizeSourcePath(sourcePath, out var normalized))
        {
            return;
        }

        _scheduler.SignalAbsent(normalized, LogText.Get("DeleteSyncContext"), stopToken);
    }

    public void RequestCopy(string sourcePath, string label, CancellationToken stopToken)
    {
        if (TryNormalizeSourcePath(sourcePath, out var normalized))
        {
            _scheduler.RequestCopy(normalized, label, stopToken);
        }
    }

    public void ObserveSourceVersion(string sourcePath, SourceFileStamp stamp, string label, CancellationToken stopToken)
    {
        if (TryNormalizeSourcePath(sourcePath, out var normalized))
        {
            _scheduler.ObservePresent(normalized, stamp, label, stopToken);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _scheduler.Dispose();
        _resolvedTargets.Dispose();
        Fingerprints.Clear();
    }


    private Task ProcessWorkItemAsync(PathWorkItem item, CancellationToken ct) =>
        item.DesiredState == DesiredSourceState.Present
            ? ProcessCopyAsync(item, ct)
            : ProcessDeleteAsync(item, ct);

    private async Task ProcessCopyAsync(PathWorkItem item, CancellationToken ct)
    {
        var sourcePath = item.SourcePath;
        try
        {
            var settings = _getSyncOptions();
            if (_initialScanSkipStateStore.IsSkipped(settings.RuntimeId, sourcePath))
            {
                return;
            }

            var mapper = _getPathMapper();
            if (mapper is null)
            {
                _logger.LogWarning(LogText.Get("MappingNotInitializedCopy"), sourcePath);
                return;
            }

            if (TrySkipExcluded(mapper, sourcePath, LogText.Get("CopyContext")))
            {
                return;
            }

            if (!await WaitForReadyFileAsync(sourcePath, settings, ct).ConfigureAwait(false))
            {
                return;
            }

            if (!_scheduler.IsCurrent(sourcePath, item.Generation, DesiredSourceState.Present))
            {
                return;
            }

            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
            {
                return;
            }

            var sourceStamp = new SourceFileStamp(sourceInfo.Length, sourceInfo.LastWriteTimeUtc);

            if (await ShouldSkipCopyAsync(sourcePath, settings, mapper, ct).ConfigureAwait(false))
            {
                return;
            }

            await TransferToConfiguredTargetsAsync(item, sourceStamp, settings, mapper, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            FileOperationErrorHandler.Log(_logger, ex, LogText.Get("CopyOperation"), sourcePath);
        }
    }

    private async Task ProcessDeleteAsync(PathWorkItem item, CancellationToken ct)
    {
        var sourcePath = item.SourcePath;
        try
        {
            if (!CanContinueAbsentWork(item, sourcePath))
            {
                return;
            }
            var settings = _getSyncOptions();
            if (_initialScanSkipStateStore.IsSkipped(settings.RuntimeId, sourcePath))
            {
                return;
            }

            var mapper = _getPathMapper();
            if (mapper is null)
            {
                _logger.LogWarning(LogText.Get("MappingNotInitializedDelete"), sourcePath);
                return;
            }

            if (TrySkipExcluded(mapper, sourcePath, LogText.Get("DeleteSyncContext")))
            {
                return;
            }

            foreach (var targetRoot in settings.TargetRoots)
            {
                if (!CanContinueAbsentWork(item, sourcePath))
                {
                    return;
                }

                var candidate = GetResolvedTargetPathForDelete(sourcePath, targetRoot, mapper);
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                if (!settings.BackupDeletedTargetsToTrash)
                {
                    await BeforeDeleteStateForgetAsync(item, candidate).ConfigureAwait(false);
                    if (CanContinueAbsentWork(item, sourcePath))
                    {
                        ForgetDeletedTargetState(sourcePath, candidate, settings);
                    }
                    continue;
                }

                try
                {
                    await MoveToTrashWithRetryAsync(item, candidate, settings, mapper, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    FileOperationErrorHandler.Log(_logger, ex, LogText.Get("MoveToTrashOperation"), candidate);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            FileOperationErrorHandler.Log(_logger, ex, LogText.Get("MoveToTrashOperation"), sourcePath);
        }
        finally
        {
            await AfterDeleteProcessingAsync(item).ConfigureAwait(false);
        }
    }

    private bool TrySkipExcluded(PathMapper mapper, string sourcePath, string context)
    {
        if (!mapper.IsExcluded(sourcePath))
        {
            return false;
        }

        _logger.LogDebug(LogText.Get("SkippedByIgnoreContext"), context, sourcePath);
        ForgetSourceState(sourcePath);
        return true;
    }

    internal enum SourcePresence { Present, Missing, Unknown }

    private static SourcePresence ProbeSourcePresence(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return SourcePresence.Present;
        }
        catch (FileNotFoundException) { return SourcePresence.Missing; }
        catch (DirectoryNotFoundException) { return SourcePresence.Missing; }
        catch (IOException) { return SourcePresence.Unknown; }
        catch (UnauthorizedAccessException) { return SourcePresence.Unknown; }
    }

    private bool CanContinueAbsentWork(PathWorkItem item, string sourcePath)
    {
        if (!_scheduler.IsCurrent(sourcePath, item.Generation, DesiredSourceState.Absent))
        {
            return false;
        }

        var presence = SourcePresenceProbe(sourcePath);
        if (presence == SourcePresence.Missing)
        {
            return true;
        }

        if (presence == SourcePresence.Present)
        {
            // A delete notification may run after a recreation.  Make the
            // observed state explicit so the latest content is processed.
            QueueCopy(sourcePath, "Recreated", CancellationToken.None);
        }
        else
        {
            _logger.LogDebug("Could not determine whether the source is absent: {SourcePath}", sourcePath);
        }

        return false;
    }

    private void ForgetSourceState(string sourcePath)
    {
        Fingerprints.TryRemove(sourcePath, out _);
        _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);
    }

    private bool TryNormalizeSourcePath(string path, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            normalized = Path.GetFullPath(path);
            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_getSyncOptions().SourceRoot));
            if (PathKeyComparer.Equals(normalized, sourceRoot) || !PathKeyComparer.StartsWith(normalized, sourceRoot + Path.DirectorySeparatorChar))
            {
                _logger.LogDebug("Ignored source path outside the configured root: {Path}", path);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogWarning(ex, "Could not normalize source path: {Path}", path);
            return false;
        }
    }

    private async Task<bool> WaitForReadyFileAsync(string sourcePath, SyncOptions settings, CancellationToken ct)
    {
        var readySignal = settings.ReadySignal ?? new ReadySignalOptions();
        readySignal.ApplyDefaults();

        if (readySignal.Mode == ReadySignalMode.RenameOnly)
        {
            return File.Exists(sourcePath);
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(readySignal.TimeoutMs);
        long? previousLength = null;
        DateTime? previousLastWriteUtc = null;
        var stableCount = 0;

        while (!ct.IsCancellationRequested && DateTime.UtcNow <= deadline)
        {
            if (!File.Exists(sourcePath))
            {
                _logger.LogDebug(LogText.Get("ReadySourceMissing"), settings.RuleId, sourcePath);
                return false;
            }

            if (readySignal.Mode == ReadySignalMode.DoneFile && !File.Exists(sourcePath + readySignal.DoneFileSuffix))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(readySignal.IntervalMs), ct).ConfigureAwait(false);
                continue;
            }

            long currentLength;
            DateTime currentLastWriteUtc;
            try
            {
                var current = ReadyFileInfoProvider(sourcePath);
                currentLength = current.Length;
                currentLastWriteUtc = current.LastWriteUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, LogText.Get("ReadySourceInfoFailed"), settings.RuleId, sourcePath);
                await Task.Delay(TimeSpan.FromMilliseconds(readySignal.IntervalMs), ct).ConfigureAwait(false);
                continue;
            }

            if (previousLength == currentLength && previousLastWriteUtc == currentLastWriteUtc)
            {
                stableCount++;
            }
            else
            {
                previousLength = currentLength;
                previousLastWriteUtc = currentLastWriteUtc;
                stableCount = 1;
            }

            if (stableCount >= readySignal.StableChecks && (!readySignal.RequireReadable || CanOpenForRead(sourcePath, settings)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(readySignal.IntervalMs), ct).ConfigureAwait(false);
        }

        if (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(LogText.Get("ReadyTimeout"), settings.RuleId, sourcePath);
        }

        return false;
    }

    private static SourceFileStamp ReadReadyFileInfo(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        return new SourceFileStamp(info.Length, info.LastWriteTimeUtc);
    }

    private static bool CanOpenForRead(string sourcePath, SyncOptions settings)
    {
        try
        {
            using var stream = OpenSourceStream(sourcePath, settings);
            return stream.CanRead;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> ShouldSkipCopyAsync(string sourcePath, SyncOptions settings, PathMapper mapper, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
        {
            _logger.LogDebug(LogText.Get("CopySourceMissingBefore"), settings.RuleId, sourcePath);
            return true;
        }

        var info = new FileInfo(sourcePath);

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings) &&
            Fingerprints.TryGetValue(sourcePath, out var fingerprint) &&
            fingerprint.Length == info.Length &&
            fingerprint.LastWriteUtc == info.LastWriteTimeUtc)
        {
            if (!IsTargetSetSatisfied(sourcePath, settings, mapper, info, requireCurrentMetadata: false))
            {
                return false;
            }

            if (settings.ComparisonMode == ComparisonMode.Hash && fingerprint.Hash is string existingHash)
            {
                var currentHash = await FileHashProvider(sourcePath, settings, ct).ConfigureAwait(false);
                if (currentHash is not null && string.Equals(existingHash, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(LogText.Get("DuplicateHashSkipped"), settings.RuleId, sourcePath);
                    return true;
                }
            }
        }

        if (!settings.OverwriteExisting &&
            settings.ComparisonMode == ComparisonMode.LengthAndTimestamp &&
            IsTargetSetSatisfied(sourcePath, settings, mapper, info, requireCurrentMetadata: true))
        {
            _logger.LogDebug(LogText.Get("DestinationCurrentSkipped"), settings.RuleId, sourcePath);
            return true;
        }

        return false;
    }

    private bool IsTargetSetSatisfied(string sourcePath, SyncOptions settings, PathMapper mapper, FileInfo sourceInfo, bool requireCurrentMetadata)
    {
        var anyExists = false;
        var hasAttemptableTarget = false;
        var missingAttemptableTarget = false;
        var now = DateTimeOffset.UtcNow;

        foreach (var targetRoot in settings.TargetRoots)
        {
            if (!TryGetOrCreateResolvedTargetPath(sourcePath, targetRoot, mapper, out var candidate))
            {
                return true;
            }

            var canAttempt = _targetHealthRegistry.CanAttempt(targetRoot, now);
            hasAttemptableTarget |= canAttempt;

            if (!File.Exists(candidate))
            {
                if (canAttempt)
                {
                    missingAttemptableTarget = true;
                }

                continue;
            }

            if (!requireCurrentMetadata)
            {
                anyExists = true;
                continue;
            }

            var destInfo = new FileInfo(candidate);
            var current = destInfo.Length == sourceInfo.Length && destInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc;
            if (current)
            {
                anyExists = true;
            }
            else if (canAttempt)
            {
                missingAttemptableTarget = true;
            }
        }

        return settings.TargetMode == TargetMode.FirstAvailable
            ? anyExists
            : hasAttemptableTarget && !missingAttemptableTarget;
    }

    private async Task TransferToConfiguredTargetsAsync(PathWorkItem item, SourceFileStamp sourceStamp, SyncOptions settings, PathMapper mapper, CancellationToken ct)
    {
        var sourcePath = item.SourcePath;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.OperationTimeoutMs));
        var delayMs = Math.Max(50, settings.InitialRetryDelayMs);
        var completedTargets = new HashSet<string>(PathKeyComparer.Comparer);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!SourceStillMatches(sourcePath, sourceStamp))
                {
                    _scheduler.MarkSourceChangedDetected(sourcePath, item.Generation, null, "SourceChanged");
                    return;
                }

                var sourceInfo = new FileInfo(sourcePath);
                var targetRoots = SelectCandidateTargetRoots(settings, completedTargets).ToArray();
                if (targetRoots.Length == 0)
                {
                    _logger.LogError(LogText.Get("AllTargetsUnhealthy"), settings.RuleId);
                    await DelayUntilTargetCanBeAttemptedAsync(settings, ct).ConfigureAwait(false);
                    continue;
                }

                foreach (var targetRoot in targetRoots)
                {
                    if (!TryGetOrCreateResolvedTargetPath(sourcePath, targetRoot, mapper, out var destPath))
                    {
                        _logger.LogDebug(LogText.Get("TransferIgnoredByMapping"), settings.RuleId, sourcePath);
                        return;
                    }

                    if (!settings.OverwriteExisting && File.Exists(destPath))
                    {
                        var existingMatch = await ExistingTargetMatchesAsync(sourceInfo, destPath, settings, ct).ConfigureAwait(false);
                        await AfterExistingTargetComparisonAsync(item, targetRoot, existingMatch.Matches).ConfigureAwait(false);
                        _logger.LogInformation(LogText.Get("KeepExisting"), settings.RuleId, destPath);
                        if (existingMatch.Matches)
                        {
                            completedTargets.Add(targetRoot);
                            _scheduler.RecordCommittedStamp(sourcePath, item.Generation, sourceStamp);
                        }
                        if (ShouldFinishAfterTarget(settings, completedTargets))
                        {
                            await DeleteSourceAfterSuccessfulTransferAsync(
                                item,
                                settings,
                                new SourceFileStamp(sourceInfo.Length, sourceInfo.LastWriteTimeUtc),
                                existingMatch.ContentHash,
                                completedTargets,
                                ct).ConfigureAwait(false);
                            return;
                        }

                        continue;
                    }

                    try
                    {
                        var destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrWhiteSpace(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        var stagingPath = PathHelper.BuildStagingPath(destPath);
                        var staged = await _stagingFileCopier.CopyAsync(sourcePath, sourceStamp, stagingPath, settings, ct).ConfigureAwait(false);
                        if (!SourceStillMatches(sourcePath, staged.SourceStamp) ||
                            (settings.ComparisonMode == ComparisonMode.Hash && !HashesMatch(staged.ContentHash, await FileHashProvider(sourcePath, settings, ct).ConfigureAwait(false))))
                        {
                            _scheduler.MarkSourceChangedDetected(sourcePath, item.Generation, null, "SourceChanged");
                            TryDeleteStagingFile(stagingPath);
                            return;
                        }

                        await BeforeCommitAsync(item, stagingPath).ConfigureAwait(false);
                        if (!_scheduler.IsCurrent(sourcePath, item.Generation, DesiredSourceState.Present))
                        {
                            TryDeleteStagingFile(stagingPath);
                            return;
                        }

                        File.Move(stagingPath, destPath, overwrite: true);
                        var fingerprint = new FileTransferFingerprint(staged.SourceStamp.Length, staged.SourceStamp.LastWriteUtc, staged.ContentHash);
                        _scheduler.RecordCommittedStamp(sourcePath, item.Generation, staged.SourceStamp);

                        _targetHealthRegistry.RecordSuccess(targetRoot, LogText.Get("CopySucceededHealthReason"));
                        _copiedCounter.Add(1);

                        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings))
                        {
                            Fingerprints[sourcePath] = fingerprint;
                        }

                        completedTargets.Add(targetRoot);
                        _logger.LogInformation(LogText.Get("CopySucceeded"), settings.RuleId, sourcePath, destPath);

                        if (ShouldFinishAfterTarget(settings, completedTargets))
                        {
                            await DeleteSourceAfterSuccessfulTransferAsync(item, settings, staged.SourceStamp, staged.ContentHash, completedTargets, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        _logger.LogWarning(LogText.Get("SourceNotFound"), settings.RuleId, sourcePath);
                        return;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        _logger.LogWarning(LogText.Get("TargetDirectoryMissing"), settings.RuleId, targetRoot);
                        _targetHealthRegistry.RecordFailure(
                            targetRoot,
                            DateTimeOffset.UtcNow,
                            settings.InitialRetryDelayMs,
                            settings.MaxRetryDelayMs,
                            LogText.Get("DirectoryNotFoundReason"));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (SourceChangedException)
                    {
                        _scheduler.MarkSourceChangedDetected(sourcePath, item.Generation, null, "SourceChanged");
                        return;
                    }
                    catch (IOException ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, LogText.Get("TargetWriteIoTransient"), settings.RuleId, destPath);
                        _targetHealthRegistry.RecordFailure(
                            targetRoot,
                            DateTimeOffset.UtcNow,
                            settings.InitialRetryDelayMs,
                            settings.MaxRetryDelayMs,
                            LogText.Get("WriteIoErrorReason"));
                    }
                    catch (UnauthorizedAccessException ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, LogText.Get("TargetWriteAccessDenied"), settings.RuleId, destPath);
                        _targetHealthRegistry.RecordFailure(
                            targetRoot,
                            DateTimeOffset.UtcNow,
                            settings.InitialRetryDelayMs,
                            settings.MaxRetryDelayMs,
                            LogText.Get("WriteAccessDeniedReason"));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LogText.Get("CopyUnhandledError"), settings.RuleId, sourcePath);
                throw;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            delayMs = Math.Min(settings.MaxRetryDelayMs, delayMs * 2);
        }

        if (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(LogText.Get("CopyTimeout"), settings.RuleId, sourcePath);
        }
    }

    private static bool SourceStillMatches(string path, SourceFileStamp stamp)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc == stamp.LastWriteUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<string> SelectCandidateTargetRoots(SyncOptions settings, HashSet<string> completedTargets)
    {
        foreach (var targetRoot in settings.TargetRoots)
        {
            if (completedTargets.Contains(targetRoot))
            {
                continue;
            }

            if (!_targetHealthRegistry.CanAttempt(targetRoot, DateTimeOffset.UtcNow))
            {
                _logger.LogDebug(LogText.Get("SkipUnhealthyTarget"), settings.RuleId, targetRoot);
                continue;
            }

            yield return targetRoot;
        }
    }

    private bool ShouldFinishAfterTarget(SyncOptions settings, HashSet<string> completedTargets)
    {
        if (settings.TargetMode == TargetMode.FirstAvailable)
        {
            return completedTargets.Count > 0;
        }

        return settings.TargetRoots.All(completedTargets.Contains);
    }

    private async Task DelayUntilTargetCanBeAttemptedAsync(SyncOptions settings, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nextAttempt = _targetHealthRegistry.GetNextAttemptUtc(settings.TargetRoots, now);
        var delay = nextAttempt is null
            ? Math.Max(50, settings.MaxRetryDelayMs)
            : Math.Max(50, Math.Min(
                settings.MaxRetryDelayMs,
                (int)Math.Ceiling((nextAttempt.Value - now).TotalMilliseconds)));

        await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
    }

    private async Task<ExistingTargetMatchResult> ExistingTargetMatchesAsync(
        FileInfo sourceInfo,
        string destPath,
        SyncOptions settings,
        CancellationToken ct)
    {
        var destination = new FileInfo(destPath);
        if (!destination.Exists || destination.Length != sourceInfo.Length)
        {
            return default;
        }

        if (settings.ComparisonMode == ComparisonMode.LengthAndTimestamp)
        {
            return new ExistingTargetMatchResult(
                destination.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc,
                null);
        }

        var sourceHash = await FileHashProvider(sourceInfo.FullName, settings, ct).ConfigureAwait(false);
        var destinationHash = await FileHashProvider(destPath, settings, ct).ConfigureAwait(false);
        return new ExistingTargetMatchResult(
            HashesMatch(sourceHash, destinationHash),
            sourceHash);
    }

    private readonly record struct ExistingTargetMatchResult(bool Matches, string? ContentHash);

    private async Task DeleteSourceAfterSuccessfulTransferAsync(
        PathWorkItem item,
        SyncOptions settings,
        SourceFileStamp copiedStamp,
        string? copiedHash,
        HashSet<string> completedTargets,
        CancellationToken ct)
    {
        if (!settings.DeleteSourceAfterCopy)
        {
            return;
        }

        var enoughTargets = settings.TargetMode == TargetMode.FirstAvailable
            ? completedTargets.Count > 0
            : settings.TargetRoots.All(completedTargets.Contains);
        if (!enoughTargets)
        {
            return;
        }

        try
        {
            await DeleteSourceAfterCopyWithRetryAsync(item, settings, copiedStamp, copiedHash, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("SourceDeleteFailed"), settings.RuleId, item.SourcePath);
        }
    }

    private async Task DeleteSourceAfterCopyWithRetryAsync(PathWorkItem item, SyncOptions settings, SourceFileStamp copiedStamp, string? copiedHash, CancellationToken ct)
    {
        var sourcePath = item.SourcePath;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.OperationTimeoutMs));
        var delayMs = Math.Max(50, settings.InitialRetryDelayMs);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!_scheduler.IsCurrent(sourcePath, item.Generation, DesiredSourceState.Present) || !SourceStillMatches(sourcePath, copiedStamp))
                {
                    _scheduler.MarkSourceChangedDetected(sourcePath, item.Generation, null, "SourceChanged");
                    return;
                }

                if (settings.ComparisonMode == ComparisonMode.Hash && !HashesMatch(copiedHash, await FileHashProvider(sourcePath, settings, ct).ConfigureAwait(false)))
                {
                    _scheduler.MarkSourceChangedDetected(sourcePath, item.Generation, null, "SourceChanged");
                    return;
                }

                await BeforeSourceDeleteAsync(item, sourcePath).ConfigureAwait(false);
                if (!_scheduler.IsCurrent(sourcePath, item.Generation, DesiredSourceState.Present) || !SourceStillMatches(sourcePath, copiedStamp))
                {
                    _scheduler.MarkSourceChangedDetected(sourcePath, item.Generation, null, "SourceChanged");
                    return;
                }

                File.Delete(sourcePath);
                _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);
                _logger.LogInformation(LogText.Get("SourceDeleted"), sourcePath);
                return;
            }
            catch (FileNotFoundException)
            {
                _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, LogText.Get("SourceDeleteIoTransient"), sourcePath);
            }
            catch (UnauthorizedAccessException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, LogText.Get("SourceDeleteAccessDenied"), sourcePath);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            delayMs = Math.Min(settings.MaxRetryDelayMs, delayMs * 2);
        }

        if (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(LogText.Get("SourceDeleteTimeout"), sourcePath);
        }
    }

    private static FileStream OpenSourceStream(string path, SyncOptions settings)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            settings.CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task<string?> ComputeFileHashAsync(string path, SyncOptions settings, CancellationToken ct)
    {
        if (settings.ComparisonMode != ComparisonMode.Hash)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                settings.HashBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(settings.HashBufferSize);

            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hasher.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("HashFailed"), path);
            return null;
        }
    }

    internal static bool HashesMatch(string? left, string? right) =>
        left is not null &&
        right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private async Task MoveToTrashWithRetryAsync(PathWorkItem item, string destPath, SyncOptions settings, PathMapper mapper, CancellationToken ct)
    {
        var sourcePath = item.SourcePath;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.OperationTimeoutMs));
        var delayMs = Math.Max(50, settings.InitialRetryDelayMs);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!CanContinueAbsentWork(item, sourcePath))
                {
                    return;
                }

                if (!File.Exists(destPath))
                {
                    _logger.LogDebug(LogText.Get("DestinationAlreadyDeleted"), destPath);
                    if (CanContinueAbsentWork(item, sourcePath))
                    {
                        ForgetDeletedTargetState(sourcePath, destPath, settings);
                    }
                    return;
                }

                var trashPath = mapper.GetTrashPath(destPath);
                var trashDir = Path.GetDirectoryName(trashPath);
                if (!string.IsNullOrEmpty(trashDir))
                {
                    Directory.CreateDirectory(trashDir);
                    EnsureTrashAttributes(trashDir);
                }

                var finalTrashPath = PathHelper.GetUniqueFilePath(trashPath);

                await BeforeTrashMoveAsync(item, destPath).ConfigureAwait(false);
                if (!CanContinueAbsentWork(item, sourcePath))
                {
                    return;
                }

                File.Move(destPath, finalTrashPath);

                await AfterTrashMoveAsync(item, finalTrashPath).ConfigureAwait(false);
                if (!CanContinueAbsentWork(item, sourcePath))
                {
                    RestoreTrashIfSafe(finalTrashPath, destPath);
                    return;
                }

                _deletedCounter.Add(1);
                ForgetDeletedTargetState(sourcePath, destPath, settings);

                _logger.LogInformation(LogText.Get("MovedToTrash"), destPath, finalTrashPath);
                return;
            }
            catch (FileNotFoundException)
            {
                if (CanContinueAbsentWork(item, sourcePath))
                {
                    ForgetDeletedTargetState(sourcePath, destPath, settings);
                }
                return;
            }
            catch (DirectoryNotFoundException)
            {
                if (CanContinueAbsentWork(item, sourcePath))
                {
                    ForgetDeletedTargetState(sourcePath, destPath, settings);
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, LogText.Get("TrashMoveIoTransient"), destPath);
            }
            catch (UnauthorizedAccessException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, LogText.Get("TrashMoveAccessDenied"), destPath);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            delayMs = Math.Min(settings.MaxRetryDelayMs, delayMs * 2);
        }

        if (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(LogText.Get("TrashMoveTimeout"), destPath);
        }
    }

    private void RestoreTrashIfSafe(string trashPath, string destinationPath)
    {
        try
        {
            if (!File.Exists(trashPath) || File.Exists(destinationPath))
            {
                return;
            }

            File.Move(trashPath, destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not restore target after a superseded delete: {TrashPath}", trashPath);
        }
    }

    private void RemoveResolvedTargetPath(string sourcePath, string? targetRoot) => _resolvedTargets.Remove(sourcePath, targetRoot);

    private void RemoveResolvedTargetRoots(string sourcePath) => _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);

    private void ForgetDeletedTargetState(string sourcePath, string destPath, SyncOptions settings)
    {
        Fingerprints.TryRemove(sourcePath, out _);
        RemoveResolvedTargetPath(sourcePath, PathHelper.GetContainingRoot(destPath, settings.TargetRoots));
    }

    private void EnsureTrashAttributes(string trashDir)
    {
#if WINDOWS
        try
        {
            var info = new DirectoryInfo(trashDir);
            if ((info.Attributes & FileAttributes.Hidden) == 0)
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not mark trash directory as hidden: {TrashDirectory}", trashDir);
        }
#else
        _ = trashDir;
#endif
    }

    private bool TryGetOrCreateResolvedTargetPath(string sourcePath, string targetRoot, PathMapper mapper, out string targetPath)
    {
        var success = _resolvedTargets.TryResolve(sourcePath, targetRoot, mapper, out targetPath);
        if (!success)
        {
            Fingerprints.TryRemove(sourcePath, out _);
        }

        return success;
    }

    private string GetResolvedTargetPathForDelete(string sourcePath, string targetRoot, PathMapper mapper)
    {
        var targetPath = _resolvedTargets.ResolveForDelete(sourcePath, targetRoot, mapper);
        if (string.IsNullOrEmpty(targetPath))
        {
            Fingerprints.TryRemove(sourcePath, out _);
        }

        return targetPath;
    }

    private void TryDeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete staging file: {StagingPath}", path);
        }
    }

}
