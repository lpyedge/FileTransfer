internal sealed class StartupInventoryReporter
{
    private const string TrashFolderName = ".trash";
    private const long MaxInventoryEntries = 10000;
    private static readonly char[] PathSeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    private readonly ILogger _logger;
    private bool _initialInventoryLogged;

    public StartupInventoryReporter(ILogger logger)
    {
        _logger = logger;
    }

    public void LogIfNeeded(SyncOptions settings)
    {
        if (_initialInventoryLogged)
        {
            return;
        }

        try
        {
            var watched = CaptureDirectorySummary(settings.SourceRoot);
            LogDirectorySummary(LogText.Get("WatchedFolderLabel"), settings.SourceRoot, watched);

            for (var i = 0; i < settings.TargetRoots.Length; i++)
            {
                var target = settings.TargetRoots[i];
                var summary = CaptureDirectorySummary(target, skipTrash: true);
                LogDirectorySummary(string.Format(CultureInfo.InvariantCulture, LogText.Get("DestinationFolderLabel"), i + 1), target, summary);
            }

            if (settings.TargetRoots.Length > 0)
            {
                var trashPath = Path.Combine(settings.TargetRoots[0], TrashFolderName);
                var trash = CaptureDirectorySummary(trashPath);
                LogDirectorySummary(LogText.Get("TrashFolderLabel"), trashPath, trash, isTrash: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, LogText.Get("InventoryFailed"));
        }
        finally
        {
            _initialInventoryLogged = true;
        }
    }

    private DirectorySummary CaptureDirectorySummary(string path, bool skipTrash = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new DirectorySummary(false, 0, 0, 0);
        }

        long directoryCount = 0;
        long fileCount = 0;
        long totalBytes = 0;

        var stack = new Stack<(string Path, bool IsRoot)>();
        stack.Push((path, true));

        while (stack.Count > 0 && directoryCount + fileCount < MaxInventoryEntries)
        {
            var (current, isRoot) = stack.Pop();
            if (!isRoot)
            {
                directoryCount++;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (directoryCount + fileCount >= MaxInventoryEntries)
                    {
                        break;
                    }

                    fileCount++;
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, LogText.Get("InventorySizeFailed"), file);
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    if (directoryCount + fileCount + stack.Count >= MaxInventoryEntries)
                    {
                        break;
                    }

                    if (skipTrash && IsTrashDescendant(path, dir))
                    {
                        continue;
                    }

                    stack.Push((dir, false));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, LogText.Get("InventoryEnumerationError"), current);
            }
        }

        return new DirectorySummary(true, directoryCount, fileCount, totalBytes);
    }

    private void LogDirectorySummary(string label, string path, DirectorySummary summary, bool isTrash = false)
    {
        if (!summary.Exists)
        {
            _logger.LogInformation(LogText.Get("InventoryMissing"), label, path);
            return;
        }

        var dirText = summary.DirectoryCount.ToString("N0", CultureInfo.InvariantCulture);
        var fileText = summary.FileCount.ToString("N0", CultureInfo.InvariantCulture);
        var sizeText = FormatByteSize(summary.TotalBytes);

        if (isTrash)
        {
            _logger.LogInformation(
                LogText.Get("InventorySummaryWithAdvice"),
                label, path, dirText, fileText, sizeText);
            return;
        }

        _logger.LogInformation(
            LogText.Get("InventorySummary"),
            label, path, dirText, fileText, sizeText);
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", bytes.ToString("N0", CultureInfo.InvariantCulture), LogText.Get("BytesUnit"));
        }

        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unitIndex = -1;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex < 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1}", bytes.ToString("N0", CultureInfo.InvariantCulture), LogText.Get("BytesUnit"))
            : string.Format(CultureInfo.InvariantCulture, "{0} {1} ({2} {3})", value.ToString("N2", CultureInfo.InvariantCulture), units[unitIndex], bytes.ToString("N0", CultureInfo.InvariantCulture), LogText.Get("BytesUnit"));
    }

    private static bool IsTrashDescendant(string root, string candidate)
    {
        try
        {
            var relative = Path.GetRelativePath(root, candidate);
            if (string.IsNullOrWhiteSpace(relative) || relative == ".")
            {
                return false;
            }

            var first = relative.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.Equals(first, TrashFolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct DirectorySummary(bool Exists, long DirectoryCount, long FileCount, long TotalBytes);
}
