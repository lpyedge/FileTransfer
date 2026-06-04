internal static class FileProcessingRules
{
    public static IReadOnlyCollection<string> NormalizeFileExtensions(IEnumerable<string>? extensions) =>
        new HashSet<string>(
            (extensions ?? Array.Empty<string>())
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Select(PathHelper.NormalizeExtension),
            StringComparer.OrdinalIgnoreCase);

    public static bool ShouldProcess(string path, IReadOnlyCollection<string> normalizedFileExtensions, ILogger logger)
    {
        if (Directory.Exists(path))
        {
            logger.LogDebug(LogText.Get("DirectoryChangeIgnored"), path);
            return false;
        }

        if (normalizedFileExtensions.Count == 0)
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(extension) && normalizedFileExtensions.Contains(extension))
        {
            return true;
        }

        logger.LogDebug(LogText.Get("ExtensionSkipped"), path);
        return false;
    }

    public static bool IsEventEnabled(FileChangeKind watcherEvent, SyncOptions settings) => watcherEvent switch
    {
        FileChangeKind.Created => settings.WatchEvents.Created,
        FileChangeKind.Changed => settings.WatchEvents.Changed,
        FileChangeKind.Renamed => settings.WatchEvents.Created || settings.WatchEvents.Changed,
        FileChangeKind.Deleted => settings.WatchEvents.Deleted && !settings.DeleteSourceAfterCopy,
        _ => true
    };

    public static string EventLabel(FileChangeKind watcherEvent) => watcherEvent switch
    {
        FileChangeKind.Created => LogText.Get("FileChangeCreated"),
        FileChangeKind.Changed => LogText.Get("FileChangeChanged"),
        FileChangeKind.Renamed => LogText.Get("FileChangeRenamed"),
        FileChangeKind.Deleted => LogText.Get("FileChangeDeleted"),
        _ => LogText.Get("FileChangeUnknown")
    };
}
