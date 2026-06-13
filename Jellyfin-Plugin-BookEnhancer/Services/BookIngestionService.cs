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
    private readonly IFileMetadataWriter _writer;
    private readonly ILogger<BookIngestionService> _logger;
    private Func<string, Task>? _logCallback;

    public BookIngestionService(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        BookGroupingService groupingService,
        LibraryOrganizationService organization,
        IFileMetadataWriter writer,
        ILogger<BookIngestionService> logger)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _groupingService = groupingService;
        _organization = organization;
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
                    var enrichmentResult = await _enrichment.EnrichAsync(
                        metadata,
                        Config.HardcoverApiKey,
                        Config.GoogleBooksApiKey,
                        Config.HardcoverEnabled,
                        Config.GoogleBooksEnabled,
                        Config.OpenLibraryEnabled,
                        titleAuthorSearchEnabled: dir.EnableTitleAuthorSearch,
                        title: metadata.Title,
                        author: metadata.Authors.Count > 0 ? metadata.Authors[0] : null,
                        ct: ct).ConfigureAwait(false);

                    metadata = enrichmentResult.Metadata;

                    if (!string.IsNullOrWhiteSpace(metadata.Isbn) && !enrichmentResult.ApiMatchFound)
                    {
                        result.EnrichmentFailures++;
                        await LogWarningAsync($"ENRICHMENT FAILURE: {file} (ISBN: {metadata.Isbn}) — no online match found").ConfigureAwait(false);
                    }
                }

                var template = string.IsNullOrWhiteSpace(dir.OrganizeTemplate)
                    ? "{Author}/{Series}/{Title}"
                    : dir.OrganizeTemplate;
                var targetPath = _organization.BuildTargetPath(dir.LibraryPath, metadata, template);
                var moveResult = await _organization.MoveFile(file, targetPath, Config?.CopyMode == true, _logCallback).ConfigureAwait(false);

                if (moveResult.Success)
                {
                    _groupingService.RegisterFile(targetPath, metadata, isPrimary: true);
                    result.FilesAdded++;

                    if (dir.EnableMetadataWriting)
                        await _writer.WriteMetadataAsync(targetPath, metadata, ct).ConfigureAwait(false);
                }
                else if (moveResult.Skipped)
                {
                    if (dir.EnableMetadataWriting)
                        await _writer.WriteMetadataAsync(targetPath, metadata, ct).ConfigureAwait(false);

                    var group = _groupingService.RegisterFile(targetPath, metadata, isPrimary: false);
                    result.FilesSkipped++;
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
    public int EnrichmentFailures { get; set; }
}
