internal static class SyncOptionsSetValidator
{
    public static bool TryPrepareAll(
        IEnumerable<SyncOptions> candidates,
        ILogger logger,
        PathTemplateRenderer templateRenderer,
        out IReadOnlyList<SyncOptions> preparedRules)
    {
        var prepared = new List<SyncOptions>();
        var allRulesPrepared = true;
        foreach (var candidate in candidates)
        {
            if (!candidate.TryPrepare(logger, templateRenderer, out var rule))
            {
                allRulesPrepared = false;
                continue;
            }

            prepared.Add(rule);
        }

        if (!allRulesPrepared)
        {
            preparedRules = Array.Empty<SyncOptions>();
            return false;
        }

        if (prepared.Count == 0)
        {
            logger.LogError(LogText.Get("NoRules"));
            preparedRules = Array.Empty<SyncOptions>();
            return false;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in prepared)
        {
            if (!keys.Add(rule.RuntimeKey))
            {
                logger.LogError(LogText.Get("DuplicateRuntimeId"), rule.RuntimeKey);
                preparedRules = Array.Empty<SyncOptions>();
                return false;
            }
        }

        foreach (var sourceRule in prepared)
        {
            foreach (var targetRule in prepared)
            {
                if (!ReferenceEquals(sourceRule, targetRule) && PathHelper.PathsOverlap(sourceRule.SourceRoot, targetRule.TargetRoots))
                {
                    logger.LogError("Cross-rule source/target overlap is not allowed: {SourceRule} -> {TargetRule}", sourceRule.RuleId, targetRule.RuleId);
                    preparedRules = Array.Empty<SyncOptions>();
                    return false;
                }
            }
        }

        preparedRules = prepared;
        return true;
    }
}
