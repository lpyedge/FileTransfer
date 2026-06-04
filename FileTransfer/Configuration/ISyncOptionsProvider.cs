internal interface ISyncOptionsProvider
{
    IReadOnlyList<SyncOptions> Current { get; }
    event Action<IReadOnlyList<SyncOptions>>? OptionsChanged;
}

internal sealed class ConfigurationSyncOptionsProvider : ISyncOptionsProvider, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationSyncOptionsProvider> _logger;
    private readonly object _sync = new();
    private IReadOnlyList<SyncOptions> _current;
    private readonly IDisposable _changeRegistration;
    private long _revision;

    public ConfigurationSyncOptionsProvider(IConfiguration configuration, ILogger<ConfigurationSyncOptionsProvider>? logger = null)
    {
        _configuration = configuration;
        _logger = logger ?? NullLogger<ConfigurationSyncOptionsProvider>.Instance;
        _current = Clone(SyncOptions.LoadFromConfiguration(configuration));
        _changeRegistration = ChangeToken.OnChange(
            () => _configuration.GetReloadToken(),
            Reload);
    }

    public IReadOnlyList<SyncOptions> Current
    {
        get
        {
            lock (_sync)
            {
                return Clone(_current);
            }
        }
    }

    public event Action<IReadOnlyList<SyncOptions>>? OptionsChanged;

    private void Reload()
    {
        var revision = Interlocked.Increment(ref _revision);
        _logger.LogInformation(LogText.Get("ConfigReloadStarted"), revision);

        IReadOnlyList<SyncOptions> updated;
        try
        {
            updated = Clone(SyncOptions.LoadFromConfiguration(_configuration));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LogText.Get("ConfigReloadParseFailed"), revision);
            return;
        }

        lock (_sync)
        {
            _current = updated;
        }

        _logger.LogInformation(LogText.Get("ConfigReloadParsed"), revision, updated.Count);
        OptionsChanged?.Invoke(Clone(updated));
    }

    public void Dispose()
    {
        _changeRegistration.Dispose();
    }

    private static IReadOnlyList<SyncOptions> Clone(IEnumerable<SyncOptions> settings) =>
        settings.Select(setting => setting.Clone()).ToArray();
}
