namespace FileTransfer.Tests;

public class ProductionFeatureTests
{
    [Fact]
    public async Task AllHealthyTargets_CopiesToEveryHealthyTarget()
    {
        using var temp = new TempRoot();
        var settings = CreateRule(temp, "mirror");
        var secondary = temp.CreateDir("target-secondary");
        settings.TargetRoots = new[] { settings.TargetRoots[0], secondary };
        settings.TargetMode = TargetMode.AllHealthyTargets;

        await WithServiceAsync(new[] { settings }, async _ =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "mirror.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var primaryDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_mirror.txt");
            var secondaryDest = Path.Combine(settings.TargetRoots[1], "Mapped", "M_mirror.txt");
            await WaitForFileContentAsync(primaryDest, "payload", TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(secondaryDest, "payload", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task FirstAvailable_CopiesOnlyToFirstHealthyTarget()
    {
        using var temp = new TempRoot();
        var settings = CreateRule(temp, "failover");
        var secondary = temp.CreateDir("target-secondary");
        settings.TargetRoots = new[] { settings.TargetRoots[0], secondary };
        settings.TargetMode = TargetMode.FirstAvailable;
        settings.ReconciliationIntervalMs = 300;

        await WithServiceAsync(new[] { settings }, async _ =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "failover.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var primaryDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_failover.txt");
            var secondaryDest = Path.Combine(settings.TargetRoots[1], "Mapped", "M_failover.txt");
            await WaitForFileContentAsync(primaryDest, "payload", TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(900), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(secondaryDest));
        });
    }

    [Fact]
    public async Task DoneFileReadySignal_WaitsForMarkerBeforeCopy()
    {
        using var temp = new TempRoot();
        var settings = CreateRule(temp, "done-file");
        settings.ReadySignal = new ReadySignalOptions
        {
            Mode = ReadySignalMode.DoneFile,
            StableChecks = 1,
            IntervalMs = 50,
            TimeoutMs = 3000,
            RequireReadable = true,
            DoneFileSuffix = ".done"
        };

        await WithServiceAsync(new[] { settings }, async _ =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "marker.txt");
            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_marker.txt");

            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(destFile));

            await File.WriteAllTextAsync(sourceFile + ".done", string.Empty, TestContext.Current.CancellationToken);
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task RenameEvent_IsProcessedWhenCreatedEnabledAndChangedDisabled()
    {
        using var temp = new TempRoot();
        var settings = CreateRule(temp, "rename-only");
        settings.ReadySignal = new ReadySignalOptions
        {
            Mode = ReadySignalMode.RenameOnly,
            StableChecks = 1,
            IntervalMs = 50,
            TimeoutMs = 3000
        };
        settings.WatchEvents = new WatchEventOptions
        {
            Created = true,
            Changed = false,
            Deleted = false
        };

        await WithServiceAsync(new[] { settings }, async _ =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var tempFile = Path.Combine(sourceDir, "renamed.tmp");
            var finalFile = Path.Combine(sourceDir, "renamed.txt");
            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_renamed.txt");

            await File.WriteAllTextAsync(tempFile, "payload", TestContext.Current.CancellationToken);
            File.Move(tempFile, finalFile);

            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task HashComparison_StoresFingerprintHash()
    {
        using var temp = new TempRoot();
        var settings = CreateRule(temp, "hash");
        settings.ComparisonMode = ComparisonMode.Hash;
        settings.WatchEvents.Deleted = true;

        await WithServiceAsync(new[] { settings }, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "hash.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_hash.txt");
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(
                () => TryGetFingerprint(service, sourceFile, out var fingerprint) && GetFingerprintHash(fingerprint!) is { Length: > 0 },
                TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task MultipleRules_RunIndependently()
    {
        using var temp = new TempRoot();
        var first = CreateRule(temp, "first");
        var second = CreateRule(temp, "second");
        var firstSourceDir = Path.Combine(first.SourceRoot, "A1");
        var secondSourceDir = Path.Combine(second.SourceRoot, "A1");
        Directory.CreateDirectory(firstSourceDir);
        Directory.CreateDirectory(secondSourceDir);

        await WithServiceAsync(new[] { first, second }, async _ =>
        {
            await File.WriteAllTextAsync(Path.Combine(firstSourceDir, "one.txt"), "one", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(secondSourceDir, "two.txt"), "two", TestContext.Current.CancellationToken);

            await WaitForFileContentAsync(Path.Combine(first.TargetRoots[0], "Mapped", "M_one.txt"), "one", TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(Path.Combine(second.TargetRoots[0], "Mapped", "M_two.txt"), "two", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task InvalidReload_DoesNotModifyRunningRuntimes()
    {
        using var temp = new TempRoot();
        var active = CreateRule(temp, "active");

        await WithServiceAsync(new[] { active }, async (_, provider) =>
        {
            var firstSourceDirectory = Path.Combine(active.SourceRoot, "A1");
            Directory.CreateDirectory(firstSourceDirectory);
            var firstSource = Path.Combine(firstSourceDirectory, "before-invalid.txt");
            await File.WriteAllTextAsync(firstSource, "before", TestContext.Current.CancellationToken);
            await WaitForFileContentAsync(
                Path.Combine(active.TargetRoots[0], "Mapped", "M_before-invalid.txt"),
                "before",
                TimeSpan.FromSeconds(5));

            var invalidFirst = CreateRule(temp, "invalid-first");
            var invalidSecond = CreateRule(temp, "invalid-second");
            invalidFirst.RuntimeId = "duplicate-runtime";
            invalidSecond.RuntimeId = "duplicate-runtime";
            provider.RaiseChanged(new[] { invalidFirst, invalidSecond });

            var afterSource = Path.Combine(firstSourceDirectory, "after-invalid.txt");
            await File.WriteAllTextAsync(afterSource, "after", TestContext.Current.CancellationToken);
            await WaitForFileContentAsync(
                Path.Combine(active.TargetRoots[0], "Mapped", "M_after-invalid.txt"),
                "after",
                TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(Path.Combine(invalidFirst.TargetRoots[0], "Mapped", "M_after-invalid.txt")));
            Assert.False(File.Exists(Path.Combine(invalidSecond.TargetRoots[0], "Mapped", "M_after-invalid.txt")));
        });
    }

    [Fact]
    public async Task RapidReloads_FinalRuntimeUsesLatestConfiguration()
    {
        using var temp = new TempRoot();
        var initial = CreateRule(temp, "rapid");
        var intermediate = initial.Clone();
        var final = initial.Clone();
        var intermediateTarget = temp.CreateDir("rapid-intermediate-target");
        var finalTarget = temp.CreateDir("rapid-final-target");
        intermediate.TargetRoots = [intermediateTarget];
        final.TargetRoots = [finalTarget];

        await WithServiceAsync(new[] { initial }, async (_, provider) =>
        {
            provider.RaiseChanged(new[] { intermediate });
            provider.RaiseChanged(new[] { final });

            var sourceDirectory = Path.Combine(initial.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "latest-only.txt");
            await File.WriteAllTextAsync(sourcePath, "latest", TestContext.Current.CancellationToken);

            await WaitForFileContentAsync(
                Path.Combine(finalTarget, "Mapped", "M_latest-only.txt"),
                "latest",
                TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(Path.Combine(initial.TargetRoots[0], "Mapped", "M_latest-only.txt")));
            Assert.False(File.Exists(Path.Combine(intermediateTarget, "Mapped", "M_latest-only.txt")));
        });
    }

    [Fact]
    public async Task CrossRuleCycle_IsRejectedBeforeAnyRuntimeMutation()
    {
        using var temp = new TempRoot();
        var active = CreateRule(temp, "cycle-active");

        await WithServiceAsync(new[] { active }, async (_, provider) =>
        {
            var cycleA = CreateRule(temp, "cycle-a");
            var cycleB = CreateRule(temp, "cycle-b");
            cycleA.TargetRoots = [cycleB.SourceRoot];
            cycleB.TargetRoots = [cycleA.SourceRoot];

            Assert.False(SyncOptionsSetValidator.TryPrepareAll(
                new[] { cycleA, cycleB },
                new ListLogger(),
                new PathTemplateRenderer(),
                out var prepared));
            Assert.Empty(prepared);
            provider.RaiseChanged(new[] { cycleA, cycleB });

            var sourceDirectory = Path.Combine(active.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "still-active.txt");
            await File.WriteAllTextAsync(sourcePath, "active", TestContext.Current.CancellationToken);
            await WaitForFileContentAsync(
                Path.Combine(active.TargetRoots[0], "Mapped", "M_still-active.txt"),
                "active",
                TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(Path.Combine(cycleA.TargetRoots[0], "Mapped", "M_still-active.txt")));
            Assert.False(File.Exists(Path.Combine(cycleB.TargetRoots[0], "Mapped", "M_still-active.txt")));
        });
    }


    [Fact]
    public async Task Reconciliation_ReplacesStaleTargetUsingLengthAndTimestamp()
    {
        using var temp = new TempRoot();
        var settings = CreateRule(temp, "stale-reconcile");
        settings.ReconciliationIntervalMs = 100;
        settings.MaxReconciliationFilesPerRun = 100;
        settings.MaxReconciliationDurationMs = 5000;

        var sourceDir = Path.Combine(settings.SourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "stale.txt");
        await File.WriteAllTextAsync(sourceFile, "fresh-payload", TestContext.Current.CancellationToken);

        var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_stale.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        await File.WriteAllTextAsync(destFile, "old", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(destFile, DateTime.UtcNow.AddMinutes(-10));

        await WithServiceAsync(new[] { settings }, async _ =>
        {
            await WaitForFileContentAsync(destFile, "fresh-payload", TimeSpan.FromSeconds(8));
        });
    }

    private static SyncOptions CreateRule(TempRoot temp, string id) => new()
    {
        RuleId = id,
        SourceRoot = temp.CreateDir($"source-{id}"),
        TargetRoots = new[] { temp.CreateDir($"target-{id}") },
        TargetMode = TargetMode.FirstAvailable,
        FileExtensions = new[] { ".txt" },
        FileNamePrefix = "M_",
        IncludeSubdirectories = true,
        OverwriteExisting = true,
        ComparisonMode = ComparisonMode.LengthAndTimestamp,
        ReadySignal = new ReadySignalOptions
        {
            Mode = ReadySignalMode.StableSize,
            StableChecks = 1,
            IntervalMs = 50,
            TimeoutMs = 3000,
            RequireReadable = true
        },
        WatchEvents = new WatchEventOptions
        {
            Created = true,
            Changed = true,
            Deleted = false
        },
        PathRules = new List<PathRule>
        {
            new()
            {
                MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                TargetTemplate = "Mapped/{rest}",
                RegexTimeoutMs = 500
            }
        },
        InitialRetryDelayMs = 50,
        MaxRetryDelayMs = 200,
        OperationTimeoutMs = 10000,
        ReconciliationIntervalMs = 200,
        MaxParallelTransfers = 2
    };

    private static async Task WithServiceAsync(IReadOnlyList<SyncOptions> settings, Func<MainService, Task> action)
    {
        await WithServiceAsync(settings, (service, _) => action(service));
    }

    private static async Task WithServiceAsync(
        IReadOnlyList<SyncOptions> settings,
        Func<MainService, TestSyncOptionsProvider, Task> action)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
        var provider = new TestSyncOptionsProvider(settings);
        var appPaths = CreateTestAppPaths(settings[0]);
        var service = new MainService(loggerFactory.CreateLogger<MainService>(), provider, appPaths);
        var started = false;

        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            started = true;
            await action(service, provider);
        }
        finally
        {
            if (started)
            {
                await service.StopAsync(TestContext.Current.CancellationToken);
            }

            service.Dispose();
        }
    }

    private static AppPathsOptions CreateTestAppPaths(SyncOptions settings)
    {
        var tempRoot = Path.GetDirectoryName(settings.SourceRoot)!;
        var stateRoot = Path.Combine(tempRoot, "state");
        var logRoot = Path.Combine(tempRoot, "logs");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(logRoot);

        return new AppPathsOptions
        {
            StateDir = stateRoot,
            LogDir = logRoot,
            ConfigPath = Path.Combine(tempRoot, "appsettings.yaml")
        };
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Condition was not satisfied within {timeout}.");
    }

    private static async Task WaitForFileContentAsync(string path, string expected, TimeSpan timeout)
    {
        await WaitForConditionAsync(() =>
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path) == expected;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }, timeout);
    }

    private static bool TryGetFingerprint(MainService service, string sourcePath, out object? fingerprint)
    {
        fingerprint = null;
        var field = typeof(MainService).GetField("_transfers", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(service) is not FileTransferCoordinator coordinator)
        {
            return false;
        }

        if (coordinator.Fingerprints.TryGetValue(sourcePath, out var value))
        {
            fingerprint = value;
            return true;
        }

        return false;
    }

    private static string? GetFingerprintHash(object fingerprint)
    {
        var property = fingerprint.GetType().GetProperty("Hash", BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(fingerprint) as string;
    }

    private sealed class TestSyncOptionsProvider : ISyncOptionsProvider
    {
        private readonly IReadOnlyList<SyncOptions> _settings;

        public TestSyncOptionsProvider(IEnumerable<SyncOptions> settings)
        {
            _settings = settings.Select(setting => setting.Clone()).ToArray();
        }

        public IReadOnlyList<SyncOptions> Current => _settings.Select(setting => setting.Clone()).ToArray();

        public event Action<IReadOnlyList<SyncOptions>>? OptionsChanged;

        public void RaiseChanged(IEnumerable<SyncOptions> settings)
        {
            OptionsChanged?.Invoke(settings.Select(setting => setting.Clone()).ToArray());
        }
    }
}
