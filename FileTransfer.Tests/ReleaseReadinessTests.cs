namespace FileTransfer.Tests;

public class ReleaseReadinessTests
{
    //[Fact]
    //public void PublicRepository_DoesNotContainIgnoredLocalOrInternalDeploymentFiles()
    //{
    //    var root = FindRepositoryRoot();
    //    var forbiddenPatterns = new[]
    //    {
    //        "*.user",
    //        "*.bat",
    //        "appsettings.*.local.yaml",
    //        "*.pubxml",
    //        "*.pubxml.user"
    //    };

    //    foreach (var pattern in forbiddenPatterns)
    //    {
    //        var matches = Directory
    //            .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
    //            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
    //            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
    //            .ToArray();

    //        Assert.Empty(matches);
    //    }

    //    Assert.False(Directory.Exists(Path.Combine(root, "FileTransfer", "Properties", "PublishProfiles")));
    //}

    [Fact]
    public void GitIgnore_DoesNotHidePublicYamlExamples()
    {
        var gitignore = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".gitignore"));

        Assert.DoesNotContain("FileTransfer/appsettings.*.yaml", gitignore, StringComparison.Ordinal);
        Assert.Contains("FileTransfer/appsettings.*.local.yaml", gitignore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileTransfer/appsettings.Output*.yaml", gitignore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileTransfer/appsettings.Scanner.yaml", gitignore, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(FindRepositoryRoot(), "FileTransfer", "appsettings.yaml")));
        Assert.True(File.Exists(Path.Combine(FindRepositoryRoot(), "FileTransfer", "appsettings.full.yaml")));
    }

    [Fact]
    public void ProjectFile_DoesNotPublishPrivateLocalYamlFiles()
    {
        var project = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "FileTransfer", "FileTransfer.csproj"));

        Assert.Contains("appsettings.*.local.yaml", project, StringComparison.Ordinal);
        Assert.Contains("appsettings.yaml", project, StringComparison.Ordinal);
        Assert.Contains("appsettings.full.yaml", project, StringComparison.Ordinal);
        Assert.DoesNotContain("appsettings*.yaml", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadmeFiles_AreMutuallyLinkedAndDocumentServiceRegistration()
    {
        var root = FindRepositoryRoot();
        var languageLinks = new Dictionary<string, string[]>
        {
            ["README.md"] = new[] { "README.ja.md", "README.zh.md" },
            ["README.ja.md"] = new[] { "README.md", "README.zh.md" },
            ["README.zh.md"] = new[] { "README.md", "README.ja.md" }
        };

        foreach (var (readme, expectedLinks) in languageLinks)
        {
            var content = File.ReadAllText(Path.Combine(root, readme));
            foreach (var expectedLink in expectedLinks)
            {
                Assert.Contains(expectedLink, content, StringComparison.Ordinal);
            }

            Assert.Contains("Windows", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("systemd", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--config", content, StringComparison.Ordinal);
            Assert.Contains("logging.language", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReleaseWorkflow_PackagesExamplesAndRunsPublishedBinarySmokeTest()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("dotnet test", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", workflow, StringComparison.Ordinal);
        Assert.Contains("--validate --config", workflow, StringComparison.Ordinal);
        Assert.Contains("cp -R examples", workflow, StringComparison.Ordinal);
        Assert.Contains("checksums.txt", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredOpenSourceGovernanceFilesExist()
    {
        var root = FindRepositoryRoot();
        var requiredFiles = new[]
        {
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            "CHANGELOG.md",
            "SECURITY.md",
            "CONTRIBUTING.md",
            "CODE_OF_CONDUCT.md",
            "global.json",
            ".github/workflows/ci.yml",
            ".github/workflows/release.yml",
            ".github/dependabot.yml"
        };

        foreach (var relativePath in requiredFiles)
        {
            Assert.True(File.Exists(Path.Combine(root, relativePath)), $"Missing required release file: {relativePath}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FileTransfer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
