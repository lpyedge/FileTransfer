internal sealed class SourceChangedException : IOException
{
    public SourceChangedException(string message) : base(message) { }
}

internal readonly record struct StagedCopyResult(SourceFileStamp SourceStamp, string? ContentHash);

internal interface IStagingFileCopier
{
    Task<StagedCopyResult> CopyAsync(
        string sourcePath,
        SourceFileStamp expectedStamp,
        string stagingPath,
        SyncOptions settings,
        CancellationToken cancellationToken);
}

internal sealed class StagingFileCopier : IStagingFileCopier
{
    private readonly ILogger? _logger;
    private readonly Func<Task>? _afterSourceOpened;

    public StagingFileCopier(ILogger? logger = null, Func<Task>? afterSourceOpened = null)
    {
        _logger = logger;
        _afterSourceOpened = afterSourceOpened;
    }

    public async Task<StagedCopyResult> CopyAsync(
        string sourcePath,
        SourceFileStamp expectedStamp,
        string stagingPath,
        SyncOptions settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, settings.CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != expectedStamp.Length)
            {
                throw new SourceChangedException("Source length changed before copying started.");
            }

            if (_afterSourceOpened is not null)
            {
                await _afterSourceOpened().ConfigureAwait(false);
            }

            await using var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, settings.CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = settings.ComparisonMode == ComparisonMode.Hash
                ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                : null;
            var buffer = ArrayPool<byte>.Shared.Rent(settings.CopyBufferSize);
            try
            {
                var remaining = expectedStamp.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new SourceChangedException("Source was truncated or replaced during copy.");
                    }

                    hasher?.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await destination.DisposeAsync().ConfigureAwait(false);

            var hash = hasher is null ? null : Convert.ToHexString(hasher.GetHashAndReset());
            if (new FileInfo(stagingPath).Length != expectedStamp.Length)
            {
                throw new IOException("Staging length verification failed.");
            }

            File.SetLastWriteTimeUtc(stagingPath, expectedStamp.LastWriteUtc);
            if (hash is not null)
            {
                var verified = await ComputeHashAsync(stagingPath, settings.HashBufferSize, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, verified, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Staging hash verification failed.");
                }
            }

            return new StagedCopyResult(expectedStamp, hash);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
    }

    private static async Task<string> ComputeHashAsync(string path, int bufferSize, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not delete staging file: {StagingPath}", path);
        }
    }
}
