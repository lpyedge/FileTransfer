internal readonly record struct TargetAttemptState(
    int ConsecutiveFailures,
    DateTimeOffset NextAttemptUtc,
    string? LastReason);

internal sealed class TargetHealthRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TargetAttemptState> _states = new(PathKeyComparer.Comparer);
    private readonly HashSet<string> _configuredTargets = new(PathKeyComparer.Comparer);
    private readonly ILogger _logger;
    private bool _hasSynchronizedTargets;

    public TargetHealthRegistry(ILogger logger)
    {
        _logger = logger;
    }

    public void Initialize(IEnumerable<string> targets)
    {
        lock (_sync)
        {
            _states.Clear();
            _configuredTargets.Clear();
            _hasSynchronizedTargets = false;
        }

        SyncTargets(targets);
    }

    public void SyncTargets(IEnumerable<string> targets)
    {
        var desired = new HashSet<string>(
            targets.Where(target => !string.IsNullOrWhiteSpace(target)),
            PathKeyComparer.Comparer);

        lock (_sync)
        {
            _hasSynchronizedTargets = true;
            _configuredTargets.Clear();
            _configuredTargets.UnionWith(desired);

            foreach (var existing in _states.Keys.ToArray())
            {
                if (!desired.Contains(existing))
                {
                    _states.Remove(existing);
                }
            }

            foreach (var target in desired)
            {
                _states.TryAdd(target, default);
            }
        }
    }

    public bool CanAttempt(string target, DateTimeOffset now)
    {
        lock (_sync)
        {
            return !_states.TryGetValue(target, out var state) || now >= state.NextAttemptUtc;
        }
    }

    public DateTimeOffset? GetNextAttemptUtc(IEnumerable<string> targets, DateTimeOffset now)
    {
        lock (_sync)
        {
            DateTimeOffset? earliest = null;
            foreach (var target in targets)
            {
                if (!_states.TryGetValue(target, out var state) || now >= state.NextAttemptUtc)
                {
                    continue;
                }

                if (earliest is null || state.NextAttemptUtc < earliest)
                {
                    earliest = state.NextAttemptUtc;
                }
            }

            return earliest;
        }
    }

    public void RecordSuccess(string target, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var recovered = false;
        lock (_sync)
        {
            if (_hasSynchronizedTargets && !_configuredTargets.Contains(target))
            {
                return;
            }

            var hadPrevious = _states.TryGetValue(target, out var previous);
            _states[target] = default;
            recovered = hadPrevious && previous.ConsecutiveFailures > 0;
        }

        if (recovered)
        {
            _logger.LogInformation(LogText.Get("TargetRecovered"), target, LogText.Get("HealthStateHealthy"), reason ?? string.Empty);
        }
    }

    public void RecordFailure(
        string target,
        DateTimeOffset now,
        int initialDelayMs,
        int maxDelayMs,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var enteringFailure = false;
        lock (_sync)
        {
            // Once configuration has supplied the target set, a result that
            // arrives after a target was removed is stale and must not revive it.
            if (_hasSynchronizedTargets && !_configuredTargets.Contains(target))
            {
                return;
            }

            var previous = _states.TryGetValue(target, out var existing) ? existing : default;
            var failures = Math.Min(previous.ConsecutiveFailures + 1, 21);
            var minimum = Math.Max(1, initialDelayMs);
            var maximum = Math.Max(minimum, maxDelayMs);
            var multiplier = 1L << Math.Min(failures - 1, 20);
            var delay = Math.Min((long)maximum, minimum * multiplier);
            _states[target] = new TargetAttemptState(failures, now.AddMilliseconds(delay), reason);
            enteringFailure = previous.ConsecutiveFailures == 0;
        }

        if (enteringFailure)
        {
            _logger.LogWarning(LogText.Get("TargetStateChanged"), target, LogText.Get("HealthStateUnhealthy"), reason ?? string.Empty);
        }
    }

    internal bool TryGetState(string target, out TargetAttemptState state)
    {
        lock (_sync)
        {
            return _states.TryGetValue(target, out state);
        }
    }
}
