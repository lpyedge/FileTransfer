[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
internal sealed class SyncOptions
{
    public const string RulesSectionName = "Rules";
    public const string DefaultRuleId = "default";

    public string RuleId { get; set; } = DefaultRuleId;
    public string RuntimeId { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = string.Empty;
    public string[] TargetRoots { get; set; } = Array.Empty<string>();
    public TargetMode TargetMode { get; set; } = TargetMode.FirstAvailable;
    public string[] FileExtensions { get; set; } = new[] { "tif", "tiff" };
    public string FileNamePrefix { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; } = true;
    public bool OverwriteExisting { get; set; } = true;
    public bool DeleteSourceAfterCopy { get; set; }
    public bool SkipInitialScan { get; set; }
    public ComparisonMode ComparisonMode { get; set; } = ComparisonMode.LengthAndTimestamp;
    public ReadySignalOptions ReadySignal { get; set; } = new();
    public QueueOptions Queue { get; set; } = new();
    public WatchEventOptions WatchEvents { get; set; } = new();
    public List<PathRule> PathRules { get; set; } = new();

    public int BufferSize { get; set; } = 0;
    public int CopyBufferSize { get; set; } = 262144;
    public int HashBufferSize { get; set; } = 262144;
    public int WatcherInternalBufferSize { get; set; } = 65536;
    public int ReconciliationIntervalMs { get; set; } = 60000;
    public int MaxReconciliationFilesPerRun { get; set; } = 10000;
    public int MaxReconciliationDurationMs { get; set; } = 30000;
    public int MaxParallelTransfers { get; set; } = 4;
    public int InitialRetryDelayMs { get; set; } = 200;
    public int MaxRetryDelayMs { get; set; } = 3000;
    public int OperationTimeoutMs { get; set; } = 300000;
    public int HealthCheckIntervalMs { get; set; } = 10000;
    public bool BackupDeletedTargetsToTrash { get; set; }
    public string[]? NotifyFilters { get; set; }

    public string RuntimeKey => RuntimeId;

    public static IReadOnlyList<SyncOptions> LoadFromConfiguration(IConfiguration configuration)
    {
        var rulesSection = configuration.GetSection(RulesSectionName);
        if (!rulesSection.Exists())
        {
            return Array.Empty<SyncOptions>();
        }

        var expanded = new List<SyncOptions>();
        var index = 0;
        foreach (var child in rulesSection.GetChildren())
        {
            var rule = SyncRuleOptions.FromConfiguration(child, index++);
            if (!rule.Enabled)
            {
                continue;
            }

            expanded.AddRange(rule.Expand());
        }

        return expanded;
    }

    public static SyncOptions FromConfiguration(IConfiguration configuration)
    {
        return LoadFromConfiguration(configuration).FirstOrDefault()?.Clone() ?? new SyncOptions();
    }

    public bool TryPrepare(ILogger logger, PathTemplateRenderer templateRenderer, out SyncOptions prepared)
    {
        prepared = Clone();
        prepared.ApplyDefaults();

        if (!prepared.ValidateRequiredValues(logger) ||
            !prepared.TryNormalizePaths(logger) ||
            !prepared.ValidatePathRules(logger, templateRenderer))
        {
            return false;
        }

        if (PathHelper.PathsOverlap(prepared.SourceRoot, prepared.TargetRoots))
        {
            logger.LogError(LogText.Get("RuleSourceTargetOverlap"), prepared.RuleId, prepared.SourceRoot, prepared.TargetRoots);
            return false;
        }

        return true;
    }

    public SyncOptions Clone() => new()
    {
        RuleId = RuleId,
        RuntimeId = RuntimeId,
        SourceRoot = SourceRoot,
        TargetRoots = TargetRoots?.ToArray() ?? Array.Empty<string>(),
        TargetMode = TargetMode,
        FileExtensions = FileExtensions?.ToArray() ?? Array.Empty<string>(),
        FileNamePrefix = FileNamePrefix,
        IncludeSubdirectories = IncludeSubdirectories,
        OverwriteExisting = OverwriteExisting,
        DeleteSourceAfterCopy = DeleteSourceAfterCopy,
        SkipInitialScan = SkipInitialScan,
        ComparisonMode = ComparisonMode,
        ReadySignal = ReadySignal?.Clone() ?? new ReadySignalOptions(),
        Queue = Queue?.Clone() ?? new QueueOptions(),
        WatchEvents = WatchEvents?.Clone() ?? new WatchEventOptions(),
        PathRules = PathRules?.Select(rule => rule.Clone()).ToList() ?? new List<PathRule>(),
        BufferSize = BufferSize,
        CopyBufferSize = CopyBufferSize,
        HashBufferSize = HashBufferSize,
        WatcherInternalBufferSize = WatcherInternalBufferSize,
        ReconciliationIntervalMs = ReconciliationIntervalMs,
        MaxReconciliationFilesPerRun = MaxReconciliationFilesPerRun,
        MaxReconciliationDurationMs = MaxReconciliationDurationMs,
        MaxParallelTransfers = MaxParallelTransfers,
        InitialRetryDelayMs = InitialRetryDelayMs,
        MaxRetryDelayMs = MaxRetryDelayMs,
        OperationTimeoutMs = OperationTimeoutMs,
        HealthCheckIntervalMs = HealthCheckIntervalMs,
        BackupDeletedTargetsToTrash = BackupDeletedTargetsToTrash,
        NotifyFilters = NotifyFilters?.ToArray()
    };

    public void ApplyDefaults()
    {
        RuleId = string.IsNullOrWhiteSpace(RuleId) ? DefaultRuleId : RuleId.Trim();
        RuntimeId = string.IsNullOrWhiteSpace(RuntimeId) || (string.Equals(RuntimeId.Trim(), DefaultRuleId, StringComparison.OrdinalIgnoreCase) && !string.Equals(RuleId, DefaultRuleId, StringComparison.OrdinalIgnoreCase)) ? RuleId : RuntimeId.Trim();
        TargetRoots ??= Array.Empty<string>();
        FileExtensions ??= Array.Empty<string>();
        ReadySignal ??= new ReadySignalOptions();
        ReadySignal.ApplyDefaults();
        Queue ??= new QueueOptions();
        Queue.ApplyDefaults();
        WatchEvents ??= new WatchEventOptions();
        PathRules ??= new List<PathRule>();

        if (BufferSize > 0)
        {
            CopyBufferSize = BufferSize;
            HashBufferSize = BufferSize;
            WatcherInternalBufferSize = Math.Min(BufferSize, 65536);
        }

        CopyBufferSize = CopyBufferSize > 0 ? CopyBufferSize : 262144;
        HashBufferSize = HashBufferSize > 0 ? HashBufferSize : CopyBufferSize;
        WatcherInternalBufferSize = WatcherInternalBufferSize > 0 ? WatcherInternalBufferSize : 65536;
        BufferSize = CopyBufferSize;
        MaxParallelTransfers = Math.Max(1, MaxParallelTransfers);
        InitialRetryDelayMs = InitialRetryDelayMs > 0 ? InitialRetryDelayMs : 200;
        MaxRetryDelayMs = MaxRetryDelayMs > InitialRetryDelayMs ? MaxRetryDelayMs : InitialRetryDelayMs * 4;
        OperationTimeoutMs = OperationTimeoutMs > 0 ? OperationTimeoutMs : 300000;
        ReconciliationIntervalMs = ReconciliationIntervalMs > 0 ? ReconciliationIntervalMs : 60000;
        MaxReconciliationFilesPerRun = MaxReconciliationFilesPerRun > 0 ? MaxReconciliationFilesPerRun : 10000;
        MaxReconciliationDurationMs = MaxReconciliationDurationMs > 0 ? MaxReconciliationDurationMs : 30000;
        HealthCheckIntervalMs = HealthCheckIntervalMs > 0 ? HealthCheckIntervalMs : 10000;
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(RuleId))
        {
            yield return LogText.Get("RuleIdMissing");
        }

        if (string.IsNullOrWhiteSpace(SourceRoot))
        {
            yield return LogText.Get("RuleSourceMissing").Replace("{RuleId}", RuleId, StringComparison.Ordinal);
        }

        if (TargetRoots is not { Length: > 0 } || TargetRoots.Any(string.IsNullOrWhiteSpace))
        {
            yield return LogText.Get("RuleTargetsMissing").Replace("{RuleId}", RuleId, StringComparison.Ordinal);
        }
    }

    private bool ValidateRequiredValues(ILogger logger)
    {
        var valid = true;
        foreach (var error in Validate())
        {
            logger.LogError(error);
            valid = false;
        }

        return valid;
    }

    private bool TryNormalizePaths(ILogger logger)
    {
        if (!PathHelper.TryGetFullPath(SourceRoot, out var normalizedSource, out var error))
        {
            logger.LogError(error, LogText.Get("RuleSourceNormalizeFailed"), RuleId, SourceRoot);
            return false;
        }

        SourceRoot = normalizedSource;
        RuntimeId = BuildRuntimeId(RuleId, SourceRoot, RuntimeId);
        var normalizedTargets = new List<string>(TargetRoots.Length);
        var seenTargets = new HashSet<string>(PathKeyComparer.Comparer);

        for (var i = 0; i < TargetRoots.Length; i++)
        {
            if (!PathHelper.TryGetFullPath(TargetRoots[i], out var normalizedTarget, out error))
            {
                logger.LogError(error, LogText.Get("RuleTargetNormalizeFailed"), RuleId, TargetRoots[i]);
                return false;
            }

            if (seenTargets.Add(normalizedTarget))
            {
                normalizedTargets.Add(normalizedTarget);
            }
        }

        TargetRoots = normalizedTargets.ToArray();
        return true;
    }

    private bool ValidatePathRules(ILogger logger, PathTemplateRenderer templateRenderer)
    {
        foreach (var rule in PathRules)
        {
            if (!rule.TryValidate(logger, templateRenderer))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildRuntimeId(string ruleId, string sourceRoot, string currentRuntimeId)
    {
        if (!currentRuntimeId.Contains("#source:", StringComparison.Ordinal))
        {
            return currentRuntimeId;
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceRoot))).ToLowerInvariant()[..12];
        return $"{ruleId}#source:{sourceHash}";
    }

    private static string[] ReadArray(IConfiguration section, string name)
    {
        var valueSection = section.GetSection(name);
        var values = valueSection.GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        if (values.Length > 0)
        {
            return values;
        }

        var scalar = section[name];
        return string.IsNullOrWhiteSpace(scalar)
            ? Array.Empty<string>()
            : new[] { scalar.Trim() };
    }

    private static string[] ReadArrayOrDefault(IConfiguration section, string name, string[] defaultValue)
    {
        var valueSection = section.GetSection(name);
        return valueSection.Exists() || !string.IsNullOrWhiteSpace(section[name])
            ? ReadArray(section, name)
            : defaultValue.ToArray();
    }

    private static void ReadBool(IConfiguration section, string name, Action<bool> apply)
    {
        if (bool.TryParse(section[name], out var value))
        {
            apply(value);
        }
    }

    private static void ReadInt(IConfiguration section, string name, Action<int> apply)
    {
        if (int.TryParse(section[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            apply(value);
        }
    }

    private static void ReadEnum<T>(IConfiguration section, string name, Action<T> apply) where T : struct
    {
        if (Enum.TryParse<T>(section[name], true, out var value))
        {
            apply(value);
        }
    }

    private static void ReadComparisonMode(IConfiguration section, Action<ComparisonMode> apply)
    {
        var raw = section["VerificationMode"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = section[nameof(ComparisonMode)];
        }

        if (string.Equals(raw, "Metadata", StringComparison.OrdinalIgnoreCase))
        {
            apply(ComparisonMode.LengthAndTimestamp);
            return;
        }

        if (Enum.TryParse<ComparisonMode>(raw, true, out var value))
        {
            apply(value);
        }
    }

    private static List<PathRule> ReadPathRules(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return new List<PathRule>();
        }

        var rules = new List<PathRule>();
        foreach (var child in section.GetChildren())
        {
            var rule = new PathRule
            {
                RegexTimeoutMs = int.TryParse(child[nameof(PathRule.RegexTimeoutMs)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var regexTimeoutMs) && regexTimeoutMs > 0 ? regexTimeoutMs : 500,
                MatchPattern = child[nameof(PathRule.MatchPattern)]?.Trim() ?? string.Empty,
                TargetTemplate = child[nameof(PathRule.TargetTemplate)]?.Trim() ?? string.Empty
            };

            ReadBool(child, nameof(PathRule.Ignore), value => rule.Ignore = value);
            if (rule.HasConfiguration)
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    private sealed class SyncRuleOptions
    {
        public string Id { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
        public string[] SourceRoots { get; init; } = Array.Empty<string>();
        public string[] TargetRoots { get; init; } = Array.Empty<string>();
        public TargetMode TargetMode { get; init; } = TargetMode.FirstAvailable;
        public string[] FileExtensions { get; init; } = Array.Empty<string>();
        public string FileNamePrefix { get; init; } = string.Empty;
        public bool IncludeSubdirectories { get; init; } = true;
        public bool OverwriteExisting { get; init; } = true;
        public bool DeleteSourceAfterCopy { get; init; }
        public bool SkipInitialScan { get; init; }
        public ComparisonMode ComparisonMode { get; init; } = ComparisonMode.LengthAndTimestamp;
        public ReadySignalOptions ReadySignal { get; init; } = new();
        public QueueOptions Queue { get; init; } = new();
        public WatchEventOptions WatchEvents { get; init; } = new();
        public List<PathRule> PathRules { get; init; } = new();
        public int BufferSize { get; init; } = 0;
        public int CopyBufferSize { get; init; } = 262144;
        public int HashBufferSize { get; init; } = 262144;
        public int WatcherInternalBufferSize { get; init; } = 65536;
        public int ReconciliationIntervalMs { get; init; } = 60000;
        public int MaxReconciliationFilesPerRun { get; init; } = 10000;
        public int MaxReconciliationDurationMs { get; init; } = 30000;
        public int MaxParallelTransfers { get; init; } = 4;
        public int InitialRetryDelayMs { get; init; } = 200;
        public int MaxRetryDelayMs { get; init; } = 3000;
        public int OperationTimeoutMs { get; init; } = 300000;
        public int HealthCheckIntervalMs { get; init; } = 10000;
        public bool BackupDeletedTargetsToTrash { get; init; }
        public string[]? NotifyFilters { get; init; }

        public static SyncRuleOptions FromConfiguration(IConfigurationSection section, int index)
        {
            var options = new SyncRuleOptionsBuilder
            {
                Id = section[nameof(Id)]?.Trim() is { Length: > 0 } id ? id : $"rule-{index + 1}",
                SourceRoots = ReadArray(section, nameof(SourceRoots)),
                TargetRoots = ReadArray(section, nameof(TargetRoots)),
                FileExtensions = ReadArrayOrDefault(section, nameof(FileExtensions), Array.Empty<string>()),
                FileNamePrefix = section[nameof(FileNamePrefix)]?.Trim() ?? string.Empty,
                NotifyFilters = ReadArray(section, nameof(NotifyFilters)) is { Length: > 0 } filters ? filters : null,
                PathRules = ReadPathRules(section.GetSection(nameof(PathRules)))
            };

            var singleSourceRoot = section[nameof(SyncOptions.SourceRoot)]?.Trim();
            if (options.SourceRoots.Length == 0 && !string.IsNullOrWhiteSpace(singleSourceRoot))
            {
                options.SourceRoots = new[] { singleSourceRoot };
            }

            ReadBool(section, nameof(Enabled), value => options.Enabled = value);
            ReadBool(section, nameof(IncludeSubdirectories), value => options.IncludeSubdirectories = value);
            ReadBool(section, nameof(OverwriteExisting), value => options.OverwriteExisting = value);
            ReadBool(section, nameof(DeleteSourceAfterCopy), value => options.DeleteSourceAfterCopy = value);
            ReadBool(section, nameof(SkipInitialScan), value => options.SkipInitialScan = value);
            ReadInt(section, nameof(BufferSize), value => options.BufferSize = value);
            ReadInt(section, nameof(CopyBufferSize), value => options.CopyBufferSize = value);
            ReadInt(section, nameof(HashBufferSize), value => options.HashBufferSize = value);
            ReadInt(section, nameof(WatcherInternalBufferSize), value => options.WatcherInternalBufferSize = value);
            ReadInt(section, nameof(ReconciliationIntervalMs), value => options.ReconciliationIntervalMs = value);
            ReadInt(section, nameof(MaxReconciliationFilesPerRun), value => options.MaxReconciliationFilesPerRun = value);
            ReadInt(section, nameof(MaxReconciliationDurationMs), value => options.MaxReconciliationDurationMs = value);
            ReadInt(section, nameof(MaxParallelTransfers), value => options.MaxParallelTransfers = value);
            ReadInt(section, nameof(InitialRetryDelayMs), value => options.InitialRetryDelayMs = value);
            ReadInt(section, nameof(MaxRetryDelayMs), value => options.MaxRetryDelayMs = value);
            ReadInt(section, nameof(OperationTimeoutMs), value => options.OperationTimeoutMs = value);
            ReadInt(section, nameof(HealthCheckIntervalMs), value => options.HealthCheckIntervalMs = value);
            ReadBool(section, nameof(BackupDeletedTargetsToTrash), value => options.BackupDeletedTargetsToTrash = value);
            ReadEnum<TargetMode>(section, nameof(TargetMode), value => options.TargetMode = value);
            ReadComparisonMode(section, value => options.ComparisonMode = value);
            options.ReadySignal = ReadySignalOptions.FromConfiguration(section.GetSection(nameof(ReadySignal)));
            options.Queue = QueueOptions.FromConfiguration(section.GetSection(nameof(Queue)));
            options.WatchEvents = WatchEventOptions.FromConfiguration(section.GetSection(nameof(WatchEvents)));

            return options.Build();
        }

        public IEnumerable<SyncOptions> Expand()
        {
            var sourceRoots = SourceRoots.Length == 0 ? new[] { string.Empty } : SourceRoots;
            for (var i = 0; i < sourceRoots.Length; i++)
            {
                var expanded = new SyncOptions
                {
                    RuleId = Id,
                    RuntimeId = sourceRoots.Length == 1 ? Id : $"{Id}#source:{i + 1}",
                    SourceRoot = sourceRoots[i],
                    TargetRoots = TargetRoots.ToArray(),
                    TargetMode = TargetMode,
                    FileExtensions = FileExtensions.ToArray(),
                    FileNamePrefix = FileNamePrefix,
                    IncludeSubdirectories = IncludeSubdirectories,
                    OverwriteExisting = OverwriteExisting,
                    DeleteSourceAfterCopy = DeleteSourceAfterCopy,
                    SkipInitialScan = SkipInitialScan,
                    ComparisonMode = ComparisonMode,
                    ReadySignal = ReadySignal.Clone(),
                    Queue = Queue.Clone(),
                    WatchEvents = WatchEvents.Clone(),
                    PathRules = PathRules.Select(rule => rule.Clone()).ToList(),
                    BufferSize = BufferSize,
                    CopyBufferSize = CopyBufferSize,
                    HashBufferSize = HashBufferSize,
                    WatcherInternalBufferSize = WatcherInternalBufferSize,
                    ReconciliationIntervalMs = ReconciliationIntervalMs,
                    MaxReconciliationFilesPerRun = MaxReconciliationFilesPerRun,
                    MaxReconciliationDurationMs = MaxReconciliationDurationMs,
                    MaxParallelTransfers = MaxParallelTransfers,
                    InitialRetryDelayMs = InitialRetryDelayMs,
                    MaxRetryDelayMs = MaxRetryDelayMs,
                    OperationTimeoutMs = OperationTimeoutMs,
                    HealthCheckIntervalMs = HealthCheckIntervalMs,
                    BackupDeletedTargetsToTrash = BackupDeletedTargetsToTrash,
                    NotifyFilters = NotifyFilters?.ToArray()
                };

                expanded.ApplyDefaults();
                yield return expanded;
            }
        }

        private sealed class SyncRuleOptionsBuilder
        {
            public string Id { get; set; } = string.Empty;
            public bool Enabled { get; set; } = true;
            public string[] SourceRoots { get; set; } = Array.Empty<string>();
            public string[] TargetRoots { get; set; } = Array.Empty<string>();
            public TargetMode TargetMode { get; set; } = TargetMode.FirstAvailable;
            public string[] FileExtensions { get; set; } = Array.Empty<string>();
            public string FileNamePrefix { get; set; } = string.Empty;
            public bool IncludeSubdirectories { get; set; } = true;
            public bool OverwriteExisting { get; set; } = true;
            public bool DeleteSourceAfterCopy { get; set; }
            public bool SkipInitialScan { get; set; }
            public ComparisonMode ComparisonMode { get; set; } = ComparisonMode.LengthAndTimestamp;
            public ReadySignalOptions ReadySignal { get; set; } = new();
            public QueueOptions Queue { get; set; } = new();
            public WatchEventOptions WatchEvents { get; set; } = new();
            public List<PathRule> PathRules { get; set; } = new();
            public int BufferSize { get; set; } = 0;
            public int CopyBufferSize { get; set; } = 262144;
            public int HashBufferSize { get; set; } = 262144;
            public int WatcherInternalBufferSize { get; set; } = 65536;
            public int ReconciliationIntervalMs { get; set; } = 60000;
            public int MaxReconciliationFilesPerRun { get; set; } = 10000;
            public int MaxReconciliationDurationMs { get; set; } = 30000;
            public int MaxParallelTransfers { get; set; } = 4;
            public int InitialRetryDelayMs { get; set; } = 200;
            public int MaxRetryDelayMs { get; set; } = 3000;
            public int OperationTimeoutMs { get; set; } = 300000;
            public int HealthCheckIntervalMs { get; set; } = 10000;
            public bool BackupDeletedTargetsToTrash { get; set; }
            public string[]? NotifyFilters { get; set; }

            public SyncRuleOptions Build() => new()
            {
                Id = Id,
                Enabled = Enabled,
                SourceRoots = SourceRoots,
                TargetRoots = TargetRoots,
                TargetMode = TargetMode,
                FileExtensions = FileExtensions,
                FileNamePrefix = FileNamePrefix,
                IncludeSubdirectories = IncludeSubdirectories,
                OverwriteExisting = OverwriteExisting,
                DeleteSourceAfterCopy = DeleteSourceAfterCopy,
                SkipInitialScan = SkipInitialScan,
                ComparisonMode = ComparisonMode,
                ReadySignal = ReadySignal,
                Queue = Queue,
                WatchEvents = WatchEvents,
                PathRules = PathRules,
                BufferSize = BufferSize,
                CopyBufferSize = CopyBufferSize,
                HashBufferSize = HashBufferSize,
                WatcherInternalBufferSize = WatcherInternalBufferSize,
                ReconciliationIntervalMs = ReconciliationIntervalMs,
                MaxReconciliationFilesPerRun = MaxReconciliationFilesPerRun,
                MaxReconciliationDurationMs = MaxReconciliationDurationMs,
                MaxParallelTransfers = MaxParallelTransfers,
                InitialRetryDelayMs = InitialRetryDelayMs,
                MaxRetryDelayMs = MaxRetryDelayMs,
                OperationTimeoutMs = OperationTimeoutMs,
                HealthCheckIntervalMs = HealthCheckIntervalMs,
                BackupDeletedTargetsToTrash = BackupDeletedTargetsToTrash,
                NotifyFilters = NotifyFilters
            };
        }
    }
}
