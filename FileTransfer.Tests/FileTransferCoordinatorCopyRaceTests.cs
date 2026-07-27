using System.Security.Cryptography;

namespace FileTransfer.Tests;

public class FileTransferCoordinatorCopyRaceTests
{
    [Fact]
    public async Task SourceChangesDuringStagingCopy_OldGenerationNeverCommits()
    {
        using var temp = new TempRoot();
        var firstCopyStarted = NewSignal();
        var releaseFirstCopy = NewSignal();
        var calls = 0;
        var copier = new DelegateStagingFileCopier(async (source, stamp, staging, settings, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstCopyStarted.TrySetResult();
                await releaseFirstCopy.Task;
                await WriteStagingAsync(staging, "old", stamp);
                return new StagedCopyResult(stamp, null);
            }

            return await CopyCurrentAsync(source, stamp, staging, settings, token);
        });
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, null, copier);
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "during-staging.txt",
            "old",
            "baseline");

        fixture.Coordinator.QueueCopy(sourcePath, "old-generation", TestContext.Current.CancellationToken);
        await firstCopyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sourcePath, "new-generation", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "new-generation", TestContext.Current.CancellationToken);
        releaseFirstCopy.TrySetResult();

        await WaitForContentAsync(destinationPath, "new-generation");
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Empty(Directory.EnumerateFiles(fixture.Settings.TargetRoots[0], "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SignalChangedBeforeCommit_OldStagingDoesNotReplaceDestination()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, null);
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "before-commit.txt",
            "old-generation",
            "baseline");
        var oldCommitEntered = NewSignal();
        var releaseOldCommit = NewSignal();
        var commitCalls = 0;
        fixture.Coordinator.BeforeCommitAsync = async (_, _) =>
        {
            if (Interlocked.Increment(ref commitCalls) == 1)
            {
                oldCommitEntered.TrySetResult();
                await releaseOldCommit.Task;
            }
        };

        fixture.Coordinator.QueueCopy(sourcePath, "old-generation", TestContext.Current.CancellationToken);
        await oldCommitEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("baseline", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(sourcePath, "new-generation", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "new-generation", TestContext.Current.CancellationToken);
        releaseOldCommit.TrySetResult();

        await WaitForContentAsync(destinationPath, "new-generation");
        Assert.True(Volatile.Read(ref commitCalls) >= 2);
        Assert.Empty(Directory.EnumerateFiles(fixture.Settings.TargetRoots[0], "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SourceChangesBeforeDeleteAfterCopy_SourceIsPreservedByOldGeneration()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, null);
        fixture.Settings.DeleteSourceAfterCopy = true;
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "before-source-delete.txt",
            "old-generation",
            "baseline");
        var firstDeleteEntered = NewSignal();
        var releaseFirstDelete = NewSignal();
        var latestDeleteEntered = NewSignal();
        var releaseLatestDelete = NewSignal();
        var deleteCalls = 0;
        fixture.Coordinator.BeforeSourceDeleteAsync = async (_, _) =>
        {
            var call = Interlocked.Increment(ref deleteCalls);
            if (call == 1)
            {
                firstDeleteEntered.TrySetResult();
                await releaseFirstDelete.Task;
            }
            else if (call == 2)
            {
                latestDeleteEntered.TrySetResult();
                await releaseLatestDelete.Task;
            }
        };

        fixture.Coordinator.QueueCopy(sourcePath, "old-generation", TestContext.Current.CancellationToken);
        await firstDeleteEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sourcePath, "new-generation", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "new-generation", TestContext.Current.CancellationToken);
        releaseFirstDelete.TrySetResult();

        await latestDeleteEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal("new-generation", await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken));
        Assert.Equal("new-generation", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));

        releaseLatestDelete.TrySetResult();
        await AsyncAssert.WaitForConditionAsync(() => !File.Exists(sourcePath), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AllHealthyTargets_DeleteSourceAfterCopy_WaitsForAllConfiguredTargets()
    {
        using var temp = new TempRoot();
        var primary = temp.CreateDir("all-primary");
        var targetParent = temp.CreateDir("all-targets");
        var blockedSecondary = Path.Combine(targetParent, "secondary");
        await File.WriteAllTextAsync(blockedSecondary, "blocked", TestContext.Current.CancellationToken);
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, settings =>
        {
            settings.TargetRoots = [primary, blockedSecondary];
            settings.TargetMode = TargetMode.AllHealthyTargets;
            settings.OperationTimeoutMs = 8_000;
            settings.InitialRetryDelayMs = 10;
            settings.MaxRetryDelayMs = 50;
        });
        fixture.Settings.DeleteSourceAfterCopy = true;
        var sourceDirectory = Path.Combine(fixture.Settings.SourceRoot, "A1");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "all-targets.txt");
        await File.WriteAllTextAsync(sourcePath, "payload", TestContext.Current.CancellationToken);
        Assert.True(fixture.Coordinator.TryResolveTargetPath(sourcePath, primary, out var primaryDestination));
        Assert.True(fixture.Coordinator.TryResolveTargetPath(sourcePath, blockedSecondary, out var secondaryDestination));

        fixture.Coordinator.QueueCopy(sourcePath, "all-targets", TestContext.Current.CancellationToken);
        await WaitForContentAsync(primaryDestination, "payload");
        Assert.True(File.Exists(sourcePath));

        File.Delete(blockedSecondary);
        Directory.CreateDirectory(blockedSecondary);
        await WaitForContentAsync(secondaryDestination, "payload");
        await AsyncAssert.WaitForConditionAsync(() => !File.Exists(sourcePath), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OverwriteExistingFalse_LengthAndTimestampDifferentTarget_IsNotCompleted()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, settings =>
        {
            settings.OverwriteExisting = false;
            settings.ComparisonMode = ComparisonMode.LengthAndTimestamp;
        });
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "metadata-conflict.txt",
            "source1",
            "target1");
        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath).AddMinutes(-5));
        var compared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewSignal();
        fixture.Coordinator.AfterExistingTargetComparisonAsync = async (_, _, matches) =>
        {
            compared.TrySetResult(matches);
            await release.Task;
        };

        fixture.Coordinator.QueueCopy(sourcePath, "metadata-conflict", TestContext.Current.CancellationToken);
        Assert.False(await compared.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("target1", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(sourcePath));
        release.TrySetResult();
    }

    [Fact]
    public async Task OverwriteExistingFalse_DifferentHashes_IsNotCompleted()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, settings =>
        {
            settings.OverwriteExisting = false;
            settings.ComparisonMode = ComparisonMode.Hash;
        });
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "hash-conflict.txt",
            "source1",
            "target1");
        var compared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewSignal();
        fixture.Coordinator.FileHashProvider = (path, _, _) =>
            Task.FromResult<string?>(PathKeyComparer.Equals(path, sourcePath) ? "SOURCE" : "TARGET");
        fixture.Coordinator.AfterExistingTargetComparisonAsync = async (_, _, matches) =>
        {
            compared.TrySetResult(matches);
            await release.Task;
        };

        fixture.Coordinator.QueueCopy(sourcePath, "hash-conflict", TestContext.Current.CancellationToken);
        Assert.False(await compared.Task.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("target1", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(sourcePath));
        release.TrySetResult();
    }

    [Fact]
    public async Task ReconciliationRequestCopy_DoesNotCancelSameVersionTransfer()
    {
        using var temp = new TempRoot();
        var copyStarted = NewSignal();
        var releaseCopy = NewSignal();
        CancellationToken activeToken = default;
        var calls = 0;
        var copier = new DelegateStagingFileCopier(async (source, stamp, staging, settings, token) =>
        {
            Interlocked.Increment(ref calls);
            activeToken = token;
            copyStarted.TrySetResult();
            await releaseCopy.Task;
            return await CopyCurrentAsync(source, stamp, staging, settings, token);
        });
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, null, copier);
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "request-copy.txt",
            "payload",
            "baseline");

        fixture.Coordinator.QueueCopy(sourcePath, "watcher", TestContext.Current.CancellationToken);
        await copyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var info = new FileInfo(sourcePath);
        fixture.Coordinator.ObserveSourceVersion(
            sourcePath,
            new SourceFileStamp(info.Length, info.LastWriteTimeUtc),
            "reconcile",
            TestContext.Current.CancellationToken);
        fixture.Coordinator.RequestCopy(sourcePath, "reconcile", TestContext.Current.CancellationToken);

        Assert.False(activeToken.IsCancellationRequested);
        releaseCopy.TrySetResult();
        await WaitForContentAsync(destinationPath, "payload");
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ReconciliationObservesNewStamp_CancelsOldTransferAndRunsLatest()
    {
        using var temp = new TempRoot();
        var firstStarted = NewSignal();
        var firstCancelled = NewSignal();
        var calls = 0;
        var copier = new DelegateStagingFileCopier(async (source, stamp, staging, settings, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    firstCancelled.TrySetResult();
                    throw;
                }
            }

            return await CopyCurrentAsync(source, stamp, staging, settings, token);
        });
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, null, copier);
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "observed-change.txt",
            "old",
            "baseline");

        fixture.Coordinator.QueueCopy(sourcePath, "watcher", TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sourcePath, "new-observed-generation", TestContext.Current.CancellationToken);
        var info = new FileInfo(sourcePath);
        fixture.Coordinator.ObserveSourceVersion(
            sourcePath,
            new SourceFileStamp(info.Length, info.LastWriteTimeUtc),
            "reconcile",
            TestContext.Current.CancellationToken);

        await firstCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitForContentAsync(destinationPath, "new-observed-generation");
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task StagingVerificationFailure_PreservesExistingDestination()
    {
        using var temp = new TempRoot();
        var verificationFailed = NewSignal();
        var copier = new DelegateStagingFileCopier(async (_, _, staging, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            await File.WriteAllTextAsync(staging, "invalid-staging", TestContext.Current.CancellationToken);
            File.Delete(staging);
            verificationFailed.TrySetResult();
            throw new IOException("Injected staging verification failure.");
        });
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, null, copier);
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "invalid-staging.txt",
            "new-source",
            "known-good");

        fixture.Coordinator.QueueCopy(sourcePath, "invalid-staging", TestContext.Current.CancellationToken);
        await verificationFailed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("known-good", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(fixture.Settings.TargetRoots[0], "*.tmp", SearchOption.AllDirectories));
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
            TimeSpan.FromSeconds(8));

    private static async Task<StagedCopyResult> CopyCurrentAsync(
        string sourcePath,
        SourceFileStamp stamp,
        string stagingPath,
        SyncOptions settings,
        CancellationToken token)
    {
        var content = await File.ReadAllTextAsync(sourcePath, token);
        await WriteStagingAsync(stagingPath, content, stamp);
        string? hash = null;
        if (settings.ComparisonMode == ComparisonMode.Hash)
        {
            hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(stagingPath, token)));
        }

        return new StagedCopyResult(stamp, hash);
    }

    private static async Task WriteStagingAsync(string stagingPath, string content, SourceFileStamp stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
        await File.WriteAllTextAsync(stagingPath, content, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(stagingPath, stamp.LastWriteUtc);
    }

    private sealed class DelegateStagingFileCopier(
        Func<string, SourceFileStamp, string, SyncOptions, CancellationToken, Task<StagedCopyResult>> copy)
        : IStagingFileCopier
    {
        public Task<StagedCopyResult> CopyAsync(
            string sourcePath,
            SourceFileStamp expectedStamp,
            string stagingPath,
            SyncOptions settings,
            CancellationToken cancellationToken) =>
            copy(sourcePath, expectedStamp, stagingPath, settings, cancellationToken);
    }
}
