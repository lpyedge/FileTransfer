internal sealed class PathTemplateRenderer
{
    private static readonly Regex LegacyNamedGroupRegex = new(@"\$\{(?<name>[A-Za-z0-9_]+)\}", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\{(?<name>[A-Za-z0-9_]+)(:(?<format>[^{}]+))?\}", RegexOptions.Compiled);
    private static readonly char[] PortableInvalidPathChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '|', '?', '*', '\0' })
        .Distinct()
        .ToArray();
    private static readonly string[] SupportedDateInputFormats =
    {
        "yyyyMMdd",
        "yyyyMMddHHmmss",
        "yyyyMMddHHmm",
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "O",
        "s"
    };

    private static readonly IReadOnlyDictionary<string, BuiltInPlaceholderDefinition> BuiltInPlaceholders =
        new Dictionary<string, BuiltInPlaceholderDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["guid"] = new("guid", TemplateValueType.Guid, SupportsFormat: true, DefaultFormat: "N"),
        ["now"] = new("now", TemplateValueType.DateTimeOffset, SupportsFormat: true, DefaultFormat: "yyyyMMddHHmmss"),
        ["utcNow"] = new("utcNow", TemplateValueType.DateTimeOffset, SupportsFormat: true, DefaultFormat: "yyyyMMddHHmmss"),
        ["fileName"] = new("fileName", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null),
        ["fileNameWithoutExtension"] = new("fileNameWithoutExtension", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null),
        ["extension"] = new("extension", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null),
        ["relativePath"] = new("relativePath", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null),
        ["relativeDirectory"] = new("relativeDirectory", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null),
        ["directoryName"] = new("directoryName", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null),
        ["machineName"] = new("machineName", TemplateValueType.String, SupportsFormat: false, DefaultFormat: null)
    };

    public bool TryRender(string template, string sourcePath, string relativePath, Match match, out string rendered, out string? error)
    {
        rendered = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            return true;
        }

        var normalizedTemplate = NormalizeTemplate(template);
        var context = TemplateRenderContext.Create(sourcePath, relativePath, match);
        var failed = false;
        string? renderError = null;

        rendered = PlaceholderRegex.Replace(normalizedTemplate, placeholder =>
        {
            var name = placeholder.Groups["name"].Value;
            var format = placeholder.Groups["format"].Success ? placeholder.Groups["format"].Value : null;

            if (!context.TryResolve(name, format, out var replacement, out var placeholderError))
            {
                failed = true;
                renderError ??= placeholderError ?? $"Unknown template placeholder '{name}'.";
                return placeholder.Value;
            }

            return replacement;
        });

        error = renderError;
        return !failed;
    }

    public bool TryValidate(PathRule mapping, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(mapping.TargetTemplate))
        {
            return true;
        }

        var normalizedTemplate = NormalizeTemplate(mapping.TargetTemplate);
        var groupNames = new HashSet<string>(
            mapping.RegexPattern
                .GetGroupNames()
                .Where(name => !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _)),
            StringComparer.OrdinalIgnoreCase);

        foreach (Match placeholder in PlaceholderRegex.Matches(normalizedTemplate))
        {
            var name = placeholder.Groups["name"].Value;
            var format = placeholder.Groups["format"].Success ? placeholder.Groups["format"].Value : null;

            if (TryValidatePlaceholder(groupNames, name, format, out error))
            {
                continue;
            }

            return false;
        }

        var residue = PlaceholderRegex.Replace(normalizedTemplate, string.Empty);
        if (residue.Contains('{', StringComparison.Ordinal) || residue.Contains('}', StringComparison.Ordinal))
        {
            error = "Template contains unmatched braces.";
            return false;
        }

        if (!IsSafeTemplateFragment(residue))
        {
            error = "Template contains characters that are invalid in Windows paths.";
            return false;
        }

        return true;
    }

    private static string NormalizeTemplate(string template)
    {
        return LegacyNamedGroupRegex.Replace(template, match => "{" + match.Groups["name"].Value + "}");
    }

    private static bool TryValidatePlaceholder(HashSet<string> groupNames, string name, string? format, out string? error)
    {
        error = null;

        if (groupNames.Contains(name))
        {
            return TryValidateNamedGroupFormat(name, format, out error);
        }

        if (BuiltInPlaceholders.TryGetValue(name, out var placeholder))
        {
            return TryValidateBuiltInFormat(placeholder, format, out error);
        }

        error = $"Unknown template placeholder '{name}'.";
        return false;
    }

    private static bool TryValidateNamedGroupFormat(string name, string? format, out string? error)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            error = null;
            return true;
        }

        return TryValidateDateTimeFormat(name, format, out error);
    }

    private static bool TryValidateBuiltInFormat(BuiltInPlaceholderDefinition placeholder, string? format, out string? error)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            error = null;
            return true;
        }

        if (!placeholder.SupportsFormat)
        {
            error = $"Template placeholder '{placeholder.Name}' does not support format strings.";
            return false;
        }

        return placeholder.Type switch
        {
            TemplateValueType.Guid => TryValidateGuidFormat(placeholder.Name, format, out error),
            TemplateValueType.DateTimeOffset => TryValidateDateTimeFormat(placeholder.Name, format, out error),
            _ => throw new InvalidOperationException($"Unsupported template value type '{placeholder.Type}'.")
        };
    }

    private static bool TryValidateGuidFormat(string name, string format, out string? error)
    {
        try
        {
            Guid.Empty.ToString(format, CultureInfo.InvariantCulture);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            error = $"Template placeholder '{name}' only supports GUID format strings: N, D, B, P, X.";
            return false;
        }
    }

    private static bool TryValidateDateTimeFormat(string name, string format, out string? error)
    {
        try
        {
            var sample = DateTimeOffset.UnixEpoch.ToString(format, CultureInfo.InvariantCulture);
            if (!IsSafeTemplateFragment(sample))
            {
                error = $"Template placeholder '{name}' format '{format}' produces characters that are invalid in Windows paths.";
                return false;
            }

            error = null;
            return true;
        }
        catch (FormatException)
        {
            error = $"Template placeholder '{name}' has an invalid date/time format string '{format}'.";
            return false;
        }
    }

    private sealed class TemplateRenderContext
    {
        private readonly Dictionary<string, string> _captureValues;
        private readonly Dictionary<string, string> _builtInStringValues;
        private readonly Guid _guid;
        private readonly DateTimeOffset _now;
        private readonly DateTimeOffset _utcNow;

        private TemplateRenderContext(Dictionary<string, string> captureValues, Dictionary<string, string> builtInStringValues)
        {
            _captureValues = captureValues;
            _builtInStringValues = builtInStringValues;
            _guid = Guid.NewGuid();
            _now = DateTimeOffset.Now;
            _utcNow = DateTimeOffset.UtcNow;
        }

        public static TemplateRenderContext Create(string sourcePath, string relativePath, Match match)
        {
            var builtInStringValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fileName"] = Path.GetFileName(sourcePath),
                ["fileNameWithoutExtension"] = Path.GetFileNameWithoutExtension(sourcePath),
                ["extension"] = Path.GetExtension(sourcePath),
                ["relativePath"] = NormalizePath(relativePath),
                ["relativeDirectory"] = NormalizePath(Path.GetDirectoryName(relativePath) ?? string.Empty),
                ["directoryName"] = Path.GetFileName(Path.GetDirectoryName(sourcePath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty),
                ["machineName"] = Environment.MachineName
            };

            var captureValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var groupName in match.Groups.Keys)
            {
                if (int.TryParse(groupName, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var group = match.Groups[groupName];
                if (group.Success)
                {
                    captureValues[groupName] = group.Value;
                }
            }

            return new TemplateRenderContext(captureValues, builtInStringValues);
        }

        public bool TryResolve(string name, string? format, out string value, out string? error)
        {
            error = null;

            if (_captureValues.TryGetValue(name, out var captureValue))
            {
                return TryResolveCapturedValue(name, captureValue, format, out value, out error);
            }

            if (!BuiltInPlaceholders.TryGetValue(name, out var placeholder))
            {
                value = string.Empty;
                error = $"Unknown template placeholder '{name}'.";
                return false;
            }

            return TryResolveBuiltIn(placeholder, format, out value, out error);
        }

        private bool TryResolveCapturedValue(string name, string value, string? format, out string rendered, out string? error)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                rendered = value;
                error = null;
                return true;
            }

            if (!TryValidateDateTimeFormat(name, format, out error))
            {
                rendered = string.Empty;
                return false;
            }

            if (TryFormatDateLikeValue(value, format, out rendered))
            {
                error = null;
                return true;
            }

            rendered = string.Empty;
            error = $"Template placeholder '{name}' can only use a date/time format string when the captured value is parseable as date/time.";
            return false;
        }

        private bool TryResolveBuiltIn(BuiltInPlaceholderDefinition placeholder, string? format, out string value, out string? error)
        {
            if (!TryValidateBuiltInFormat(placeholder, format, out error))
            {
                value = string.Empty;
                return false;
            }

            error = null;

            switch (placeholder.Type)
            {
                case TemplateValueType.Guid:
                    value = _guid.ToString(string.IsNullOrWhiteSpace(format) ? placeholder.DefaultFormat : format, CultureInfo.InvariantCulture);
                    return true;
                case TemplateValueType.DateTimeOffset:
                    value = ApplyDateFormat(
                        string.Equals(placeholder.Name, "utcNow", StringComparison.OrdinalIgnoreCase) ? _utcNow : _now,
                        string.IsNullOrWhiteSpace(format) ? placeholder.DefaultFormat : format);
                    return true;
                case TemplateValueType.String:
                    value = _builtInStringValues[placeholder.Name];
                    return true;
                default:
                    throw new InvalidOperationException($"Unsupported template value type '{placeholder.Type}'.");
            }
        }

        private static bool TryFormatDateLikeValue(string value, string format, out string formatted)
        {
            formatted = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (DateTimeOffset.TryParseExact(value, SupportedDateInputFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            {
                formatted = exact.ToString(format, CultureInfo.InvariantCulture);
                return true;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                formatted = parsed.ToString(format, CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        private static string ApplyDateFormat(DateTimeOffset value, string? format)
        {
            return string.IsNullOrWhiteSpace(format)
                ? value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                : value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string NormalizePath(string value)
        {
            return value.Replace('\\', '/').Replace('/', '/');
        }
    }

    private static bool IsSafeTemplateFragment(string value)
    {
        foreach (var part in value.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.IndexOfAny(PortableInvalidPathChars) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private enum TemplateValueType
    {
        String,
        Guid,
        DateTimeOffset
    }

    private sealed record BuiltInPlaceholderDefinition(string Name, TemplateValueType Type, bool SupportsFormat, string? DefaultFormat);
}
