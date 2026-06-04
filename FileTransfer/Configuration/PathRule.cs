[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class PathRule
{
    private static readonly Regex NonMatchingRegex = new("(?!.*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private string _matchPattern = string.Empty;
    private int _regexTimeoutMs = 500;

    public string MatchPattern
    {
        get => _matchPattern;
        set
        {
            _matchPattern = value ?? string.Empty;
            RebuildRegex();
        }
    }

    public int RegexTimeoutMs
    {
        get => _regexTimeoutMs;
        set
        {
            _regexTimeoutMs = value > 0 ? value : 500;
            RebuildRegex();
        }
    }

    public string TargetTemplate { get; set; } = string.Empty;
    public bool Ignore { get; set; }
    public Regex RegexPattern { get; private set; } = NonMatchingRegex;
    public string? RegexError { get; private set; }

    public bool HasConfiguration => Ignore || !string.IsNullOrWhiteSpace(MatchPattern) || !string.IsNullOrWhiteSpace(TargetTemplate);

    public PathRule Clone() => new()
    {
        RegexTimeoutMs = RegexTimeoutMs,
        MatchPattern = MatchPattern,
        TargetTemplate = TargetTemplate,
        Ignore = Ignore
    };

    public bool TryValidate(ILogger logger, PathTemplateRenderer templateRenderer)
    {
        if (string.IsNullOrWhiteSpace(MatchPattern))
        {
            logger.LogError(LogText.Get("PathRuleMissingPattern"));
            return false;
        }

        if (RegexError is not null)
        {
            logger.LogError(LogText.Get("PathRuleInvalidPattern"), MatchPattern, RegexError);
            return false;
        }

        if (RegexTimeoutMs <= 0)
        {
            logger.LogError(LogText.Get("PathRuleInvalidTimeout"), MatchPattern);
            return false;
        }

        if (Ignore)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(TargetTemplate))
        {
            logger.LogError(LogText.Get("PathRuleMissingTemplate"), MatchPattern);
            return false;
        }

        if (!templateRenderer.TryValidate(this, out var templateError))
        {
            logger.LogError(LogText.Get("PathRuleInvalidTemplate"), TargetTemplate, templateError);
            return false;
        }

        return true;
    }

    private void RebuildRegex()
    {
        RegexError = null;

        if (string.IsNullOrWhiteSpace(_matchPattern))
        {
            RegexPattern = NonMatchingRegex;
            return;
        }

        try
        {
            RegexPattern = new Regex(
                _matchPattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(Math.Max(1, _regexTimeoutMs)));
        }
        catch (ArgumentException ex)
        {
            RegexPattern = NonMatchingRegex;
            RegexError = ex.Message;
        }
    }
}
