namespace FileTransfer.Tests;

public class InfrastructureTests
{
    [Fact]
    public void ResolvedTargetPathStateStore_UpsertLoadRemoveAndClear_WorkAsExpected()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var databasePath = temp.GetPath(Path.Combine("state", "resolved-target-paths.db"));
        var source = temp.GetPath("source.txt");
        var targetRoot = temp.CreateDir("target");
        var targetPath = Path.Combine(targetRoot, "Mapped", "source.txt");
        File.WriteAllText(source, "payload");
        var creationUtc = new FileInfo(source).CreationTimeUtc;

        using var store = new ResolvedTargetPathStateStore(logger, databasePath);
        store.Upsert(new PersistedResolvedTargetPathEntry(source, targetRoot, targetPath, creationUtc));

        var loaded = store.LoadEntries();
        var entry = Assert.Single(loaded);
        Assert.Equal(source, entry.SourceRoot);
        Assert.Equal(targetRoot, entry.TargetRoot);
        Assert.Equal(targetPath, entry.Path);

        store.RemoveSourceRoot(source);
        Assert.Empty(store.LoadEntries());

        store.Upsert(new PersistedResolvedTargetPathEntry(source, targetRoot, targetPath, creationUtc));
        Assert.Single(store.LoadEntries());

        store.Clear();
        Assert.Empty(store.LoadEntries());
    }

    [Fact]
    public void ResolvedTargetPathStateStore_Clear_PersistsEmptyState()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var statePath = temp.GetPath(Path.Combine("state", "resolved-target-paths"));
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath + ".snapshot", "snapshot");
        File.WriteAllText(statePath + ".journal", "journal");

        var source = temp.GetPath("source.txt");
        var targetRoot = temp.CreateDir("target");
        File.WriteAllText(source, "payload");
        var creationUtc = new FileInfo(source).CreationTimeUtc;

        using (var store = new ResolvedTargetPathStateStore(logger, statePath))
        {
            store.Upsert(new PersistedResolvedTargetPathEntry(source, targetRoot, Path.Combine(targetRoot, "source.txt"), creationUtc));
            store.Clear();
        }

        using var reloaded = new ResolvedTargetPathStateStore(logger, statePath);
        Assert.Empty(reloaded.LoadEntries());
    }

    [Fact]
    public void ResolvedTargetPathStateStore_LoadEntries_ResetsCorruptJournalState()
    {
        using var culture = TestCultureScope.Use("ja");
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var statePath = temp.GetPath(Path.Combine("state", "resolved-target-paths"));
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath + ".snapshot", "not-json");

        using var store = new ResolvedTargetPathStateStore(logger, statePath);

        var entries = store.LoadEntries();

        Assert.Empty(entries);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("読み込みに失敗しました"));
        Assert.False(File.Exists(statePath + ".snapshot"));

        var sourcePath = temp.GetPath("source.txt");
        var targetRoot = temp.CreateDir("target");
        var targetPath = Path.Combine(targetRoot, "Mapped", "source.txt");
        File.WriteAllText(sourcePath, "payload");
        var creationUtc = new FileInfo(sourcePath).CreationTimeUtc;

        store.Upsert(new PersistedResolvedTargetPathEntry(sourcePath, targetRoot, targetPath, creationUtc));
        Assert.Single(store.LoadEntries());
    }

    [Fact]
    public void ResolvedTargetPathStateStore_LoadEntries_PrunesMissingAndStaleSources()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var databasePath = temp.GetPath(Path.Combine("state", "resolved-target-paths.db"));
        using var store = new ResolvedTargetPathStateStore(logger, databasePath);

        var liveSource = temp.GetPath("live.txt");
        File.WriteAllText(liveSource, "live");
        var liveCreationUtc = new FileInfo(liveSource).CreationTimeUtc;

        var missingSource = temp.GetPath("missing.txt");
        var staleSource = temp.GetPath("stale.txt");
        File.WriteAllText(staleSource, "after");
        var staleCurrentCreationUtc = new FileInfo(staleSource).CreationTimeUtc;

        var targetRoot = temp.CreateDir("target");
        store.Upsert(new PersistedResolvedTargetPathEntry(liveSource, targetRoot, Path.Combine(targetRoot, "live.txt"), liveCreationUtc));
        store.Upsert(new PersistedResolvedTargetPathEntry(missingSource, targetRoot, Path.Combine(targetRoot, "missing.txt"), DateTime.UtcNow));
        store.Upsert(new PersistedResolvedTargetPathEntry(staleSource, targetRoot, Path.Combine(targetRoot, "stale.txt"), staleCurrentCreationUtc.AddSeconds(-5)));

        var loaded = store.LoadEntries();
        var entry = Assert.Single(loaded);
        Assert.Equal(liveSource, entry.SourceRoot);

        var reloaded = store.LoadEntries();
        var persisted = Assert.Single(reloaded);
        Assert.Equal(liveSource, persisted.SourceRoot);
    }

    [Fact]
    public void FileOperationErrorHandler_LogsWithoutContext()
    {
        using var culture = TestCultureScope.Use("ja");
        var logger = new ListLogger();
        var exception = new IOException("boom");

        FileOperationErrorHandler.Log(logger, exception, "コピー", @"C:\a.txt");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(exception, entry.Exception);
        Assert.Contains("コピー に失敗しました: C:\\a.txt", entry.Message);
    }

    [Fact]
    public void FileOperationErrorHandler_LogsWithContext()
    {
        using var culture = TestCultureScope.Use("ja");
        var logger = new ListLogger();
        var exception = new IOException("boom");

        FileOperationErrorHandler.Log(logger, exception, "コピー", @"C:\a.txt", "retry exhausted");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("retry exhausted", entry.Message);
    }

    [Fact]
    public async Task ConfigurationSyncOptionsProvider_CurrentAndReload_ReturnClones()
    {
        using var temp = new TempRoot();
        var configPath = temp.GetPath("appsettings.yaml");
        await File.WriteAllTextAsync(configPath, """
        version: 2
        rules:
          - id: test
            sourceRoots:
              - C:\Inbox
            targetRoots:
              - D:\Archive
            watchEvents:
              created: true
              changed: true
              deleted: false
        """, TestContext.Current.CancellationToken);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(temp.RootPath)
            .AddYamlFile("appsettings.yaml", optional: false, reloadOnChange: false)
            .Build();

        using var provider = new ConfigurationSyncOptionsProvider(configuration);
        var first = provider.Current.Single();
        first.SourceRoot = @"C:\Mutated";

        Assert.Equal(@"C:\Inbox", provider.Current.Single().SourceRoot);

        IReadOnlyList<SyncOptions>? changed = null;
        provider.OptionsChanged += settings => changed = settings;

        await File.WriteAllTextAsync(configPath, """
        version: 2
        rules:
          - id: test
            sourceRoots:
              - C:\Reloaded
            targetRoots:
              - E:\Mirror
            watchEvents:
              created: false
              changed: true
              deleted: true
        """, TestContext.Current.CancellationToken);

        ((IConfigurationRoot)configuration).Reload();
        await AsyncAssert.WaitForConditionAsync(() => changed is not null, TimeSpan.FromSeconds(2));

        Assert.NotNull(changed);
        var changedRule = changed!.Single();
        Assert.Equal(@"C:\Reloaded", changedRule.SourceRoot);
        Assert.Equal(@"E:\Mirror", changedRule.TargetRoots[0]);
        Assert.False(changedRule.WatchEvents.Created);
        Assert.True(changedRule.WatchEvents.Deleted);

        changedRule.SourceRoot = @"C:\ChangedAgain";
        Assert.Equal(@"C:\Reloaded", provider.Current.Single().SourceRoot);
    }

    [Fact]
    public async Task TransferConcurrencyGate_UpdateLimit_AllowsAdditionalConcurrentAcquire()
    {
        using var gate = new TransferConcurrencyGate(1);
        using var first = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        gate.UpdateLimit(2);

        using var second = await gate.AcquireAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task TransferConcurrencyGate_CanceledAcquire_DoesNotBreakSubsequentAcquire()
    {
        using var gate = new TransferConcurrencyGate(1);
        using var first = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AcquireAsync(cts.Token));
        first.Dispose();

        using var second = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(second);
    }

    [Fact]
    public void TargetHealthRegistry_TracksStateAndPreferredTarget()
    {
        using var culture = TestCultureScope.Use("ja");
        var logger = new ListLogger();
        var registry = new TargetHealthRegistry(logger);
        var changes = new List<(string Target, bool Healthy)>();
        registry.StateChanged += (target, healthy) => changes.Add((target, healthy));

        registry.Initialize(new[] { @"D:\A", "", @"E:\B" });
        registry.Update(@"D:\A", false, "disk offline");
        registry.Update(@"D:\A", true, "recovered");

        Assert.Equal(4, changes.Count);
        Assert.True(registry.IsHealthy(@"E:\B"));
        Assert.True(registry.AnyHealthyTarget(new[] { @"D:\A", @"E:\B" }));
        Assert.Equal(@"D:\A", registry.GetPreferredTarget(new[] { @"D:\A", @"E:\B" }));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("状態が変化しました"));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("状態が回復しました"));
    }

    [Fact]
    public void StartupInventoryReporter_LogsSummariesOnceAndSkipsTrashFromTargetSummary()
    {
        using var culture = TestCultureScope.Use("ja");
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var source = temp.CreateDir("source");
        var target = temp.CreateDir("target");
        var targetData = Path.Combine(target, "live");
        var trash = Path.Combine(target, ".trash");
        Directory.CreateDirectory(targetData);
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(source, "one.txt"), "abc");
        File.WriteAllText(Path.Combine(targetData, "two.txt"), "12345");
        File.WriteAllText(Path.Combine(trash, "old.txt"), "should-not-count-in-target-summary");

        var reporter = new StartupInventoryReporter(logger);
        var settings = new SyncOptions
        {
            SourceRoot = source,
            TargetRoots = new[] { target },
            BackupDeletedTargetsToTrash = true
        };

        reporter.LogIfNeeded(settings);
        reporter.LogIfNeeded(settings);

        var infoEntries = logger.Entries.Where(entry => entry.Level == LogLevel.Information).ToArray();
        Assert.Equal(3, infoEntries.Length);
        Assert.Contains(infoEntries, entry => entry.Message.Contains("監視元フォルダー"));
        Assert.Contains(infoEntries, entry => entry.Message.Contains("転送先フォルダー (1)") && entry.Message.Contains("サブディレクトリ=1 件") && entry.Message.Contains("ファイル=1 件"));
        Assert.Contains(infoEntries, entry => entry.Message.Contains(".trash フォルダー") && entry.Message.Contains("不要データ"));
    }

    [Fact]
    public void StartupInventoryReporter_DoesNotLogTrashSummaryWhenTrashBackupDisabled()
    {
        using var culture = TestCultureScope.Use("ja");
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var source = temp.CreateDir("source");
        var target = temp.CreateDir("target");
        var targetData = Path.Combine(target, "live");
        var trash = Path.Combine(target, ".trash");
        Directory.CreateDirectory(targetData);
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(source, "one.txt"), "abc");
        File.WriteAllText(Path.Combine(targetData, "two.txt"), "12345");
        File.WriteAllText(Path.Combine(trash, "old.txt"), "should-not-be-logged");

        var reporter = new StartupInventoryReporter(logger);
        reporter.LogIfNeeded(new SyncOptions
        {
            SourceRoot = source,
            TargetRoots = new[] { target },
            BackupDeletedTargetsToTrash = false
        });

        var infoEntries = logger.Entries.Where(entry => entry.Level == LogLevel.Information).ToArray();
        Assert.Equal(2, infoEntries.Length);
        Assert.Contains(infoEntries, entry => entry.Message.Contains("監視元フォルダー"));
        Assert.Contains(infoEntries, entry => entry.Message.Contains("転送先フォルダー (1)"));
        Assert.DoesNotContain(infoEntries, entry => entry.Message.Contains(".trash フォルダー"));
    }

    [Fact]
    public void StartupInventoryReporter_AllowsEmptyTargetRoots()
    {
        using var culture = TestCultureScope.Use("ja");
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var source = temp.CreateDir("source");
        File.WriteAllText(Path.Combine(source, "one.txt"), "abc");

        var reporter = new StartupInventoryReporter(logger);
        reporter.LogIfNeeded(new SyncOptions
        {
            SourceRoot = source,
            TargetRoots = Array.Empty<string>()
        });

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("監視元フォルダー"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("起動時インベントリの収集に失敗しました"));
    }
}

