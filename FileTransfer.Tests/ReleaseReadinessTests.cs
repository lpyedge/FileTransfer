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
    public void DotnetTenTesting_UsesMicrosoftTestingPlatformNativeRunner()
    {
        var root = FindRepositoryRoot();
        var globalJson = File.ReadAllText(Path.Combine(root, "global.json"));
        var testProject = File.ReadAllText(Path.Combine(root, "FileTransfer.Tests", "FileTransfer.Tests.csproj"));
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains("\"runner\": \"Microsoft.Testing.Platform\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("<OutputType>Exe</OutputType>", testProject, StringComparison.Ordinal);
        Assert.Contains("<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>", testProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Testing.Extensions.TrxReport", testProject, StringComparison.Ordinal);
        Assert.Contains("--report-trx", ci, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v7", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/upload-artifact@v4", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("--logger trx", ci, StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains("Microsoft.Testing.Platform", content, StringComparison.Ordinal);
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
        Assert.Contains("actions/upload-artifact@v7", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
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

    private static IEnumerable<string> EnumerateRepositoryFiles()
    {
        var root = FindRepositoryRoot();
        var gitFiles = TryListTrackedGitFiles(root);
        if (gitFiles is not null)
        {
            return gitFiles;
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static string[]? TryListTrackedGitFiles(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0
                ? output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(file => file.Replace('\\', '/'))
                    .ToArray()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBuildArtifact(string file)
    {
        var normalized = file.Replace(Path.DirectorySeparatorChar, '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/dist/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
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
