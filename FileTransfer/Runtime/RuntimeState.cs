internal sealed record RuntimeState(SyncOptions Options, PathMapper PathMapper, IReadOnlyCollection<string> FileExtensions)
{
    public static RuntimeState Empty { get; } = Create(new SyncOptions(), NullLogger.Instance, new PathTemplateRenderer());

    public static RuntimeState Create(SyncOptions settings, ILogger logger, PathTemplateRenderer templateRenderer) =>
        new(settings, new PathMapper(settings, logger, templateRenderer), FileProcessingRules.NormalizeFileExtensions(settings.FileExtensions));

    public bool PathResolutionChanged(RuntimeState next)
    {
        var previous = Options;
        var current = next.Options;

        if (!PathKeyComparer.Equals(previous.SourceRoot, current.SourceRoot) ||
            !string.Equals(previous.FileNamePrefix, current.FileNamePrefix, StringComparison.Ordinal) ||
            !previous.TargetRoots.SequenceEqual(current.TargetRoots, PathKeyComparer.Comparer) ||
            previous.PathRules.Count != current.PathRules.Count)
        {
            return true;
        }

        for (var i = 0; i < previous.PathRules.Count; i++)
        {
            var left = previous.PathRules[i];
            var right = current.PathRules[i];

            if (left.Ignore != right.Ignore ||
                !string.Equals(left.MatchPattern, right.MatchPattern, StringComparison.Ordinal) ||
                !string.Equals(left.TargetTemplate, right.TargetTemplate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
