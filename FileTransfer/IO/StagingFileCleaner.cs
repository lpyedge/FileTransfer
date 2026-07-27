internal sealed class StagingFileCleaner
{
    private static readonly Regex OwnedStaging = new(@"^.+\.[0-9a-fA-F]{32}\.tmp$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ILogger _logger;

    public StagingFileCleaner(ILogger logger) => _logger = logger;

    public void Clean(IEnumerable<string> targetRoots, int operationTimeoutMs)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-Math.Max(operationTimeoutMs * 2L, 3_600_000L));
        foreach (var root in targetRoots.Distinct(PathKeyComparer.Comparer))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var info = new FileInfo(path);
                    if (OwnedStaging.IsMatch(info.Name) && info.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(path);
                    }
                }

                var legacyPrefix = ".filetransfer-" + "health.";
                var legacySuffix = ".pro" + "be";
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(path);
                    if (info.Name.StartsWith(legacyPrefix, PathKeyComparer.Comparison) &&
                        info.Name.EndsWith(legacySuffix, PathKeyComparer.Comparison) &&
                        info.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-10))
                    {
                        File.Delete(path);
                    }
                }

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not clean stale staging files under {TargetRoot}", root);
            }
        }
    }
}
