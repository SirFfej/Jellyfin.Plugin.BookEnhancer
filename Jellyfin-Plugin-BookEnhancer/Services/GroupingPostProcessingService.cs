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
    private readonly ILogger<GroupingPostProcessingService> _logger;
    private Func<string, Task>? _logCallback;

    public GroupingPostProcessingService(
        BookGroupingService groupingService,
        ILibraryManager libraryManager,
        ILogger<GroupingPostProcessingService> logger)
    {
        _groupingService = groupingService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private async Task LogInfoAsync(string message)
    {
        if (_logCallback is not null)
            await _logCallback(message).ConfigureAwait(false);
        else
            _logger.LogInformation("{Message}", message);
    }

    private async Task LogWarningAsync(string message)
    {
        if (_logCallback is not null)
            await _logCallback(message).ConfigureAwait(false);
        else
            _logger.LogWarning("{Message}", message);
    }

    private async Task LogWarningAsync(Exception ex, string message)
    {
        if (_logCallback is not null)
            await _logCallback($"{message} — {ex.Message}").ConfigureAwait(false);
        else
            _logger.LogWarning(ex, "{Message}", message);
    }

    public async Task ProcessAllGroupsAsync(Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        _logCallback = logCallback;

        var groups = _groupingService.GetAllGroupsWithMultipleFormats();
        await LogInfoAsync($"Processing {groups.Count} book groups with multiple formats").ConfigureAwait(false);

        foreach (var group in groups)
        {
            if (ct.IsCancellationRequested)
                break;

            await ProcessGroupAsync(group, ct).ConfigureAwait(false);
        }
    }

    public async Task ProcessGroupAsync(BookGroup group, CancellationToken ct = default)
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
            await LogWarningAsync($"Primary item not found in library for group {group.Id} at {primary.FilePath}").ConfigureAwait(false);
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
                if (altItem is not null)
                {
                    await DeleteItemWithRetryAsync(altItem, ct).ConfigureAwait(false);
                }

                if (formatsDir is not null && File.Exists(alternate.FilePath))
                {
                    Directory.CreateDirectory(formatsDir);
                    var fileName = Path.GetFileName(alternate.FilePath);
                    var destPath = Path.Combine(formatsDir, fileName);

                    if (!File.Exists(destPath))
                    {
                        File.Move(alternate.FilePath, destPath);
                    }
                }

                _groupingService.RemoveFormat(alternate.Id);
            }
            catch (Exception ex)
            {
                await LogWarningAsync(ex, $"Failed to process alternate format {alternate.FilePath}").ConfigureAwait(false);
            }
        }

        await LogInfoAsync($"Merged {alternates.Count} alternate formats into primary item '{primaryItem.Name}' (ID: {primaryItem.Id})").ConfigureAwait(false);
    }

    public async Task<RepairResult> RepairFormatPathsAsync(Func<string, Task>? logCallback = null, CancellationToken ct = default)
    {
        _logCallback = logCallback;

        var formats = _groupingService.GetAllFormats();
        var result = new RepairResult
        {
            TotalFormats = formats.Count
        };

        await LogInfoAsync($"Starting format path repair across {formats.Count} formats").ConfigureAwait(false);

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
                await LogWarningAsync($"Format {format.Id} points to path not found in Jellyfin library: {format.FilePath}").ConfigureAwait(false);
            }
        }

        await LogInfoAsync($"Repair complete — Total: {result.TotalFormats}, Fixed: {result.Fixed}, Skipped (OK): {result.Skipped}, Not Found: {result.NotFound}").ConfigureAwait(false);

        return result;
    }

    private BaseItem? FindItemByPath(string path)
    {
        try
        {
            return _libraryManager.FindByPath(path, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to find item by path: {Path}", path);
            return null;
        }
    }

    private async Task DeleteItemWithRetryAsync(BaseItem item, CancellationToken ct)
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
            catch (Exception ex) when (attempt <= MaxDeleteRetries)
            {
                var isLocked = ex.GetType().Name == "SqliteException" && ex.Message.Contains("database is locked");
                if (!isLocked)
                    throw;

                await LogWarningAsync($"Database locked, retrying delete (attempt {attempt}/{MaxDeleteRetries})").ConfigureAwait(false);

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
