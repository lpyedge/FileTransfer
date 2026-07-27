namespace FileTransfer.Tests;

public class SyncOptionsSetValidatorTests
{
    [Fact]
    public void DuplicateRuntimeKey_IsRejectedWithoutPartialRules()
    {
        using var temp = new TempRoot();
        var first = CreateRule(temp, "first");
        var second = CreateRule(temp, "second");
        second.RuntimeId = first.RuntimeId;

        Assert.False(SyncOptionsSetValidator.TryPrepareAll(new[] { first, second }, new ListLogger(), new PathTemplateRenderer(), out var prepared));
        Assert.Empty(prepared);
    }

    [Fact]
    public void EmptyRuleSet_IsRejected()
    {
        Assert.False(SyncOptionsSetValidator.TryPrepareAll(Array.Empty<SyncOptions>(), new ListLogger(), new PathTemplateRenderer(), out var prepared));
        Assert.Empty(prepared);
    }

    [Fact]
    public void CrossRuleSourceTargetOverlap_IsRejected()
    {
        using var temp = new TempRoot();
        var first = CreateRule(temp, "first");
        var second = CreateRule(temp, "second");
        second.TargetRoots = new[] { first.SourceRoot };

        Assert.False(SyncOptionsSetValidator.TryPrepareAll(new[] { first, second }, new ListLogger(), new PathTemplateRenderer(), out _));
    }

    [Fact]
    public void ValidIndependentRules_ArePrepared()
    {
        using var temp = new TempRoot();
        var first = CreateRule(temp, "first");
        var second = CreateRule(temp, "second");

        Assert.True(SyncOptionsSetValidator.TryPrepareAll(
            new[] { first, second },
            new ListLogger(),
            new PathTemplateRenderer(),
            out var prepared));
        Assert.Equal(2, prepared.Count);
        Assert.All(prepared, rule => Assert.True(Path.IsPathFullyQualified(rule.SourceRoot)));
    }

    [Fact]
    public void ValidationFailure_DoesNotReturnPartialPreparedRules()
    {
        using var temp = new TempRoot();
        var valid = CreateRule(temp, "valid");
        var invalid = CreateRule(temp, "invalid");
        invalid.TargetRoots = Array.Empty<string>();

        Assert.False(SyncOptionsSetValidator.TryPrepareAll(
            new[] { valid, invalid },
            new ListLogger(),
            new PathTemplateRenderer(),
            out var prepared));
        Assert.Empty(prepared);
    }

    private static SyncOptions CreateRule(TempRoot temp, string name) => new()
    {
        RuleId = name,
        RuntimeId = name,
        SourceRoot = temp.CreateDir($"source-{name}"),
        TargetRoots = new[] { temp.CreateDir($"target-{name}") },
        FileExtensions = new[] { ".txt" }
    };
}
