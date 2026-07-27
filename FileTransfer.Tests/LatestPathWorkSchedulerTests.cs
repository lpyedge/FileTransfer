namespace FileTransfer.Tests;

public class LatestPathWorkSchedulerTests
{
    [Fact]
    public async Task SamePath_NeverRunsConcurrently()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximum = 0;
        var calls = 0;
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 2, async (_, token) =>
        {
            var current = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, current);
            var call = Interlocked.Increment(ref calls);
            started.TrySetResult();
            try
            {
                if (call == 1)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
                if (call == 2)
                {
                    finished.TrySetResult();
                }
            }
        });

        scheduler.SignalPresent("one.txt", "created", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.SignalPresent("one.txt", "changed", TestContext.Current.CancellationToken);

        await finished.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task DifferentPaths_RunUpToConfiguredParallelism()
    {
        var twoRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximum = 0;
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 2, async (_, token) =>
        {
            var current = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, current);
            if (current == 2)
            {
                twoRunning.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(token);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        });

        scheduler.SignalPresent("one.txt", "event", TestContext.Current.CancellationToken);
        scheduler.SignalPresent("two.txt", "event", TestContext.Current.CancellationToken);
        scheduler.SignalPresent("three.txt", "event", TestContext.Current.CancellationToken);
        await twoRunning.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        await AsyncAssert.WaitForConditionAsync(() => Volatile.Read(ref running) == 0, TimeSpan.FromSeconds(2));

        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task SourceChange_CancelsOldGenerationAndRunsLatestGeneration()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRan = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, async (item, token) =>
        {
            if (item.Generation == 1)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            latestRan.TrySetResult(item);
        });

        scheduler.SignalPresent("one.txt", "created", TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.SignalPresent("one.txt", "changed", TestContext.Current.CancellationToken);

        var latest = await latestRan.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, latest.Generation);
        Assert.Equal("changed", latest.Label);
    }

    [Fact]
    public async Task DuplicateSignals_AreCoalesced()
    {
        var workerDequeued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, (item, _) =>
        {
            Interlocked.Increment(ref calls);
            ran.TrySetResult(item);
            return Task.CompletedTask;
        });
        scheduler.BeforeWorkItemSnapshotAsync = async _ =>
        {
            workerDequeued.TrySetResult();
            await releaseSnapshot.Task;
        };

        scheduler.SignalPresent("one.txt", "created", TestContext.Current.CancellationToken);
        await workerDequeued.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.SignalPresent("one.txt", "changed", TestContext.Current.CancellationToken);
        releaseSnapshot.TrySetResult();
        var item = await ran.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, item.Generation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RequestCopy_DoesNotCancelRunningGeneration()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        });

        scheduler.SignalPresent("one.txt", "created", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.RequestCopy("one.txt", "reconcile", TestContext.Current.CancellationToken);

        var completed = await Task.WhenAny(cancelled.Task, Task.Delay(100, TestContext.Current.CancellationToken));
        Assert.NotSame(cancelled.Task, completed);
        release.TrySetResult();
    }

    [Fact]
    public async Task AbsentThenPresent_EndsAsPresent()
    {
        var absentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentRan = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, async (item, token) =>
        {
            if (item.DesiredState == DesiredSourceState.Absent)
            {
                absentStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            presentRan.TrySetResult(item);
        });

        scheduler.SignalAbsent("one.txt", "deleted", TestContext.Current.CancellationToken);
        await absentStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.SignalPresent("one.txt", "created", TestContext.Current.CancellationToken);

        var item = await presentRan.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DesiredSourceState.Present, item.DesiredState);
        Assert.Equal(2, item.Generation);
    }

    [Fact]
    public async Task ObservePresent_SameStampDoesNotIncrementGeneration()
    {
        var stamp = new SourceFileStamp(4, new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc));
        var ran = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, (item, _) =>
        {
            ran.TrySetResult(item);
            return Task.CompletedTask;
        });

        scheduler.ObservePresent("one.txt", stamp, "reconcile", TestContext.Current.CancellationToken);
        scheduler.ObservePresent("one.txt", stamp, "reconcile", TestContext.Current.CancellationToken);
        scheduler.RequestCopy("one.txt", "reconcile", TestContext.Current.CancellationToken);

        var item = await ran.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, item.Generation);
    }

    [Fact]
    public async Task ObservePresent_ChangedStampCancelsAndRequeues()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRan = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new SourceFileStamp(4, new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc));
        var second = new SourceFileStamp(5, first.LastWriteUtc.AddSeconds(1));
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, async (item, token) =>
        {
            if (item.Generation == 0)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            latestRan.TrySetResult(item);
        });

        scheduler.ObservePresent("one.txt", first, "reconcile", TestContext.Current.CancellationToken);
        scheduler.RequestCopy("one.txt", "reconcile", TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.ObservePresent("one.txt", second, "reconcile", TestContext.Current.CancellationToken);

        var item = await latestRan.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, item.Generation);
    }

    [Fact]
    public async Task Dispose_CancelsWorkersAndActiveHandler()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        });

        scheduler.SignalPresent("one.txt", "created", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.Dispose();

        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompletedAbsentWork_RemovesIdleState()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, (item, _) =>
        {
            Assert.Equal(DesiredSourceState.Absent, item.DesiredState);
            handled.TrySetResult();
            return Task.CompletedTask;
        });

        scheduler.SignalAbsent("retired.txt", "deleted", TestContext.Current.CancellationToken);
        await handled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await AsyncAssert.WaitForConditionAsync(() => scheduler.StateCount == 0, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PresentSignalDuringAbsentCompletion_IsNotLost()
    {
        var absentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbsent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentRan = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, async (item, _) =>
        {
            if (item.DesiredState == DesiredSourceState.Absent)
            {
                absentStarted.TrySetResult();
                await releaseAbsent.Task;
                return;
            }

            presentRan.TrySetResult(item);
        });

        scheduler.SignalAbsent("recreated.txt", "deleted", TestContext.Current.CancellationToken);
        await absentStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        scheduler.SignalPresent("recreated.txt", "created", TestContext.Current.CancellationToken);
        releaseAbsent.TrySetResult();

        var present = await presentRan.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DesiredSourceState.Present, present.DesiredState);
        Assert.Equal(2, present.Generation);
    }

    [Fact]
    public async Task SignalAgainstRetiredState_RetriesOnNewState()
    {
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStateLookedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentRan = new TaskCompletionSource<PathWorkItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scheduler = new LatestPathWorkScheduler(new ListLogger(), 1, (item, _) =>
        {
            if (item.DesiredState == DesiredSourceState.Present)
            {
                presentRan.TrySetResult(item);
            }

            return Task.CompletedTask;
        });
        scheduler.BeforeRetiredStateRemoval = _ =>
        {
            retired.TrySetResult();
            releaseRemoval.Task.GetAwaiter().GetResult();
        };

        scheduler.SignalAbsent("retire-race.txt", "deleted", cancellationToken);
        await retired.Task.WaitAsync(cancellationToken);
        scheduler.AfterSignalStateLookup = _ => oldStateLookedUp.TrySetResult();
        var signalTask = Task.Run(
            () => scheduler.SignalPresent("retire-race.txt", "created", cancellationToken),
            cancellationToken);
        await oldStateLookedUp.Task.WaitAsync(cancellationToken);
        releaseRemoval.TrySetResult();
        await signalTask.WaitAsync(cancellationToken);

        var present = await presentRan.Task.WaitAsync(cancellationToken);
        Assert.Equal(DesiredSourceState.Present, present.DesiredState);
        Assert.Equal(1, present.Generation);
    }
}
