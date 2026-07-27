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

}
