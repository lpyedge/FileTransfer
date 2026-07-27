namespace FileTransfer.Tests;

public class TargetHealthBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnknownTarget_IsImmediatelyAttemptable()
    {
        var registry = new TargetHealthRegistry(new ListLogger());

        Assert.True(registry.CanAttempt("target", Now));
    }

    [Fact]
    public void RecordFailure_EntersBackoff()
    {
        var registry = new TargetHealthRegistry(new ListLogger());

        registry.RecordFailure("target", Now, 100, 1_000, "offline");

        Assert.False(registry.CanAttempt("target", Now.AddMilliseconds(99)));
        Assert.True(registry.TryGetState("target", out var state));
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(Now.AddMilliseconds(100), state.NextAttemptUtc);
        Assert.Equal("offline", state.LastReason);
    }

    [Fact]
    public void RepeatedFailure_UsesExponentialBackoffAndCapsAtMaximum()
    {
        var registry = new TargetHealthRegistry(new ListLogger());

        registry.RecordFailure("target", Now, 100, 350, "first");
        registry.RecordFailure("target", Now.AddSeconds(1), 100, 350, "second");
        registry.RecordFailure("target", Now.AddSeconds(2), 100, 350, "third");

        Assert.True(registry.TryGetState("target", out var state));
        Assert.Equal(3, state.ConsecutiveFailures);
        Assert.Equal(Now.AddSeconds(2).AddMilliseconds(350), state.NextAttemptUtc);
    }

    [Fact]
    public void BackoffExpiry_AllowsRealAttemptButDoesNotAutoRecover()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.RecordFailure("target", Now, 100, 1_000, "offline");

        Assert.True(registry.CanAttempt("target", Now.AddMilliseconds(100)));
        Assert.True(registry.TryGetState("target", out var state));
        Assert.Equal(1, state.ConsecutiveFailures);
    }

    [Fact]
    public void RecordSuccess_ResetsFailureState()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.RecordFailure("target", Now, 100, 1_000, "offline");

        registry.RecordSuccess("target", "copied");

        Assert.True(registry.CanAttempt("target", Now));
        Assert.True(registry.TryGetState("target", out var state));
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(default, state.NextAttemptUtc);
    }

    [Fact]
    public void SyncTargets_PreservesExistingBackoff()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.Initialize(new[] { "primary", "secondary" });
        registry.RecordFailure("primary", Now, 100, 1_000, "offline");

        registry.SyncTargets(new[] { "primary", "tertiary" });

        Assert.False(registry.CanAttempt("primary", Now));
        Assert.True(registry.CanAttempt("tertiary", Now));
        Assert.False(registry.TryGetState("secondary", out _));
    }

    [Fact]
    public void RemovedTarget_IsRemovedFromRegistry()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.Initialize(new[] { "primary" });
        registry.RecordFailure("primary", Now, 100, 1_000, "offline");

        registry.SyncTargets(Array.Empty<string>());

        Assert.False(registry.TryGetState("primary", out _));
        Assert.True(registry.CanAttempt("primary", Now));
    }

    [Fact]
    public async Task ConcurrentFailures_DoNotLoseFailureCount()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.Initialize(new[] { "target" });
        var cancellationToken = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = Enumerable.Range(0, 8)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        var failures = ready.Select(started => Task.Run(async () =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            registry.RecordFailure("target", Now, 10, 10_000, "offline");
        })).ToArray();

        await Task.WhenAll(ready.Select(started => started.Task.WaitAsync(cancellationToken)));
        release.SetResult();
        await Task.WhenAll(failures).WaitAsync(cancellationToken);

        Assert.True(registry.TryGetState("target", out var state));
        Assert.Equal(8, state.ConsecutiveFailures);
    }

    [Fact]
    public void RemovedTarget_LateFailureDoesNotRestoreIt()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.Initialize(new[] { "target" });
        registry.SyncTargets(Array.Empty<string>());

        registry.RecordFailure("target", Now, 100, 1_000, "late failure");

        Assert.False(registry.TryGetState("target", out _));
    }

    [Fact]
    public void RemovedTarget_LateSuccessDoesNotRestoreIt()
    {
        var registry = new TargetHealthRegistry(new ListLogger());
        registry.Initialize(new[] { "target" });
        registry.SyncTargets(Array.Empty<string>());

        registry.RecordSuccess("target", "late success");

        Assert.False(registry.TryGetState("target", out _));
    }
}
