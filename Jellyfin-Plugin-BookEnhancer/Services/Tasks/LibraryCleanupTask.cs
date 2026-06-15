using System.Text;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class LibraryCleanupTask : IScheduledTask
{
    private readonly LibraryCleanupService _cleanupService;
    private readonly IApplicationPaths _appPaths;
    private int _isRunning;

    public LibraryCleanupTask(
        LibraryCleanupService cleanupService,
        IApplicationPaths appPaths)
    {
        _cleanupService = cleanupService;
        _appPaths = appPaths;
    }

    public string Name => "Library Cleanup & Reorganize";

    public string Key => "BookEnhancerLibraryCleanup";

    public string Description => "Reorganizes library files to match current organize template and removes empty directories.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = _appPaths.LogDirectoryPath;
        using var logger = new TaskLogger(logDir, "log_LibraryCleanup");

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            logger.LogWarning("Library cleanup is already running; skipping duplicate start");
            return;
        }

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.TrashDirectory))
            {
                logger.LogWarning("Trash directory not configured. All tasks are disabled until a trash directory is set in plugin settings.");
                return;
            }

            var logBuffer = new StringBuilder();

            var timeoutMinutes = config.LibraryCleanupTimeoutMinutes;
            using var timeoutCts = timeoutMinutes > 0 ? new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes)) : null;
            using var linkedCts = timeoutCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
                : null;
            var ct = linkedCts?.Token ?? cancellationToken;

            try
            {
                logger.LogInformation("Starting library cleanup...");
                if (timeoutCts is not null)
                    logger.LogInformation($"Library cleanup timeout set to {timeoutMinutes} minutes");

                logBuffer.AppendLine("=== Library Cleanup & Reorganize ===");
                logBuffer.AppendLine($"Started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                logBuffer.AppendLine();

                async Task LogCallback(string message)
                {
                    logger.LogInformation(message);
                    logBuffer.AppendLine(message);
                }

                var result = await _cleanupService.RunCleanupAsync(progress, LogCallback, ct).ConfigureAwait(false);

                logBuffer.AppendLine();
                logBuffer.AppendLine("=== Summary ===");
                logBuffer.AppendLine($"Files checked: {result.FilesMoved + result.FilesSkipped}");
                logBuffer.AppendLine($"Files moved:   {result.FilesMoved}");
                logBuffer.AppendLine($"Already correct: {result.FilesSkipped}");
                logBuffer.AppendLine($"Errors:        {result.Errors}");
                logBuffer.AppendLine($"Empty dirs removed: {result.EmptyDirectoriesRemoved}");
                logBuffer.AppendLine($"Completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                var logPath = Path.Combine(logDir, $"log_LibraryCleanup-{DateTime.Now:yyyyMMdd-HHmmss}-summary.log");
                await File.WriteAllTextAsync(logPath, logBuffer.ToString(), ct).ConfigureAwait(false);

                logger.LogInformation($"Cleanup complete. Summary written to: {logPath}");
                ((IProgress<double>)logger).Report(1.0);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts?.IsCancellationRequested == true)
                    logger.LogWarning($"Library cleanup timed out after {timeoutMinutes} minutes");
                else
                    logger.LogWarning("Library cleanup was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Library cleanup failed");
                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
