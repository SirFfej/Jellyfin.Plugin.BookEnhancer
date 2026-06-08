using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class LibraryCleanupService
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly LibraryOrganizationService _organization;
    private readonly BookGroupingService _groupingService;
    private readonly ILogger<LibraryCleanupService> _logger;

    public LibraryCleanupService(
        FileMetadataExtractor fileExtractor,
        LibraryOrganizationService organization,
        BookGroupingService groupingService,
        ILogger<LibraryCleanupService> logger)
    {
        _fileExtractor = fileExtractor;
        _organization = organization;
        _groupingService = groupingService;
        _logger = logger;
    }

    private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    public async Task<CleanupResult> RunCleanupAsync(IProgress<double> progress, Func<string, Task> logCallback, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return new CleanupResult { Errors = 1 };

        var result = new CleanupResult();
        var dirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .ToList();

        if (dirs.Count == 0)
        {
            await logCallback("No enabled source directories with library paths configured.").ConfigureAwait(false);
            return result;
        }

        var supportedExts = GetSupportedExtensions(config);

        var totalFiles = 0;
        var processed = 0;
        var allFiles = new List<(string FilePath, ManagedSourceDirectory Dir)>();

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir.LibraryPath))
            {
                await logCallback($"Library path does not exist, skipping: {dir.LibraryPath}").ConfigureAwait(false);
                continue;
            }

            var files = Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                .Where(f => supportedExts.Contains(Path.GetExtension(f)))
                .ToList();

            totalFiles += files.Count;
            allFiles.AddRange(files.Select(f => (f, dir)));
        }

        await logCallback($"Found {totalFiles} files to check across {dirs.Count} directories.").ConfigureAwait(false);
        progress.Report(0.0);

        foreach (var (file, dir) in allFiles)
        {
            if (ct.IsCancellationRequested)
            {
                await logCallback("Cleanup cancelled.").ConfigureAwait(false);
                break;
            }

            processed++;

            try
            {
                var metadata = await _fileExtractor.ExtractAsync(file, ct).ConfigureAwait(false);
                if (metadata is null)
                {
                    metadata = CreateMinimalMetadata(file);
                }

                var template = string.IsNullOrWhiteSpace(dir.OrganizeTemplate)
                    ? "{Author}/{Series}/{Title}"
                    : dir.OrganizeTemplate;

                var expectedPath = _organization.BuildTargetPath(dir.LibraryPath, metadata, template);

                if (string.Equals(file, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.FilesSkipped++;
                    continue;
                }

                var dirToClean = Path.GetDirectoryName(file);

                var moveResult = _organization.MoveFile(file, expectedPath, copy: false);
                if (moveResult.Success)
                {
                    result.FilesMoved++;
                    await logCallback($"Moved: {file} -> {expectedPath}").ConfigureAwait(false);

                    _groupingService.UpdateFormatPath(file, expectedPath);

                    await CleanupEmptyDirectories(dirToClean, result, logCallback).ConfigureAwait(false);
                }
                else if (moveResult.Skipped)
                {
                    result.FilesSkipped++;
                }
                else
                {
                    await logCallback($"Failed to move {file}: {moveResult.ErrorMessage}").ConfigureAwait(false);
                    result.Errors++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing file during cleanup: {File}", file);
                await logCallback($"Error processing {file}: {ex.Message}").ConfigureAwait(false);
                result.Errors++;
            }

            if (totalFiles > 0)
                progress.Report((double)processed / totalFiles);
        }

        await logCallback(
            $"Cleanup complete — Checked: {totalFiles}, Moved: {result.FilesMoved}, " +
            $"Already correct: {result.FilesSkipped}, Errors: {result.Errors}, " +
            $"Empty dirs removed: {result.EmptyDirectoriesRemoved}").ConfigureAwait(false);
        await logCallback("IMPORTANT: Run a Jellyfin library scan to re-link moved files to library items.").ConfigureAwait(false);

        progress.Report(1.0);
        return result;
    }

    private async Task CleanupEmptyDirectories(string? startPath, CleanupResult result, Func<string, Task> logCallback)
    {
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
            return;

        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (dir.EnumerateFileSystemInfos().Any())
                    break;

                dir.Delete();
                result.EmptyDirectoriesRemoved++;
                await logCallback($"Removed empty directory: {dir.FullName}").ConfigureAwait(false);
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove empty directory: {Path}", startPath);
        }
    }

    private static HashSet<string> GetSupportedExtensions(PluginConfiguration config)
    {
        if (config.IngestionFileExtensions is null || config.IngestionFileExtensions.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".epub", ".pdf",
                ".cbz", ".cbr", ".cb7",
                ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
            };
        }

        return new HashSet<string>(config.IngestionFileExtensions, StringComparer.OrdinalIgnoreCase);
    }

    private static FileMetadata CreateMinimalMetadata(string filePath)
    {
        return new FileMetadata
        {
            FilePath = filePath,
            FileFormat = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
            Title = Path.GetFileNameWithoutExtension(filePath)
        };
    }
}

public class CleanupResult
{
    public int FilesMoved { get; set; }
    public int FilesSkipped { get; set; }
    public int Errors { get; set; }
    public int EmptyDirectoriesRemoved { get; set; }
}
