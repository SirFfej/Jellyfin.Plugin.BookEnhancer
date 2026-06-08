using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class LibraryCleanupTask : IScheduledTask
{
    private readonly LibraryCleanupService _cleanupService;
    private readonly IApplicationPaths _appPaths;

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

        var logBuffer = new StringBuilder();

        try
        {
            logger.LogInformation("Starting library cleanup...");
            logBuffer.AppendLine("=== Library Cleanup & Reorganize ===");
            logBuffer.AppendLine($"Started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            logBuffer.AppendLine();

            async Task LogCallback(string message)
            {
                logger.LogInformation(message);
                logBuffer.AppendLine(message);
            }

            var result = await _cleanupService.RunCleanupAsync(progress, LogCallback, cancellationToken).ConfigureAwait(false);

            logBuffer.AppendLine();
            logBuffer.AppendLine("=== Summary ===");
            logBuffer.AppendLine($"Files checked: {result.FilesMoved + result.FilesSkipped}");
            logBuffer.AppendLine($"Files moved:   {result.FilesMoved}");
            logBuffer.AppendLine($"Already correct: {result.FilesSkipped}");
            logBuffer.AppendLine($"Errors:        {result.Errors}");
            logBuffer.AppendLine($"Empty dirs removed: {result.EmptyDirectoriesRemoved}");
            logBuffer.AppendLine($"Completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var logPath = Path.Combine(logDir, $"log_LibraryCleanup-{DateTime.Now:yyyyMMdd-HHmmss}-summary.log");
            await File.WriteAllTextAsync(logPath, logBuffer.ToString(), cancellationToken).ConfigureAwait(false);

            logger.LogInformation($"Cleanup complete. Summary written to: {logPath}");
            ((IProgress<double>)logger).Report(1.0);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Library cleanup was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Library cleanup failed");
            throw;
        }
    }
}
