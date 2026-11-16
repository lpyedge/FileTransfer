namespace FileTransfer.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task ReconciliationManager_QueuesMissingFileWhenHealthyTargetAvailable()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var settings = new SyncOptions
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.CreateDir("target") },
            FileExtensions = Array.Empty<string>(),
            IncludeSubdirectories = true,
            ReconciliationIntervalMs = 1,
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = false
            }
        };
        var sourceFile = Path.Combine(settings.SourceRoot, "pending.txt");
        await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

        var healthRegistry = new TargetHealthRegistry(logger);
        healthRegistry.Initialize(settings.TargetRoots);
        var mapper = new PathMapper(settings, logger, new PathTemplateRenderer());
        var queued = new ConcurrentQueue<(string SourceRoot, string Label)>();
        var completedRuns = 0;

        using var manager = new ReconciliationManager(
            logger,
            () => settings,
            (string sourcePath, string targetRoot, out string targetPath) =>
            {
                var resolution = mapper.ResolveTargetPathForRoot(sourcePath, targetRoot);
                targetPath = resolution.TargetPath;
                return !resolution.Ignored;
            },
            (path, label, _) => queued.Enqueue((path, label)),
            healthRegistry,
            () => Interlocked.Increment(ref completedRuns));

        manager.Configure(settings, TestContext.Current.CancellationToken);
        await AsyncAssert.WaitForConditionAsync(() => queued.Count == 1 && Volatile.Read(ref completedRuns) > 0, TimeSpan.FromSeconds(3));

        Assert.True(queued.TryDequeue(out var queuedEntry));
        Assert.Equal(sourceFile, queuedEntry.SourceRoot);
        Assert.Equal("Reconcile", queuedEntry.Label);
    }

    [Fact]
    public async Task ReconciliationManager_SkipsWhenAllTargetsAreInBackoff()
    {
        using var culture = TestCultureScope.Use("ja");
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var settings = new SyncOptions
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.GetPath("missing-target") },
            ReconciliationIntervalMs = 1,
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = false
            }
        };
        await File.WriteAllTextAsync(Path.Combine(settings.SourceRoot, "pending.txt"), "payload", TestContext.Current.CancellationToken);

        var healthRegistry = new TargetHealthRegistry(logger);
        healthRegistry.Initialize(settings.TargetRoots);
        healthRegistry.RecordFailure(
            settings.TargetRoots[0],
            DateTimeOffset.UtcNow,
            initialDelayMs: 60_000,
            maxDelayMs: 60_000,
            reason: "offline");
        var mapper = new PathMapper(settings, logger, new PathTemplateRenderer());
        var queued = new ConcurrentQueue<(string SourceRoot, string Label)>();
        var completedRuns = 0;

        using var manager = new ReconciliationManager(
            logger,
            () => settings,
            (string sourcePath, string targetRoot, out string targetPath) =>
            {
                var resolution = mapper.ResolveTargetPathForRoot(sourcePath, targetRoot);
                targetPath = resolution.TargetPath;
                return !resolution.Ignored;
            },
            (path, label, _) => queued.Enqueue((path, label)),
            healthRegistry,
            () => Interlocked.Increment(ref completedRuns));

        manager.Configure(settings, TestContext.Current.CancellationToken);
        await AsyncAssert.WaitForConditionAsync(
            () => Volatile.Read(ref completedRuns) > 0,
            TimeSpan.FromSeconds(3));

        Assert.Empty(queued);
    }

    [Fact]
    public async Task ReconciliationManager_RequestRunCoversAllFilesAcrossLimitedBatches()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var settings = new SyncOptions
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.CreateDir("target") },
            FileExtensions = Array.Empty<string>(),
            IncludeSubdirectories = true,
            // Periodic runs effectively never fire; only the lost-events request drives the pass.
            ReconciliationIntervalMs = 3_600_000,
            MaxReconciliationFilesPerRun = 2,
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = false
            }
        };
        for (var i = 0; i < 7; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(settings.SourceRoot, $"file{i}.txt"), "payload", TestContext.Current.CancellationToken);
        }

        var healthRegistry = new TargetHealthRegistry(logger);
        healthRegistry.Initialize(settings.TargetRoots);
        var mapper = new PathMapper(settings, logger, new PathTemplateRenderer());
        var queued = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using var manager = new ReconciliationManager(
            logger,
            () => settings,
            (string sourcePath, string targetRoot, out string targetPath) =>
            {
                var resolution = mapper.ResolveTargetPathForRoot(sourcePath, targetRoot);
                targetPath = resolution.TargetPath;
                return !resolution.Ignored;
            },
            (path, _, _) => queued.AddOrUpdate(path, 1, (_, n) => n + 1),
            healthRegistry,
            () => { });

        manager.Configure(settings, TestContext.Current.CancellationToken);
        manager.RequestRun("test");

        await AsyncAssert.WaitForConditionAsync(() => queued.Count == 7, TimeSpan.FromSeconds(20));
        Assert.All(queued.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ReconciliationManager_QueuesOnlyMissingOrStaleTargetsUsingDirectoryListing()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var settings = new SyncOptions
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.CreateDir("target") },
            FileExtensions = Array.Empty<string>(),
            IncludeSubdirectories = true,
            ReconciliationIntervalMs = 1,
            WatchEvents = new WatchEventOptions { Created = true, Changed = true, Deleted = false }
        };
        var ct = TestContext.Current.CancellationToken;
        var sourceTime = DateTime.UtcNow.AddMinutes(-10);

        async Task WriteAsync(string root, string name, string content, DateTime mtime)
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path, content, ct);
            File.SetLastWriteTimeUtc(path, mtime);
        }

        await WriteAsync(settings.SourceRoot, "current.txt", "same", sourceTime);
        await WriteAsync(settings.TargetRoots[0], "current.txt", "same", sourceTime);
        await WriteAsync(settings.SourceRoot, "stale-size.txt", "new-content", sourceTime);
        await WriteAsync(settings.TargetRoots[0], "stale-size.txt", "old", sourceTime);
        await WriteAsync(settings.SourceRoot, "stale-time.txt", "same", sourceTime);
        await WriteAsync(settings.TargetRoots[0], "stale-time.txt", "same", sourceTime.AddMinutes(-5));
        await WriteAsync(settings.SourceRoot, "missing.txt", "x", sourceTime);

        var healthRegistry = new TargetHealthRegistry(logger);
        healthRegistry.Initialize(settings.TargetRoots);
        var mapper = new PathMapper(settings, logger, new PathTemplateRenderer());
        var queued = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var completedRuns = 0;

        using var manager = new ReconciliationManager(
            logger,
            () => settings,
            (string sourcePath, string targetRoot, out string targetPath) =>
            {
                var resolution = mapper.ResolveTargetPathForRoot(sourcePath, targetRoot);
                targetPath = resolution.TargetPath;
                return !resolution.Ignored;
            },
            (path, _, _) => queued.TryAdd(Path.GetFileName(path), 0),
            healthRegistry,
            () => Interlocked.Increment(ref completedRuns));

        manager.Configure(settings, ct);
        await AsyncAssert.WaitForConditionAsync(() => Volatile.Read(ref completedRuns) > 0, TimeSpan.FromSeconds(5));

        Assert.Equal(
            new[] { "missing.txt", "stale-size.txt", "stale-time.txt" },
            queued.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(@"\\server\share\in", 262144, 65536)]
    [InlineData(@"\\server\share\in", 32768, 32768)]
    [InlineData(@"C:\local\in", 262144, 262144)]
    [InlineData(@"C:\local\in", 0, 65536)]
    public void FileWatcherManager_ClampsInternalBufferForNetworkPaths(string root, int configured, int expected)
    {
        Assert.Equal(expected, FileWatcherManager.GetEffectiveInternalBufferSize(root, configured));
    }

    [Fact]
    public async Task FileWatcherManager_DispatchesEventsOffTheWatcherThread()
    {
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var settings = new SyncOptions
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.CreateDir("target") },
            FileExtensions = Array.Empty<string>(),
            IncludeSubdirectories = true,
            WatchEvents = new WatchEventOptions { Created = true, Changed = true, Deleted = false }
        };
        var received = new ConcurrentQueue<string>();
        using var release = new ManualResetEventSlim(false);

        using var watcher = new FileWatcherManager(
            logger,
            () => settings,
            (_, path, _) =>
            {
                received.Enqueue(path);
                // A slow consumer must not block further notifications from being accepted.
                release.Wait(TimeSpan.FromSeconds(5));
            },
            _ => { });
        watcher.Update(settings);

        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(settings.SourceRoot, $"f{i}.txt"), "x", TestContext.Current.CancellationToken);
        }

        await AsyncAssert.WaitForConditionAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(5));
        release.Set();
        await AsyncAssert.WaitForConditionAsync(
            () => received.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
            TimeSpan.FromSeconds(5));
    }
}
