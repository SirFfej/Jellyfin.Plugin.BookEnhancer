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

            await ProcessGroupAsync(group, ct);
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

        if (primary == null || alternates.Count == 0)
            return;

        var primaryItem = FindItemByPath(primary.FilePath);
        if (primaryItem == null)
        {
            _logger.LogWarning(
                "Primary item not found in library for group {GroupId} at {Path}",
                group.Id,
                primary.FilePath);
            return;
        }

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
