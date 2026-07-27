namespace FileTransfer.Tests;

public class SyncOptionsTests
{
    [Fact]
    public void ApplyDefaults_NormalizesNullCollectionsAndInvalidNumbers()
    {
        var settings = new SyncOptions
        {
            TargetRoots = null!,
            FileExtensions = null!,
            WatchEvents = null!,
            PathRules = null!,
            BufferSize = 0,
            MaxParallelTransfers = 0,
            InitialRetryDelayMs = 0,
            MaxRetryDelayMs = 100,
            OperationTimeoutMs = 0,
            ReconciliationIntervalMs = 0
        };

        settings.ApplyDefaults();

        Assert.Empty(settings.TargetRoots);
        Assert.Empty(settings.FileExtensions);
        Assert.NotNull(settings.WatchEvents);
        Assert.NotNull(settings.PathRules);
        Assert.Equal(262144, settings.BufferSize);
        Assert.Equal(262144, settings.CopyBufferSize);
        Assert.Equal(262144, settings.HashBufferSize);
        Assert.Equal(65536, settings.WatcherInternalBufferSize);
        Assert.NotNull(settings.ReadySignal);
        Assert.Equal(1, settings.MaxParallelTransfers);
        Assert.Equal(200, settings.InitialRetryDelayMs);
        Assert.Equal(800, settings.MaxRetryDelayMs);
        Assert.Equal(300000, settings.OperationTimeoutMs);
        Assert.Equal(60000, settings.ReconciliationIntervalMs);
        Assert.False(settings.BackupDeletedTargetsToTrash);
    }

    [Fact]
    public void Validate_ReturnsErrorsForMissingRequiredValues()
    {
        using var culture = TestCultureScope.Use("ja");
        var settings = new SyncOptions
        {
            SourceRoot = "",
            TargetRoots = Array.Empty<string>()
        };

        var errors = settings.Validate().ToArray();

        Assert.Equal(2, errors.Length);
        Assert.Contains($"Rule {SyncOptions.DefaultRuleId}: 設定値 SourceRoot が未設定です。", errors);
        Assert.Contains($"Rule {SyncOptions.DefaultRuleId}: 設定値 TargetRoots が未設定です。", errors);
    }

    [Fact]
    public void Clone_CreatesDeepCopy()
    {
        var settings = new SyncOptions
        {
            SourceRoot = @"C:\Source",
            TargetRoots = new[] { @"C:\Target" },
            FileExtensions = new[] { ".txt" },
            FileNamePrefix = "M_",
            IncludeSubdirectories = false,
            OverwriteExisting = false,
            DeleteSourceAfterCopy = true,
            SkipInitialScan = true,
            ComparisonMode = ComparisonMode.Hash,
            NotifyFilters = new[] { "FileName", "LastWrite" },
            BackupDeletedTargetsToTrash = true,
            WatchEvents = new WatchEventOptions
            {
                Created = false,
                Changed = true,
                Deleted = true
            },
            PathRules = new List<PathRule>
            {
                new()
                {
                    MatchPattern = @"^(?<tail>.+)$",
                    TargetTemplate = "Mapped/{tail}"
                }
            }
        };

        var clone = settings.Clone();
        clone.TargetRoots[0] = @"D:\Other";
        clone.FileExtensions[0] = ".yaml";
        clone.NotifyFilters![0] = "DirectoryName";
        clone.WatchEvents.Created = true;
        clone.PathRules[0].TargetTemplate = "Changed";

        Assert.Equal(@"C:\Target", settings.TargetRoots[0]);
        Assert.Equal(".txt", settings.FileExtensions[0]);
        Assert.Equal("FileName", settings.NotifyFilters![0]);
        Assert.False(settings.WatchEvents.Created);
        Assert.Equal("Mapped/{tail}", settings.PathRules[0].TargetTemplate);
        Assert.True(settings.SkipInitialScan);
        Assert.True(settings.BackupDeletedTargetsToTrash);
    }

