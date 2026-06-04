internal sealed class InFlightOperationTracker
{
    private readonly ConcurrentDictionary<string, byte> _registry = new(PathKeyComparer.Comparer);

    public bool TryBegin(string key)
    {
        return _registry.TryAdd(key, 0);
    }

    public void Complete(string key)
    {
        _registry.TryRemove(key, out _);
    }
}