public partial class AdditionalInfrastructureTests
{
    [Fact]
    public void ResolvedTargetPathCache_ReusesDynamicRelativePathAcrossTargetRoots()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var sourceRoot = temp.CreateDir("source");
        var firstTargetRoot = temp.CreateDir("target1");
        var secondTargetRoot = temp.CreateDir("target2");
        var sourceDir = Path.Combine(sourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "report.txt");
        File.WriteAllText(sourceFile, "payload");

        var options = new SyncOptions
        {
            SourceRoot = sourceRoot,
            TargetRoots = new[] { firstTargetRoot, secondTargetRoot },
            FileExtensions = Array.Empty<string>(),
            PathRules = new List<PathRule>
            {
                new()
                {
                    MatchPattern = @"^A1[\\/](?<file>.+)$",
                    TargetTemplate = "Rendered/{guid:N}/{file}"
                }
            }
        };

        var mapper = new PathMapper(options, logger, new PathTemplateRenderer());
        var store = new ResolvedTargetPathStateStore(logger, temp.GetPath(Path.Combine("state", "resolved-target-paths")));
        using var cache = new ResolvedTargetPathCache(logger, store);

        Assert.True(cache.TryResolve(sourceFile, firstTargetRoot, mapper, out var firstTargetPath));
        var deleteTargetPath = cache.ResolveForDelete(sourceFile, secondTargetRoot, mapper);
        Assert.True(cache.TryResolve(sourceFile, secondTargetRoot, mapper, out var secondTargetPath));

