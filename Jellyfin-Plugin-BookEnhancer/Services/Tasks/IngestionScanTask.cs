using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class IngestionScanTask : IScheduledTask
{
    private readonly BookIngestionService _ingestionService;
    private readonly IApplicationPaths _appPaths;

    public IngestionScanTask(
        BookIngestionService ingestionService,
        IApplicationPaths appPaths)
    {
        _ingestionService = ingestionService;
        _appPaths = appPaths;
    }

    public string Name => "Book Ingestion Scan";

    public string Key => "BookEnhancerIngestionScan";

    public string Description => "Scans enabled source directories and imports book files into the library.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = _appPaths.LogDirectoryPath;
        using var logger = new TaskLogger(logDir, "IngestionScan", useDailyFile: true);

        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.TrashDirectory))
        {
            logger.LogWarning("Trash directory not configured. All tasks are disabled until a trash directory is set in plugin settings.");
            return;
        }

        Func<string, Task> logCallback = msg =>
        {
            logger.LogInformation(msg);
            return Task.CompletedTask;
        };

        try
        {
            logger.LogInformation("Starting ingestion scan...");
            var result = await _ingestionService.ScanAllAsync(logCallback, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                $"Scan complete — Found: {result.FilesFound}, Added: {result.FilesAdded}, " +
                $"Skipped: {result.FilesSkipped}, Enrichment failures: {result.EnrichmentFailures}, Errors: {result.Errors}");
            ((IProgress<double>)logger).Report(1.0);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Ingestion scan was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion scan failed");
            throw;
        }
    }
}
