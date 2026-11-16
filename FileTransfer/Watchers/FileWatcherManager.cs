internal sealed class FileWatcherManager : IDisposable
{
    /// <summary>
    /// ReadDirectoryChangesW rejects buffers larger than 64 KB on network shares (SMB limit).
    /// </summary>
    internal const int MaxNetworkInternalBufferSize = 65536;

    private readonly record struct WatcherEvent(FileChangeKind Kind, string Path, string? OldPath);

    private readonly ILogger _logger;
    private readonly Func<SyncOptions> _getSyncOptions;
    private readonly Action<FileChangeKind, string, string?> _onChange;
    private readonly Action<string> _onDelete;
    private readonly Action<string> _onEventsLost;
    private readonly object _sync = new();

    // Watcher callbacks only enqueue; all real work (path rules, metadata IO, scheduling)
    // runs on the dispatcher. FileSystemWatcher re-arms ReadDirectoryChangesW only after
    // the handlers return, so slow handlers directly cause kernel buffer overflows.
    private readonly Channel<WatcherEvent> _events = Channel.CreateUnbounded<WatcherEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _dispatcher;

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
        Action<FileChangeKind, string, string?> onChange,
        Action<string> onDelete,
        Action<string>? onEventsLost = null)
    {
        _logger = logger;
        _getSyncOptions = getSyncOptions;
        _onChange = onChange;
        _onDelete = onDelete;
        _onEventsLost = onEventsLost ?? (static _ => { });
        _dispatcher = Task.Run(DispatchAsync);
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

    internal static int GetEffectiveInternalBufferSize(string sourceRoot, int configured)
    {
        var size = configured > 0 ? configured : MaxNetworkInternalBufferSize;
        return IsNetworkPath(sourceRoot) ? Math.Min(size, MaxNetworkInternalBufferSize) : size;
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
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
            InternalBufferSize = GetEffectiveInternalBufferSize(settings.SourceRoot, settings.WatcherInternalBufferSize)
        };

        ConfigureFilters(watcher, FileProcessingRules.NormalizeFileExtensions(settings.FileExtensions));

        _onErrorHandler = OnWatcherError;
        watcher.Error += _onErrorHandler;

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Created, settings))
        {
            _onCreatedHandler = (_, e) => Post(FileChangeKind.Created, e.FullPath, null);
            watcher.Created += _onCreatedHandler;
        }

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Changed, settings))
        {
            _onChangedHandler = (_, e) => Post(FileChangeKind.Changed, e.FullPath, null);
            watcher.Changed += _onChangedHandler;
        }

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Renamed, settings))
        {
            _onRenamedHandler = (_, e) => Post(FileChangeKind.Renamed, e.FullPath, e.OldFullPath);
            watcher.Renamed += _onRenamedHandler;
        }

        if (FileProcessingRules.IsEventEnabled(FileChangeKind.Deleted, settings))
        {
            _onDeletedHandler = (_, e) => Post(FileChangeKind.Deleted, e.FullPath, null);
            watcher.Deleted += _onDeletedHandler;
        }

        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        _restartAttempts = 0;
        return true;
    }

    private void Post(FileChangeKind kind, string path, string? oldPath) =>
        _events.Writer.TryWrite(new WatcherEvent(kind, path, oldPath));

    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync(_disposeCts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (evt.Kind == FileChangeKind.Deleted)
                    {
                        _onDelete(evt.Path);
                    }
                    else
                    {
                        _onChange(evt.Kind, evt.Path, evt.OldPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, LogText.Get("WatcherEventDispatchFailed"), evt.Kind, evt.Path);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
        if (ex is InternalBufferOverflowException)
        {
            // The watcher keeps running after an overflow; only the notifications that did not
            // fit in the buffer were dropped. Restarting would add a blind window, so keep it
            // running and let reconciliation recover the lost changes right away.
            _logger.LogWarning(ex, LogText.Get("WatcherBufferOverflow"), _getSyncOptions().SourceRoot);
            NotifyEventsLost("WatcherBufferOverflow");
            return;
        }

        _logger.LogWarning(ex, LogText.Get("WatcherErrorRestart"));
        ScheduleRestart();
    }

    private void NotifyEventsLost(string reason)
    {
        try
        {
            _onEventsLost(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("WatcherEventDispatchFailed"), reason, _getSyncOptions().SourceRoot);
        }
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
                        // Anything that changed while the watcher was down was never reported.
                        NotifyEventsLost("WatcherRestarted");
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWatcher();
            CancelRestartTimer();
        }

        _events.Writer.TryComplete();
        _disposeCts.Cancel();
        try
        {
            _dispatcher.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _disposeCts.Dispose();
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
