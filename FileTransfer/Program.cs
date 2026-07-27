using System.Reflection;

var baseDir = AppContext.BaseDirectory;
var launchDir = Environment.CurrentDirectory;
var configPath = ResolveConfigPath(args, launchDir, baseDir);

if (HasSwitch(args, "--version"))
{
    Console.WriteLine(GetVersion());
    return;
}

if (HasSwitch(args, "--help") || HasSwitch(args, "-h") || HasSwitch(args, "/?"))
{
    Console.WriteLine(string.Format(CultureInfo.CurrentUICulture, LogText.Get("Usage"), GetVersion()));
    return;
}

var configDir = Path.GetDirectoryName(configPath) ?? baseDir;
var configFileName = Path.GetFileName(configPath);

Directory.SetCurrentDirectory(baseDir);

var settings = new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = baseDir,
    DisableDefaults = true
};

var builder = Host.CreateApplicationBuilder(settings);

builder.Configuration
    .SetBasePath(configDir)
    .AddYamlFile(configFileName, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var selectedLanguage = RuntimeLanguage.Apply(builder.Configuration["Logging:Language"]);

if (HasSwitch(args, "--validate"))
{
    var valid = ValidateConfiguration(builder.Configuration, selectedLanguage);
    Environment.ExitCode = valid ? 0 : 1;
    return;
}

var appPaths = AppPathsOptions.FromConfiguration(builder.Configuration, baseDir, configPath);
appPaths.EnsureDirectories();
builder.Services.AddSingleton(appPaths);

builder.Services.AddSingleton<ISyncOptionsProvider>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ConfigurationSyncOptionsProvider>>();
    return new ConfigurationSyncOptionsProvider(configuration, logger);
});

builder.Services.AddLogging(logging =>
{
    logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    });
    logging.AddFile(builder.Configuration.GetSection("Logging:File"), options =>
    {
        options.FormatLogFileName = fileName => appPaths.ResolveLogFile(fileName);
    });
#if WINDOWS_SERVICE
    logging.AddEventLog();
#endif
});

#if WINDOWS_SERVICE
builder.Services.AddWindowsService(options => options.ServiceName = "FileTransfer");
#endif

#if LINUX
builder.Services.AddSystemd();
#endif

builder.Services.AddHostedService<MainService>();

await builder.Build().RunAsync();


static bool HasSwitch(string[] args, string name) =>
    args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

static string GetVersion()
{
    var assembly = Assembly.GetExecutingAssembly();
    var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() ?? "unknown" : informational;
}

static bool ValidateConfiguration(IConfiguration configuration, string selectedLanguage)
{
    try
    {
        var options = SyncOptions.LoadFromConfiguration(configuration);
        var templateRenderer = new PathTemplateRenderer();
        var allValid = SyncOptionsSetValidator.TryPrepareAll(options, NullLogger.Instance, templateRenderer, out var preparedRules);

        Console.WriteLine(allValid
            ? string.Format(CultureInfo.CurrentUICulture, LogText.Get("ConfigurationValid"), preparedRules.Count)
            : LogText.Get("ConfigurationInvalid"));
        Console.WriteLine($"Language: {selectedLanguage}");
        return allValid;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(LogText.Get("ConfigurationInvalid"));
        Console.Error.WriteLine(ex.Message);
        return false;
    }
}

static string ResolveConfigPath(string[] args, string launchDir, string baseDir)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-c", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return ResolvePath(args[i + 1], launchDir);
            }
        }

        const string longPrefix = "--config=";
        if (arg.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePath(arg[longPrefix.Length..], launchDir);
        }

        const string shortPrefix = "-c=";
        if (arg.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ResolvePath(arg[shortPrefix.Length..], launchDir);
        }
    }

    return Path.Combine(baseDir, "appsettings.yaml");
}

static string ResolvePath(string path, string basePath) =>
    Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
