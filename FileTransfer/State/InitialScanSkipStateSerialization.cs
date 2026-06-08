internal sealed class InitialScanSkipStateStore : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ILogger _logger;
    private readonly string _journalPath;
    private readonly ConcurrentDictionary<string, PersistedSkippedSourceEntry> _entries = new(PathKeyComparer.Comparer);
    private readonly object _loadSync = new();
    private readonly object _fileSync = new();
    private int _loaded;
    private int _disposed;

    public InitialScanSkipStateStore(ILogger logger)
        : this(logger, Path.Combine(AppPathsOptions.FromConfiguration(new ConfigurationBuilder().Build(), AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "appsettings.yaml")).StateDir, "initial-scan-skipped-files"))
    {
    }

    internal InitialScanSkipStateStore(ILogger logger, string statePath)
    {
        _logger = logger;
        _journalPath = ResolveStateFile(statePath);
    }

    public bool IsSkipped(string runtimeId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        EnsureLoaded();
        return _entries.ContainsKey(BuildKey(runtimeId, sourcePath));
    }

    public int SeedFromSourceRoot(SyncOptions settings)
    {
        if (settings.SourceRoot.Length == 0 || !Directory.Exists(settings.SourceRoot))
        {
            return 0;
        }

        var seeded = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.IncludeSubdirectories,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in Directory.EnumerateFiles(settings.SourceRoot, "*", options))
        {
            if (MarkSkipped(settings.RuntimeId, file))
            {
                seeded++;
            }
        }

        return seeded;
    }

    public bool MarkSkipped(string runtimeId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        EnsureLoaded();

        var entry = new PersistedSkippedSourceEntry(NormalizeNamespaceId(runtimeId), sourcePath);
        if (!_entries.TryAdd(BuildKey(entry.RuntimeId, entry.SourcePath), entry))
        {
            return false;
        }

        AppendRecord(new JournalRecord
        {
            Op = "skip",
            RuntimeId = entry.RuntimeId,
            SourcePath = entry.SourcePath
        });

        return true;
    }

    public IReadOnlyList<PersistedSkippedSourceEntry> LoadEntries()
    {
        EnsureLoaded();
        return _entries.Values
            .OrderBy(entry => entry.RuntimeId, StringComparer.Ordinal)
            .ThenBy(entry => entry.SourcePath, PathKeyComparer.Comparer)
            .ToArray();
    }

    public void Clear()
    {
        EnsureLoaded();
        _entries.Clear();

        lock (_fileSync)
        {
            if (File.Exists(_journalPath))
            {
                File.Delete(_journalPath);
            }
        }

        Volatile.Write(ref _loaded, 0);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded) == 1 && File.Exists(_journalPath))
        {
            return;
        }

        lock (_loadSync)
        {
            if (_loaded == 1 && File.Exists(_journalPath))
            {
                return;
            }

            _entries.Clear();

            if (!File.Exists(_journalPath))
            {
                Volatile.Write(ref _loaded, 0);
                return;
            }

            try
            {
                foreach (var line in File.ReadLines(_journalPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var record = System.Text.Json.JsonSerializer.Deserialize<JournalRecord>(line, JsonOptions);
                    if (record is { Op: "skip" } && record.ToEntry() is { } entry)
                    {
                        _entries[BuildKey(entry.RuntimeId, entry.SourcePath)] = entry;
                    }
                }

                Volatile.Write(ref _loaded, 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
            {
                _logger.LogWarning(ex, "Initial scan skip state load failed.");
                _entries.Clear();
                try
                {
                    File.Delete(_journalPath);
                }
                catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(deleteEx, "Initial scan skip state delete failed: {Path}", _journalPath);
                }

                Volatile.Write(ref _loaded, 0);
            }
        }
    }

    private void AppendRecord(JournalRecord record)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_fileSync)
        {
            EnsureStateDirectory();
            File.AppendAllText(_journalPath, System.Text.Json.JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine, System.Text.Encoding.UTF8);
        }
    }

    private void EnsureStateDirectory()
    {
        var directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ResolveStateFile(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            statePath = Path.Combine(AppContext.BaseDirectory, "state", "initial-scan-skipped-files");
        }

        return Path.HasExtension(statePath) ? statePath : statePath + ".journal";
    }

    private static string BuildKey(string runtimeId, string sourcePath) => NormalizeNamespaceId(runtimeId) + "\n" + sourcePath;

    private static string NormalizeNamespaceId(string runtimeId) => string.IsNullOrWhiteSpace(runtimeId) ? SyncOptions.DefaultRuleId : runtimeId.Trim();

    private sealed class JournalRecord
    {
        public string? Op { get; set; }
        public string? RuntimeId { get; set; }
        public string? SourcePath { get; set; }

        public PersistedSkippedSourceEntry? ToEntry()
        {
            if (string.IsNullOrWhiteSpace(RuntimeId) || string.IsNullOrWhiteSpace(SourcePath))
            {
                return null;
            }

            return new PersistedSkippedSourceEntry(NormalizeNamespaceId(RuntimeId), SourcePath);
        }
    }
}

internal readonly record struct PersistedSkippedSourceEntry(string RuntimeId, string SourcePath);