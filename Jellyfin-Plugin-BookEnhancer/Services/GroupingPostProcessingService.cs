using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class GroupingPostProcessingService
{
    private const int MaxDeleteRetries = 5;
    private const int DeleteRetryBaseDelayMs = 200;

    private readonly BookGroupingService _groupingService;
    private readonly ILibraryManager _libraryManager;
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly ILogger<GroupingPostProcessingService> _logger;

    public GroupingPostProcessingService(
        BookGroupingService groupingService,
        ILibraryManager libraryManager,
        FileMetadataExtractor fileExtractor,
        ILogger<GroupingPostProcessingService> logger)
    {
        _groupingService = groupingService;
        _libraryManager = libraryManager;
        _fileExtractor = fileExtractor;
        _logger = logger;
    }

    private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    private static async Task LogInfoAsync(string message, Func<string, Task>? logCallback, ILogger logger)
    {
        if (logCallback is not null)
            await logCallback(message).ConfigureAwait(false);
        else
            logger.LogInformation("{Message}", message);
    }

    private static async Task LogWarningAsync(string message, Func<string, Task>? logCallback, ILogger logger)
    {
        if (logCallback is not null)
            await logCallback(message).ConfigureAwait(false);
        else
            logger.LogWarning("{Message}", message);
    }

    private static async Task LogWarningAsync(Exception ex, string message, Func<string, Task>? logCallback, ILogger logger)
    {
        if (logCallback is not null)
            await logCallback($"{message} — {ex.Message}").ConfigureAwait(false);
        else
            logger.LogWarning(ex, "{Message}", message);
    }

    public async Task ProcessAllGroupsAsync(Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        var groups = _groupingService.GetAllGroupsWithMultipleFormats();
        await LogInfoAsync($"Processing {groups.Count} book groups with multiple formats", logCallback, _logger).ConfigureAwait(false);

        foreach (var group in groups)
        {
            if (ct.IsCancellationRequested)
                break;

            await ProcessGroupAsync(group, logCallback, ct).ConfigureAwait(false);
        }
    }

    public async Task ScanLibrariesAsync(Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        var config = Config;
        if (config is null)
        {
            await LogInfoAsync("Plugin configuration not available", logCallback, _logger).ConfigureAwait(false);
            return;
        }

        var dirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .ToList();

        if (dirs.Count == 0)
        {
            await LogInfoAsync("No enabled managed directories with library paths configured", logCallback, _logger).ConfigureAwait(false);
            return;
        }

        var supportedExts = GetSupportedExtensions(config);

        var totalRegistered = 0;
        foreach (var dir in dirs)
        {
            if (ct.IsCancellationRequested) break;

            if (!Directory.Exists(dir.LibraryPath))
            {
                await LogWarningAsync($"Library path does not exist: {dir.LibraryPath}", logCallback, _logger).ConfigureAwait(false);
                continue;
            }

            var files = Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                .Where(f => supportedExts.Contains(Path.GetExtension(f)))
                .ToList();

            if (files.Count == 0)
            {
                await LogInfoAsync($"No supported files found in {dir.LibraryPath}", logCallback, _logger).ConfigureAwait(false);
                continue;
            }

            var registered = 0;
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var metadata = await _fileExtractor.ExtractAsync(file, ct).ConfigureAwait(false);
                    if (metadata is null)
                        continue;

                    var group = _groupingService.RegisterFile(file, metadata, isPrimary: true, config.GroupingStrategy);
                    if (group is not null)
                        registered++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await LogWarningAsync($"Failed to scan {file}: {ex.Message}", logCallback, _logger).ConfigureAwait(false);
                }
            }

            await LogInfoAsync($"Registered {registered} files in {files.Count} from {dir.LibraryPath}", logCallback, _logger).ConfigureAwait(false);
            totalRegistered += registered;
        }

        await LogInfoAsync($"Library scan complete — {totalRegistered} files registered in grouping database", logCallback, _logger).ConfigureAwait(false);
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

    public async Task ProcessGroupAsync(BookGroup group, Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        var best = group.Formats.OrderBy(f => BookGroupingService.GetFormatPriority(f.FormatType)).First();
        if (!best.IsPrimary)
        {
            _groupingService.SetPrimaryFormat(group.Id, best.Id);
            group.Formats = _groupingService.GetFormatsForGroup(group.Id);
        }

        var primary = group.Formats.FirstOrDefault(f => f.IsPrimary);
        var alternates = group.Formats.Where(f => !f.IsPrimary).ToList();

        if (primary is null || alternates.Count == 0)
            return;

        var primaryItem = FindItemByPath(primary.FilePath);
        if (primaryItem is null)
        {
            await LogWarningAsync($"Primary item not found in library for group {group.Id} at {primary.FilePath}", logCallback, _logger).ConfigureAwait(false);
            return;
        }

        _groupingService.UpdateFormatJellyfinId(primary.Id, primaryItem.Id.ToString("N"));

        var primaryDir = Path.GetDirectoryName(primary.FilePath);
        var formatsDir = primaryDir is not null
            ? Path.Combine(primaryDir, ".formats")
            : null;

        foreach (var alternate in alternates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var altItem = FindItemByPath(alternate.FilePath);

                if (formatsDir is not null && File.Exists(alternate.FilePath))
                {
                    Directory.CreateDirectory(formatsDir);
                    var fileName = Path.GetFileName(alternate.FilePath);
                    var destPath = Path.Combine(formatsDir, fileName);

                    if (!File.Exists(destPath))
                    {
                        await SafeFileOperations.MoveFileAsync(alternate.FilePath, destPath, ct).ConfigureAwait(false);
                    }
                }

                if (altItem is not null)
                {
                    await DeleteItemWithRetryAsync(altItem, logCallback, ct).ConfigureAwait(false);
                }

                _groupingService.RemoveFormat(alternate.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await LogWarningAsync(ex, $"Failed to process alternate format {alternate.FilePath}", logCallback, _logger).ConfigureAwait(false);
            }
        }

        await LogInfoAsync($"Merged {alternates.Count} alternate formats into primary item '{primaryItem.Name}' (ID: {primaryItem.Id})", logCallback, _logger).ConfigureAwait(false);
    }

    public async Task<RepairResult> RepairFormatPathsAsync(Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        var formats = _groupingService.GetAllFormats();
        var result = new RepairResult
        {
            TotalFormats = formats.Count
        };

        await LogInfoAsync($"Starting format path repair across {formats.Count} formats", logCallback, _logger).ConfigureAwait(false);

        foreach (var format in formats)
        {
            if (ct.IsCancellationRequested)
                break;

            var item = FindItemByPath(format.FilePath);
            if (item is not null)
            {
                var itemIdStr = item.Id.ToString("N");
                if (format.JellyfinItemId != itemIdStr)
                {
                    _groupingService.UpdateFormatJellyfinId(format.Id, itemIdStr);
                    result.Fixed++;
                }
                else
                {
                    result.Skipped++;
                }
            }
            else
            {
                result.NotFound++;
                result.StalePaths.Add(format.FilePath);
                await LogWarningAsync($"Format {format.Id} points to path not found in Jellyfin library: {format.FilePath}", logCallback, _logger).ConfigureAwait(false);
            }
        }

        await LogInfoAsync($"Repair complete — Total: {result.TotalFormats}, Fixed: {result.Fixed}, Skipped (OK): {result.Skipped}, Not Found: {result.NotFound}", logCallback, _logger).ConfigureAwait(false);

        return result;
    }

    private BaseItem? FindItemByPath(string path)
    {
        try
        {
            return _libraryManager.FindByPath(path, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to find item by path: {Path}", path);
            return null;
        }
    }

    private async Task DeleteItemWithRetryAsync(BaseItem item, Func<string, Task>? logCallback, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                _libraryManager.DeleteItem(
                    item,
                    new DeleteOptions { DeleteFileLocation = false },
                    false);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt <= MaxDeleteRetries)
            {
                var isLocked = ex.GetType().Name == "SqliteException" && ex.Message.Contains("database is locked");
                if (!isLocked)
                    throw;

                await LogWarningAsync($"Database locked, retrying delete (attempt {attempt}/{MaxDeleteRetries})", logCallback, _logger).ConfigureAwait(false);

                var delay = DeleteRetryBaseDelayMs * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}

public class RepairResult
{
    public int TotalFormats { get; set; }
    public int Fixed { get; set; }
    public int Skipped { get; set; }
    public int NotFound { get; set; }
    public List<string> StalePaths { get; set; } = new();
}
