using System.Text;
using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class MetadataEnrichmentTask : IScheduledTask
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly BookGroupingService _groupingService;
    private readonly IApplicationPaths _appPaths;

    public MetadataEnrichmentTask(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        BookGroupingService groupingService,
        IApplicationPaths appPaths)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _groupingService = groupingService;
        _appPaths = appPaths;
    }

    public string Name => "Book Metadata Enrichment";

    public string Key => "BookEnhancerMetadataEnrichment";

    public string Description => "Attempts online enrichment for all library files and reports items that could not be matched.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = _appPaths.LogDirectoryPath;
        using var logger = new TaskLogger(logDir, "MetadataEnrichment");

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            logger.LogError("Plugin configuration not available");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.TrashDirectory))
        {
            logger.LogWarning("Trash directory not configured. All tasks are disabled until a trash directory is set in plugin settings.");
            return;
        }

        var summaryBuffer = new StringBuilder();

        try
        {
            if (!config.UnifiedMetadataEnabled || (!config.HardcoverEnabled && !config.GoogleBooksEnabled && !config.OpenLibraryEnabled && !config.ComicVineEnabled && !config.MetronEnabled && !config.VerseDbEnabled && !config.GrandComicsDbEnabled))
            {
                logger.LogWarning("Online enrichment is disabled in plugin configuration");
                return;
            }

            var dirs = config.ManagedDirectories
                .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath))
                .ToList();

            if (dirs.Count == 0)
            {
                logger.LogWarning("No enabled source directories with library paths configured");
                return;
            }

            var supportedExts = GetSupportedExtensions(config);

            var allFiles = new List<(string Path, ManagedSourceDirectory Dir)>();
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir.LibraryPath))
                {
                    logger.LogWarning($"Library path does not exist: {dir.LibraryPath}");
                    continue;
                }

                var files = Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                    .Where(f => supportedExts.Contains(Path.GetExtension(f)))
                    .ToList();

                allFiles.AddRange(files.Select(f => (f, dir)));
            }

            var total = allFiles.Count;
            var enriched = 0;
            var noIsbn = 0;
            var noMatch = 0;
            var errors = 0;
            var skippedCooldown = 0;
            var unenriched = new List<string>();

            logger.LogInformation($"Found {total} files across {dirs.Count} directories");
            logger.LogInformation(
                $"Enrichment cascade — Hardcover: {(config.HardcoverEnabled && !string.IsNullOrWhiteSpace(config.HardcoverApiKey) ? "enabled" : "disabled")}, " +
                $"Google Books: {(config.GoogleBooksEnabled ? "enabled" : "disabled")}, " +
                $"OpenLibrary: {(config.OpenLibraryEnabled ? "enabled" : "disabled")}, " +
                $"Comic Vine: {(config.ComicVineEnabled && !string.IsNullOrWhiteSpace(config.ComicVineApiKey) ? "enabled" : "disabled")}, " +
                $"Metron: {(config.MetronEnabled && !string.IsNullOrWhiteSpace(config.MetronUsername) && !string.IsNullOrWhiteSpace(config.MetronPassword) ? "enabled" : "disabled")}, " +
                $"VerseDB: {(config.VerseDbEnabled && !string.IsNullOrWhiteSpace(config.VerseDbApiKey) ? "enabled" : "disabled")}, " +
                $"GrandComicsDb: {(config.GrandComicsDbEnabled && !string.IsNullOrWhiteSpace(config.GrandComicsDbUsername) && !string.IsNullOrWhiteSpace(config.GrandComicsDbPassword) ? "enabled" : "disabled")}");

            summaryBuffer.AppendLine("=== Metadata Enrichment Report ===");
            summaryBuffer.AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            summaryBuffer.AppendLine($"Total files: {total}");
            summaryBuffer.AppendLine();

            for (var i = 0; i < allFiles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Enrichment cancelled");
                    break;
                }

                var (filePath, _) = allFiles[i];

                try
                {
                    var metadata = await _fileExtractor.ExtractAsync(filePath, cancellationToken).ConfigureAwait(false);
                    if (metadata is null)
                    {
                        logger.LogError($"Could not extract metadata: {filePath}");
                        errors++;
                        unenriched.Add($"PARSE ERROR: {filePath}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(metadata.Isbn))
                    {
                        var isComic = string.Equals(metadata.FileFormat, "Comic", StringComparison.OrdinalIgnoreCase);
                        if (!isComic)
                        {
                            noIsbn++;
                            unenriched.Add($"NO ISBN: {filePath}");
                            continue;
                        }
                    }

                    var cooldown = config.EnrichmentCooldownDays;
                    if (cooldown > 0)
                    {
                        var lastEnriched = _groupingService.GetLastEnrichmentTime(filePath);
                        if (lastEnriched.HasValue && (DateTime.UtcNow - lastEnriched.Value).TotalDays < cooldown)
                        {
                            skippedCooldown++;
                            logger.LogInformation($"Skipped (cooldown): {filePath}");
                            continue;
                        }
                    }

                    var result = await _enrichment.EnrichAsync(
                        metadata,
                        config.HardcoverApiKey,
                        config.GoogleBooksApiKey,
                        config.HardcoverEnabled,
                        config.GoogleBooksEnabled,
                        config.OpenLibraryEnabled,
                        comicVineEnabled: config.ComicVineEnabled,
                        comicVineApiKey: config.ComicVineApiKey ?? "",
                        metronEnabled: config.MetronEnabled,
                        metronUsername: config.MetronUsername ?? "",
                        metronPassword: config.MetronPassword ?? "",
                        versedbEnabled: config.VerseDbEnabled,
                        versedbApiKey: config.VerseDbApiKey ?? "",
                        grandComicsDbEnabled: config.GrandComicsDbEnabled,
                        grandComicsDbUsername: config.GrandComicsDbUsername ?? "",
                        grandComicsDbPassword: config.GrandComicsDbPassword ?? "",
                        ct: cancellationToken).ConfigureAwait(false);

                    _groupingService.SetLastEnrichmentTime(filePath);

                    var enrichedMeta = result.Metadata;

                    if (result.ApiMatchFound)
                    {
                        enriched++;
                        logger.LogInformation($"Enriched: {filePath} (ISBN: {metadata.Isbn})");
                    }
                    else
                    {
                        noMatch++;
                        unenriched.Add($"NO MATCH: {filePath} (ISBN: {metadata.Isbn})");
                        logger.LogInformation($"No enrichment found: {filePath} (ISBN: {metadata.Isbn})");
                    }

                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error processing {filePath}: {ex.Message}");
                    errors++;
                    unenriched.Add($"ERROR: {filePath} — {ex.Message}");
                }

                if (total > 0)
                    ((IProgress<double>)logger).Report((double)i / total);
            }

            summaryBuffer.AppendLine($"Enriched:       {enriched}");
            summaryBuffer.AppendLine($"No ISBN:        {noIsbn}");
            summaryBuffer.AppendLine($"No match:       {noMatch}");
            summaryBuffer.AppendLine($"Errors:         {errors}");
            summaryBuffer.AppendLine($"Skipped (cooldown): {skippedCooldown}");
            summaryBuffer.AppendLine();

            if (unenriched.Count > 0)
            {
                summaryBuffer.AppendLine($"=== Unenriched Items ({unenriched.Count}) ===");
                summaryBuffer.AppendLine();
                foreach (var item in unenriched)
                    summaryBuffer.AppendLine(item);
                summaryBuffer.AppendLine();
            }
            else
            {
                summaryBuffer.AppendLine("All items were enriched successfully.");
                summaryBuffer.AppendLine();
            }

            summaryBuffer.AppendLine($"Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var summaryPath = Path.Combine(logDir, $"MetadataEnrichment-{DateTime.Now:yyyyMMdd-HHmmss}-summary.log");
            await File.WriteAllTextAsync(summaryPath, summaryBuffer.ToString(), cancellationToken).ConfigureAwait(false);

            logger.LogInformation($"Enrichment complete — Enriched: {enriched}, No ISBN: {noIsbn}, No match: {noMatch}, Errors: {errors}, Skipped (cooldown): {skippedCooldown}");
            logger.LogInformation($"Summary written to: {summaryPath}");
            ((IProgress<double>)logger).Report(1.0);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Enrichment was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Enrichment failed");
            throw;
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
}
