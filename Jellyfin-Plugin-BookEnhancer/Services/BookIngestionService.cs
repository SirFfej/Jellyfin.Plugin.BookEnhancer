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

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf",
        ".cbz", ".cbr", ".cb7",
        ".mp3", ".m4a", ".m4b", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
    };

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

    public async Task<IngestionResult> ScanAllAsync(CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return new IngestionResult { Errors = 1 };

        var result = new IngestionResult();
        var enabledDirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.SourcePath) && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .ToList();

        if (enabledDirs.Count == 0)
        {
            _logger.LogInformation("No enabled source directories configured");
            return result;
        }

        foreach (var dir in enabledDirs)
        {
            if (ct.IsCancellationRequested)
                break;

            var dirResult = await ScanDirectoryAsync(dir, ct);
            result.FilesFound += dirResult.FilesFound;
            result.FilesAdded += dirResult.FilesAdded;
            result.FilesSkipped += dirResult.FilesSkipped;
            result.Errors += dirResult.Errors;
        }

        return result;
    }

    private async Task<IngestionResult> ScanDirectoryAsync(ManagedSourceDirectory dir, CancellationToken ct)
    {
        var result = new IngestionResult();

        if (!Directory.Exists(dir.SourcePath))
        {
            _logger.LogWarning("Source directory does not exist: {Path}", dir.SourcePath);
            result.Errors++;
            return result;
        }

        var files = Directory.EnumerateFiles(dir.SourcePath, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        result.FilesFound = files.Count;
        _logger.LogInformation("Found {Count} files in {Path}", files.Count, dir.SourcePath);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var metadata = await _fileExtractor.ExtractAsync(file, ct);
                if (metadata == null)
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
                        ct);

                    metadata = enriched;
                }

                var targetPath = _organization.BuildTargetPath(dir.LibraryPath, metadata);
                var moveResult = _organization.MoveFile(file, targetPath);

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

                    if (existingGroup != null)
                    {
                        _groupingService.AddFormatToGroup(
                            existingGroup.Id, targetPath, metadata.FileFormat, isPrimary: false);
                        _logger.LogDebug("Registered alternate format for existing book: {Path}", targetPath);
                        result.FilesSkipped++;
                    }
                    else
                    {
                        result.FilesSkipped++;
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to move file: {Path} - {Error}", file, moveResult.ErrorMessage);
                    result.Errors++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing file: {Path}", file);
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

        if (existingGroup == null && isPrimary)
        {
            var group = _groupingService.CreateGroup(metadata);
            _groupingService.AddFormatToGroup(group.Id, path, metadata.FileFormat, isPrimary: true);
            _logger.LogDebug("Created group for new book: {Title}", metadata.Title);
        }
        else if (existingGroup != null && !isPrimary)
        {
            _groupingService.AddFormatToGroup(existingGroup.Id, path, metadata.FileFormat, isPrimary: false);
            _logger.LogDebug("Added alternate format for existing book: {Title}", metadata.Title);
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