        var firstRelative = PathHelper.NormalizeForMatch(Path.GetRelativePath(firstTargetRoot, firstTargetPath));
        var deleteRelative = PathHelper.NormalizeForMatch(Path.GetRelativePath(secondTargetRoot, deleteTargetPath));
        var secondRelative = PathHelper.NormalizeForMatch(Path.GetRelativePath(secondTargetRoot, secondTargetPath));
        Assert.Equal(firstRelative, deleteRelative);
        Assert.Equal(firstRelative, secondRelative);
    }

    [Fact]
    public void TargetHealthRegistry_SyncTargets_PreservesExistingUnhealthyState()
    {
        var logger = new ListLogger();
        var registry = new TargetHealthRegistry(logger);
        registry.Initialize(new[] { "primary", "secondary" });
        registry.Update("primary", false, "offline");

        registry.SyncTargets(new[] { "primary", "tertiary" });

        Assert.False(registry.IsHealthy("primary"));
        Assert.True(registry.IsHealthy("tertiary"));
        Assert.True(registry.IsHealthy("secondary"));
    }
    [Fact]
    public void AppPathsOptions_FromConfiguration_UsesConfiguredDirectories()
    {
        using var temp = new TempRoot();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Paths:StateDir"] = "custom-state",
                ["Paths:LogDir"] = "custom-logs"
            })
            .Build();

        var options = AppPathsOptions.FromConfiguration(config, temp.RootPath, temp.GetPath("appsettings.yaml"));

        Assert.Equal(Path.GetFullPath(temp.GetPath("custom-state")), options.StateDir);
        Assert.Equal(Path.GetFullPath(temp.GetPath("custom-logs")), options.LogDir);
        Assert.Equal(Path.Combine(options.StateDir, "resolved-target-paths"), options.ResolvedTargetPathStatePath);
        Assert.Equal(Path.Combine(options.LogDir, "app_20260122.log"), options.ResolveLogFile("app_{0:yyyyMMdd}.log", new DateTime(2026, 1, 22)));
    }

    [Fact]
    public void PathKeyComparer_FollowsCurrentPlatformPathSemantics()
    {
        var left = "Sample.TXT";
        var right = "sample.txt";

        if (OperatingSystem.IsWindows())
        {
            Assert.True(PathKeyComparer.Equals(left, right));
        }
        else
        {
            Assert.False(PathKeyComparer.Equals(left, right));
        }
    }

}
