internal sealed class FileTransferCoordinator : IDisposable
{
    private readonly ILogger _logger;
    private readonly TargetHealthRegistry _targetHealthRegistry;
    private readonly Func<SyncOptions> _getSyncOptions;
    private readonly Func<PathMapper?> _getPathMapper;
    private readonly InitialScanSkipStateStore _initialScanSkipStateStore;
    private readonly TransferConcurrencyGate _concurrencyGate = new(1);
    private readonly InFlightOperationTracker _inflightTracker = new();
    private readonly Counter<long> _copiedCounter;
    private readonly Counter<long> _deletedCounter;
    private readonly ResolvedTargetPathCache _resolvedTargets;
    private readonly ConcurrentDictionary<string, byte> _pendingCopyRequests = new(PathKeyComparer.Comparer);
    private readonly ConcurrentDictionary<string, byte> _pendingDeleteRequests = new(PathKeyComparer.Comparer);
    private readonly Channel<CopyRequest> _copyQueue;
    private readonly Channel<DeleteRequest> _deleteQueue;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _workerSync = new();
    private readonly List<Task> _copyWorkers = new();
    private readonly List<Task> _deleteWorkers = new();
    private int _disposed;

    public FileTransferCoordinator(
        ILogger logger,
        TargetHealthRegistry targetHealthRegistry,
        Func<SyncOptions> getSyncOptions,
        Func<PathMapper?> getPathMapper,
        Counter<long> copiedCounter,
        Counter<long> deletedCounter,
        ResolvedTargetPathStateStore resolvedTargetPathStateStore,
        InitialScanSkipStateStore initialScanSkipStateStore)
    {
        _logger = logger;
        _targetHealthRegistry = targetHealthRegistry;
        _getSyncOptions = getSyncOptions;
        _getPathMapper = getPathMapper;
        _initialScanSkipStateStore = initialScanSkipStateStore;
        _copiedCounter = copiedCounter;
        _deletedCounter = deletedCounter;
        var initialSettings = getSyncOptions();
        _copyQueue = Channel.CreateBounded<CopyRequest>(new BoundedChannelOptions(Math.Max(1, initialSettings.Queue.CopyCapacity))
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _deleteQueue = Channel.CreateBounded<DeleteRequest>(new BoundedChannelOptions(Math.Max(1, initialSettings.Queue.DeleteCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _resolvedTargets = new ResolvedTargetPathCache(logger, resolvedTargetPathStateStore, initialSettings.RuntimeId, ownsStore: true);
    }

    public ConcurrentDictionary<string, FileTransferFingerprint> Fingerprints { get; } = new(PathKeyComparer.Comparer);


    public void UpdateSyncOptions(SyncOptions settings)
    {
        _resolvedTargets.UpdateRuntimeId(settings.RuntimeId);
        _concurrencyGate.UpdateLimit(settings.MaxParallelTransfers);
        EnsureCopyWorkerCount(settings.MaxParallelTransfers);
        EnsureDeleteWorkerCount();

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

        EnsureCopyWorkerCount(_getSyncOptions().MaxParallelTransfers);

        if (!_inflightTracker.TryBegin(sourcePath))
        {
            _pendingCopyRequests[sourcePath] = 0;
            _logger.LogDebug(LogText.Get("CopyDuplicatePending"), sourcePath);
            return;
        }

        if (!_copyQueue.Writer.TryWrite(new CopyRequest(sourcePath, label, stopToken)))
        {
            _inflightTracker.Complete(sourcePath);
            _logger.LogWarning(LogText.Get("CopyQueueFull"), sourcePath);
            return;
        }

        _logger.LogDebug(LogText.Get("CopyQueued"), label, sourcePath);
    }

    public void ScheduleMirrorDelete(string sourcePath, CancellationToken stopToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || stopToken.IsCancellationRequested)
        {
            return;
        }

        EnsureDeleteWorkerCount();

        if (!_inflightTracker.TryBegin(sourcePath))
        {
            _pendingDeleteRequests[sourcePath] = 0;
            _logger.LogDebug(LogText.Get("DeleteDuplicatePending"), sourcePath);
            return;
        }

        if (!_deleteQueue.Writer.TryWrite(new DeleteRequest(sourcePath, stopToken)))
        {
            _inflightTracker.Complete(sourcePath);
            _logger.LogWarning(LogText.Get("DeleteQueueFull"), sourcePath);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _copyQueue.Writer.TryComplete();
        _deleteQueue.Writer.TryComplete();
        _disposeCts.Cancel();

        try
        {
            Task.WaitAll(_copyWorkers.Concat(_deleteWorkers).ToArray(), TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _disposeCts.Dispose();
        _concurrencyGate.Dispose();
        _resolvedTargets.Dispose();
        Fingerprints.Clear();
        _pendingCopyRequests.Clear();
        _pendingDeleteRequests.Clear();
    }


    private void EnsureCopyWorkerCount(int maxParallel)
    {
        var desired = Math.Max(1, maxParallel);

        lock (_workerSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            while (_copyWorkers.Count < desired)
            {
                var workerId = _copyWorkers.Count + 1;
                _copyWorkers.Add(Task.Run(() => CopyWorkerLoopAsync(workerId)));
            }
        }
    }

    private void EnsureDeleteWorkerCount()
    {
        lock (_workerSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || _deleteWorkers.Count > 0)
            {
                return;
            }

            _deleteWorkers.Add(Task.Run(() => DeleteWorkerLoopAsync()));
        }
    }

    private async Task CopyWorkerLoopAsync(int workerId)
    {
        try
        {
            await foreach (var request in _copyQueue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                await ProcessCopyRequestAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("CopyWorkerStopped"), workerId);
        }
    }

    private async Task DeleteWorkerLoopAsync()
    {
        try
        {
            await foreach (var request in _deleteQueue.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                await ProcessDeleteRequestAsync(request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("DeleteWorkerStopped"));
        }
    }

    private async Task ProcessCopyRequestAsync(CopyRequest request)
    {
        IDisposable? lease = null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(request.StopToken, _disposeCts.Token);
        var ct = linkedCts.Token;
        var sourcePath = request.SourcePath;

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

            lease = await _concurrencyGate.AcquireAsync(ct).ConfigureAwait(false);

            if (!await WaitForReadyFileAsync(sourcePath, settings, ct).ConfigureAwait(false))
            {
                return;
            }

            if (await ShouldSkipCopyAsync(sourcePath, settings, mapper, ct).ConfigureAwait(false))
            {
                return;
            }

            await TransferToConfiguredTargetsAsync(sourcePath, settings, mapper, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileOperationErrorHandler.Log(_logger, ex, LogText.Get("CopyOperation"), sourcePath);
        }
        finally
        {
            lease?.Dispose();
            _inflightTracker.Complete(sourcePath);
            ReplayPendingRequestsIfNeeded(sourcePath, request.StopToken);
        }
    }

    private async Task ProcessDeleteRequestAsync(DeleteRequest request)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(request.StopToken, _disposeCts.Token);
        var ct = linkedCts.Token;
        var sourcePath = request.SourcePath;

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
                _logger.LogWarning(LogText.Get("MappingNotInitializedDelete"), sourcePath);
                return;
            }

            if (TrySkipExcluded(mapper, sourcePath, LogText.Get("DeleteSyncContext")))
            {
                return;
            }

            foreach (var targetRoot in settings.TargetRoots)
            {
                var candidate = GetResolvedTargetPathForDelete(sourcePath, targetRoot, mapper);
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                if (!settings.BackupDeletedTargetsToTrash)
                {
                    ForgetDeletedTargetState(sourcePath, candidate, settings);
                    continue;
                }

                try
                {
                    await MoveToTrashWithRetryAsync(sourcePath, candidate, settings, mapper, ct).ConfigureAwait(false);
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
        }
        catch (Exception ex)
        {
            FileOperationErrorHandler.Log(_logger, ex, LogText.Get("MoveToTrashOperation"), sourcePath);
        }
        finally
        {
            _inflightTracker.Complete(sourcePath);
            ReplayPendingRequestsIfNeeded(sourcePath, request.StopToken);
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

    private void ForgetSourceState(string sourcePath)
    {
        Fingerprints.TryRemove(sourcePath, out _);
        _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);
    }

    private void ReplayPendingRequestsIfNeeded(string sourcePath, CancellationToken stopToken)
    {
        if (stopToken.IsCancellationRequested)
        {
            _pendingCopyRequests.TryRemove(sourcePath, out _);
            _pendingDeleteRequests.TryRemove(sourcePath, out _);
            return;
        }

        if (_pendingDeleteRequests.TryRemove(sourcePath, out _))
        {
            ScheduleMirrorDelete(sourcePath, stopToken);
            return;
        }

        if (_pendingCopyRequests.TryRemove(sourcePath, out _))
        {
            QueueCopy(sourcePath, "Pending", stopToken);
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

            FileInfo info;
            try
            {
                info = new FileInfo(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, LogText.Get("ReadySourceInfoFailed"), settings.RuleId, sourcePath);
                await Task.Delay(TimeSpan.FromMilliseconds(readySignal.IntervalMs), ct).ConfigureAwait(false);
                continue;
            }

            if (previousLength == info.Length && previousLastWriteUtc == info.LastWriteTimeUtc)
            {
                stableCount++;
            }
            else
            {
                previousLength = info.Length;
                previousLastWriteUtc = info.LastWriteTimeUtc;
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
                var currentHash = await ComputeFileHashAsync(sourcePath, settings, ct).ConfigureAwait(false);
                if (currentHash is not null && string.Equals(existingHash, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(LogText.Get("DuplicateHashSkipped"), settings.RuleId, sourcePath);
                    return true;
                }
            }
        }

        if (!settings.OverwriteExisting && IsTargetSetSatisfied(sourcePath, settings, mapper, info, requireCurrentMetadata: true))
        {
            _logger.LogDebug(LogText.Get("DestinationCurrentSkipped"), settings.RuleId, sourcePath);
            return true;
        }

        return false;
    }

    private bool IsTargetSetSatisfied(string sourcePath, SyncOptions settings, PathMapper mapper, FileInfo sourceInfo, bool requireCurrentMetadata)
    {
        var anyExists = false;
        var hasHealthyTarget = false;
        var missingHealthyTarget = false;

        foreach (var targetRoot in settings.TargetRoots)
        {
            if (!TryGetOrCreateResolvedTargetPath(sourcePath, targetRoot, mapper, out var candidate))
            {
                return true;
            }

            var healthy = _targetHealthRegistry.IsHealthy(targetRoot);
            hasHealthyTarget |= healthy;

            if (!File.Exists(candidate))
            {
                if (healthy)
                {
                    missingHealthyTarget = true;
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
            else if (healthy)
            {
                missingHealthyTarget = true;
            }
        }

        return settings.TargetMode == TargetMode.FirstAvailable
            ? anyExists
            : hasHealthyTarget && !missingHealthyTarget;
    }

    private async Task TransferToConfiguredTargetsAsync(string sourcePath, SyncOptions settings, PathMapper mapper, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.OperationTimeoutMs));
        var delayMs = Math.Max(50, settings.InitialRetryDelayMs);
        var completedTargets = new HashSet<string>(PathKeyComparer.Comparer);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(sourcePath))
                {
                    _logger.LogWarning(LogText.Get("SourceDisappearedBeforeCopy"), settings.RuleId, sourcePath);
                    return;
                }

                var sourceInfo = new FileInfo(sourcePath);
                var targetRoots = SelectCandidateTargetRoots(settings, completedTargets).ToArray();
                if (targetRoots.Length == 0)
                {
                    if (completedTargets.Count > 0)
                    {
                        await DeleteSourceAfterSuccessfulTransferAsync(sourcePath, settings, ct).ConfigureAwait(false);
                        return;
                    }

                    _logger.LogError(LogText.Get("AllTargetsUnhealthy"), settings.RuleId);
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
                        _logger.LogInformation(LogText.Get("KeepExisting"), settings.RuleId, destPath);
                        completedTargets.Add(targetRoot);
                        if (ShouldFinishAfterTarget(settings, completedTargets))
                        {
                            await DeleteSourceAfterSuccessfulTransferAsync(sourcePath, settings, ct).ConfigureAwait(false);
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

                        var fingerprint = await CopyFileAsync(sourceInfo, destPath, settings, ct).ConfigureAwait(false);
                        File.SetLastWriteTimeUtc(destPath, sourceInfo.LastWriteTimeUtc);
                        await VerifyCopiedFileAsync(sourceInfo, destPath, fingerprint.Hash, settings, ct).ConfigureAwait(false);

                        _targetHealthRegistry.Update(targetRoot, true, LogText.Get("CopySucceededHealthReason"));
                        _copiedCounter.Add(1);

                        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings))
                        {
                            Fingerprints[sourcePath] = fingerprint;
                        }

                        completedTargets.Add(targetRoot);
                        _logger.LogInformation(LogText.Get("CopySucceeded"), settings.RuleId, sourcePath, destPath);

                        if (ShouldFinishAfterTarget(settings, completedTargets))
                        {
                            await DeleteSourceAfterSuccessfulTransferAsync(sourcePath, settings, ct).ConfigureAwait(false);
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
                        _targetHealthRegistry.Update(targetRoot, false, LogText.Get("DirectoryNotFoundReason"));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (IOException ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, LogText.Get("TargetWriteIoTransient"), settings.RuleId, destPath);
                        _targetHealthRegistry.Update(targetRoot, false, LogText.Get("WriteIoErrorReason"));
                    }
                    catch (UnauthorizedAccessException ex) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogDebug(ex, LogText.Get("TargetWriteAccessDenied"), settings.RuleId, destPath);
                        _targetHealthRegistry.Update(targetRoot, false, LogText.Get("WriteAccessDeniedReason"));
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

    private IEnumerable<string> SelectCandidateTargetRoots(SyncOptions settings, HashSet<string> completedTargets)
    {
        foreach (var targetRoot in settings.TargetRoots)
        {
            if (completedTargets.Contains(targetRoot))
            {
                continue;
            }

            if (!_targetHealthRegistry.IsHealthy(targetRoot))
            {
                _logger.LogDebug(LogText.Get("SkipUnhealthyTarget"), settings.RuleId, targetRoot);
                continue;
            }

            yield return targetRoot;

            if (settings.TargetMode == TargetMode.FirstAvailable)
            {
                yield break;
            }
        }
    }

    private bool ShouldFinishAfterTarget(SyncOptions settings, HashSet<string> completedTargets)
    {
        if (settings.TargetMode == TargetMode.FirstAvailable)
        {
            return completedTargets.Count > 0;
        }

        return settings.TargetRoots
            .Where(_targetHealthRegistry.IsHealthy)
            .All(completedTargets.Contains);
    }

    private async Task DeleteSourceAfterSuccessfulTransferAsync(string sourcePath, SyncOptions settings, CancellationToken ct)
    {
        if (!settings.DeleteSourceAfterCopy)
        {
            return;
        }

        try
        {
            await DeleteSourceAfterCopyWithRetryAsync(sourcePath, settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("SourceDeleteFailed"), settings.RuleId, sourcePath);
        }
    }

    private async Task DeleteSourceAfterCopyWithRetryAsync(string sourcePath, SyncOptions settings, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.OperationTimeoutMs));
        var delayMs = Math.Max(50, settings.InitialRetryDelayMs);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(sourcePath))
                {
                    _logger.LogDebug(LogText.Get("SourceAlreadyMissing"), sourcePath);
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

    private async Task<FileTransferFingerprint> CopyFileAsync(FileInfo sourceInfo, string destPath, SyncOptions settings, CancellationToken ct)
    {
        var tempPath = PathHelper.BuildStagingPath(destPath);
        var tempDir = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrWhiteSpace(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        try
        {
            string? hashValue;

            await using (var source = OpenSourceStream(sourceInfo.FullName, settings))
            await using (var destination = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                settings.CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                IncrementalHash? hasher = null;
                if (settings.ComparisonMode == ComparisonMode.Hash)
                {
                    hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                }

                var buffer = ArrayPool<byte>.Shared.Rent(settings.CopyBufferSize);
                hashValue = null;

                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        hasher?.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    }

                    await destination.FlushAsync(ct).ConfigureAwait(false);

                    if (hasher is not null)
                    {
                        hashValue = Convert.ToHexString(hasher.GetHashAndReset());
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    hasher?.Dispose();
                }
            }

            File.SetLastWriteTimeUtc(tempPath, sourceInfo.LastWriteTimeUtc);
            File.Move(tempPath, destPath, overwrite: true);
            return new FileTransferFingerprint(sourceInfo.Length, sourceInfo.LastWriteTimeUtc, hashValue);
        }
        catch
        {
            TryDeleteStagingFile(tempPath);
            throw;
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

    private async Task VerifyCopiedFileAsync(FileInfo sourceInfo, string destPath, string? sourceHash, SyncOptions settings, CancellationToken ct)
    {
        var destInfo = new FileInfo(destPath);
        if (!destInfo.Exists)
        {
            throw new IOException($"Destination file was not created: {destPath}");
        }

        var toleratedSourceWriteUtc = sourceInfo.LastWriteTimeUtc.AddSeconds(-2);
        if (destInfo.Length != sourceInfo.Length || destInfo.LastWriteTimeUtc < toleratedSourceWriteUtc)
        {
            throw new IOException($"Destination metadata verification failed: {destPath}");
        }

        if (settings.ComparisonMode == ComparisonMode.Hash && !string.IsNullOrEmpty(sourceHash))
        {
            var destHash = await ComputeFileHashAsync(destPath, settings, ct).ConfigureAwait(false);
            if (!string.Equals(sourceHash, destHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Destination hash verification failed: {destPath}");
            }
        }
    }

    private async Task MoveToTrashWithRetryAsync(string sourcePath, string destPath, SyncOptions settings, PathMapper mapper, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, settings.OperationTimeoutMs));
        var delayMs = Math.Max(50, settings.InitialRetryDelayMs);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(destPath))
                {
                    _logger.LogDebug(LogText.Get("DestinationAlreadyDeleted"), destPath);
                    ForgetDeletedTargetState(sourcePath, destPath, settings);
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
                File.Move(destPath, finalTrashPath);

                _deletedCounter.Add(1);
                ForgetDeletedTargetState(sourcePath, destPath, settings);

                _logger.LogInformation(LogText.Get("MovedToTrash"), destPath, finalTrashPath);
                return;
            }
            catch (FileNotFoundException)
            {
                ForgetDeletedTargetState(sourcePath, destPath, settings);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                ForgetDeletedTargetState(sourcePath, destPath, settings);
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

    private void RemoveResolvedTargetPath(string sourcePath, string? targetRoot) => _resolvedTargets.Remove(sourcePath, targetRoot);

    private void RemoveResolvedTargetRoots(string sourcePath) => _resolvedTargets.RemoveSource(sourcePath, _getSyncOptions().TargetRoots);

    private void ForgetDeletedTargetState(string sourcePath, string destPath, SyncOptions settings)
    {
        Fingerprints.TryRemove(sourcePath, out _);
        RemoveResolvedTargetPath(sourcePath, PathHelper.GetContainingRoot(destPath, settings.TargetRoots));
    }

    private static void EnsureTrashAttributes(string trashDir)
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
        catch
        {
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

    private static void TryDeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private readonly record struct CopyRequest(string SourcePath, string Label, CancellationToken StopToken);

    private readonly record struct DeleteRequest(string SourcePath, CancellationToken StopToken);
}