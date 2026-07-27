namespace FileTransfer.Tests;

public class FileTransferCoordinatorReadyTests
{
    [Fact]
    public async Task ReadyWait_SourceDisappearsAndReappears_DoesNotKillGeneration()
    {
        using var temp = new TempRoot();
        using var fixture = new FileTransferCoordinatorDeleteRaceTests.CoordinatorFixture(temp, settings =>
        {
            settings.ReadySignal = new ReadySignalOptions
            {
                Mode = ReadySignalMode.StableSize,
                StableChecks = 1,
                IntervalMs = 10,
                TimeoutMs = 2_000,
                RequireReadable = true
            };
        });
        var (sourcePath, destinationPath) = await fixture.CreateSourceWithTargetAsync(
            "ready-recreated.txt",
            "first",
            "old");
        var metadataReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadataRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Coordinator.ReadyFileInfoProvider = path =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                metadataReadEntered.TrySetResult();
                releaseMetadataRead.Task.GetAwaiter().GetResult();
                throw new FileNotFoundException("Source disappeared during metadata read.", path);
            }

            var info = new FileInfo(path);
            return new SourceFileStamp(info.Length, info.LastWriteTimeUtc);
        };

        fixture.Coordinator.QueueCopy(sourcePath, "first-generation", TestContext.Current.CancellationToken);
        await metadataReadEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        File.Delete(sourcePath);
        await File.WriteAllTextAsync(sourcePath, "latest", TestContext.Current.CancellationToken);
        fixture.Coordinator.QueueCopy(sourcePath, "latest-generation", TestContext.Current.CancellationToken);
        releaseMetadataRead.TrySetResult();

        await AsyncAssert.WaitForConditionAsync(
            () =>
            {
                try
                {
                    return File.Exists(destinationPath) && File.ReadAllText(destinationPath) == "latest";
                }
                catch (IOException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref calls) >= 2);
    }
}
