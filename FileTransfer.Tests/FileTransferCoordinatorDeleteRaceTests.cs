using System.Diagnostics.Metrics;

namespace FileTransfer.Tests;

public class FileTransferCoordinatorDeleteRaceTests
{
    [Fact]
    public async Task PresentSupersedesAbsentBeforeTrashMove_DestinationIsNotMoved()
    {
        using var temp = new TempRoot();
        using var fixture = CreateFixture(temp);
        var (sourcePath, destinationPath) = await fixture.CreateDeletedSourceWithTargetAsync("before-move.txt", "old");
        var moveEntered = NewSignal();
        var releaseMove = NewSignal();
        var deleteFinished = NewSignal();
        var afterMoveCalled = false;
        fixture.Coordinator.BeforeTrashMoveAsync = async (_, _) =>
        {
            moveEntered.TrySetResult();
            await releaseMove.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        fixture.Coordinator.AfterTrashMoveAsync = (_, _) =>
        {
            afterMoveCalled = true;
            return Task.CompletedTask;
        };
        fixture.Coordinator.AfterDeleteProcessingAsync = _ =>
        {
            deleteFinished.TrySetResult();
            return Task.CompletedTask;
        };

        fixture.Coordinator.ScheduleMirrorDelete(sourcePath, TestContext.Current.CancellationToken);
        await moveEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sourcePath, "new", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "recreated", TestContext.Current.CancellationToken);
        releaseMove.TrySetResult();

        await deleteFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitForContentAsync(destinationPath, "new");
        Assert.False(afterMoveCalled);
    }

