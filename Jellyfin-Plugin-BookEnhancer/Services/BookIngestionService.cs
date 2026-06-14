using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class BookIngestionService
{
    private const int CheckpointInterval = 10;
    private static readonly TimeSpan CheckpointMaxAge = TimeSpan.FromHours(24);

    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".avif", ".svg"
    };

    private static readonly HashSet<string> _imageBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover", "folder", "poster", "thumbnail", "thumb", "front", "back", "spine"
    };

    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly BookGroupingService _groupingService;
    private readonly LibraryOrganizationService _organization;
    private readonly TaskCheckpointService _checkpointService;
    private readonly IFileMetadataWriter _writer;
    private readonly ILogger<BookIngestionService> _logger;

    public BookIngestionService(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        BookGroupingService groupingService,
        LibraryOrganizationService organization,
        TaskCheckpointService checkpointService,
        IFileMetadataWriter writer,
        ILogger<BookIngestionService> logger)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _groupingService = groupingService;
        _organization = organization;
        _checkpointService = checkpointService;
        _writer = writer;
        _logger = logger;
    }

    private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    private static HashSet<string> GetSupportedExtensions()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.IngestionFileExtensions is null || config.IngestionFileExtensions.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".epub", ".mobi", ".pdf",
                ".cbz", ".cbr", ".cb7",
                ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
            };
        }

        return new HashSet<string>(config.IngestionFileExtensions, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IngestionResult> ScanAllAsync(Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return new IngestionResult { Errors = 1 };

        if (string.IsNullOrWhiteSpace(config.TrashDirectory))
        {
            await LogWarningAsync("Trash directory not configured. All tasks are disabled until a trash directory is set in plugin settings.", logCallback, _logger).ConfigureAwait(false);
            return new IngestionResult { Errors = 1 };
        }

        var result = new IngestionResult();
        var enabledDirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.SourcePath) && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .ToList();

        if (enabledDirs.Count == 0)
        {
            await LogInfoAsync("No enabled source directories configured", logCallback, _logger).ConfigureAwait(false);
            return result;
        }

        foreach (var dir in enabledDirs)
        {
            if (ct.IsCancellationRequested)
                break;

            var dirResult = await ScanDirectoryAsync(dir, logCallback, ct).ConfigureAwait(false);
            result.FilesFound += dirResult.FilesFound;
            result.FilesAdded += dirResult.FilesAdded;
            result.FilesSkipped += dirResult.FilesSkipped;
            result.Errors += dirResult.Errors;
        }

        return result;
    }

    private static async Task LogInfoAsync(string message, Func<string, Task>? logCallback, ILogger logger)
    {
        if (logCallback is not null)
            await logCallback(message).ConfigureAwait(false);
        else
            logger.LogInformation("{Message}", message);
    }

    private static async Task LogWarningAsync(string message, Func<string, Task>? logCallback, ILogger logger)
    {
        if (logCallback is not null)
            await logCallback(message).ConfigureAwait(false);
        else
            logger.LogWarning("{Message}", message);
    }

    private static async Task LogErrorAsync(Exception ex, string message, Func<string, Task>? logCallback, ILogger logger)
    {
        if (logCallback is not null)
            await logCallback($"{message} — {ex.Message}").ConfigureAwait(false);
        else
            logger.LogWarning(ex, "{Message}", message);
    }

    private async Task<IngestionResult> ScanDirectoryAsync(ManagedSourceDirectory dir, Func<string, Task>? logCallback, CancellationToken ct)
    {
        var result = new IngestionResult();

        if (!Directory.Exists(dir.SourcePath))
        {
            await LogWarningAsync($"Source directory does not exist: {dir.SourcePath}", logCallback, _logger).ConfigureAwait(false);
            result.Errors++;
            return result;
        }

        var supportedExts = GetSupportedExtensions();
        var files = Directory.EnumerateFiles(dir.SourcePath, "*", SearchOption.AllDirectories)
            .Where(f => supportedExts.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.FilesFound = files.Count;
        await LogInfoAsync($"Found {files.Count} files in {dir.SourcePath}", logCallback, _logger).ConfigureAwait(false);

        var checkpointKey = GetCheckpointKey(dir.SourcePath);
        var checkpoint = _checkpointService.LoadCheckpoint(checkpointKey, CheckpointMaxAge);
        var resumeFrom = checkpoint?.LastProcessedPath;
        var resuming = !string.IsNullOrWhiteSpace(resumeFrom);
        if (resuming)
        {
            await LogInfoAsync($"Resuming from checkpoint: {resumeFrom}", logCallback, _logger).ConfigureAwait(false);
        }

        var movedByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var skippedByDir = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var processedSinceCheckpoint = 0;
        var checkpointCleared = false;

        foreach (var file in files)
        {
            if (resuming)
            {
                if (string.Equals(file, resumeFrom, StringComparison.OrdinalIgnoreCase))
                {
                    resuming = false;
                }
                else
                {
                    result.FilesSkipped++;
                    continue;
                }
            }

            if (ct.IsCancellationRequested)
                break;

            try
            {
                var metadata = await _fileExtractor.ExtractAsync(file, ct).ConfigureAwait(false);
                if (metadata is null)
                {
                    metadata = CreateMinimalMetadata(file);
                }

                EnrichmentResult? enrichmentResult = null;
                var enrichmentAttempted = false;
                if (Config?.UnifiedMetadataEnabled == true)
                {
                    var cooldown = Config.EnrichmentCooldownDays;
                    var cooldownInfo = _groupingService.GetEnrichmentCooldownInfo(file, cooldown);
                    if (cooldownInfo.OnCooldown)
                    {
                        var by = string.IsNullOrWhiteSpace(cooldownInfo.EnrichedBy) ? "unknown API" : cooldownInfo.EnrichedBy;
                        await LogInfoAsync($"Skipped enrichment (cooldown, last by {by}): {file}", logCallback, _logger).ConfigureAwait(false);
                    }
                    else
                    {
                        enrichmentAttempted = true;
                        var apiConfig = Config.GetEffectiveApiConfig(dir);
                        enrichmentResult = await _enrichment.EnrichAsync(
                            metadata,
                            apiConfig,
                            titleAuthorSearchEnabled: dir.EnableTitleAuthorSearch,
                            title: metadata.Title,
                            author: metadata.Authors.Count > 0 ? metadata.Authors[0] : null,
                            ct: ct).ConfigureAwait(false);

                        metadata = enrichmentResult.Metadata;

                        if (!string.IsNullOrWhiteSpace(metadata.Isbn) && !enrichmentResult.ApiMatchFound)
                        {
                            result.EnrichmentFailures++;
                            await LogWarningAsync($"ENRICHMENT FAILURE: {file} (ISBN: {metadata.Isbn}) — no online match found", logCallback, _logger).ConfigureAwait(false);
                        }
                    }
                }

                var template = string.IsNullOrWhiteSpace(dir.OrganizeTemplate)
                    ? LibraryOrganizationService.GetDefaultTemplate(metadata, dir.FlatSeriesStructure)
                    : dir.OrganizeTemplate;
                var targetPath = _organization.BuildTargetPath(dir.LibraryPath, metadata, template);
                var sourceDir = Path.GetDirectoryName(file);
                var targetDir = Path.GetDirectoryName(targetPath);
                var moveResult = await _organization.MoveFile(file, targetPath, Config?.CopyMode == true, logCallback: null).ConfigureAwait(false);

                if (moveResult.Success)
                {
                    _groupingService.RegisterFile(targetPath, metadata, isPrimary: true, Config?.GroupingStrategy ?? "IsbnOnly");
                    if (enrichmentAttempted && enrichmentResult?.ApiMatchFound == true)
                        _groupingService.SetLastEnrichmentTime(targetPath, enrichmentResult.EnrichedBy);
                    result.FilesAdded++;

                    if (!string.IsNullOrWhiteSpace(targetDir))
                    {
                        if (!movedByDir.TryGetValue(targetDir, out var movedFiles))
                        {
                            movedFiles = new List<string>();
                            movedByDir[targetDir] = movedFiles;
                        }

                        movedFiles.Add(Path.GetFileName(file));
                    }

                    if (dir.EnableMetadataWriting)
                        await _writer.WriteMetadataAsync(targetPath, metadata, ct).ConfigureAwait(false);

                    await MoveCompanionImagesAsync(sourceDir, targetDir, logCallback).ConfigureAwait(false);
                    await CleanupEmptyDirectories(sourceDir, logCallback).ConfigureAwait(false);
                }
                else if (moveResult.Skipped)
                {
                    if (dir.EnableMetadataWriting)
                        await _writer.WriteMetadataAsync(targetPath, metadata, ct).ConfigureAwait(false);

                    var group = _groupingService.RegisterFile(targetPath, metadata, isPrimary: false, Config?.GroupingStrategy ?? "IsbnOnly");
                    if (enrichmentAttempted && enrichmentResult?.ApiMatchFound == true)
                        _groupingService.SetLastEnrichmentTime(targetPath, enrichmentResult.EnrichedBy);
                    result.FilesSkipped++;

                    if (!string.IsNullOrWhiteSpace(targetDir))
                        skippedByDir[targetDir] = skippedByDir.GetValueOrDefault(targetDir) + 1;

                    await MoveCompanionImagesAsync(sourceDir, targetDir, logCallback).ConfigureAwait(false);
                    await CleanupEmptyDirectories(sourceDir, logCallback).ConfigureAwait(false);
                }
                else
                {
                    await LogWarningAsync($"Failed to move file: {file} — {moveResult.ErrorMessage}", logCallback, _logger).ConfigureAwait(false);
                    result.Errors++;
                }

                processedSinceCheckpoint++;
                if (processedSinceCheckpoint >= CheckpointInterval)
                {
                    _checkpointService.SaveCheckpoint(checkpointKey, file);
                    processedSinceCheckpoint = 0;
                }
            }
            catch (Exception ex)
            {
                await LogErrorAsync(ex, $"Error processing file: {file}", logCallback, _logger).ConfigureAwait(false);
                result.Errors++;
            }
        }

        if (!checkpointCleared)
        {
            _checkpointService.ClearCheckpoint(checkpointKey);
            checkpointCleared = true;
        }

        await LogDirectorySummariesAsync(movedByDir, skippedByDir, logCallback).ConfigureAwait(false);

        return result;
    }

    private static string GetCheckpointKey(string sourcePath)
    {
        return $"IngestionScan:{sourcePath}";
    }

    private async Task LogDirectorySummariesAsync(
        Dictionary<string, List<string>> movedByDir,
        Dictionary<string, int> skippedByDir,
        Func<string, Task>? logCallback)
    {
        foreach (var kvp in movedByDir.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var count = kvp.Value.Count;
            var names = string.Join(", ", kvp.Value.Take(5));
            var more = count > 5 ? $" and {count - 5} more" : string.Empty;
            await LogInfoAsync($"Moved {count} files to {kvp.Key}: {names}{more}", logCallback, _logger).ConfigureAwait(false);
        }

        foreach (var kvp in skippedByDir.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            await LogInfoAsync($"Skipped {kvp.Value} existing files in {kvp.Key}", logCallback, _logger).ConfigureAwait(false);
        }
    }

    private static FileMetadata CreateMinimalMetadata(string filePath)
    {
        return new FileMetadata
        {
            FilePath = filePath,
            FileFormat = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
            Title = SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(filePath))
        };
    }

    private async Task MoveCompanionImagesAsync(string? sourceDir, string? targetDir, Func<string, Task>? logCallback)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(sourceDir))
            return;

        if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            foreach (var imagePath in Directory.EnumerateFiles(sourceDir))
            {
                var ext = Path.GetExtension(imagePath);
                if (!_imageExtensions.Contains(ext)) continue;

                var nameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);

                if (!_imageBaseNames.Contains(nameWithoutExt) && !nameWithoutExt.Contains("cover", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetPath = Path.Combine(targetDir, Path.GetFileName(imagePath));

                if (File.Exists(targetPath))
                {
                    var sourceInfo = new FileInfo(imagePath);
                    var targetInfo = new FileInfo(targetPath);
                    if (sourceInfo.Exists && targetInfo.Exists && sourceInfo.Length == targetInfo.Length)
                    {
                        File.Delete(imagePath);
                        await LogInfoAsync($"  Removed stale companion image (already at target): {imagePath}", logCallback, _logger).ConfigureAwait(false);
                    }
                    continue;
                }

                Directory.CreateDirectory(targetDir);
                File.Move(imagePath, targetPath);
                await LogInfoAsync($"  Moved companion image: {imagePath} -> {targetPath}", logCallback, _logger).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex, $"Failed to move companion images from {sourceDir}", logCallback, _logger).ConfigureAwait(false);
        }
    }

    private async Task CleanupEmptyDirectories(string? startPath, Func<string, Task>? logCallback)
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
                await LogInfoAsync($"  Removed empty directory: {dir.FullName}", logCallback, _logger).ConfigureAwait(false);
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex, $"Failed to remove empty directory {startPath}", logCallback, _logger).ConfigureAwait(false);
        }
    }
}

public class IngestionResult
{
    public int FilesFound { get; set; }
    public int FilesAdded { get; set; }
    public int FilesSkipped { get; set; }
    public int Errors { get; set; }
    public int EnrichmentFailures { get; set; }
}
