namespace FileTransfer.Tests;

public class PathMapperTests
{
    [Fact]
    public void ResolveTargetPathForRoot_AppliesPathRuleAndFileNamePrefix()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            prefix: "M_",
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "Mapped/{rest}"
                }
            ]);

        var resolution = mapper.ResolveTargetPathForRoot(
            Path.Combine(mapper.SourceRoot, "A1", "report.txt"),
            mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "Mapped", "M_report.txt"), resolution.TargetPath);
    }

    [Fact]
    public void ResolveTargetPathForRoot_DoesNotDuplicateFileNamePrefix()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            prefix: "M_",
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "Mapped/{rest}"
                }
            ]);

        var resolution = mapper.ResolveTargetPathForRoot(
            Path.Combine(mapper.SourceRoot, "A1", "M_report.txt"),
            mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "Mapped", "M_report.txt"), resolution.TargetPath);
    }

    [Fact]
    public void ResolveTargetPathForRoot_ReturnsExcludedWhenExcludePathRuleMatches()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^skip[\\/].+$",
                    Ignore = true
                }
            ]);

        var resolution = mapper.ResolveTargetPathForRoot(
            Path.Combine(mapper.SourceRoot, "skip", "report.txt"),
            mapper.TargetRoots[0]);

        Assert.True(resolution.Ignored);
        Assert.Equal(string.Empty, resolution.RelativePath);
        Assert.Equal(string.Empty, resolution.TargetPath);
    }

    [Fact]
    public void ResolveTargetPathForRoot_FallsBackWhenTemplatePlaceholderIsInvalid()
    {
        using var culture = TestCultureScope.Use("ja");
        using var temp = new TempRoot();
        var logger = new ListLogger();
        var mapper = CreateMapper(
            temp,
            logger,
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "Mapped/{unknown}"
                }
            ]);

        var sourcePath = Path.Combine(mapper.SourceRoot, "A1", "report.txt");
        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "A1", "report.txt"), resolution.TargetPath);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("テンプレートの展開に失敗"));
    }

    [Fact]
    public void ResolveTargetPathForRoot_FallsBackWhenTemplateEscapesTargetRoot()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "../escape/{fileName}"
                }
            ]);

        var sourcePath = Path.Combine(mapper.SourceRoot, "A1", "report.txt");
        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "A1", "report.txt"), resolution.TargetPath);
    }

    [Fact]
    public void ResolveTargetPathForRoot_UsesRawRelativePathAsRegexFallback()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)\\(?<rest>.+)$",
                    TargetTemplate = "Backslash/{rest}"
                }
            ]);

        // Build a raw relative path with backslashes explicitly. Path.Combine uses the
        // current OS separator, so it cannot exercise the raw-backslash fallback on Linux.
        var sourcePath = mapper.SourceRoot + Path.DirectorySeparatorChar + @"A1\nested\report.txt";
        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "Backslash", "nested", "report.txt"), resolution.TargetPath);
    }

    [Fact]
    public void GetTrashPath_ReturnsPathUnderMatchingTrashRoot()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(temp);
        var destPath = Path.Combine(mapper.TargetRoots[0], "Mapped", "report.txt");

        var trashPath = mapper.GetTrashPath(destPath);

        Assert.Equal(Path.Combine(mapper.TargetRoots[0], ".trash", "Mapped", "report.txt"), trashPath);
    }


    [Fact]
    public void ResolveTargetPathForRoot_RejectsWindowsInvalidCharactersInRenderedPathOnAllPlatforms()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "Mapped/{top}:invalid/{rest}"
                }
            ]);

        var sourcePath = Path.Combine(mapper.SourceRoot, "A1", "report.txt");
        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "A1", "report.txt"), resolution.TargetPath);
    }

    [Fact]
    public void ResolveTargetPathForRoot_NormalizesBackslashesRenderedByTemplate()
    {
        using var temp = new TempRoot();
        var mapper = CreateMapper(
            temp,
            mappings:
            [
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = @"Mapped\{rest}"
                }
            ]);

        // Build a raw relative path with backslashes explicitly. Path.Combine uses the
        // current OS separator, so it cannot exercise the raw-backslash fallback on Linux.
        var sourcePath = mapper.SourceRoot + Path.DirectorySeparatorChar + @"A1\nested\report.txt";
        var resolution = mapper.ResolveTargetPathForRoot(sourcePath, mapper.TargetRoots[0]);

        Assert.False(resolution.Ignored);
        Assert.Equal(Path.Combine(mapper.TargetRoots[0], "Mapped", "nested", "report.txt"), resolution.TargetPath);
    }
    private static PathMapper CreateMapper(
        TempRoot temp,
        ListLogger? logger = null,
        string prefix = "",
        List<PathRule>? mappings = null)
    {
        var settings = new SyncOptions
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new[] { temp.CreateDir("target"), temp.CreateDir("secondary") },
            FileNamePrefix = prefix,
            PathRules = mappings ?? new List<PathRule>()
        };

        return new PathMapper(settings, logger ?? new ListLogger(), new PathTemplateRenderer());
    }
}
