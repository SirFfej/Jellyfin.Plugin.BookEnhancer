using System.Text;
using System.Threading;
using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class PostOrganizationEnrichmentTask : IScheduledTask
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly BookGroupingService _groupingService;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private int _isRunning;

    public PostOrganizationEnrichmentTask(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        BookGroupingService groupingService,
        ILibraryManager libraryManager,
        IApplicationPaths appPaths)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _groupingService = groupingService;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
    }

    public string Name => "Book Post-Organization Enrichment";

    public string Key => "BookEnhancerPostOrganizationEnrichment";

    public string Description => "Enriches library files that are incomplete or were organized after their last enrichment. Skips locked items and ignores cooldown.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = _appPaths.LogDirectoryPath;
        using var logger = new TaskLogger(logDir, "PostOrganizationEnrichment");

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            logger.LogWarning("Post-organization enrichment is already running; skipping duplicate start");
            return;
        }

        try
        {
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

            var timeoutMinutes = config.MetadataEnrichmentTimeoutMinutes;
            using var timeoutCts = timeoutMinutes > 0 ? new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes)) : null;
            using var linkedCts = timeoutCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                : null;
            var ct = linkedCts?.Token ?? cancellationToken;

            try
            {
                if (timeoutCts is not null)
                    logger.LogInformation($"Post-organization enrichment timeout set to {timeoutMinutes} minutes");

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

                var total = 0;
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir.LibraryPath))
                    {
                        logger.LogWarning($"Library path does not exist: {dir.LibraryPath}");
                        continue;
                    }

                    total += Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                        .Count(f => supportedExts.Contains(Path.GetExtension(f)));
                }

                var enriched = 0;
                var skippedLocked = 0;
                var skippedComplete = 0;
                var noMatch = 0;
                var errors = 0;
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

                summaryBuffer.AppendLine("=== Post-Organization Enrichment Report ===");
                summaryBuffer.AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                summaryBuffer.AppendLine($"Total files: {total}");
                summaryBuffer.AppendLine();

                var processed = 0;
                var cancelled = false;
                foreach (var dir in dirs)
                {
                    if (cancelled || !Directory.Exists(dir.LibraryPath))
                        continue;

                    var files = Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                        .Where(f => supportedExts.Contains(Path.GetExtension(f)));

                    foreach (var filePath in files)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            logger.LogWarning("Post-organization enrichment cancelled");
                            cancelled = true;
                            break;
                        }

                        processed++;

                        try
                        {
                            var metadata = await _fileExtractor.ExtractAsync(filePath, ct).ConfigureAwait(false);
                            if (metadata is null)
                            {
                                logger.LogError($"Could not extract metadata: {filePath}");
                                errors++;
                                unenriched.Add($"PARSE ERROR: {filePath}");
                                continue;
                            }

                            var item = _libraryManager.FindByPath(filePath, false);
                            if (item?.IsLocked == true)
                            {
                                skippedLocked++;
                                logger.LogInformation($"Skipped (locked): {filePath}");
                                continue;
                            }

                            var isComplete = MetadataEnrichmentService.HasCompleteMetadata(metadata);
                            var lastEnriched = _groupingService.GetLastEnrichmentTime(filePath);
                            var fileModified = File.GetLastWriteTimeUtc(filePath);
                            var processedSinceOrganization = lastEnriched is null || lastEnriched.Value < fileModified;

                            if (isComplete && !processedSinceOrganization)
                            {
                                skippedComplete++;
                                logger.LogInformation($"Skipped (complete and already processed since organization): {filePath}");
                                continue;
                            }

                            var apiConfig = config.GetEffectiveApiConfig(dir);
                            var result = await _enrichment.EnrichAsync(
                                metadata,
                                apiConfig,
                                titleAuthorSearchEnabled: dir.EnableTitleAuthorSearch,
                                title: metadata.Title,
                                author: metadata.Authors.Count > 0 ? metadata.Authors[0] : null,
                                ct: ct).ConfigureAwait(false);

                            if (result.ApiMatchFound && MetadataEnrichmentService.HasCompleteMetadata(result.Metadata))
                                _groupingService.SetLastEnrichmentTime(filePath, result.EnrichedBy);

                            if (result.ApiMatchFound)
                            {
                                enriched++;
                                logger.LogInformation($"Enriched: {filePath}");
                            }
                            else
                            {
                                noMatch++;
                                unenriched.Add($"NO MATCH: {filePath}");
                                logger.LogInformation($"No enrichment found: {filePath}");
                            }

                            await Task.Delay(250, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException oce) when (oce.IsCallerCancellation(ct))
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"Error processing {filePath}: {ex.Message}");
                            errors++;
                            unenriched.Add($"ERROR: {filePath} — {ex.Message}");
                        }

                        if (total > 0)
                            ((IProgress<double>)logger).Report((double)processed / total);
                    }
                }

                summaryBuffer.AppendLine($"Enriched:       {enriched}");
                summaryBuffer.AppendLine($"No match:       {noMatch}");
                summaryBuffer.AppendLine($"Errors:         {errors}");
                summaryBuffer.AppendLine($"Skipped (locked): {skippedLocked}");
                summaryBuffer.AppendLine($"Skipped (complete): {skippedComplete}");
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
                    summaryBuffer.AppendLine("All eligible items were enriched successfully.");
                    summaryBuffer.AppendLine();
                }

                summaryBuffer.AppendLine($"Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                var summaryPath = Path.Combine(logDir, $"PostOrganizationEnrichment-{DateTime.Now:yyyyMMdd-HHmmss}-summary.log");
                await File.WriteAllTextAsync(summaryPath, summaryBuffer.ToString(), ct).ConfigureAwait(false);

                logger.LogInformation($"Post-organization enrichment complete — Enriched: {enriched}, No match: {noMatch}, Errors: {errors}, Skipped (locked): {skippedLocked}, Skipped (complete): {skippedComplete}");
                logger.LogInformation($"Summary written to: {summaryPath}");
                ((IProgress<double>)logger).Report(1.0);
            }
            catch (OperationCanceledException oce) when (oce.IsCallerCancellation(ct))
            {
                if (timeoutCts?.IsCancellationRequested == true)
                    logger.LogWarning($"Post-organization enrichment timed out after {timeoutMinutes} minutes");
                else
                    logger.LogWarning("Post-organization enrichment was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Post-organization enrichment failed");
                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
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
