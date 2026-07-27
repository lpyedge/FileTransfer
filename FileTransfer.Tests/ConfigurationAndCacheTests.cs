namespace FileTransfer.Tests;

public class ConfigurationAndCacheTests
{
    [Fact]
    public void MaliciousResolvedTargetStateOutsideRoot_IsIgnored()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var sourceRoot = temp.CreateDir("source-malicious");
        var targetRoot = temp.CreateDir("target-malicious");
        var outsideRoot = temp.CreateDir("outside");
        var source = Path.Combine(sourceRoot, "report.txt");
        var outside = Path.Combine(outsideRoot, "victim.txt");
        File.WriteAllText(source, "source");
        File.WriteAllText(outside, "outside");
        var statePath = temp.GetPath(Path.Combine("state-malicious", "resolved-target-paths"));
        using var store = new ResolvedTargetPathStateStore(logger, statePath);
        store.Upsert(new PersistedResolvedTargetPathEntry(source, targetRoot, outside, new FileInfo(source).CreationTimeUtc));
        using var cache = new ResolvedTargetPathCache(logger, store);
        cache.Restore();
        var options = new SyncOptions { SourceRoot = sourceRoot, TargetRoots = new[] { targetRoot }, FileExtensions = Array.Empty<string>() };
        var mapper = new PathMapper(options, logger, new PathTemplateRenderer());

        var resolved = cache.ResolveForDelete(source, targetRoot, mapper);

        Assert.True(PathHelper.IsStrictChildPath(targetRoot, resolved));
        Assert.True(File.Exists(outside));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(outside));
    }

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
    public void Restore_NormalizesPersistedSourceAndTargetKeys()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var sourceRoot = temp.CreateDir("source-normalized");
        var targetRoot = temp.CreateDir("target-normalized");
        var sourceDirectory = Path.Combine(sourceRoot, "A1");
        Directory.CreateDirectory(sourceDirectory);
        var normalizedSource = Path.GetFullPath(Path.Combine(sourceDirectory, "report.txt"));
        File.WriteAllText(normalizedSource, "source");
        var cachedDirectory = Path.Combine(targetRoot, "Cached");
        Directory.CreateDirectory(cachedDirectory);
        var normalizedCachedPath = Path.GetFullPath(Path.Combine(cachedDirectory, "result.txt"));
        var persistedSource = Path.Combine(sourceRoot, ".", "A1", "sub", "..", "report.txt");
        var persistedTargetRoot = Path.Combine(targetRoot, "level", "..");
        var persistedTargetPath = Path.Combine(targetRoot, "Cached", ".", "result.txt");
        using var store = new ResolvedTargetPathStateStore(
            logger,
            temp.GetPath(Path.Combine("state-normalized", "resolved-target-paths")));
        store.Upsert(new PersistedResolvedTargetPathEntry(
            persistedSource,
            persistedTargetRoot,
            persistedTargetPath,
            new FileInfo(normalizedSource).CreationTimeUtc));
        using var cache = new ResolvedTargetPathCache(logger, store);

        cache.Restore();
        var mapper = CreateMapper(sourceRoot, targetRoot, "Mapped/{fileName}");
        var resolved = cache.ResolveForDelete(normalizedSource, Path.GetFullPath(targetRoot), mapper);

        Assert.Equal(normalizedCachedPath, resolved);
    }

    [Fact]
    public void Restore_InvalidPersistedPath_DoesNotAbortOtherEntries()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var sourceRoot = temp.CreateDir("source-mixed");
        var targetRoot = temp.CreateDir("target-mixed");
        var sourceDirectory = Path.Combine(sourceRoot, "A1");
        Directory.CreateDirectory(sourceDirectory);
        var validSource = Path.Combine(sourceDirectory, "valid.txt");
        File.WriteAllText(validSource, "valid");
        var validCachedPath = Path.Combine(targetRoot, "Cached", "valid.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(validCachedPath)!);
        var invalidTargetRoot = "\0invalid-target";
        using var store = new ResolvedTargetPathStateStore(
            logger,
            temp.GetPath(Path.Combine("state-mixed", "resolved-target-paths")));
        store.Upsert(new PersistedResolvedTargetPathEntry(
            validSource,
            invalidTargetRoot,
            Path.Combine(targetRoot, "Cached", "invalid.txt"),
            new FileInfo(validSource).CreationTimeUtc));
        store.Upsert(new PersistedResolvedTargetPathEntry(
            validSource,
            targetRoot,
            validCachedPath,
            new FileInfo(validSource).CreationTimeUtc));
        using var cache = new ResolvedTargetPathCache(logger, store);

        cache.Restore();
        var mapper = CreateMapper(sourceRoot, targetRoot, "Mapped/{fileName}");
        var resolved = cache.ResolveForDelete(validSource, targetRoot, mapper);

        Assert.Equal(Path.GetFullPath(validCachedPath), resolved);
        Assert.DoesNotContain(store.LoadEntries(), entry => entry.TargetRoot == invalidTargetRoot);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("invalid"));
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
