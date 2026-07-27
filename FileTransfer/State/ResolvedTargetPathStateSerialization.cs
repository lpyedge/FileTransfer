internal sealed class ResolvedTargetPathStateStore : IDisposable
{
    private const long DefaultCompactAfterBytes = 64L * 1024L * 1024L;
    private const int WriteBatchSize = 1024;
    private const int DefaultJournalQueueCapacity = 8192;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger _logger;
    private readonly string _snapshotPath;
    private readonly string _journalPath;
    private readonly long _compactAfterBytes;
    private readonly Channel<JournalRecord> _writeQueue;
    private readonly Task _writerTask;
    private readonly ConcurrentDictionary<string, PersistedResolvedTargetPathEntry> _latest = new(PathKeyComparer.Comparer);
    private readonly object _loadSync = new();
    private readonly object _fileSync = new();
    private int _loaded;
    private int _disposed;
    private int _degraded;

    public ResolvedTargetPathStateStore(ILogger logger)
        : this(logger, Path.Combine(AppPathsOptions.FromConfiguration(new ConfigurationBuilder().Build(), AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "appsettings.yaml")).StateDir, "resolved-target-paths"))
    {
    }

    internal ResolvedTargetPathStateStore(ILogger logger, string statePath)
    {
        _logger = logger;
        (_snapshotPath, _journalPath) = ResolveStateFiles(statePath);
        _compactAfterBytes = DefaultCompactAfterBytes;
        _writeQueue = Channel.CreateBounded<JournalRecord>(new BoundedChannelOptions(DefaultJournalQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(ProcessWritesAsync);
    }

    public IReadOnlyList<PersistedResolvedTargetPathEntry> LoadEntries()
    {
        EnsureLoaded();
        return _latest.Values
            .Where(IsLiveEntry)
            .OrderBy(entry => entry.RuleId, StringComparer.Ordinal)
            .ThenBy(entry => entry.SourceRoot, PathKeyComparer.Comparer)
            .ThenBy(entry => entry.TargetRoot, PathKeyComparer.Comparer)
            .ToArray();
    }

    public void Upsert(PersistedResolvedTargetPathEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RuleId) ||
            string.IsNullOrWhiteSpace(entry.SourceRoot) ||
            string.IsNullOrWhiteSpace(entry.TargetRoot) ||
            string.IsNullOrWhiteSpace(entry.Path))
        {
            return;
        }

        EnsureLoaded();
        var normalized = entry with { RuleId = NormalizeRuleId(entry.RuleId) };
        _latest[BuildKey(normalized.RuleId, normalized.SourceRoot, normalized.TargetRoot)] = normalized;
        Enqueue(JournalRecord.FromEntry("upsert", normalized));
    }

    public void Remove(string ruleId, string sourcePath, string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetRoot))
        {
            return;
        }

        EnsureLoaded();
        var normalizedRuleId = NormalizeRuleId(ruleId);
        _latest.TryRemove(BuildKey(normalizedRuleId, sourcePath, targetRoot), out _);
        Enqueue(new JournalRecord
        {
            Op = "remove",
            RuleId = normalizedRuleId,
            SourceRoot = sourcePath,
            TargetRoot = targetRoot
        });
    }

    public void Remove(string sourcePath, string targetRoot) => Remove(SyncOptions.DefaultRuleId, sourcePath, targetRoot);

    public void RemoveSourceRoot(string ruleId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        EnsureLoaded();
        var normalizedRuleId = NormalizeRuleId(ruleId);
        var prefix = BuildSourcePrefix(normalizedRuleId, sourcePath);
        foreach (var key in _latest.Keys)
        {
            if (PathKeyComparer.StartsWith(key, prefix))
            {
                _latest.TryRemove(key, out _);
            }
        }

        Enqueue(new JournalRecord
        {
            Op = "removeSource",
            RuleId = normalizedRuleId,
            SourceRoot = sourcePath
        });
    }

    public void RemoveSourceRoot(string sourcePath) => RemoveSourceRoot(SyncOptions.DefaultRuleId, sourcePath);

    public void ClearRule(string ruleId)
    {
        EnsureLoaded();
        var normalizedRuleId = NormalizeRuleId(ruleId);
        var prefix = normalizedRuleId + "\n";
        foreach (var key in _latest.Keys)
        {
            if (PathKeyComparer.StartsWith(key, prefix))
            {
                _latest.TryRemove(key, out _);
            }
        }

        Enqueue(new JournalRecord
        {
            Op = "clearRule",
            RuleId = normalizedRuleId
        });
    }

    public void Clear()
    {
        EnsureLoaded();
        _latest.Clear();
        TryDeleteStateFiles();
        Enqueue(new JournalRecord { Op = "clear" });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writeQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Target-path state writer did not stop within the disposal window.");
        }
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded) == 1)
        {
            return;
        }

        lock (_loadSync)
        {
            if (_loaded == 1)
            {
                return;
            }

            try
            {
                _latest.Clear();
                ReadRecords(_snapshotPath, applyToLatest: true);
                ReadRecords(_journalPath, applyToLatest: true);
                PruneStaleEntries();
                CompactFromLatest();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
            {
                _logger.LogWarning(ex, LogText.Get("SavedTargetStateInitFailed"));
                _latest.Clear();
                TryDeleteStateFiles();
            }
            finally
            {
                Volatile.Write(ref _loaded, 1);
            }
        }
    }

    private void ReadRecords(string path, bool applyToLatest)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = System.Text.Json.JsonSerializer.Deserialize<JournalRecord>(line, JsonOptions);
            if (record is null)
            {
                continue;
            }

            if (applyToLatest)
            {
                Apply(record);
            }
        }
    }

    private void Apply(JournalRecord record)
    {
        switch (record.Op?.Trim())
        {
            case "upsert":
                if (record.ToEntry() is { } entry)
                {
                    _latest[BuildKey(entry.RuleId, entry.SourceRoot, entry.TargetRoot)] = entry;
                }
                break;
            case "remove":
                if (!string.IsNullOrWhiteSpace(record.RuleId) && !string.IsNullOrWhiteSpace(record.SourceRoot) && !string.IsNullOrWhiteSpace(record.TargetRoot))
                {
                    _latest.TryRemove(BuildKey(NormalizeRuleId(record.RuleId), record.SourceRoot, record.TargetRoot), out _);
                }
                break;
            case "removeSource":
                if (!string.IsNullOrWhiteSpace(record.RuleId) && !string.IsNullOrWhiteSpace(record.SourceRoot))
                {
                    var prefix = BuildSourcePrefix(NormalizeRuleId(record.RuleId), record.SourceRoot);
                    foreach (var key in _latest.Keys)
                    {
                        if (PathKeyComparer.StartsWith(key, prefix))
                        {
                            _latest.TryRemove(key, out _);
                        }
                    }
                }
                break;
            case "clearRule":
                if (!string.IsNullOrWhiteSpace(record.RuleId))
                {
                    var prefix = NormalizeRuleId(record.RuleId) + "\n";
                    foreach (var key in _latest.Keys)
                    {
                        if (PathKeyComparer.StartsWith(key, prefix))
                        {
                            _latest.TryRemove(key, out _);
                        }
                    }
                }
                break;
            case "clear":
                _latest.Clear();
                break;
        }
    }

    private void PruneStaleEntries()
    {
        foreach (var pair in _latest.ToArray())
        {
            if (!IsLiveEntry(pair.Value))
            {
                _latest.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool IsLiveEntry(PersistedResolvedTargetPathEntry entry)
    {
        try
        {
            if (!File.Exists(entry.SourceRoot))
            {
                return false;
            }

            var currentCreationUtc = new FileInfo(entry.SourceRoot).CreationTimeUtc;
            return currentCreationUtc == entry.SourceCreationUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Enqueue(JournalRecord record)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (_writeQueue.Writer.TryWrite(record))
        {
            return;
        }

        try
        {
            var writeTask = _writeQueue.Writer.WriteAsync(record).AsTask();
            if (!writeTask.Wait(TimeSpan.FromSeconds(2)))
            {
                if (Interlocked.Exchange(ref _degraded, 1) == 0)
                {
                    _logger.LogError(LogText.Get("TargetStateQueueFull"));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
        {
            _logger.LogWarning(ex, LogText.Get("TargetStateQueueEnqueueFailed"));
        }
    }

    private async Task ProcessWritesAsync()
    {
        var buffer = new List<JournalRecord>(WriteBatchSize);

        try
        {
            await foreach (var record in _writeQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                buffer.Add(record);
                while (buffer.Count < WriteBatchSize && _writeQueue.Reader.TryRead(out var next))
                {
                    buffer.Add(next);
                }

                await PersistBatchWithRetryAsync(buffer).ConfigureAwait(false);
                buffer.Clear();
                CompactIfNeeded();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("TargetStateWorkerStopped"));
        }
    }

    private async Task PersistBatchWithRetryAsync(IReadOnlyList<JournalRecord> records)
    {
        var delayMs = 100;
        var attempts = 0;
        var retryLimit = Volatile.Read(ref _disposed) == 0 ? 5 : 3;

        while (true)
        {
            try
            {
                await AppendRecordsAsync(records).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _degraded, 0) != 0)
                {
                    _logger.LogInformation(LogText.Get("TargetStateRecovered"));
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                attempts++;
                if (attempts >= retryLimit)
                {
                    Interlocked.Exchange(ref _degraded, 1);
                    _logger.LogError(ex, LogText.Get("TargetStateConsecutiveFailures"));
                    return;
                }

                _logger.LogWarning(ex, LogText.Get("TargetStateSaveFailedRetry"));
                await Task.Delay(delayMs).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 5000);
            }
        }
    }

    private Task AppendRecordsAsync(IReadOnlyList<JournalRecord> records)
    {
        if (records.Count == 0)
        {
            return Task.CompletedTask;
        }

        var lines = records
            .Select(record => System.Text.Json.JsonSerializer.Serialize(record, JsonOptions))
            .ToArray();

        lock (_fileSync)
        {
            EnsureStateDirectory();
            File.AppendAllLines(_journalPath, lines, System.Text.Encoding.UTF8);
        }

        return Task.CompletedTask;
    }

    private void CompactIfNeeded()
    {
        try
        {
            if (File.Exists(_journalPath) && new FileInfo(_journalPath).Length >= _compactAfterBytes)
            {
                CompactFromLatest();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("TargetStateCompactCheckFailed"));
        }
    }

    private void CompactFromLatest()
    {
        try
        {
            lock (_fileSync)
            {
                EnsureStateDirectory();
                var tempPath = _snapshotPath + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
                {
                    foreach (var entry in _latest.Values.OrderBy(entry => entry.RuleId, StringComparer.Ordinal).ThenBy(entry => entry.SourceRoot, PathKeyComparer.Comparer).ThenBy(entry => entry.TargetRoot, PathKeyComparer.Comparer))
                    {
                        writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(JournalRecord.FromEntry("upsert", entry), JsonOptions));
                    }
                }

                File.Move(tempPath, _snapshotPath, overwrite: true);
                if (File.Exists(_journalPath))
                {
                    File.Delete(_journalPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("TargetStateCompactFailed"));
        }
    }

    private void TryDeleteStateFiles()
    {
        lock (_fileSync)
        {
            foreach (var path in new[] { _snapshotPath, _journalPath, _snapshotPath + ".tmp" })
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
                    _logger.LogDebug(ex, LogText.Get("TargetStateFileDeleteFailed"), path);
                }
            }
        }
    }

    private void EnsureStateDirectory()
    {
        var directory = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static (string SnapshotPath, string JournalPath) ResolveStateFiles(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            statePath = Path.Combine(AppContext.BaseDirectory, "state", "resolved-target-paths");
        }

        if (Path.HasExtension(statePath))
        {
            return (statePath + ".snapshot", statePath + ".journal");
        }

        return (statePath + ".snapshot", statePath + ".journal");
    }

    private static string BuildKey(string ruleId, string sourcePath, string targetRoot) => NormalizeRuleId(ruleId) + "\n" + sourcePath + "\n" + targetRoot;

    private static string BuildSourcePrefix(string ruleId, string sourcePath) => NormalizeRuleId(ruleId) + "\n" + sourcePath + "\n";

    private static string NormalizeRuleId(string ruleId) => string.IsNullOrWhiteSpace(ruleId) ? SyncOptions.DefaultRuleId : ruleId.Trim();

    private sealed class JournalRecord
    {
        public string? Op { get; set; }
        public string? RuleId { get; set; }
        public string? SourceRoot { get; set; }
        public string? TargetRoot { get; set; }
        public string? Path { get; set; }
        public DateTime SourceCreationUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public static JournalRecord FromEntry(string op, PersistedResolvedTargetPathEntry entry) => new()
        {
            Op = op,
            RuleId = NormalizeRuleId(entry.RuleId),
            SourceRoot = entry.SourceRoot,
            TargetRoot = entry.TargetRoot,
            Path = entry.Path,
            SourceCreationUtc = entry.SourceCreationUtc,
            UpdatedUtc = DateTime.UtcNow
        };

        public PersistedResolvedTargetPathEntry? ToEntry()
        {
            if (string.IsNullOrWhiteSpace(RuleId) ||
                string.IsNullOrWhiteSpace(SourceRoot) ||
                string.IsNullOrWhiteSpace(TargetRoot) ||
                string.IsNullOrWhiteSpace(Path))
            {
                return null;
            }

            return new PersistedResolvedTargetPathEntry(NormalizeRuleId(RuleId), SourceRoot, TargetRoot, Path, SourceCreationUtc);
        }
    }
}

internal readonly record struct PersistedResolvedTargetPathEntry(
    string RuleId,
    string SourceRoot,
    string TargetRoot,
    string Path,
    DateTime SourceCreationUtc)
{
    public PersistedResolvedTargetPathEntry(string sourceRoot, string targetRoot, string path, DateTime sourceCreationUtc)
        : this(SyncOptions.DefaultRuleId, sourceRoot, targetRoot, path, sourceCreationUtc)
    {
    }
}
