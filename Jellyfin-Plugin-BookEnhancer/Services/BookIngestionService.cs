using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class BookIngestionService
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly BookGroupingService _groupingService;
    private readonly LibraryOrganizationService _organization;
    private readonly ILogger<BookIngestionService> _logger;
    private Func<string, Task>? _logCallback;

    public BookIngestionService(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        BookGroupingService groupingService,
        LibraryOrganizationService organization,
        ILogger<BookIngestionService> logger)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _groupingService = groupingService;
        _organization = organization;
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
        _logCallback = logCallback;

        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return new IngestionResult { Errors = 1 };

        var result = new IngestionResult();
        var enabledDirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.SourcePath) && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .ToList();

        if (enabledDirs.Count == 0)
        {
            await LogInfoAsync("No enabled source directories configured").ConfigureAwait(false);
            return result;
        }

        foreach (var dir in enabledDirs)
        {
            if (ct.IsCancellationRequested)
                break;

            var dirResult = await ScanDirectoryAsync(dir, ct).ConfigureAwait(false);
            result.FilesFound += dirResult.FilesFound;
            result.FilesAdded += dirResult.FilesAdded;
            result.FilesSkipped += dirResult.FilesSkipped;
            result.Errors += dirResult.Errors;
        }

        return result;
    }

    private async Task LogInfoAsync(string message)
    {
        if (_logCallback is not null)
            await _logCallback(message).ConfigureAwait(false);
        else
            _logger.LogInformation("{Message}", message);
    }

    private async Task LogWarningAsync(string message)
    {
        if (_logCallback is not null)
            await _logCallback(message).ConfigureAwait(false);
        else
            _logger.LogWarning("{Message}", message);
    }

    private async Task LogErrorAsync(Exception ex, string message)
    {
        if (_logCallback is not null)
            await _logCallback($"{message} — {ex.Message}").ConfigureAwait(false);
        else
            _logger.LogWarning(ex, "{Message}", message);
    }

    private async Task<IngestionResult> ScanDirectoryAsync(ManagedSourceDirectory dir, CancellationToken ct)
    {
        var result = new IngestionResult();

        if (!Directory.Exists(dir.SourcePath))
        {
            await LogWarningAsync($"Source directory does not exist: {dir.SourcePath}").ConfigureAwait(false);
            result.Errors++;
            return result;
        }

        var supportedExts = GetSupportedExtensions();
        var files = Directory.EnumerateFiles(dir.SourcePath, "*", SearchOption.AllDirectories)
            .Where(f => supportedExts.Contains(Path.GetExtension(f)))
            .ToList();

        result.FilesFound = files.Count;
        await LogInfoAsync($"Found {files.Count} files in {dir.SourcePath}").ConfigureAwait(false);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var metadata = await _fileExtractor.ExtractAsync(file, ct).ConfigureAwait(false);
                if (metadata is null)
                {
                    metadata = CreateMinimalMetadata(file);
                }

                if (Config?.UnifiedMetadataEnabled == true)
                {
                    var enriched = await _enrichment.EnrichAsync(
                        metadata,
                        Config.HardcoverApiKey,
                        Config.GoogleBooksApiKey,
                        Config.HardcoverEnabled,
                        Config.GoogleBooksEnabled,
                        Config.OpenLibraryEnabled,
                        ct).ConfigureAwait(false);

                    metadata = enriched;
                }

                var template = string.IsNullOrWhiteSpace(dir.OrganizeTemplate)
                    ? "{Author}/{Series}/{Title}"
                    : dir.OrganizeTemplate;
                var targetPath = _organization.BuildTargetPath(dir.LibraryPath, metadata, template);
                var moveResult = _organization.MoveFile(file, targetPath, Config?.CopyMode == true);

                if (moveResult.Success)
                {
                    RegisterFile(targetPath, metadata, isPrimary: true);
                    result.FilesAdded++;
                }
                else if (moveResult.Skipped)
                {
                    var existingGroup = !string.IsNullOrWhiteSpace(metadata.Isbn)
                        ? _groupingService.GetGroupByIsbn(metadata.Isbn)
                        : null;

                    if (existingGroup is not null)
                    {
                        _groupingService.AddFormatToGroup(
                            existingGroup.Id, targetPath, metadata.FileFormat, isPrimary: false);
                        result.FilesSkipped++;
                    }
                    else
                    {
                        result.FilesSkipped++;
                    }
                }
                else
                {
                    await LogWarningAsync($"Failed to move file: {file} — {moveResult.ErrorMessage}").ConfigureAwait(false);
                    result.Errors++;
                }
            }
            catch (Exception ex)
            {
                await LogErrorAsync(ex, $"Error processing file: {file}").ConfigureAwait(false);
                result.Errors++;
            }
        }

        return result;
    }

    private void RegisterFile(string path, FileMetadata metadata, bool isPrimary)
    {
        var existingGroup = !string.IsNullOrWhiteSpace(metadata.Isbn)
            ? _groupingService.GetGroupByIsbn(metadata.Isbn)
            : null;

        if (existingGroup is null && isPrimary)
        {
            var group = _groupingService.CreateGroup(metadata);
            _groupingService.AddFormatToGroup(group.Id, path, metadata.FileFormat, isPrimary: true);
        }
        else if (existingGroup is not null && !isPrimary)
        {
            _groupingService.AddFormatToGroup(existingGroup.Id, path, metadata.FileFormat, isPrimary: false);
        }
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

public class IngestionResult
{
    public int FilesFound { get; set; }
    public int FilesAdded { get; set; }
    public int FilesSkipped { get; set; }
    public int Errors { get; set; }
}
