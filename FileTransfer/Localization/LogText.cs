using System.Reflection;
using System.Resources;

internal static class LogText
{
    private static readonly ResourceManager ResourceManager = new("FileTransfer.Localization.LogMessages", Assembly.GetExecutingAssembly());

    public static string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return ResourceManager.GetString(key, CultureInfo.CurrentUICulture)
            ?? ResourceManager.GetString(key, CultureInfo.InvariantCulture)
            ?? key;
    }
}

internal static class RuntimeLanguage
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "en",
        "ja",
        "zh-Hant"
    };

    public static string Apply(string? configuredLanguage)
    {
        var requested = string.IsNullOrWhiteSpace(configuredLanguage) ? "auto" : configuredLanguage.Trim();
        if (string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
        {
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
            return Normalize(CultureInfo.CurrentUICulture.Name);
        }

        var normalized = Normalize(requested);
        if (!Supported.Contains(normalized))
        {
            normalized = "en";
        }

        var culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        return normalized;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        if (value.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }

        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hant";
        }

        return "en";
    }
}
