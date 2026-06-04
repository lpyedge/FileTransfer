internal sealed class FileWatcherManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<SyncOptions> _getSyncOptions;
    private readonly Action<FileChangeKind, string> _onChange;
    private readonly Action<string> _onDelete;
    private readonly object _sync = new();

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _restartTimer;
    private int _restartAttempts;
    private bool _disposed;

    private FileSystemEventHandler? _onCreatedHandler;
    private FileSystemEventHandler? _onChangedHandler;
    private RenamedEventHandler? _onRenamedHandler;
    private FileSystemEventHandler? _onDeletedHandler;
    private ErrorEventHandler? _onErrorHandler;

    public FileWatcherManager(
        ILogger logger,
        Func<SyncOptions> getSyncOptions,
        Action<FileChangeKind, string> onChange,
        Action<string> onDelete)
    {
        _logger = logger;
        _getSyncOptions = getSyncOptions;
        _onChange = onChange;
        _onDelete = onDelete;
    }

    public void Update(SyncOptions settings)
    {
        var needsRestart = false;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            CancelRestartTimer();
            DisposeWatcher();
            needsRestart = !TryStartWatcher(settings);
        }

        if (needsRestart)
        {
            ScheduleRestart();
        }
    }

    private bool TryStartWatcher(SyncOptions settings)
    {
        if (!Directory.Exists(settings.SourceRoot))
        {
            _logger.LogWarning(LogText.Get("WatcherSourceMissing"), settings.SourceRoot);
            return false;
        }

        var watcher = new FileSystemWatcher(settings.SourceRoot)
        {
            IncludeSubdirectories = settings.IncludeSubdirectories,
            NotifyFilter = GetNotifyFilters(settings),
            InternalBufferSize = settings.WatcherInternalBufferSize
        };

        ConfigureFilters(watcher, FileProcessingRules.NormalizeFileExtensions(settings.FileExtensions));

        _onErrorHandler = OnWatcherError;
        watcher.Error += _onErrorHandler;

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Created, settings))
        {
            _onCreatedHandler = (_, e) => _onChange(FileChangeKind.Created, e.FullPath);
            watcher.Created += _onCreatedHandler;
        }

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Changed, settings))
        {
            _onChangedHandler = (_, e) => _onChange(FileChangeKind.Changed, e.FullPath);
            watcher.Changed += _onChangedHandler;
        }

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Renamed, settings))
        {
            _onRenamedHandler = (_, e) => _onChange(FileChangeKind.Renamed, e.FullPath);
            watcher.Renamed += _onRenamedHandler;
        }

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings))
        {
            _onDeletedHandler = (_, e) => _onDelete(e.FullPath);
            watcher.Deleted += _onDeletedHandler;
        }

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        _restartAttempts = 0;
        return true;
    }

    private void ConfigureFilters(FileSystemWatcher watcher, IReadOnlyCollection<string> extensions)
    {
        watcher.Filter = "*.*";
        if (extensions.Count == 0)
        {
            return;
        }

        watcher.Filters.Clear();
        foreach (var ext in extensions)
        {
            watcher.Filters.Add($"*{ext}");
        }
    }

    private System.IO.NotifyFilters GetNotifyFilters(SyncOptions settings)
    {
        if (settings.NotifyFilters is not { Length: > 0 })
        {
            return System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName | System.IO.NotifyFilters.LastWrite;
        }

        var filters = (System.IO.NotifyFilters)0;
        foreach (var item in settings.NotifyFilters)
        {
            if (Enum.TryParse<System.IO.NotifyFilters>(item, true, out var parsed))
            {
                filters |= parsed;
            }
        }

        return filters == 0
            ? System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName | System.IO.NotifyFilters.LastWrite
            : filters;
    }

    private void OnWatcherError(object? sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logger.LogWarning(ex, LogText.Get("WatcherErrorRestart"));
        ScheduleRestart();
    }

    private void ScheduleRestart(TimeSpan? delay = null)
    {
        lock (_sync)
        {
            if (_disposed || _restartTimer is not null)
            {
                return;
            }

            DisposeWatcher();

            var settings = _getSyncOptions();
            var baseDelayMs = Math.Max(50, settings.InitialRetryDelayMs);
            var nextDelay = delay ?? TimeSpan.FromMilliseconds(Math.Min(settings.MaxRetryDelayMs, baseDelayMs * Math.Pow(2, Math.Max(0, _restartAttempts++))));

            _restartTimer?.Dispose();
            _restartTimer = new System.Threading.Timer(_ =>
            {
                var restarted = false;
                try
                {
                    lock (_sync)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        CancelRestartTimer();
                        DisposeWatcher();
                        restarted = TryStartWatcher(_getSyncOptions());
                    }

                    if (restarted)
                    {
                        _logger.LogInformation(LogText.Get("WatcherRestarted"), _restartAttempts);
                    }
                    else
                    {
                        ScheduleRestart();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LogText.Get("WatcherRestartFailed"));
                    ScheduleRestart();
                }
            }, null, nextDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            DisposeWatcher();
            CancelRestartTimer();
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("WatcherStopFailed"));
        }

        DetachHandlers(_watcher);
        _watcher.Dispose();
        _watcher = null;
    }

    private void CancelRestartTimer()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
    }

    private void DetachHandlers(FileSystemWatcher watcher)
    {
        if (_onCreatedHandler is not null) watcher.Created -= _onCreatedHandler;
        if (_onChangedHandler is not null) watcher.Changed -= _onChangedHandler;
        if (_onRenamedHandler is not null) watcher.Renamed -= _onRenamedHandler;
        if (_onErrorHandler is not null) watcher.Error -= _onErrorHandler;
        if (_onDeletedHandler is not null) watcher.Deleted -= _onDeletedHandler;

        _onCreatedHandler = null;
        _onChangedHandler = null;
        _onRenamedHandler = null;
        _onDeletedHandler = null;
        _onErrorHandler = null;
    }
}
