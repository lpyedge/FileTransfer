namespace FileTransfer.Tests;

public class MainServiceTests
{
    // 監視フォルダでファイルを作成した際に、マッピング済みのターゲットへ確実にコピーされることを検証する
    [Fact]
    public async Task CreatedFile_IsTransferredWithPathRule()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "sample.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_sample.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task CreatedFile_DoesNotLeaveStagingDirectoryOrTempFiles()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "clean-copy.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var targetDir = Path.Combine(settings.TargetRoots[0], "Mapped");
            var destFile = Path.Combine(targetDir, "M_clean-copy.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));

            Assert.False(Directory.Exists(Path.Combine(targetDir, ".filetransfer-staging")));
            Assert.False(Directory.EnumerateFiles(targetDir, "*.tmp", SearchOption.TopDirectoryOnly).Any());
        });
    }

    [Fact]
    public async Task MultiTarget_DeleteOnOneMissingTarget_PreservesOtherResolvedPaths()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        var secondaryRoot = temp.CreateDir("secondary");
        settings.TargetRoots = new[] { temp.CreateDir("primary"), secondaryRoot };

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "delete-multi.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var primaryDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_delete-multi.txt");
            var secondaryDest = Path.Combine(settings.TargetRoots[1], "Mapped", "M_delete-multi.txt");
            await WaitForFileExistsAsync(primaryDest, TimeSpan.FromSeconds(5));

            Directory.CreateDirectory(Path.GetDirectoryName(secondaryDest)!);
            File.Copy(primaryDest, secondaryDest, overwrite: true);
            await DeleteFileWithRetryAsync(primaryDest, TimeSpan.FromSeconds(2));

            File.Delete(sourceFile);

            await WaitForFileMissingAsync(secondaryDest, TimeSpan.FromSeconds(5));

            var trashDir = Path.Combine(settings.TargetRoots[1], ".trash", "Mapped");
            await WaitForConditionAsync(
                () => Directory.Exists(trashDir) && Directory.EnumerateFiles(trashDir, "M_delete-multi*.txt").Any(),
                TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task SkipInitialScan_True_DoesNotSyncHistoricalFilesOnStartup()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.SkipInitialScan = true;

        var sourceDir = Path.Combine(settings.SourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var historicalFile = Path.Combine(sourceDir, "historical.txt");
        await File.WriteAllTextAsync(historicalFile, "history", TestContext.Current.CancellationToken);

        await WithServiceAsync(settings, async service =>
        {
            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_historical.txt");
            await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(destFile));

            var newFile = Path.Combine(sourceDir, "new.txt");
            await File.WriteAllTextAsync(newFile, "fresh", TestContext.Current.CancellationToken);

            var newDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_new.txt");
            await WaitForFileExistsAsync(newDest, TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(newDest, "fresh", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task SkipInitialScan_False_SyncsHistoricalFilesOnStartup()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        var sourceDir = Path.Combine(settings.SourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var historicalFile = Path.Combine(sourceDir, "historical-default.txt");
        await File.WriteAllTextAsync(historicalFile, "history-default", TestContext.Current.CancellationToken);

        await WithServiceAsync(settings, async service =>
        {
            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_historical-default.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(10));
            await WaitForFileContentAsync(destFile, "history-default", TimeSpan.FromSeconds(5));
        });
    }

    // 既存ファイルを書き換えた場合に、ターゲット側の内容も最新状態へ上書きされるかを確認する
    [Fact]
    public async Task ChangedFile_IsOverwritten()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "change.txt");
            await File.WriteAllTextAsync(sourceFile, "v1", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_change.txt");
            await WaitForFileContentAsync(destFile, "v1", TimeSpan.FromSeconds(5));

            await File.WriteAllTextAsync(sourceFile, "v2", TestContext.Current.CancellationToken);
            await WaitForFileContentAsync(destFile, "v2", TimeSpan.FromSeconds(5));
        });
    }

    // 削除イベントを受け取った際に、対応するターゲットファイルが .trash へ退避されることを担保する
    [Fact]
    public async Task DeletedFile_IsMovedToTrash()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "delete.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_delete.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            File.Delete(sourceFile);
            await WaitForFileMissingAsync(destFile, TimeSpan.FromSeconds(5));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash", "Mapped");
            await WaitForConditionAsync(
                () => Directory.Exists(trashDir) && Directory.EnumerateFiles(trashDir, "M_delete*.txt").Any(),
                TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task DeletedFile_WithTrashBackupDisabled_KeepsTargetAndDoesNotCreateTrash()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.BackupDeletedTargetsToTrash = false;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "keep-target.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_keep-target.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            File.Delete(sourceFile);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(destFile));
            Assert.False(Directory.Exists(Path.Combine(settings.TargetRoots[0], ".trash")));
        });
    }

    // リネームイベントでは新旧両方のコピーが保持されることを確認し、誤削除を防ぐ
    [Fact]
    public async Task RenamedFile_CopiesNewAndKeepsOldTarget()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var originalFile = Path.Combine(sourceDir, "origin.txt");
            await File.WriteAllTextAsync(originalFile, "payload", TestContext.Current.CancellationToken);

            var originalDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_origin.txt");
            await WaitForFileExistsAsync(originalDest, TimeSpan.FromSeconds(5));

            var renamedFile = Path.Combine(sourceDir, "renamed.txt");
            File.Move(originalFile, renamedFile);

            var renamedDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_renamed.txt");
            await WaitForFileExistsAsync(renamedDest, TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(originalDest));
        });
    }

    // 設定された拡張子フィルタに一致しないファイルは無視されることを明示する
    [Fact]
    public async Task NonMatchingExtension_IsIgnored()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "sample.bin");
            await File.WriteAllBytesAsync(sourceFile, new byte[] { 0x01, 0x02 });

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_sample.bin");
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            Assert.False(File.Exists(destFile));
        });
    }

    // リコンシリエーションがターゲット側の欠損ファイルを補完できるかを検証する
    [Fact]
    public async Task Reconciliation_RestoresMissingCopy()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "sync.txt");
            await File.WriteAllTextAsync(sourceFile, "sync", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_sync.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            File.Delete(destFile);

            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(10));
        });
    }

    // 孤立したターゲットファイル（ソースに存在しない）は一方向転送のためそのまま残ることを確認する
    [Fact]
    public async Task Reconciliation_LeavesOrphanTargetUntouched()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_orphan.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

        await WithServiceAsync(settings, async service =>
        {
            await File.WriteAllTextAsync(destFile, "orphan", TestContext.Current.CancellationToken);

            // This app is a one-way transfer tool (source -> target) and must not mirror-delete.
            // Orphaned target files are expected to remain untouched.
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(destFile));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash", "Mapped");
            if (Directory.Exists(trashDir))
            {
                Assert.False(Directory.EnumerateFiles(trashDir, "M_orphan*.txt").Any());
            }
        });
    }

    [Theory]
    [InlineData("CATEGORY_A", "category-a", "1")]
    [InlineData("CATEGORY_B", "category-b", "2")]
    [InlineData("CATEGORY_C", "category-c", "3")]
    [InlineData("CATEGORY_D", "category-d", "4")]
    [InlineData("CATEGORY_E", "category-e", "5")]
    public async Task CreatedFile_IsTransferredWithCategoryDirectoryPathRule(string keyword, string targetFolder, string insertNumber)
    {
        using var temp = new TempRoot();
        var settings = CreateCategoryPathRuleSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var purpose = "incoming";
            var date = "20260122";
            var tail = "0038-0001.tif";

            var sourceDir = Path.Combine(settings.SourceRoot, purpose, keyword, date);
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, $"{date}-{tail}");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var expectedFileName = $"{settings.FileNamePrefix}{date}-{insertNumber}-{tail}";
            var destFile = Path.Combine(settings.TargetRoots[0], purpose, targetFolder, date, expectedFileName);
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task CreatedFile_IsTransferredWithTemplateSpecialPlaceholders()
    {
        using var temp = new TempRoot();
        var settings = CreateTemplatePlaceholderSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "20260122-report.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var expectedDir = Path.Combine(settings.TargetRoots[0], "Rendered", "2026-01-22");
            await WaitForConditionAsync(() => Directory.Exists(expectedDir), TimeSpan.FromSeconds(5));

            string[] files = Array.Empty<string>();
            await WaitForConditionAsync(() =>
            {
                files = Directory.GetFiles(expectedDir);
                return files.Length == 1;
            }, TimeSpan.FromSeconds(5));

            var fileName = Path.GetFileName(files[0]);
            Assert.Matches(@"^M_20260122-report_[0-9a-f]{32}_\d{8}\.txt$", fileName);
            await WaitForFileContentAsync(files[0], "payload", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task DeletedFile_WithTemplateSpecialPlaceholders_IsMovedToTrash()
    {
        using var temp = new TempRoot();
        var settings = CreateTemplatePlaceholderSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "20260122-report.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var expectedDir = Path.Combine(settings.TargetRoots[0], "Rendered", "2026-01-22");
            string[] files = Array.Empty<string>();
            await WaitForConditionAsync(() =>
            {
                files = Directory.Exists(expectedDir) ? Directory.GetFiles(expectedDir) : Array.Empty<string>();
                return files.Length == 1;
            }, TimeSpan.FromSeconds(5));

            var destFile = files[0];
            File.Delete(sourceFile);

            await WaitForFileMissingAsync(destFile, TimeSpan.FromSeconds(5));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash", "Rendered", "2026-01-22");
            await WaitForConditionAsync(
                () => Directory.Exists(trashDir) && Directory.GetFiles(trashDir).Length == 1,
                TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task DeleteSourceAfterCopy_WithTemplateSpecialPlaceholders_ReusesPathOnlyForSameFileInstance()
    {
        using var temp = new TempRoot();
        var settings = CreateTemplatePlaceholderSyncOptions(temp);
        settings.DeleteSourceAfterCopy = true;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "20260122-report.txt");
            var expectedDir = Path.Combine(settings.TargetRoots[0], "Rendered", "2026-01-22");

            await File.WriteAllTextAsync(sourceFile, "first", TestContext.Current.CancellationToken);
            await WaitForFileMissingAsync(sourceFile, TimeSpan.FromSeconds(5));

            string[] firstFiles = Array.Empty<string>();
            await WaitForConditionAsync(() =>
            {
                firstFiles = Directory.Exists(expectedDir) ? Directory.GetFiles(expectedDir) : Array.Empty<string>();
                return firstFiles.Length == 1;
            }, TimeSpan.FromSeconds(5));

            var firstTarget = firstFiles[0];

            await File.WriteAllTextAsync(sourceFile, "second", TestContext.Current.CancellationToken);
            await WaitForFileMissingAsync(sourceFile, TimeSpan.FromSeconds(5));

            string[] allFiles = Array.Empty<string>();
            await WaitForConditionAsync(() =>
            {
                allFiles = Directory.Exists(expectedDir) ? Directory.GetFiles(expectedDir) : Array.Empty<string>();
                return allFiles.Length == 2;
            }, TimeSpan.FromSeconds(5));

            var secondTarget = Assert.Single(allFiles, path => !string.Equals(path, firstTarget, StringComparison.OrdinalIgnoreCase));
            await WaitForFileContentAsync(secondTarget, "second", TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task Restart_PreservesDynamicTemplateResolutionForReconcileAndDelete()
    {
        using var temp = new TempRoot();
        var settings = CreateTemplatePlaceholderSyncOptions(temp);

        var sourceDir = Path.Combine(settings.SourceRoot, "A1");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "20260122-report.txt");
        var expectedDir = Path.Combine(settings.TargetRoots[0], "Rendered", "2026-01-22");

        await WithServiceAsync(settings, async service =>
        {
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            await WaitForConditionAsync(
                () => Directory.Exists(expectedDir) && Directory.GetFiles(expectedDir).Length == 1,
                TimeSpan.FromSeconds(5));
        });

        var initialTarget = Assert.Single(Directory.GetFiles(expectedDir));

        await WithServiceAsync(settings, async service =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), TestContext.Current.CancellationToken);

            var filesAfterRestart = Directory.GetFiles(expectedDir);
            Assert.Single(filesAfterRestart);
            Assert.Equal(initialTarget, filesAfterRestart[0]);

            File.Delete(sourceFile);

            await WaitForFileMissingAsync(initialTarget, TimeSpan.FromSeconds(5));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash", "Rendered", "2026-01-22");
            await WaitForConditionAsync(
                () => Directory.Exists(trashDir) && Directory.GetFiles(trashDir).Length == 1,
                TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task CreatedFile_IsTransferredWithLegacyAndSourceDerivedTemplateVariables()
    {
        using var temp = new TempRoot();
        var settings = CreateLegacyTemplateSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1", "Sub");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "extra.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Legacy", "Sub", "M_extra_copy.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
        });
    }

    // 作成イベントを無効化した場合に、新規ファイルがコピーされないことをチェックする
    [Fact]
    public async Task ProcessCreatedDisabled_DoesNotCopy()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.WatchEvents.Created = false;
        settings.WatchEvents.Changed = false;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "no_copy.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_no_copy.txt");
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.False(File.Exists(destFile));
        });
    }

    // 削除イベントを無効化した状態では、ターゲット側ファイルが残り続けることを保証する
    [Fact]
    public async Task ProcessDeletedDisabled_DoesNotMoveToTrash()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.WatchEvents.Deleted = false;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "no_trash.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_no_trash.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            File.Delete(sourceFile);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(destFile));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash");
            Assert.False(Directory.Exists(trashDir));
        });
    }

    [Fact]
    public async Task ProcessDeletedDisabled_DoesNotMoveOrphanTarget()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.WatchEvents.Deleted = false;

        var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_orphan.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        await File.WriteAllTextAsync(destFile, "orphan", TestContext.Current.CancellationToken);

        await WithServiceAsync(settings, async service =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(File.Exists(destFile));
        });
    }

    // 設定ホットリロードで作成イベントをオフにした際、以降のファイルが処理されないことを確認する
    [Fact]
    public async Task SyncOptionsReload_DisablesCreatedProcessing()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async (_, provider) =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var initialFile = Path.Combine(sourceDir, "initial.txt");
            await File.WriteAllTextAsync(initialFile, "v1", TestContext.Current.CancellationToken);

            var initialDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_initial.txt");
            await WaitForFileExistsAsync(initialDest, TimeSpan.FromSeconds(5));

            settings.WatchEvents.Created = false;
            settings.WatchEvents.Changed = false;
            provider.Update(settings);

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            var newFile = Path.Combine(sourceDir, "after_reload.txt");
            await File.WriteAllTextAsync(newFile, "payload", TestContext.Current.CancellationToken);

            var newDest = Path.Combine(settings.TargetRoots[0], "Mapped", "M_after_reload.txt");
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.False(File.Exists(newDest));
        });
    }

    [Fact]
    public async Task Watcher_RestartsWhenSourceDirectoryAppearsLater()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        var delayedSourceRoot = temp.CreateDir("late-root");
        settings.SourceRoot = Path.Combine(delayedSourceRoot, "source");

        await WithServiceAsync(settings, async service =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "late.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_late.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForFileContentAsync(destFile, "payload", TimeSpan.FromSeconds(5));
        });
    }

    // 同名ファイルを繰り返し削除しても .trash 内のファイル名が衝突しないことを検証する
    [Fact]
    public async Task Delete_CreatesUniqueTrashEntries()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "dup.txt");
            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_dup.txt");
            await File.WriteAllTextAsync(sourceFile, "first", TestContext.Current.CancellationToken);
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            File.Delete(sourceFile);
            await WaitForFileMissingAsync(destFile, TimeSpan.FromSeconds(5));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash", "Mapped");
            await WaitForConditionAsync(
                () => Directory.Exists(trashDir) && Directory.EnumerateFiles(trashDir).Any(),
                TimeSpan.FromSeconds(5));

            await File.WriteAllTextAsync(sourceFile, "second", TestContext.Current.CancellationToken);
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            File.Delete(sourceFile);
            await WaitForFileMissingAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(
                () => Directory.Exists(trashDir) && Directory.GetFiles(trashDir).Length >= 2,
                TimeSpan.FromSeconds(5));

            var trashFiles = Directory.GetFiles(trashDir);
            Assert.True(trashFiles.Length >= 2);
            Assert.NotEqual(trashFiles[0], trashFiles[1]);
        });
    }

    // When DeleteSourceAfterCopy = true, the service should delete the source after a successful copy
    // and should not move the target to .trash due to delete events.
    [Fact]
    public async Task DeleteSourceAfterCopy_True_DeletesSourceAfterCopyAndDoesNotTrash()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.DeleteSourceAfterCopy = true;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "delete_source.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_delete_source.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));
            await WaitForFileMissingAsync(sourceFile, TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(destFile));

            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash");
            Assert.False(Directory.Exists(trashDir));
        });
    }

    // If DeleteSourceAfterCopy is turned on via settings reload, subsequent external deletes should be ignored
    // (i.e., target must not be moved to .trash because delete events are disabled when DeleteSourceAfterCopy=true).
    [Fact]
    public async Task DeleteSourceAfterCopy_ToggledOn_ExternalDeleteDoesNotMoveTargetToTrash()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.DeleteSourceAfterCopy = false; // start with normal behavior

        await WithServiceAsync(settings, async (_, provider) =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Combine(sourceDir, "external.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_external.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            // source should still exist because DeleteSourceAfterCopy was false
            Assert.True(File.Exists(sourceFile));

            // enable DeleteSourceAfterCopy at runtime
            settings.DeleteSourceAfterCopy = true;
            provider.Update(settings);

            // let watcher reconfigure
            await Task.Delay(200, TestContext.Current.CancellationToken);

            // externally delete source
            File.Delete(sourceFile);

            // give system a moment
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            // target must remain and no .trash should be created
            Assert.True(File.Exists(destFile));
            var trashDir = Path.Combine(settings.TargetRoots[0], ".trash");
            Assert.False(Directory.Exists(trashDir));
        });
    }

    [Fact]
    public async Task MultiTarget_FailoverAndRecovery()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        var offlineRoot = Path.Combine(temp.CreateDir("targets"), "offline_root");
        await File.WriteAllTextAsync(offlineRoot, "offline", TestContext.Current.CancellationToken);
        var healthyRoot = temp.CreateDir("secondary");

        settings.TargetRoots = new[] { offlineRoot, healthyRoot };
        settings.HealthCheckIntervalMs = 100;
        settings.ReconciliationIntervalMs = 200;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var firstFile = Path.Combine(sourceDir, "first.txt");
            await File.WriteAllTextAsync(firstFile, "one", TestContext.Current.CancellationToken);

            var firstDest = Path.Combine(healthyRoot, "Mapped", "M_first.txt");
            await WaitForFileExistsAsync(firstDest, TimeSpan.FromSeconds(5));

            File.Delete(offlineRoot);
            Directory.CreateDirectory(offlineRoot);

            await WaitForTargetHealthAsync(service, offlineRoot, expected: true, TimeSpan.FromSeconds(5));

            var secondFile = Path.Combine(sourceDir, "second.txt");
            await File.WriteAllTextAsync(secondFile, "two", TestContext.Current.CancellationToken);

            var secondDestPrimary = Path.Combine(offlineRoot, "Mapped", "M_second.txt");
            await WaitForFileExistsAsync(secondDestPrimary, TimeSpan.FromSeconds(5));

            var secondDestSecondary = Path.Combine(healthyRoot, "Mapped", "M_second.txt");
            Assert.False(File.Exists(secondDestSecondary));
        });
    }

    [Fact]
    public async Task Reconciliation_UsesHealthyTargetForMissingCopies()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        var offlineRoot = Path.Combine(temp.CreateDir("targets"), "offline_root");
        await File.WriteAllTextAsync(offlineRoot, "offline", TestContext.Current.CancellationToken);
        var healthyRoot = temp.CreateDir("healthy");
        settings.TargetRoots = new[] { offlineRoot, healthyRoot };
        settings.HealthCheckIntervalMs = 100;
        settings.ReconciliationIntervalMs = 200;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "reconcile.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var healthyDest = Path.Combine(healthyRoot, "Mapped", "M_reconcile.txt");
            await WaitForFileExistsAsync(healthyDest, TimeSpan.FromSeconds(5));

            await DeleteFileWithRetryAsync(healthyDest, TimeSpan.FromSeconds(2));

            await WaitForFileExistsAsync(healthyDest, TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task AllTargetsDown_RecoversAfterTargetRestored()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        var targetsRoot = temp.CreateDir("targets");
        var blockedTarget = Path.Combine(targetsRoot, "blocked");
        await File.WriteAllTextAsync(blockedTarget, "offline", TestContext.Current.CancellationToken);

        settings.TargetRoots = new[] { blockedTarget };
        settings.HealthCheckIntervalMs = 100;
        settings.InitialRetryDelayMs = 50;
        settings.MaxRetryDelayMs = 200;
        settings.OperationTimeoutMs = 10_000;

        await WithServiceAsync(settings, async service =>
        {
            await WaitForTargetHealthAsync(service, blockedTarget, expected: false, TimeSpan.FromSeconds(5));

            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "recover.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(blockedTarget, "Mapped", "M_recover.txt");

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            if (File.Exists(blockedTarget))
            {
                File.Delete(blockedTarget);
            }

            Directory.CreateDirectory(blockedTarget);

            await WaitForTargetHealthAsync(service, blockedTarget, expected: true, TimeSpan.FromSeconds(5));

            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(10));
        });
    }

    [Fact]
    public async Task TargetHealthMonitor_DetectsMissingDirectoryAndRecovers()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);

        var primary = temp.CreateDir("primary");
        var secondary = temp.CreateDir("secondary");
        settings.TargetRoots = new[] { primary, secondary };
        settings.HealthCheckIntervalMs = 100;

        await WithServiceAsync(settings, async service =>
        {
            await WaitForTargetHealthAsync(service, primary, expected: true, TimeSpan.FromSeconds(5));

            Directory.Delete(primary, recursive: true);
            await WaitForTargetHealthAsync(service, primary, expected: false, TimeSpan.FromSeconds(5));

            Directory.CreateDirectory(primary);
            await WaitForTargetHealthAsync(service, primary, expected: true, TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public async Task LengthAndTimestampComparison_DisablesHashing()
    {
        using var temp = new TempRoot();
        var settings = CreateDefaultSyncOptions(temp);
        settings.ComparisonMode = ComparisonMode.LengthAndTimestamp;

        await WithServiceAsync(settings, async service =>
        {
            var sourceDir = Path.Combine(settings.SourceRoot, "A1");
            Directory.CreateDirectory(sourceDir);

            var sourceFile = Path.Combine(sourceDir, "meta.txt");
            await File.WriteAllTextAsync(sourceFile, "payload", TestContext.Current.CancellationToken);

            var destFile = Path.Combine(settings.TargetRoots[0], "Mapped", "M_meta.txt");
            await WaitForFileExistsAsync(destFile, TimeSpan.FromSeconds(5));

            await WaitForConditionAsync(
                () => TryGetFingerprint(service, sourceFile, out _),
                TimeSpan.FromSeconds(5));

            Assert.True(TryGetFingerprint(service, sourceFile, out var fingerprint));
            Assert.NotNull(fingerprint);

            var hashValue = GetFingerprintHash(fingerprint!);
            Assert.Null(hashValue);
        });
    }

    private static bool TryGetFingerprint(MainService service, string sourcePath, out object? fingerprint)
    {
        fingerprint = null;
        var field = typeof(MainService).GetField("_transfers", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(service) is not FileTransferCoordinator coordinator)
        {
            return false;
        }

        if (coordinator.Fingerprints.TryGetValue(sourcePath, out var value))
        {
            fingerprint = value;
            return true;
        }

        return false;
    }

    private static string? GetFingerprintHash(object fingerprint)
    {
        var property = fingerprint.GetType().GetProperty("Hash", BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(fingerprint) as string;
    }

    private static Task DeleteFileWithRetryAsync(string path, TimeSpan timeout) =>
        WaitForConditionAsync(() =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                File.Delete(path);
                return !File.Exists(path);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }, timeout);

    private static Task WaitForTargetHealthAsync(MainService service, string targetPath, bool expected, TimeSpan timeout) =>
        WaitForConditionAsync(
            () => TryGetTargetHealth(service, targetPath, out var health) && health == expected,
            timeout,
            TimeSpan.FromMilliseconds(50));

    private static bool TryGetTargetHealth(MainService service, string targetPath, out bool healthy)
    {
        healthy = false;
        var field = typeof(MainService).GetField("_healthRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(service) is not TargetHealthRegistry registry)
        {
            return false;
        }

        healthy = registry.IsHealthy(targetPath);
        return true;
    }

    // 任意の条件が満たされるまでポーリングし続ける基本的な待機ヘルパー
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(interval, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Condition was not satisfied within the allotted time.");
    }

    // 指定したファイルが出現するまで待機するショートハンド
    private static Task WaitForFileExistsAsync(string path, TimeSpan timeout) =>
        WaitForConditionAsync(() => File.Exists(path), timeout);

    // 指定したファイルが消えるまで待機するショートハンド
    private static Task WaitForFileMissingAsync(string path, TimeSpan timeout) =>
        WaitForConditionAsync(() => !File.Exists(path), timeout);

    // ファイル内容が期待値と一致するまで再読み込みし続ける
    private static async Task WaitForFileContentAsync(string path, string expected, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path))
                {
                    var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                    if (content == expected)
                    {
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"File content did not match the expected value within {timeout}.");
    }

    // 各テストで使い回す共通設定を生成する
    private static SyncOptions CreateDefaultSyncOptions(TempRoot temp) =>
        new()
        {
            SourceRoot = temp.CreateDir("source"),
            TargetRoots = new string[] { temp.CreateDir("target") },
            IncludeSubdirectories = true,
            OverwriteExisting = true,
            FileExtensions = new[] { ".txt", ".tif" },
            FileNamePrefix = "M_",
            PathRules = new List<PathRule>
            {
                new PathRule
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<rest>.+)$",
                    TargetTemplate = "Mapped/{rest}"
                }
            },
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = true
            },
            BackupDeletedTargetsToTrash = true,

            /**********************************************************************************************/

            InitialRetryDelayMs = 50,
            MaxRetryDelayMs = 200,
            OperationTimeoutMs = 10_000,
            ReconciliationIntervalMs = 200
        };

    private static SyncOptions CreateCategoryPathRuleSyncOptions(TempRoot temp) =>
        new()
        {
            SourceRoot = temp.CreateDir("category-source"),
            TargetRoots = new string[] { temp.CreateDir("category-target") },
            IncludeSubdirectories = true,
            OverwriteExisting = true,
            DeleteSourceAfterCopy = false,
            FileExtensions = new[] { "tif", "tiff" },
            FileNamePrefix = "M02_",
            PathRules = new List<PathRule>
            {
                new PathRule
                {
                    MatchPattern = @"^(?<purpose>[^\\/]+)[\\/]CATEGORY_A[\\/](?<date1>\d{8})[\\/](?<date2>\d{8})-(?<tail>[A-Za-z0-9_.-]+)$",
                    TargetTemplate = "{purpose}/category-a/{date1}/{date2}-1-{tail}"
                },
                new PathRule
                {
                    MatchPattern = @"^(?<purpose>[^\\/]+)[\\/]CATEGORY_B[\\/](?<date1>\d{8})[\\/](?<date2>\d{8})-(?<tail>[A-Za-z0-9_.-]+)$",
                    TargetTemplate = "{purpose}/category-b/{date1}/{date2}-2-{tail}"
                },
                new PathRule
                {
                    MatchPattern = @"^(?<purpose>[^\\/]+)[\\/]CATEGORY_C[\\/](?<date1>\d{8})[\\/](?<date2>\d{8})-(?<tail>[A-Za-z0-9_.-]+)$",
                    TargetTemplate = "{purpose}/category-c/{date1}/{date2}-3-{tail}"
                },
                new PathRule
                {
                    MatchPattern = @"^(?<purpose>[^\\/]+)[\\/]CATEGORY_D[\\/](?<date1>\d{8})[\\/](?<date2>\d{8})-(?<tail>[A-Za-z0-9_.-]+)$",
                    TargetTemplate = "{purpose}/category-d/{date1}/{date2}-4-{tail}"
                },
                new PathRule
                {
                    MatchPattern = @"^(?<purpose>[^\\/]+)[\\/]CATEGORY_E[\\/](?<date1>\d{8})[\\/](?<date2>\d{8})-(?<tail>[A-Za-z0-9_.-]+)$",
                    TargetTemplate = "{purpose}/category-e/{date1}/{date2}-5-{tail}"
                }
            },
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = false
            },

            /**********************************************************************************************/

            InitialRetryDelayMs = 50,
            MaxRetryDelayMs = 200,
            OperationTimeoutMs = 10_000,
            ReconciliationIntervalMs = 200
        };

    private static SyncOptions CreateTemplatePlaceholderSyncOptions(TempRoot temp) =>
        new()
        {
            SourceRoot = temp.CreateDir("placeholder-source"),
            TargetRoots = new[] { temp.CreateDir("placeholder-target") },
            IncludeSubdirectories = true,
            OverwriteExisting = true,
            FileExtensions = new[] { ".txt" },
            FileNamePrefix = "M_",
            PathRules = new List<PathRule>
            {
                new()
                {
                    MatchPattern = @"^(?<purpose>A1)[\\/](?<date>\d{8})-(?<tail>.+)\.txt$",
                    TargetTemplate = "Rendered/{date:yyyy-MM-dd}/{fileNameWithoutExtension}_{guid:N}_{now:yyyyMMdd}{extension}"
                }
            },
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = true
            },
            BackupDeletedTargetsToTrash = true,
            InitialRetryDelayMs = 50,
            MaxRetryDelayMs = 200,
            OperationTimeoutMs = 10_000,
            ReconciliationIntervalMs = 200
        };

    private static SyncOptions CreateLegacyTemplateSyncOptions(TempRoot temp) =>
        new()
        {
            SourceRoot = temp.CreateDir("legacy-source"),
            TargetRoots = new[] { temp.CreateDir("legacy-target") },
            IncludeSubdirectories = true,
            OverwriteExisting = true,
            FileExtensions = new[] { ".txt" },
            FileNamePrefix = "M_",
            PathRules = new List<PathRule>
            {
                new()
                {
                    MatchPattern = @"^(?<top>A1)[\\/](?<subdir>[^\\/]+)[\\/](?<name>.+)\.txt$",
                    TargetTemplate = "Legacy/${subdir}/{fileNameWithoutExtension}_copy{extension}"
                }
            },
            WatchEvents = new WatchEventOptions
            {
                Created = true,
                Changed = true,
                Deleted = true
            },
            BackupDeletedTargetsToTrash = true,
            InitialRetryDelayMs = 50,
            MaxRetryDelayMs = 200,
            OperationTimeoutMs = 10_000,
            ReconciliationIntervalMs = 200
        };

    // MainService のライフサイクル制御を共通化するためのヘルパー
    private static Task WithServiceAsync(SyncOptions settings, Func<MainService, Task> action) =>
        WithServiceAsync(settings, (service, _) => action(service));

    // ILogger/ISyncOptionsProvider を注入しつつ、サービスの開始・停止を確実に行う
    private static async Task WithServiceAsync(SyncOptions settings, Func<MainService, TestSyncOptionsProvider, Task> action)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
        var provider = new TestSyncOptionsProvider(settings.Clone());
        var service = new MainService(loggerFactory.CreateLogger<MainService>(), provider);
        var started = false;
        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            started = true;
            await action(service, provider);
        }
        finally
        {
            if (started)
            {
                await service.StopAsync(TestContext.Current.CancellationToken);
            }

            service.Dispose();
        }
    }

    // テストごとに分離された一時ディレクトリを管理し、後片付けを自動化する
    private sealed class TempRoot : IDisposable
    {
        private readonly string _root;
        private bool _disposed;

        public TempRoot()
        {
            _root = Path.Combine(Path.GetTempPath(), "FileTransfer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public string CreateDir(string relative)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    // ランタイムの設定更新を疑似的に再現するための簡易 ISyncOptionsProvider 実装
    private sealed class TestSyncOptionsProvider : ISyncOptionsProvider
    {
        private SyncOptions _settings;

        public TestSyncOptionsProvider(SyncOptions settings)
        {
            _settings = settings;
        }

        public IReadOnlyList<SyncOptions> Current => new[] { _settings.Clone() };

        public event Action<IReadOnlyList<SyncOptions>>? OptionsChanged;

        public void Update(SyncOptions settings)
        {
            _settings = settings.Clone();
            OptionsChanged?.Invoke(new[] { _settings.Clone() });
        }
    }
}
