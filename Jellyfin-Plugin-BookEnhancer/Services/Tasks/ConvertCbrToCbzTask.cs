using System.Text;
using System.Threading;
using Jellyfin.Plugin.BookEnhancer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.BookEnhancer.Services.Tasks;

public class ConvertCbrToCbzTask : IScheduledTask
{
    private readonly CbrToCbzService _service;
    private readonly IApplicationPaths _appPaths;
    private int _isRunning;

    public ConvertCbrToCbzTask(CbrToCbzService service, IApplicationPaths appPaths)
    {
        _service = service;
        _appPaths = appPaths;
    }

    public string Name => "Convert CBR/CB7/PDF to CBZ";

    public string Key => "BookEnhancerConvertCbrToCbz";

    public string Description => "Converts CBR/CB7 archives and PDF comic files in Comic Library directories to CBZ, enriches metadata, and writes ComicInfo.xml.";

    public string Category => "BookEnhancers";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var logDir = _appPaths.LogDirectoryPath;
        using var logger = new TaskLogger(logDir, "ConvertCbrToCbz");

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            logger.LogWarning("Convert CBR/CB7 to CBZ is already running; skipping duplicate start");
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

            var dirs = config.ManagedDirectories
                .Where(d => d.Enabled && d.IsComicLibrary && !string.IsNullOrWhiteSpace(d.LibraryPath))
                .ToList();

            if (dirs.Count == 0)
            {
                logger.LogWarning("No enabled managed directories flagged as Comic Library");
                return;
            }

            var summaryBuffer = new StringBuilder();
            summaryBuffer.AppendLine("=== Convert CBR/CB7 to CBZ Report ===");
            summaryBuffer.AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            summaryBuffer.AppendLine();

            var filesFoundTotal = 0;
            var convertedTotal = 0;
            var errorsTotal = 0;

            logger.LogInformation($"Scanning {dirs.Count} comic library directories for CBR/CB7/PDF archives");

            foreach (var dir in dirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(dir.LibraryPath))
                {
                    logger.LogWarning($"Library path does not exist: {dir.LibraryPath}");
                    continue;
                }

                logger.LogInformation($"Processing directory: {dir.LibraryPath}");
                var result = await _service.ConvertAsync(
                    dir.LibraryPath,
                    msg =>
                    {
                        logger.LogInformation(msg);
                        return Task.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false);

                filesFoundTotal += result.FilesFound;
                convertedTotal += result.Converted;
                errorsTotal += result.Errors;

                summaryBuffer.AppendLine($"[{dir.LibraryPath}] Found: {result.FilesFound}, Converted: {result.Converted}, Errors: {result.Errors}");
                if (!string.IsNullOrWhiteSpace(result.FailureLogPath))
                    summaryBuffer.AppendLine($"  Failure log: {result.FailureLogPath}");
            }

            summaryBuffer.AppendLine();
            summaryBuffer.AppendLine($"Total — Found: {filesFoundTotal}, Converted: {convertedTotal}, Errors: {errorsTotal}");
            summaryBuffer.AppendLine($"Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            var summaryPath = Path.Combine(logDir, $"ConvertCbrToCbz-{DateTime.Now:yyyyMMdd-HHmmss}-summary.log");
            await File.WriteAllTextAsync(summaryPath, summaryBuffer.ToString(), cancellationToken).ConfigureAwait(false);

            logger.LogInformation($"Conversion complete — Found: {filesFoundTotal}, Converted: {convertedTotal}, Errors: {errorsTotal}");
            logger.LogInformation($"Summary written to: {summaryPath}");
            progress.Report(1.0);
        }
        catch (OperationCanceledException oce) when (oce.IsCallerCancellation(cancellationToken))
        {
            logger.LogWarning("Convert CBR/CB7 to CBZ was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Convert CBR/CB7 to CBZ failed");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
