internal sealed class ResolvedTargetPathCache : IDisposable
{
    private const int MaxEntries = 100000;

    private readonly ILogger _logger;
    private readonly ResolvedTargetPathStateStore _store;
    private readonly bool _ownsStore;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(PathKeyComparer.Comparer);
    private string _runtimeId;

    public ResolvedTargetPathCache(ILogger logger, string runtimeId = SyncOptions.DefaultRuleId)
        : this(logger, new ResolvedTargetPathStateStore(logger), runtimeId, ownsStore: true)
    {
    }

    internal ResolvedTargetPathCache(ILogger logger, ResolvedTargetPathStateStore store, string runtimeId = SyncOptions.DefaultRuleId, bool ownsStore = false)
    {
        _logger = logger;
        _store = store;
        _ownsStore = ownsStore;
        _runtimeId = NormalizeNamespaceId(runtimeId);
    }

    public void UpdateRuntimeId(string runtimeId)
    {
        var normalized = NormalizeNamespaceId(runtimeId);
        if (string.Equals(_runtimeId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _runtimeId = normalized;
        _entries.Clear();
    }

    public void UpdateRuleId(string ruleId) => UpdateRuntimeId(ruleId);

    public void Restore()
    {
        try
        {
            _entries.Clear();
            foreach (var entry in _store.LoadEntries().Where(entry => string.Equals(entry.RuleId, _runtimeId, StringComparison.Ordinal)))
            {
                if (string.IsNullOrWhiteSpace(entry.SourceRoot) ||
                    string.IsNullOrWhiteSpace(entry.TargetRoot) ||
                    string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                _entries[BuildKey(entry.SourceRoot, entry.TargetRoot)] = Entry.Create(entry.Path, entry.SourceCreationUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, LogText.Get("SavedTargetStateLoadFailed"));
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _store.ClearRule(_runtimeId);
    }

    public bool TryResolve(string sourcePath, string targetRoot, PathMapper mapper, out string targetPath)
    {
        targetPath = string.Empty;
        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, targetRoot);
        if (resolution.Ignored)
        {
            Remove(sourcePath, targetRoot);
            return false;
        }

        var key = BuildKey(sourcePath, targetRoot);
        var hasCurrentCreationUtc = TryGetSourceCreationUtc(sourcePath, out var currentCreationUtc);

        if (_entries.TryGetValue(key, out var existing) &&
            (!hasCurrentCreationUtc || existing.SourceCreationUtc == currentCreationUtc))
        {
            targetPath = existing.Path;
            _entries[key] = existing.Touch();
            return true;
        }

        if (hasCurrentCreationUtc && TryReuseRelativePathFromExistingTarget(sourcePath, targetRoot, currentCreationUtc, out targetPath))
        {
            var reused = Entry.Create(targetPath, currentCreationUtc);
            _entries[key] = reused;
            PersistIfNeeded(mapper, sourcePath, targetRoot, reused);
            TrimIfNeeded();
            return true;
        }

        var updated = Entry.Create(resolution.TargetPath, hasCurrentCreationUtc ? currentCreationUtc : DateTime.MinValue);
        _entries[key] = updated;
        PersistIfNeeded(mapper, sourcePath, targetRoot, updated);
        TrimIfNeeded();

        targetPath = updated.Path;
        return true;
    }

    private bool TryReuseRelativePathFromExistingTarget(string sourcePath, string targetRoot, DateTime? currentCreationUtc, out string targetPath)
    {
        targetPath = string.Empty;
        var sourceKeyPrefix = sourcePath + "\n";

        foreach (var pair in _entries)
        {
            if (!PathKeyComparer.StartsWith(pair.Key, sourceKeyPrefix))
            {
                continue;
            }

            if (currentCreationUtc is DateTime expectedCreationUtc && pair.Value.SourceCreationUtc != expectedCreationUtc)
            {
                continue;
            }

            var existingTargetRoot = pair.Key[sourceKeyPrefix.Length..];
            if (string.IsNullOrWhiteSpace(existingTargetRoot))
            {
                continue;
            }

            var relative = Path.GetRelativePath(existingTargetRoot, pair.Value.Path);
            if (!PathHelper.IsSafeRelativePath(relative))
            {
                continue;
            }

            targetPath = Path.Combine(targetRoot, PathHelper.CombinePath(PathHelper.SplitPath(relative)));
            return true;
        }

        return false;
    }

    public string ResolveForDelete(string sourcePath, string targetRoot, PathMapper mapper)
    {
        var key = BuildKey(sourcePath, targetRoot);
        if (_entries.TryGetValue(key, out var existing))
        {
            _entries[key] = existing.Touch();
            return existing.Path;
        }

        if (TryReuseRelativePathFromExistingTarget(sourcePath, targetRoot, currentCreationUtc: null, out var reusedTargetPath))
        {
            return reusedTargetPath;
        }

        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, targetRoot);
        if (resolution.Ignored)
        {
            Remove(sourcePath, targetRoot);
            return string.Empty;
        }

        return resolution.TargetPath;
    }

    public void Remove(string sourcePath, string? targetRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            RemoveSource(sourcePath);
            return;
        }

        if (_entries.TryRemove(BuildKey(sourcePath, targetRoot), out _))
        {
            _store.Remove(_runtimeId, sourcePath, targetRoot);
        }
    }

    public void RemoveSource(string sourcePath, IEnumerable<string>? targetRoots = null)
    {
        var removedAny = false;

        if (targetRoots is not null)
        {
            foreach (var targetRoot in targetRoots)
            {
                removedAny |= _entries.TryRemove(BuildKey(sourcePath, targetRoot), out _);
            }
        }
        else
        {
            foreach (var key in _entries.Keys)
            {
                if (PathKeyComparer.StartsWith(key, sourcePath + "\n"))
                {
                    removedAny |= _entries.TryRemove(key, out _);
                }
            }
        }

        if (removedAny)
        {
            if (targetRoots is not null)
            {
                foreach (var targetRoot in targetRoots)
                {
                    _store.Remove(_runtimeId, sourcePath, targetRoot);
                }
            }
            else
            {
                _store.RemoveSourceRoot(_runtimeId, sourcePath);
            }
        }
    }

    private void PersistIfNeeded(PathMapper mapper, string sourcePath, string targetRoot, Entry entry)
    {
        if (mapper.HasDynamicTemplates)
        {
            _store.Upsert(new PersistedResolvedTargetPathEntry(_runtimeId, sourcePath, targetRoot, entry.Path, entry.SourceCreationUtc));
        }
    }

    private void TrimIfNeeded()
    {
        var overflow = _entries.Count - MaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var pair in _entries.OrderBy(pair => pair.Value.LastAccessUtc).Take(overflow))
        {
            if (_entries.TryRemove(pair.Key, out _))
            {
                var separator = pair.Key.IndexOf('\n');
                if (separator > 0 && separator < pair.Key.Length - 1)
                {
                    var sourcePath = pair.Key[..separator];
                    var targetRoot = pair.Key[(separator + 1)..];
                    _store.Remove(_runtimeId, sourcePath, targetRoot);
                }
            }
        }
    }

    private static string BuildKey(string sourcePath, string targetRoot) => sourcePath + "\n" + targetRoot;

    public void Dispose()
    {
        if (_ownsStore)
        {
            _store.Dispose();
        }
    }

    private static string NormalizeNamespaceId(string runtimeId) => string.IsNullOrWhiteSpace(runtimeId) ? SyncOptions.DefaultRuleId : runtimeId.Trim();

    private static bool TryGetSourceCreationUtc(string sourcePath, out DateTime creationUtc)
    {
        creationUtc = DateTime.MinValue;

        try
        {
            if (!File.Exists(sourcePath))
            {
                return false;
            }

            creationUtc = new FileInfo(sourcePath).CreationTimeUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct Entry(string Path, DateTime SourceCreationUtc, DateTime LastAccessUtc)
    {
        public static Entry Create(string path, DateTime sourceCreationUtc) => new(path, sourceCreationUtc, DateTime.UtcNow);
        public Entry Touch() => this with { LastAccessUtc = DateTime.UtcNow };
    }
}
