using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class FullMaintenanceTask : IScheduledTask
{
    private readonly LibraryCleanupService _cleanupService;
    private readonly GroupingPostProcessingService _groupingService;
    private readonly IApplicationPaths _appPaths;

    public FullMaintenanceTask(
        LibraryCleanupService cleanupService,
        GroupingPostProcessingService groupingService,
        IApplicationPaths appPaths)
    {
        _cleanupService = cleanupService;
        _groupingService = groupingService;
        _appPaths = appPaths;
    }

    public string Name => "Book Full Maintenance";

    public string Key => "BookEnhancerMaintenance";

    public string Description => "Reorganizes library files and runs grouping post-processing.";

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
            logger.LogInformation("Starting full maintenance — library cleanup...");
            ((IProgress<double>)logger).Report(0.0);

            var cleanupResult = await _cleanupService.RunCleanupAsync(progress, logCallback, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                $"Library cleanup complete — " +
                $"Moved: {cleanupResult.FilesMoved}, Skipped: {cleanupResult.FilesSkipped}, " +
                $"Errors: {cleanupResult.Errors}, Duplicates: {cleanupResult.DuplicatesFound}");
            ((IProgress<double>)logger).Report(0.5);

            logger.LogInformation("Scanning libraries for grouping...");
            await _groupingService.ScanLibrariesAsync(logCallback, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Starting grouping post-processing...");
            await _groupingService.ProcessAllGroupsAsync(logCallback, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Full maintenance complete");
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
