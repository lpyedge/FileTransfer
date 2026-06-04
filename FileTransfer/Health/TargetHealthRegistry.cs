internal sealed class TargetHealthRegistry
{
    private readonly ConcurrentDictionary<string, bool> _states = new(PathKeyComparer.Comparer);
    private readonly ILogger _logger;

    public TargetHealthRegistry(ILogger logger)
    {
        _logger = logger;
    }

    public event Action<string, bool>? StateChanged;

    public void Initialize(IEnumerable<string> targets)
    {
        _states.Clear();
        SyncTargets(targets);
    }

    public void SyncTargets(IEnumerable<string> targets)
    {
        var desired = new HashSet<string>(
            targets.Where(target => !string.IsNullOrWhiteSpace(target)),
            PathKeyComparer.Comparer);

        foreach (var existing in _states.Keys.ToArray())
        {
            if (desired.Contains(existing))
            {
                continue;
            }

            if (_states.TryRemove(existing, out _))
            {
                StateChanged?.Invoke(existing, false);
            }
        }

        foreach (var target in desired)
        {
            if (_states.TryAdd(target, true))
            {
                StateChanged?.Invoke(target, true);
            }
        }
    }

    public bool IsHealthy(string target)
    {
        return !_states.TryGetValue(target, out var healthy) || healthy;
    }

    public void Update(string target, bool healthy, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var hadPrevious = _states.TryGetValue(target, out var previous);
        _states[target] = healthy;
        StateChanged?.Invoke(target, healthy);

        if (!hadPrevious || previous == healthy)
        {
            return;
        }

        var stateText = healthy ? LogText.Get("HealthStateHealthy") : LogText.Get("HealthStateUnhealthy");
        if (healthy)
        {
            _logger.LogWarning(LogText.Get("TargetRecovered"), target, stateText, reason ?? string.Empty);
        }
        else
        {
            _logger.LogWarning(LogText.Get("TargetStateChanged"), target, stateText, reason ?? string.Empty);
        }
    }

    public bool AnyHealthyTarget(IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            if (IsHealthy(target))
            {
                return true;
            }
        }

        return false;
    }

    public string? GetPreferredTarget(IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            if (IsHealthy(target))
            {
                return target;
            }
        }

        return null;
    }
}
