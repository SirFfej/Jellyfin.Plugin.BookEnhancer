using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class FullMaintenanceTask : IScheduledTask
{
    private readonly BookIngestionService _ingestionService;
    private readonly GroupingPostProcessingService _groupingService;
    private readonly IApplicationPaths _appPaths;

    public FullMaintenanceTask(
        BookIngestionService ingestionService,
        GroupingPostProcessingService groupingService,
        IApplicationPaths appPaths)
    {
        _ingestionService = ingestionService;
        _groupingService = groupingService;
        _appPaths = appPaths;
    }

    public string Name => "Book Full Maintenance";

    public string Key => "BookEnhancerMaintenance";

    public string Description => "Runs ingestion scan followed by grouping post-processing.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.AutoScanIntervalMinutes <= 0)
            return [];

        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(config.AutoScanIntervalMinutes).Ticks
            }
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = _appPaths.LogDirectoryPath;
        using var logger = new TaskLogger(logDir, "FullMaintenance");

        Func<string, Task> logCallback = msg =>
        {
            logger.LogInformation(msg);
            return Task.CompletedTask;
        };

        try
        {
            logger.LogInformation("Starting full maintenance — ingestion scan...");
            ((IProgress<double>)logger).Report(0.0);

            var ingestionResult = await _ingestionService.ScanAllAsync(logCallback, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                $"Ingestion complete — Found: {ingestionResult.FilesFound}, " +
                $"Added: {ingestionResult.FilesAdded}, " +
                $"Skipped: {ingestionResult.FilesSkipped}, Enrichment failures: {ingestionResult.EnrichmentFailures}, Errors: {ingestionResult.Errors}");
            ((IProgress<double>)logger).Report(0.5);

            logger.LogInformation("Starting grouping post-processing...");
            await _groupingService.ProcessAllGroupsAsync(logCallback, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Grouping process complete");
            ((IProgress<double>)logger).Report(1.0);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Full maintenance was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Full maintenance failed");
            throw;
        }
    }
}
