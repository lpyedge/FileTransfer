namespace FileTransfer.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("en", "Service started successfully.")]
    [InlineData("ja", "サービスが正常に起動しました。")]
    [InlineData("zh-Hant", "服務已成功啟動。")]
    [InlineData("zh-TW", "服務已成功啟動。")]
    public void RuntimeLanguage_UsesConfiguredLanguage(string configuredLanguage, string expectedMessage)
    {
        using var culture = TestCultureScope.Use("en-US");

        var selected = RuntimeLanguage.Apply(configuredLanguage);

        Assert.Equal(configuredLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : configuredLanguage, selected);
        Assert.Equal(expectedMessage, LogText.Get("ServiceStarted"));
    }

    [Fact]
    public void RuntimeLanguage_AutoFollowsCurrentUiCulture()
    {
        using var culture = TestCultureScope.Use("ja-JP");

        var selected = RuntimeLanguage.Apply("auto");

        Assert.Equal("ja", selected);
        Assert.Equal("サービスが正常に起動しました。", LogText.Get("ServiceStarted"));
    }

    [Fact]
    public void RuntimeLanguage_UnsupportedLanguageFallsBackToEnglish()
    {
        using var culture = TestCultureScope.Use("ja-JP");

        var selected = RuntimeLanguage.Apply("fr-FR");

        Assert.Equal("en", selected);
        Assert.Equal("Service started successfully.", LogText.Get("ServiceStarted"));
    }

    [Fact]
    public void LogText_MissingKeyFallsBackToKeyName()
    {
        using var culture = TestCultureScope.Use("en-US");
        RuntimeLanguage.Apply("en");

        Assert.Equal("ThisKeyDoesNotExist", LogText.Get("ThisKeyDoesNotExist"));
    }

    [Fact]
    public void LogResources_HaveSameKeysAndStructuredPlaceholdersAcrossLanguages()
    {
        var english = ReadResources("LogMessages.resx");
        var japanese = ReadResources("LogMessages.ja.resx");
        var traditionalChinese = ReadResources("LogMessages.zh-Hant.resx");

        Assert.Equal(english.Keys.OrderBy(key => key), japanese.Keys.OrderBy(key => key));
        Assert.Equal(english.Keys.OrderBy(key => key), traditionalChinese.Keys.OrderBy(key => key));

        foreach (var key in english.Keys)
        {
            Assert.Equal(ExtractPlaceholders(english[key]), ExtractPlaceholders(japanese[key]));
            Assert.Equal(ExtractPlaceholders(english[key]), ExtractPlaceholders(traditionalChinese[key]));
        }
    }

    [Fact]
    public void LoggingLanguageConfiguration_AllowsAutoAndExplicitSupportedValues()
    {
        var values = new Dictionary<string, string?>
        {
            ["Logging:Language"] = "zh-Hant"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        using var culture = TestCultureScope.Use("en-US");
        var selected = RuntimeLanguage.Apply(configuration["Logging:Language"]);

        Assert.Equal("zh-Hant", selected);
        Assert.Equal("服務已成功啟動。", LogText.Get("ServiceStarted"));
    }

    private static Dictionary<string, string> ReadResources(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "FileTransfer", "Localization", fileName);
        var document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string[] ExtractPlaceholders(string message) =>
        Regex.Matches(message, "\\{[^{}]+\\}")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FileTransfer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
