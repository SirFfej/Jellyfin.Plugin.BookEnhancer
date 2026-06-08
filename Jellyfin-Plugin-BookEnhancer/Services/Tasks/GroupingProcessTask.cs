using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class GroupingProcessTask : IScheduledTask
{
    private readonly GroupingPostProcessingService _groupingService;
    private readonly IApplicationPaths _appPaths;

    public GroupingProcessTask(
        GroupingPostProcessingService groupingService,
        IApplicationPaths appPaths)
    {
        _groupingService = groupingService;
        _appPaths = appPaths;
    }

    public string Name => "Book Grouping Process";

    public string Key => "BookEnhancerGroupingProcess";

    public string Description => "Processes book groups and merges alternate-format files into primary items.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = Path.Combine(_appPaths.DataPath, "plugins", "BookEnhancer", "logs");
        using var logger = new TaskLogger(logDir, "GroupingProcess");

        Func<string, Task> logCallback = msg =>
        {
            logger.LogInformation(msg);
            return Task.CompletedTask;
        };

        try
        {
            logger.LogInformation("Starting grouping post-processing...");
            await _groupingService.ProcessAllGroupsAsync(logCallback, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Grouping process complete");
            ((IProgress<double>)logger).Report(1.0);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Grouping process was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grouping process failed");
            throw;
        }
    }
}
