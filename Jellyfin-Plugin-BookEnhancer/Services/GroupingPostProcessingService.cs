using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class GroupingPostProcessingService
{
    private readonly BookGroupingService _groupingService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<GroupingPostProcessingService> _logger;

    public GroupingPostProcessingService(
        BookGroupingService groupingService,
        ILibraryManager libraryManager,
        ILogger<GroupingPostProcessingService> logger)
    {
        _groupingService = groupingService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task ProcessAllGroupsAsync(CancellationToken ct = default)
    {
        var groups = _groupingService.GetAllGroupsWithMultipleFormats();
        _logger.LogInformation("Processing {Count} book groups with multiple formats", groups.Count);

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
            _logger.LogDebug(
                "Reassigning primary from current to higher-priority format {Format} ({Path})",
                best.FormatType,
                best.FilePath);

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
            _logger.LogWarning(
                "Primary item not found in library for group {GroupId} at {Path}",
                group.Id,
                primary.FilePath);
            return;
        }

        _groupingService.UpdateFormatJellyfinId(primary.Id, primaryItem.Id.ToString("N"));

        var primaryDir = Path.GetDirectoryName(primary.FilePath);
        var formatsDir = primaryDir != null
            ? Path.Combine(primaryDir, ".formats")
            : null;

        foreach (var alternate in alternates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var altItem = FindItemByPath(alternate.FilePath);
                if (altItem != null)
                {
                    _logger.LogDebug(
                        "Removing duplicate item {ItemId} for {Path}",
                        altItem.Id,
                        alternate.FilePath);

                    _libraryManager.DeleteItem(
                        altItem,
                        new DeleteOptions { DeleteFileLocation = false },
                        false);
                }

                if (formatsDir != null && File.Exists(alternate.FilePath))
                {
                    Directory.CreateDirectory(formatsDir);
                    var fileName = Path.GetFileName(alternate.FilePath);
                    var destPath = Path.Combine(formatsDir, fileName);

                    if (!File.Exists(destPath))
                    {
                        File.Move(alternate.FilePath, destPath);
                        _logger.LogDebug("Moved {Src} -> {Dst}", alternate.FilePath, destPath);
                    }
                }

                _groupingService.RemoveFormat(alternate.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process alternate format {Path}", alternate.FilePath);
            }
        }

        _logger.LogInformation(
            "Merged {Count} alternate formats into primary item '{Title}' (ID: {ItemId})",
            alternates.Count,
            primaryItem.Name,
            primaryItem.Id);
    }

    public async Task<RepairResult> RepairFormatPathsAsync(CancellationToken ct = default)
    {
        var formats = _groupingService.GetAllFormats();
        var result = new RepairResult
        {
            TotalFormats = formats.Count
        };

        _logger.LogInformation("Starting format path repair across {Count} formats", formats.Count);

        foreach (var format in formats)
        {
            if (ct.IsCancellationRequested)
                break;

            var item = FindItemByPath(format.FilePath);
            if (item != null)
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
                _logger.LogWarning(
                    "Format {FormatId} points to path not found in Jellyfin library: {Path}",
                    format.Id,
                    format.FilePath);
            }
        }

        _logger.LogInformation(
            "Repair complete — Total: {Total}, Fixed: {Fixed}, Skipped (OK): {Skipped}, Not Found: {NotFound}",
            result.TotalFormats,
            result.Fixed,
            result.Skipped,
            result.NotFound);

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
}

public class RepairResult
{
    public int TotalFormats { get; set; }
    public int Fixed { get; set; }
    public int Skipped { get; set; }
    public int NotFound { get; set; }
    public List<string> StalePaths { get; set; } = new();
}
