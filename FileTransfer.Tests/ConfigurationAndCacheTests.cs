namespace FileTransfer.Tests;

public class ConfigurationAndCacheTests
{
    [Fact]
    public void FromConfiguration_ExpandsMultipleSourceRootsAndSkipsDisabledRules()
    {
        var values = new Dictionary<string, string?>
        {
            ["Rules:0:Id"] = "multi",
            ["Rules:0:SourceRoots:0"] = @"C:\InboxA",
            ["Rules:0:SourceRoots:1"] = @"C:\InboxB",
            ["Rules:0:TargetRoots:0"] = @"D:\Archive",
            ["Rules:0:TargetMode"] = "AllHealthyTargets",
            ["Rules:1:Id"] = "disabled",
            ["Rules:1:Enabled"] = "false",
            ["Rules:1:SourceRoots:0"] = @"C:\Disabled",
            ["Rules:1:TargetRoots:0"] = @"D:\Disabled"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = SyncOptions.LoadFromConfiguration(configuration).ToArray();

        Assert.Equal(2, settings.Length);
        Assert.All(settings, setting => Assert.Equal("multi", setting.RuleId));
        Assert.All(settings, setting => Assert.Equal(TargetMode.AllHealthyTargets, setting.TargetMode));
        Assert.Equal(@"C:\InboxA", settings[0].SourceRoot);
        Assert.Equal(@"C:\InboxB", settings[1].SourceRoot);
        Assert.NotEqual(settings[0].RuntimeId, settings[1].RuntimeId);
    }

    [Fact]
    public void DirectOptions_DefaultRuntimeIdFollowsRuleId()
    {
        var settings = new SyncOptions
        {
            RuleId = "direct-rule",
            SourceRoot = @"C:\Inbox",
            TargetRoots = new[] { @"D:\Archive" }
        };

        settings.ApplyDefaults();

        Assert.Equal("direct-rule", settings.RuntimeId);
    }

    [Fact]
    public void MinimalConfiguration_UsesStableProductionDefaults()
    {
        var values = new Dictionary<string, string?>
        {
            ["Rules:0:Id"] = "minimal",
            ["Rules:0:SourceRoots:0"] = @"C:\Inbox",
            ["Rules:0:TargetRoots:0"] = @"D:\Archive"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var settings = SyncOptions.FromConfiguration(configuration);

        Assert.Equal(TargetMode.FirstAvailable, settings.TargetMode);
        Assert.Equal(ComparisonMode.LengthAndTimestamp, settings.ComparisonMode);
        Assert.Equal(ReadySignalMode.StableSize, settings.ReadySignal.Mode);
        Assert.Equal(3, settings.ReadySignal.StableChecks);
        Assert.Equal(10000, settings.Queue.CopyCapacity);
        Assert.Equal(5000, settings.Queue.DeleteCapacity);
        Assert.Equal(4, settings.MaxParallelTransfers);
        Assert.Equal(10000, settings.MaxReconciliationFilesPerRun);
        Assert.Equal(30000, settings.MaxReconciliationDurationMs);
        Assert.Equal(262144, settings.CopyBufferSize);
        Assert.Equal(65536, settings.WatcherInternalBufferSize);
        Assert.True(settings.WatchEvents.Created);
        Assert.True(settings.WatchEvents.Changed);
        Assert.False(settings.WatchEvents.Deleted);
    }

    [Fact]
    public void RemovedPrimaryAndBackupMode_IsNotAvailable()
    {
        Assert.DoesNotContain("PrimaryAndBackup", Enum.GetNames<TargetMode>());
    }

    [Fact]
    public void FileProcessingRules_TreatsRenamedAsEnabledWhenCreatedIsEnabled()
    {
        var settings = new SyncOptions
        {
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = false,
                Deleted = false
            }
        };

        Assert.True(FileProcessingRules.IsEventEnabled(FileChangeKind.Renamed, settings));
    }

    [Fact]
    public void ResolvedTargetPathStateStore_PersistsJournalAcrossInstancesAndIsolatesRules()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var statePath = temp.GetPath(Path.Combine("state", "resolved-target-paths"));
        var sourceA = temp.GetPath("source-a.txt");
        var sourceB = temp.GetPath("source-b.txt");
        var targetRoot = temp.CreateDir("target");
        File.WriteAllText(sourceA, "a");
        File.WriteAllText(sourceB, "b");
        var sourceACreation = new FileInfo(sourceA).CreationTimeUtc;
        var sourceBCreation = new FileInfo(sourceB).CreationTimeUtc;

        using (var store = new ResolvedTargetPathStateStore(logger, statePath))
        {
            store.Upsert(new PersistedResolvedTargetPathEntry("rule-a", sourceA, targetRoot, Path.Combine(targetRoot, "a.txt"), sourceACreation));
            store.Upsert(new PersistedResolvedTargetPathEntry("rule-b", sourceB, targetRoot, Path.Combine(targetRoot, "b.txt"), sourceBCreation));
        }

        using var reloaded = new ResolvedTargetPathStateStore(logger, statePath);
        var entries = reloaded.LoadEntries();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.RuleId == "rule-a" && entry.SourceRoot == sourceA);
        Assert.Contains(entries, entry => entry.RuleId == "rule-b" && entry.SourceRoot == sourceB);
    }

    [Fact]
    public void ResolvedTargetPathCache_PersistsOnlyDynamicTemplateResolutions()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var statePath = temp.GetPath(Path.Combine("state", "resolved-target-paths"));
        var sourceRoot = temp.CreateDir("source");
        var targetRoot = temp.CreateDir("target");
        var sourceDir = Path.Combine(sourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "report.txt");
        File.WriteAllText(sourceFile, "payload");

        using (var store = new ResolvedTargetPathStateStore(logger, statePath))
        using (var cache = new ResolvedTargetPathCache(logger, store, "deterministic"))
        {
            var deterministicMapper = CreateMapper(sourceRoot, targetRoot, "Mapped/{fileName}");
            Assert.True(cache.TryResolve(sourceFile, targetRoot, deterministicMapper, out _));
        }

        using (var store = new ResolvedTargetPathStateStore(logger, statePath))
        {
            Assert.Empty(store.LoadEntries());
        }

        using (var store = new ResolvedTargetPathStateStore(logger, statePath))
        using (var cache = new ResolvedTargetPathCache(logger, store, "dynamic"))
        {
            var dynamicMapper = CreateMapper(sourceRoot, targetRoot, "Mapped/{guid:N}/{fileName}");
            Assert.True(cache.TryResolve(sourceFile, targetRoot, dynamicMapper, out _));
        }

        using (var store = new ResolvedTargetPathStateStore(logger, statePath))
        {
            var entry = Assert.Single(store.LoadEntries());
            Assert.Equal("dynamic", entry.RuleId);
            Assert.Equal(sourceFile, entry.SourceRoot);
        }
    }


    [Fact]
    public void ResolvedTargetPathCache_UsesRuntimeNamespaceForSharedStore()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        using var store = new ResolvedTargetPathStateStore(logger, temp.GetPath(Path.Combine("state", "resolved-target-paths")));
        var sourceRoot = temp.CreateDir("source");
        var targetRoot = temp.CreateDir("target");
        var sourceDir = Path.Combine(sourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var firstSource = Path.Combine(sourceDir, "first.txt");
        var secondSource = Path.Combine(sourceDir, "second.txt");
        File.WriteAllText(firstSource, "first");
        File.WriteAllText(secondSource, "second");
        var mapper = CreateMapper(sourceRoot, targetRoot, "Mapped/{guid:N}/{fileName}");

        using var firstCache = new ResolvedTargetPathCache(logger, store, "rule#source:first");
        using var secondCache = new ResolvedTargetPathCache(logger, store, "rule#source:second");

        Assert.True(firstCache.TryResolve(firstSource, targetRoot, mapper, out _));
        Assert.True(secondCache.TryResolve(secondSource, targetRoot, mapper, out _));
        firstCache.Clear();

        var entries = store.LoadEntries();
        Assert.DoesNotContain(entries, entry => entry.RuleId == "rule#source:first");
        Assert.Contains(entries, entry => entry.RuleId == "rule#source:second" && entry.SourceRoot == secondSource);
    }

    [Fact]
    public void PathRule_ClampsInvalidRegexTimeoutToSafeDefault()
    {
        var rule = new PathRule
        {
            RegexTimeoutMs = 0,
            MatchPattern = @"^(?<file>.+)$",
            TargetTemplate = "Mapped/{file}"
        };

        Assert.Equal(500, rule.RegexTimeoutMs);
        Assert.Null(rule.RegexError);
        Assert.True(rule.RegexPattern.Match("report.txt").Success);
    }

    private static PathMapper CreateMapper(string sourceRoot, string targetRoot, string targetTemplate)
    {
        var settings = new SyncOptions
        {
            SourceRoot = sourceRoot,
            TargetRoots = new[] { targetRoot },
            FileExtensions = Array.Empty<string>(),
            PathRules = new List<PathRule>
            {
                new()
                {
                    MatchPattern = @"^A1[\\/](?<fileName>.+)$",
                    TargetTemplate = targetTemplate,
                    RegexTimeoutMs = 500
                }
            }
        };

        return new PathMapper(settings, new ListLogger(), new PathTemplateRenderer());
    }
}
