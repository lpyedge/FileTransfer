internal static class PathKeyComparer
{
    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static bool Equals(string? left, string? right) =>
        string.Equals(left, right, Comparison);

    public static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, Comparison);
}
