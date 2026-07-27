namespace FileTransfer.Tests;

public class StagingFileCleanerTests
{
    [Fact]
    public void StartupCleanup_RemovesOnlyOwnedExpiredStagingFiles()
    {
        using var temp = new TempRoot();
        var root = temp.CreateDir("target");
        var owned = Path.Combine(root, "report.txt.0123456789abcdef0123456789abcdef.tmp");
        var ordinary = Path.Combine(root, "notes.tmp");
        File.WriteAllText(owned, "stale");
        File.WriteAllText(ordinary, "keep");
        File.SetLastWriteTimeUtc(owned, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(ordinary, DateTime.UtcNow.AddHours(-2));

        new StagingFileCleaner(new ListLogger()).Clean(new[] { root }, 1);

        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(ordinary));
    }

    [Fact]
    public void StartupCleanup_DoesNotDeleteRecentOrUnrelatedTmpFiles()
    {
        using var temp = new TempRoot();
        var root = temp.CreateDir("target");
        var recent = Path.Combine(root, "report.txt.0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllText(recent, "recent");

        new StagingFileCleaner(new ListLogger()).Clean(new[] { root }, 1);

        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void LegacyProbeCleanup_RemovesOnlyOldMatchingProbeFiles()
    {
        using var temp = new TempRoot();
        var root = temp.CreateDir("target");
        var prefix = ".filetransfer-" + "health.";
        var suffix = ".pro" + "be";
        var oldMatching = Path.Combine(root, prefix + "legacy" + suffix);
        var recentMatching = Path.Combine(root, prefix + "recent" + suffix);
        var unrelated = Path.Combine(root, "ordinary" + suffix);
        File.WriteAllText(oldMatching, "old");
        File.WriteAllText(recentMatching, "recent");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(oldMatching, DateTime.UtcNow.AddMinutes(-20));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddMinutes(-20));

        new StagingFileCleaner(new ListLogger()).Clean(new[] { root }, 1);

        Assert.False(File.Exists(oldMatching));
        Assert.True(File.Exists(recentMatching));
        Assert.True(File.Exists(unrelated));
    }
}
