namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>
/// Provides file-system move helpers that fall back to copy-and-delete when the source and
/// destination are on different volumes, avoiding the <see cref="IOException"/> that
/// <see cref="File.Move(string, string)"/> and <see cref="Directory.Move(string, string)"/>
/// throw for cross-volume moves.
/// </summary>
public static class SafeFileOperations
{
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// Moves a file, falling back to a streaming copy followed by source deletion when a direct
    /// move fails. Creates the target directory if it does not exist.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="targetPath">The target file path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task MoveFileAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            return;

        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDir))
            Directory.CreateDirectory(targetDir);

        try
        {
            File.Move(sourcePath, targetPath);
        }
        catch (IOException)
        {
            await CopyAndDeleteFileAsync(sourcePath, targetPath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies a file asynchronously, creating the target directory if it does not exist.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="targetPath">The target file path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task CopyFileAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            return;

        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDir))
            Directory.CreateDirectory(targetDir);

        var sourceInfo = new FileInfo(sourcePath);
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            useAsync: true);

        await using var targetStream = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DefaultBufferSize,
            useAsync: true);

        await sourceStream.CopyToAsync(targetStream, ct).ConfigureAwait(false);

        if (sourceInfo.Exists)
        {
            File.SetCreationTime(targetPath, sourceInfo.CreationTime);
            File.SetLastWriteTime(targetPath, sourceInfo.LastWriteTime);
        }
    }

    /// <summary>
    /// Moves a directory, falling back to recursive copy-and-delete when a direct move fails.
    /// </summary>
    /// <param name="sourcePath">The source directory path.</param>
    /// <param name="targetPath">The target directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task MoveDirectoryAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Directory.Move(sourcePath, targetPath);
        }
        catch (IOException)
        {
            await CopyAndDeleteDirectoryAsync(sourcePath, targetPath, ct).ConfigureAwait(false);
        }
    }

    private static async Task CopyAndDeleteFileAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        var sourceInfo = new FileInfo(sourcePath);
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            useAsync: true);

        await using var targetStream = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            DefaultBufferSize,
            useAsync: true);

        await sourceStream.CopyToAsync(targetStream, ct).ConfigureAwait(false);

        if (sourceInfo.Exists)
        {
            File.SetCreationTime(targetPath, sourceInfo.CreationTime);
            File.SetLastWriteTime(targetPath, sourceInfo.LastWriteTime);
        }

        File.Delete(sourcePath);
    }

    private static async Task CopyAndDeleteDirectoryAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        Directory.CreateDirectory(targetPath);

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourcePath, file);
            var destFile = Path.Combine(targetPath, relativePath);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrWhiteSpace(destDir))
                Directory.CreateDirectory(destDir);

            await CopyAndDeleteFileAsync(file, destFile, ct).ConfigureAwait(false);
        }

        Directory.Delete(sourcePath, recursive: true);
    }
}
