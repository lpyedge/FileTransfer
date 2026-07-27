namespace FileTransfer.Tests;

public class StagingFileCopierTests
{
    [Fact]
    public async Task CopiesExactlyExpectedLength_WhenSourceAppendsDuringCopy()
    {
        using var temp = new TempRoot();
        var source = temp.GetPath("source.txt");
        var staging = temp.GetPath("destination.tmp");
        await File.WriteAllTextAsync(source, "first", TestContext.Current.CancellationToken);
        var stamp = new SourceFileStamp(5, File.GetLastWriteTimeUtc(source));
        var copier = new StagingFileCopier(afterSourceOpened: () => File.AppendAllTextAsync(source, "-appended", TestContext.Current.CancellationToken));

        await copier.CopyAsync(source, stamp, staging, new SyncOptions { ComparisonMode = ComparisonMode.LengthAndTimestamp }, TestContext.Current.CancellationToken);

        Assert.Equal("first", await File.ReadAllTextAsync(staging, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThrowsSourceChanged_WhenSourceTruncatedBeforeExpectedLength()
    {
        using var temp = new TempRoot();
        var source = temp.GetPath("source.txt");
        var staging = temp.GetPath("destination.tmp");
        await File.WriteAllTextAsync(source, "long-content", TestContext.Current.CancellationToken);
        var stamp = new SourceFileStamp(12, File.GetLastWriteTimeUtc(source));
        await File.WriteAllTextAsync(source, "short", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SourceChangedException>(() => new StagingFileCopier().CopyAsync(source, stamp, staging, new SyncOptions(), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task Cancellation_DeletesStagingFile()
    {
        using var temp = new TempRoot();
        var source = temp.GetPath("source.txt");
        var staging = temp.GetPath("destination.tmp");
        await File.WriteAllTextAsync(source, "payload", TestContext.Current.CancellationToken);
        var stamp = new SourceFileStamp(7, File.GetLastWriteTimeUtc(source));
        using var cts = new CancellationTokenSource();
        var copier = new StagingFileCopier(afterSourceOpened: () =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copier.CopyAsync(source, stamp, staging, new SyncOptions(), cts.Token));
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task HashMode_ReturnsHashOfStagedBytes()
    {
        using var temp = new TempRoot();
        var source = temp.GetPath("source.txt");
        var staging = temp.GetPath("destination.tmp");
        await File.WriteAllTextAsync(source, "payload", TestContext.Current.CancellationToken);
        var stamp = new SourceFileStamp(7, File.GetLastWriteTimeUtc(source));

        var result = await new StagingFileCopier().CopyAsync(source, stamp, staging, new SyncOptions { ComparisonMode = ComparisonMode.Hash }, TestContext.Current.CancellationToken);

        Assert.NotNull(result.ContentHash);
        Assert.Equal(7, new FileInfo(staging).Length);
    }
}
