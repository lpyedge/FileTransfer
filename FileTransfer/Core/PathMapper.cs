internal sealed class PathMapper
{
    private readonly SyncOptions _settings;
    private readonly ILogger _logger;
    private readonly PathTemplateRenderer _templateRenderer;

    public PathMapper(SyncOptions settings, ILogger logger, PathTemplateRenderer templateRenderer)
    {
        _settings = settings;
        _logger = logger;
        _templateRenderer = templateRenderer;
    }

    public string SourceRoot => _settings.SourceRoot;
    public IReadOnlyList<string> TargetRoots => _settings.TargetRoots;
    public bool HasDynamicTemplates => _settings.PathRules.Any(rule => !rule.Ignore && IsDynamicTemplate(rule.TargetTemplate));

    public PathResolution ResolveTargetPathForRoot(string sourcePath, string targetRoot)
    {
        var relative = ResolveRelativePath(sourcePath);
        return relative.Ignored
            ? PathResolution.Ignore()
            : PathResolution.Include(relative.RelativePath, PathHelper.BuildTargetPath(relative.RelativePath, targetRoot, _settings.FileNamePrefix));
    }

    public bool IsExcluded(string sourcePath) => ResolveRelativePath(sourcePath).Ignored;

    public string GetTrashPath(string destinationPath)
    {
        var root = PathHelper.GetContainingRoot(destinationPath, _settings.TargetRoots) ?? _settings.TargetRoots[0];
        var relative = Path.GetRelativePath(root, destinationPath);
        return Path.Combine(root, ".trash", relative);
    }

    private PathResolution ResolveRelativePath(string sourcePath)
    {
        var relativePath = Path.GetRelativePath(_settings.SourceRoot, sourcePath);
        var normalizedRelative = PathHelper.NormalizeForMatch(relativePath);

        foreach (var mapping in _settings.PathRules)
        {
            if (!TryMatch(mapping, normalizedRelative, relativePath, out var match))
            {
                continue;
            }

            if (mapping.Ignore)
            {
                _logger.LogDebug(LogText.Get("MappingIgnored"), sourcePath);
                return PathResolution.Ignore();
            }

            if (!_templateRenderer.TryRender(mapping.TargetTemplate, sourcePath, relativePath, match, out var rendered, out var templateError))
            {
                _logger.LogWarning(LogText.Get("MappingTemplateFailed"), mapping.TargetTemplate, templateError);
                continue;
            }

            var candidate = PathHelper.NormalizeForPath(rendered);
            if (!PathHelper.IsSafeRelativePath(candidate))
            {
                _logger.LogWarning(LogText.Get("MappingUnsafe"), mapping.TargetTemplate, candidate);
                continue;
            }

            return PathResolution.Include(candidate);
        }

        var fallback = PathHelper.NormalizeForPath(relativePath);
        if (!PathHelper.IsSafeRelativePath(fallback))
        {
            _logger.LogWarning(LogText.Get("DefaultRelativeUnsafe"), sourcePath, relativePath);
            return PathResolution.Ignore();
        }

        return PathResolution.Include(fallback);
    }

    private static bool IsDynamicTemplate(string template) =>
        template.Contains("{now", StringComparison.OrdinalIgnoreCase) ||
        template.Contains("{utcNow", StringComparison.OrdinalIgnoreCase) ||
        template.Contains("{guid", StringComparison.OrdinalIgnoreCase);

    private bool TryMatch(PathRule mapping, string normalizedRelative, string rawRelative, out Match match)
    {
        match = Match.Empty;

        if (mapping.RegexError is not null)
        {
            _logger.LogWarning(LogText.Get("MappingInvalidPattern"), mapping.MatchPattern, mapping.RegexError);
            return false;
        }

        try
        {
            match = mapping.RegexPattern.Match(normalizedRelative);
            if (!match.Success && !string.Equals(normalizedRelative, rawRelative, StringComparison.Ordinal))
            {
                match = mapping.RegexPattern.Match(rawRelative);
            }

            return match.Success;
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, LogText.Get("PathRuleRegexTimeout"), mapping.MatchPattern);
            match = Match.Empty;
            return false;
        }
    }
}

internal readonly record struct PathResolution(bool Ignored, string RelativePath, string TargetPath)
{
    public static PathResolution Include(string relativePath, string targetPath = "") => new(false, relativePath, targetPath);
    public static PathResolution Ignore() => new(true, string.Empty, string.Empty);
}