    [Fact]
    public void FromConfiguration_ParsesCurrentShape()
    {
        var values = new Dictionary<string, string?>
        {
            ["Rules:0:SourceRoot"] = @"C:\Inbox",
            ["Rules:0:TargetRoots:0"] = @"D:\Archive",
            ["Rules:0:FileExtensions:0"] = ".txt",
            ["Rules:0:FileExtensions:1"] = ".csv",
            ["Rules:0:FileNamePrefix"] = "M_",
            ["Rules:0:IncludeSubdirectories"] = "false",
            ["Rules:0:OverwriteExisting"] = "false",
            ["Rules:0:DeleteSourceAfterCopy"] = "true",
            ["Rules:0:SkipInitialScan"] = "true",
            ["Rules:0:WatchEvents:Created"] = "false",
            ["Rules:0:WatchEvents:Changed"] = "true",
            ["Rules:0:WatchEvents:Deleted"] = "true",
            ["Rules:0:PathRules:0:MatchPattern"] = @"^(?<date>\d{8})-(?<tail>.+)\.txt$",
            ["Rules:0:PathRules:0:TargetTemplate"] = "Rendered/{date:yyyy-MM-dd}/{tail}",
            ["Rules:0:CopyBufferSize"] = "8192",
            ["Rules:0:HashBufferSize"] = "4096",
            ["Rules:0:WatcherInternalBufferSize"] = "32768",
            ["Rules:0:ReadySignal:Mode"] = "StableSize",
            ["Rules:0:ReadySignal:StableChecks"] = "4",
            ["Rules:0:ReconciliationIntervalMs"] = "1500",
            ["Rules:0:MaxParallelTransfers"] = "8",
            ["Rules:0:InitialRetryDelayMs"] = "75",
            ["Rules:0:MaxRetryDelayMs"] = "900",
            ["Rules:0:OperationTimeoutMs"] = "12000",
            ["Rules:0:BackupDeletedTargetsToTrash"] = "true",
            ["Rules:0:ComparisonMode"] = "Hash",
            ["Rules:0:NotifyFilters:0"] = "FileName",
            ["Rules:0:NotifyFilters:1"] = "LastWrite"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = SyncOptions.FromConfiguration(configuration);

        Assert.Equal(@"C:\Inbox", settings.SourceRoot);
        Assert.Equal(new[] { @"D:\Archive" }, settings.TargetRoots);
        Assert.Equal(new[] { ".txt", ".csv" }, settings.FileExtensions);
        Assert.Equal("M_", settings.FileNamePrefix);
        Assert.False(settings.IncludeSubdirectories);
        Assert.False(settings.OverwriteExisting);
        Assert.True(settings.DeleteSourceAfterCopy);
        Assert.True(settings.SkipInitialScan);
        Assert.False(settings.WatchEvents.Created);
        Assert.True(settings.WatchEvents.Changed);
        Assert.True(settings.WatchEvents.Deleted);
        Assert.Single(settings.PathRules);
        Assert.Equal(ComparisonMode.Hash, settings.ComparisonMode);
        Assert.Equal(8192, settings.BufferSize);
        Assert.Equal(8192, settings.CopyBufferSize);
        Assert.Equal(4096, settings.HashBufferSize);
        Assert.Equal(32768, settings.WatcherInternalBufferSize);
        Assert.Equal(ReadySignalMode.StableSize, settings.ReadySignal.Mode);
        Assert.Equal(4, settings.ReadySignal.StableChecks);
        Assert.Equal(1500, settings.ReconciliationIntervalMs);
        Assert.Equal(8, settings.MaxParallelTransfers);
        Assert.Equal(75, settings.InitialRetryDelayMs);
        Assert.Equal(900, settings.MaxRetryDelayMs);
        Assert.Equal(12000, settings.OperationTimeoutMs);
        Assert.True(settings.BackupDeletedTargetsToTrash);
        Assert.Equal(new[] { "FileName", "LastWrite" }, settings.NotifyFilters);
    }

    [Fact]
    public void FromConfiguration_CapturesInvalidRegex()
    {
        var values = new Dictionary<string, string?>
        {
            ["Rules:0:SourceRoot"] = @"C:\Inbox",
            ["Rules:0:TargetRoots:0"] = @"D:\Archive",
            ["Rules:0:WatchEvents:Created"] = "true",
            ["Rules:0:WatchEvents:Changed"] = "false",
            ["Rules:0:WatchEvents:Deleted"] = "true",
            ["Rules:0:PathRules:0:MatchPattern"] = "(",
            ["Rules:0:PathRules:0:TargetTemplate"] = "Rendered/{fileName}"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = SyncOptions.FromConfiguration(configuration);

        Assert.True(settings.WatchEvents.Created);
        Assert.False(settings.WatchEvents.Changed);
        Assert.True(settings.WatchEvents.Deleted);
        Assert.Single(settings.PathRules);
        Assert.NotNull(settings.PathRules[0].RegexError);
        Assert.DoesNotMatch(settings.PathRules[0].RegexPattern, "anything");
    }

    [Fact]
    public void FromConfiguration_DefaultsTrashBackupToFalseWhenSettingIsMissing()
    {
        var values = new Dictionary<string, string?>
        {
            ["Rules:0:SourceRoot"] = @"C:\Inbox",
            ["Rules:0:TargetRoots:0"] = @"D:\Archive",
            ["Rules:0:WatchEvents:Deleted"] = "true"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = SyncOptions.FromConfiguration(configuration);

        Assert.True(settings.WatchEvents.Deleted);
        Assert.False(settings.BackupDeletedTargetsToTrash);
    }

    [Fact]
    public void FromConfiguration_PreservesDefaultFileExtensionsWhenSectionIsMissing()
    {
        var values = new Dictionary<string, string?>
        {
            ["Rules:0:SourceRoot"] = @"C:\Inbox",
            ["Rules:0:TargetRoots:0"] = @"D:\Archive"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = SyncOptions.FromConfiguration(configuration);

        Assert.Equal(Array.Empty<string>(), settings.FileExtensions);
    }

    [Fact]
    public void TryPrepare_DeduplicatesTargetRootsAfterNormalization()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var source = temp.CreateDir("source");
        var target = temp.CreateDir("target");

        var settings = new SyncOptions
        {
            SourceRoot = source,
            TargetRoots = new[] { target, target },
            FileExtensions = Array.Empty<string>()
        };

        var success = settings.TryPrepare(logger, new PathTemplateRenderer(), out var prepared);

        Assert.True(success);
        Assert.Single(prepared.TargetRoots);
    }

    [Fact]
    public void OverwriteFalseAndDeleteSourceTrue_IsRejected()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var settings = new SyncOptions
        {
            RuleId = "unsafe-rule",
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.CreateDir("target") },
            OverwriteExisting = false,
            DeleteSourceAfterCopy = true
        };

        Assert.False(settings.TryPrepare(logger, new PathTemplateRenderer(), out _));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("unsafe-rule") && entry.Message.Contains("OverwriteExisting"));
    }

}
