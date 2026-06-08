[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class AppPathsOptions
{
    public string StateDir { get; init; } = string.Empty;
    public string LogDir { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;

    public static AppPathsOptions FromConfiguration(IConfiguration configuration, string baseDir, string configPath)
    {
        var section = configuration.GetSection("Paths");
        var stateDir = ResolveDirectory(section["StateDir"], DefaultStateDir(baseDir), baseDir);
        var logDir = ResolveDirectory(section["LogDir"], DefaultLogDir(baseDir), baseDir);

        return new AppPathsOptions
        {
            StateDir = stateDir,
            LogDir = logDir,
            ConfigPath = configPath
        };
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(LogDir);
    }

    public string ResolveLogFile(string fileName) => ResolveLogFile(fileName, DateTime.Now);

    internal string ResolveLogFile(string fileName, DateTime timestamp)
    {
        var formatted = string.Format(CultureInfo.InvariantCulture, fileName, timestamp);
        return Path.IsPathRooted(formatted) ? formatted : Path.Combine(LogDir, formatted);
    }

    public string ResolvedTargetPathStatePath(string ruleId) => Path.Combine(StateDir, SanitizeFileName(ruleId), "resolved-target-paths");

    public string InitialScanSkipStatePath(string ruleId) => Path.Combine(StateDir, SanitizeFileName(ruleId), "initial-scan-skipped-files");

    private static string ResolveDirectory(string? configured, string fallback, string baseDir)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDir, value));
    }

    private static string DefaultStateDir(string baseDir)
    {
        var appData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetEnvironmentVariable("XDG_STATE_HOME");

        if (string.IsNullOrWhiteSpace(appData) && !OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = string.IsNullOrWhiteSpace(home) ? string.Empty : Path.Combine(home, ".local", "state");
        }

        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(baseDir, "state")
            : Path.Combine(appData, "FileTransfer", "state");
    }

    private static string DefaultLogDir(string baseDir)
    {
        var appData = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetEnvironmentVariable("XDG_STATE_HOME");

        if (string.IsNullOrWhiteSpace(appData) && !OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = string.IsNullOrWhiteSpace(home) ? string.Empty : Path.Combine(home, ".local", "state");
        }

        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(baseDir, "logs")
            : Path.Combine(appData, "FileTransfer", "logs");
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SyncOptions.DefaultRuleId;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
