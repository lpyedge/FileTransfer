internal sealed class TargetHealthMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly TargetHealthRegistry _registry;
    private readonly Func<SyncOptions> _getSyncOptions;

    private System.Threading.Timer? _timer;
    private CancellationToken _stopToken;
    private int _running;

    public TargetHealthMonitor(ILogger logger, TargetHealthRegistry registry, Func<SyncOptions> getSyncOptions)
    {
        _logger = logger;
        _registry = registry;
        _getSyncOptions = getSyncOptions;
    }

    public void Configure(SyncOptions settings, CancellationToken stopToken)
    {
        _stopToken = stopToken;
        _timer?.Dispose();
        if (settings.TargetRoots is null || settings.TargetRoots.Length == 0)
        {
            _timer = null;
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, settings.HealthCheckIntervalMs));
        _timer = new System.Threading.Timer(_ => Probe(), null, TimeSpan.Zero, interval);
    }

    private void Probe()
    {
        if (_stopToken.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var settings = _getSyncOptions();
                if (settings.TargetRoots is null || settings.TargetRoots.Length == 0)
                {
                    return;
                }

                foreach (var targetRoot in settings.TargetRoots)
                {
                    var healthy = Evaluate(targetRoot, settings);
                    _registry.Update(targetRoot, healthy, healthy ? LogText.Get("HealthCheckSucceededReason") : LogText.Get("HealthCheckFailedReason"));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, LogText.Get("HealthCheckFailed"));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }, _stopToken);
    }

    private bool Evaluate(string targetRoot, SyncOptions settings)
    {
        var runtimePart = SanitizeFileName(settings.RuntimeId);
        var probePath = Path.Combine(
            targetRoot,
            $".filetransfer-health.{Environment.MachineName}.{Environment.ProcessId}.{runtimePart}.{Guid.NewGuid():N}.probe");

        try
        {
            if (!Directory.Exists(targetRoot))
            {
                return false;
            }

            File.WriteAllText(probePath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            TryMarkProbeHidden(probePath);
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, LogText.Get("HealthWriteProbeFailed"), targetRoot);
            TryDeleteProbe(probePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, LogText.Get("HealthUnexpectedError"), targetRoot);
            TryDeleteProbe(probePath);
            return false;
        }
    }

    private static void TryDeleteProbe(string probePath)
    {
        try
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
        catch
        {
        }
    }

    private static void TryMarkProbeHidden(string probePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var attributes = File.GetAttributes(probePath);
            if ((attributes & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(probePath, attributes | FileAttributes.Hidden);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SyncOptions.DefaultRuleId;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
