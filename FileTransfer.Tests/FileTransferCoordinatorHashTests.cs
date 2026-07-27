namespace FileTransfer.Tests;

public class FileTransferCoordinatorHashTests
{
    [Fact]
    public void HashesMatch_TwoNullValues_ReturnsFalse()
    {
        Assert.False(FileTransferCoordinator.HashesMatch(null, null));
    }

    [Fact]
    public async Task OverwriteExistingFalse_HashUnavailable_DoesNotCompleteTargetOrDeleteSource()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, settings =>
        {
            settings.OverwriteExisting = false;
            settings.ComparisonMode = ComparisonMode.Hash;
        });
        fixture.Settings.DeleteSourceAfterCopy = true;
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "hash-unavailable.txt",
            "source",
            "target");
        var comparisonEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseComparison = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceDeleteCalled = false;
        fixture.Coordinator.FileHashProvider = static (_, _, _) => Task.FromResult<string?>(null);
        fixture.Coordinator.AfterExistingTargetComparisonAsync = async (_, _, matches) =>
        {
            comparisonEntered.TrySetResult(matches);
            await releaseComparison.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        fixture.Coordinator.BeforeSourceDeleteAsync = (_, _) =>
        {
            sourceDeleteCalled = true;
            return Task.CompletedTask;
        };

        fixture.Coordinator.QueueCopy(sourcePath, "hash-unavailable", TestContext.Current.CancellationToken);
        var matches = await comparisonEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(matches);
        Assert.True(File.Exists(sourcePath));
        Assert.False(sourceDeleteCalled);
        Assert.Equal("target", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));
        releaseComparison.TrySetResult();
    }

    [Fact]
    public async Task OverwriteExistingFalse_EqualNonNullHashes_CompletesTargetAndAllowsVerifiedSourceDelete()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, settings =>
        {
            settings.OverwriteExisting = false;
            settings.ComparisonMode = ComparisonMode.Hash;
        });
        fixture.Settings.DeleteSourceAfterCopy = true;
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "hash-equal.txt",
            "source",
            "target");
        var deleteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Coordinator.FileHashProvider = static (_, _, _) => Task.FromResult<string?>("ABC123");
        fixture.Coordinator.BeforeSourceDeleteAsync = async (_, _) =>
        {
            deleteEntered.TrySetResult();
            await releaseDelete.Task.WaitAsync(TestContext.Current.CancellationToken);
        };

        fixture.Coordinator.QueueCopy(sourcePath, "hash-equal", TestContext.Current.CancellationToken);
        await deleteEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("target", await File.ReadAllTextAsync(destinationPath, TestContext.Current.CancellationToken));

        releaseDelete.TrySetResult();
        await AsyncAssert.WaitForConditionAsync(() => !File.Exists(sourcePath), TimeSpan.FromSeconds(5));
    }
}
