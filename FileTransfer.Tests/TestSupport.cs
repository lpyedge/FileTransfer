namespace FileTransfer.Tests;

internal sealed class TempRoot : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    public TempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "FileTransfer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public string CreateDir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetPath(string relative)
    {
        return Path.Combine(_root, relative);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed class ListLogger : ILogger
{
    private static readonly IDisposable Scope = new NoopDisposable();
    private readonly object _sync = new();
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return Scope;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    internal readonly record struct LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal static class AsyncAssert
{
    public static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }
}

internal sealed class TestCultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;
    private readonly CultureInfo? _previousDefaultCulture;
    private readonly CultureInfo? _previousDefaultUiCulture;

    private TestCultureScope(string cultureName)
    {
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static TestCultureScope Use(string cultureName) => new(cultureName);

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _previousDefaultUiCulture;
    }
}
