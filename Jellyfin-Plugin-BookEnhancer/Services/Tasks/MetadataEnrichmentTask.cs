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
    private readonly IApplicationPaths _appPaths;

    public MetadataEnrichmentTask(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        IApplicationPaths appPaths)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
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

        var summaryBuffer = new StringBuilder();

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                logger.LogError("Plugin configuration not available");
                return;
            }

            if (!config.UnifiedMetadataEnabled || (!config.HardcoverEnabled && !config.GoogleBooksEnabled && !config.OpenLibraryEnabled))
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
            var unenriched = new List<string>();

            logger.LogInformation($"Found {total} files across {dirs.Count} directories");
            logger.LogInformation(
                $"Enrichment cascade — Hardcover: {(config.HardcoverEnabled && !string.IsNullOrWhiteSpace(config.HardcoverApiKey) ? "enabled" : "disabled")}, " +
                $"Google Books: {(config.GoogleBooksEnabled ? "enabled" : "disabled")}, " +
                $"OpenLibrary: {(config.OpenLibraryEnabled ? "enabled" : "disabled")}");

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
                        noIsbn++;
                        unenriched.Add($"NO ISBN: {filePath}");
                        continue;
                    }

                    // Snapshot fields before enrichment to detect changes
                    var beforeTitle = metadata.Title;
                    var beforeAuthorFirst = metadata.Authors.Count > 0 ? metadata.Authors[0] : null;
                    var beforePublisher = metadata.Publisher;
                    var beforeSeries = metadata.SeriesName;
                    var beforeGenreCount = metadata.Genres.Count;
                    var beforeHasDescription = !string.IsNullOrWhiteSpace(metadata.Description);

                    var enrichedMeta = await _enrichment.EnrichAsync(
                        metadata,
                        config.HardcoverApiKey,
                        config.GoogleBooksApiKey,
                        config.HardcoverEnabled,
                        config.GoogleBooksEnabled,
                        config.OpenLibraryEnabled,
                        ct: cancellationToken).ConfigureAwait(false);

                    var titleChanged = enrichedMeta.Title != beforeTitle;
                    var authorChanged = enrichedMeta.Authors.Count > 0 &&
                        (beforeAuthorFirst is null || !string.Equals(enrichedMeta.Authors[0], beforeAuthorFirst, StringComparison.OrdinalIgnoreCase));
                    var publisherChanged = enrichedMeta.Publisher != beforePublisher;
                    var seriesChanged = enrichedMeta.SeriesName != beforeSeries;
                    var genresAdded = enrichedMeta.Genres.Count > beforeGenreCount;
                    var descriptionAdded = !string.IsNullOrWhiteSpace(enrichedMeta.Description) && !beforeHasDescription;

                    if (titleChanged || authorChanged || publisherChanged || seriesChanged || genresAdded || descriptionAdded)
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

            summaryBuffer.AppendLine($"Enriched:      {enriched}");
            summaryBuffer.AppendLine($"No ISBN:       {noIsbn}");
            summaryBuffer.AppendLine($"No match:      {noMatch}");
            summaryBuffer.AppendLine($"Errors:        {errors}");
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

            logger.LogInformation($"Enrichment complete — Enriched: {enriched}, No ISBN: {noIsbn}, No match: {noMatch}, Errors: {errors}");
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