    [Fact]
    public async Task PresentSupersedesAbsentAfterTrashMove_RestoresOldTargetBeforeLatestCommit()
    {
        using var temp = new TempRoot();
        using var fixture = CreateFixture(temp);
        var (sourcePath, destinationPath) = await fixture.CreateDeletedSourceWithTargetAsync("after-move.txt", "old");
        var moveCompleted = NewSignal();
        var releaseMove = NewSignal();
        var deleteFinished = NewSignal();
        var latestCommitEntered = NewSignal();
        var releaseCommit = NewSignal();
        fixture.Coordinator.AfterTrashMoveAsync = async (_, _) =>
        {
            moveCompleted.TrySetResult();
            await releaseMove.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        fixture.Coordinator.AfterDeleteProcessingAsync = _ =>
        {
            deleteFinished.TrySetResult();
            return Task.CompletedTask;
        };
        fixture.Coordinator.BeforeCommitAsync = async (_, _) =>
        {
            latestCommitEntered.TrySetResult();
            await releaseCommit.Task.WaitAsync(TestContext.Current.CancellationToken);
        };

        fixture.Coordinator.ScheduleMirrorDelete(sourcePath, TestContext.Current.CancellationToken);
        await moveCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(destinationPath));

        await File.WriteAllTextAsync(sourcePath, "new", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "recreated", TestContext.Current.CancellationToken);
        releaseMove.TrySetResult();

        await deleteFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await latestCommitEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));

        releaseCommit.TrySetResult();
        await WaitForContentAsync(destinationPath, "new");
    }

    [Fact]
    public async Task PresentSupersedesAbsentAfterTrashMove_DoesNotOverwriteNewDestinationDuringCompensation()
    {
        using var temp = new TempRoot();
        using var fixture = CreateFixture(temp);
        var (sourcePath, destinationPath) = await fixture.CreateDeletedSourceWithTargetAsync("preserve-new.txt", "old");
        var moveCompleted = NewSignal();
        var releaseMove = NewSignal();
        var deleteFinished = NewSignal();
        var latestCommitEntered = NewSignal();
        var releaseCommit = NewSignal();
        fixture.Coordinator.AfterTrashMoveAsync = async (_, _) =>
        {
            moveCompleted.TrySetResult();
            await releaseMove.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        fixture.Coordinator.AfterDeleteProcessingAsync = _ =>
        {
            deleteFinished.TrySetResult();
            return Task.CompletedTask;
        };
        fixture.Coordinator.BeforeCommitAsync = async (_, _) =>
        {
            latestCommitEntered.TrySetResult();
            await releaseCommit.Task.WaitAsync(TestContext.Current.CancellationToken);
        };

        fixture.Coordinator.ScheduleMirrorDelete(sourcePath, TestContext.Current.CancellationToken);
        await moveCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(destinationPath, "new-destination", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sourcePath, "latest-source", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "recreated", TestContext.Current.CancellationToken);
        releaseMove.TrySetResult();

        await deleteFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
        await latestCommitEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("new-destination", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));

        releaseCommit.TrySetResult();
        await WaitForContentAsync(destinationPath, "latest-source");
    }

    [Fact]
    public async Task UnknownSourcePresence_DoesNotMoveDestination()
    {
        using var temp = new TempRoot();
        using var fixture = CreateFixture(temp);
        var (sourcePath, destinationPath) = await fixture.CreateDeletedSourceWithTargetAsync("unknown.txt", "old");
        var deleteFinished = NewSignal();
        var moveCalled = false;
        fixture.Coordinator.SourcePresenceProbe = _ => FileTransferCoordinator.SourcePresence.Unknown;
        fixture.Coordinator.BeforeTrashMoveAsync = (_, _) =>
        {
            moveCalled = true;
            return Task.CompletedTask;
        };
        fixture.Coordinator.AfterDeleteProcessingAsync = _ =>
        {
            deleteFinished.TrySetResult();
            return Task.CompletedTask;
        };

        fixture.Coordinator.ScheduleMirrorDelete(sourcePath, TestContext.Current.CancellationToken);
        await deleteFinished.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(moveCalled);
        Assert.Equal("old", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackupDisabled_SupersededAbsentDoesNotForgetResolvedTarget()
    {
        using var temp = new TempRoot();
        using var fixture = CreateFixture(temp, settings =>
        {
            settings.BackupDeletedTargetsToTrash = false;
            settings.PathRules =
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "Mapped/{guid:N}/{rest}"
                }
            ];
        });
        var (sourcePath, destinationPath) = await fixture.CreateDeletedSourceWithTargetAsync("resolved.txt", "old");
        var forgetEntered = NewSignal();
        var releaseForget = NewSignal();
        var deleteFinished = NewSignal();
        fixture.Coordinator.BeforeDeleteStateForgetAsync = async (_, _) =>
        {
            forgetEntered.TrySetResult();
            await releaseForget.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        fixture.Coordinator.AfterDeleteProcessingAsync = _ =>
        {
            deleteFinished.TrySetResult();
            return Task.CompletedTask;
        };

        fixture.Coordinator.ScheduleMirrorDelete(sourcePath, TestContext.Current.CancellationToken);
        await forgetEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "superseding-present", TestContext.Current.CancellationToken);
        releaseForget.TrySetResult();
        await deleteFinished.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Coordinator.TryResolveTargetPath(sourcePath, fixture.Settings.TargetRoots[0], out var resolvedAgain));
        Assert.Equal(destinationPath, resolvedAgain);
        Assert.Equal("old", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitForContentAsync(string path, string expected) =>
        AsyncAssert.WaitForConditionAsync(
            () =>
            {
                try
                {
                    return File.Exists(path) && File.ReadAllText(path) == expected;
                }
                catch (IOException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(5));

    private static CoordinatorFixture CreateFixture(TempRoot temp, Action<SyncOptions>? configure = null) =>
        new(temp, configure);

    internal sealed class CoordinatorFixture : IDisposable
    {
        private readonly Meter _meter = new($"FileTransfer.Tests.DeleteRace.{Guid.NewGuid():N}");
        private readonly InitialScanSkipStateStore _skipStore;

        public CoordinatorFixture(
            TempRoot temp,
            Action<SyncOptions>? configure,
            IStagingFileCopier? stagingFileCopier = null)
        {
            var candidate = new SyncOptions
            {
                RuleId = "delete-race",
                RuntimeId = "delete-race",
                SourceRoot = temp.CreateDir("source"),
                TargetRoots = [temp.CreateDir("target")],
                FileExtensions = [".txt"],
                IncludeSubdirectories = true,
                OverwriteExisting = true,
                WatchEvents = new WatchEventOptions
                {
                    Created = true,
                    Changed = true,
                    Deleted = true
                },
                BackupDeletedTargetsToTrash = true,
                ReadySignal = new ReadySignalOptions
                {
                    Mode = ReadySignalMode.StableSize,
                    StableChecks = 1,
                    IntervalMs = 10,
                    TimeoutMs = 2_000,
                    RequireReadable = true
                },
                PathRules =
                [
                    new PathRule
                    {
                        MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                        TargetTemplate = "Mapped/{rest}"
                    }
                ],
                InitialRetryDelayMs = 10,
                MaxRetryDelayMs = 50,
                OperationTimeoutMs = 2_000,
                MaxParallelTransfers = 1
            };
            configure?.Invoke(candidate);

            var logger = new ListLogger();
            Assert.True(candidate.TryPrepare(logger, new PathTemplateRenderer(), out var prepared));
            Settings = prepared;
            var mapper = new PathMapper(Settings, logger, new PathTemplateRenderer());
            var health = new TargetHealthRegistry(logger);
            health.Initialize(Settings.TargetRoots);
            var resolvedStore = new ResolvedTargetPathStateStore(
                logger,
                temp.GetPath(Path.Combine("state", "resolved-target-paths")));
            _skipStore = new InitialScanSkipStateStore(
                logger,
                temp.GetPath(Path.Combine("state", "initial-scan-skipped-files")));
            Coordinator = new FileTransferCoordinator(
                logger,
                health,
                () => Settings,
                () => mapper,
                _meter.CreateCounter<long>("copied"),
                _meter.CreateCounter<long>("deleted"),
                resolvedStore,
                _skipStore,
                stagingFileCopier);
        }

        public SyncOptions Settings { get; }

        public FileTransferCoordinator Coordinator { get; }

        public async Task<(string SourcePath, string DestinationPath)> CreateDeletedSourceWithTargetAsync(
            string fileName,
            string targetContent)
        {
            var paths = await CreateSourceWithTargetAsync(fileName, "source", targetContent);
            File.Delete(paths.SourcePath);
            return paths;
        }

        public async Task<(string SourcePath, string DestinationPath)> CreateSourceWithTargetAsync(
            string fileName,
            string sourceContent,
            string targetContent)
        {
            var sourceDirectory = Path.Combine(Settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            await File.WriteAllTextAsync(sourcePath, sourceContent, TestContext.Current.CancellationToken);
            Assert.True(Coordinator.TryResolveTargetPath(sourcePath, Settings.TargetRoots[0], out var destinationPath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllTextAsync(destinationPath, targetContent, TestContext.Current.CancellationToken);
            return (sourcePath, destinationPath);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            _skipStore.Dispose();
            _meter.Dispose();
        }
    }
}
