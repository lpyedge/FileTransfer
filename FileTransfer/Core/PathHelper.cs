internal static class PathHelper
{
    private static readonly char[] InvalidPathSegmentChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '|', '?', '*', '\0' })
        .Distinct()
        .ToArray();

    public static bool TryGetFullPath(string path, out string fullPath, out Exception? error)
    {
        fullPath = string.Empty;
        error = null;

        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            error = ex;
            return false;
        }
    }

    public static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : "." + trimmed;
    }

    public static string NormalizeForMatch(string path) =>
        path.Replace('\\', '/').Replace('/', '/');

    public static string NormalizeForPath(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

    public static string[] SplitPath(string value) =>
        value.Split(new[] { '/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

    public static string CombinePath(string[] parts) =>
        parts.Length == 0 ? string.Empty : Path.Combine(parts);

    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var parts = SplitPath(relativePath);
        return parts.Length > 0 && parts.All(part =>
            !string.Equals(part, ".", StringComparison.Ordinal) &&
            !string.Equals(part, "..", StringComparison.Ordinal) &&
            part.IndexOfAny(InvalidPathSegmentChars) < 0);
    }

    public static string BuildTargetPath(string relativePath, string targetRoot, string prefix)
    {
        var parts = SplitPath(relativePath);
        if (!string.IsNullOrWhiteSpace(prefix) && parts.Length > 0)
        {
            var lastIndex = parts.Length - 1;
            var fileName = parts[lastIndex];
            if (!string.IsNullOrWhiteSpace(fileName) &&
                !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                parts[lastIndex] = prefix + fileName;
            }
        }

        return Path.Combine(targetRoot, CombinePath(parts));
    }

    public static bool PathsOverlap(string left, IEnumerable<string> rightList)
    {
        foreach (var right in rightList)
        {
            if (PathEquals(left, right) ||
                IsSubPath(left, right) ||
                IsSubPath(right, left))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSubPath(string parent, string candidate)
    {
        var parentRoot = Path.GetPathRoot(parent);
        var candidateRoot = Path.GetPathRoot(candidate);
        if (string.IsNullOrEmpty(parentRoot) || string.IsNullOrEmpty(candidateRoot))
        {
            return false;
        }

        if (!PathEquals(parentRoot, candidateRoot))
        {
            return false;
        }

        var relative = Path.GetRelativePath(parent, candidate);
        return IsChildRelativePath(relative);
    }

    public static string BuildStagingPath(string destinationPath)
    {
        return $"{destinationPath}.{Guid.NewGuid():N}.tmp";
    }

    public static string GetUniqueFilePath(string initialPath)
    {
        if (!File.Exists(initialPath))
        {
            return initialPath;
        }

        var directory = Path.GetDirectoryName(initialPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(initialPath);
        var extension = Path.GetExtension(initialPath);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{fileName}_{i:000}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{fileName}_{Guid.NewGuid():N}{extension}");
    }

    public static string? GetContainingRoot(string path, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                var relative = Path.GetRelativePath(root, path);
                if (PathEquals(relative, ".") || IsChildRelativePath(relative))
                {
                    return root;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return null;
    }
    private static bool IsChildRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || PathEquals(relativePath, "."))
        {
            return false;
        }

        return !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relativePath.StartsWith("../", StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

}
