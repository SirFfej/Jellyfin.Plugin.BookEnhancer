using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Api;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Route("Books/Grouping")]
public class GroupingController : ControllerBase
{
    private readonly BookGroupingService _groupingService;
    private readonly GroupingPostProcessingService _postProcessing;
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly ILogger<GroupingController> _logger;

    public GroupingController(
        BookGroupingService groupingService,
        GroupingPostProcessingService postProcessing,
        FileMetadataExtractor fileExtractor,
        ILogger<GroupingController> logger)
    {
        _groupingService = groupingService;
        _postProcessing = postProcessing;
        _fileExtractor = fileExtractor;
        _logger = logger;
    }

    [HttpPost("Process")]
    [Authorize]
    public async Task<ActionResult<GroupingProcessResult>> Process()
    {
        var groups = _groupingService.GetAllGroupsWithMultipleFormats();
        var result = new GroupingProcessResult
        {
            ProcessedGroups = groups.Count,
            TotalFormatsMerged = groups.Sum(g => g.Formats.Count(f => !f.IsPrimary))
        };

        await _postProcessing.ProcessAllGroupsAsync();

        return Ok(result);
    }

    [HttpGet("Preview")]
    [Authorize]
    public async Task<ActionResult<GroupingPreviewResult>> Preview(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return BadRequest("Plugin not initialized.");

        var dirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath) && Directory.Exists(d.LibraryPath))
            .ToList();

        var supportedExts = GetSupportedExtensions(config);
        var result = new GroupingPreviewResult
        {
            MatchingStrategy = config.GroupingStrategy ?? "IsbnOnly"
        };

        var metadataList = new List<(FileMetadata Meta, string FilePath)>();

        foreach (var dir in dirs)
        {
            var files = Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                .Where(f => supportedExts.Contains(Path.GetExtension(f)))
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var meta = await _fileExtractor.ExtractAsync(file, ct);
                    if (meta != null)
                        metadataList.Add((meta, file));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping file (metadata extraction failed): {File}", file);
                }
            }
        }

        result.TotalFiles = metadataList.Count;

        var groups = new List<PreviewGroup>();

        foreach (var (meta, path) in metadataList)
        {
            var matchKey = BuildMatchKey(meta);
            var existing = groups.FirstOrDefault(g => g.MatchKey == matchKey);

            var formatType = GetFormatType(path);
            var priority = BookGroupingService.GetFormatPriority(formatType);

            if (existing != null)
            {
                existing.Formats.Add(new PreviewFormat
                {
                    FilePath = path,
                    FormatType = formatType,
                    Priority = priority
                });
            }
            else
            {
                groups.Add(new PreviewGroup
                {
                    MatchKey = matchKey,
                    Title = meta.Title,
                    Author = meta.Authors.Count > 0 ? string.Join("; ", meta.Authors) : null,
                    Formats = new List<PreviewFormat>
                    {
                        new()
                        {
                            FilePath = path,
                            FormatType = formatType,
                            Priority = priority
                        }
                    }
                });
            }
        }

        result.Groups = groups.Where(g => g.Formats.Count > 1).ToList();
        result.GroupedCount = result.Groups.Count;
        result.UngroupedCount = groups.Count(g => g.Formats.Count == 1);

        return Ok(result);
    }

    [HttpPost("Repair")]
    [Authorize]
    public async Task<ActionResult<RepairResult>> Repair(CancellationToken ct)
    {
        var result = await _postProcessing.RepairFormatPathsAsync(ct);
        return Ok(result);
    }

    private static string BuildMatchKey(FileMetadata meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.Isbn))
            return $"isbn:{meta.Isbn}";

        var title = meta.Title?.Trim().ToLowerInvariant() ?? "";
        var author = meta.Authors.Count > 0 ? meta.Authors[0].Trim().ToLowerInvariant() : "";
        return $"title:{title}|author:{author}";
    }

    private static string GetFormatType(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "EPUB" => "EPUB",
            "MOBI" => "MOBI",
            "PDF" => "PDF",
            "CBZ" or "CBR" or "CB7" => "Comic",
            "MP3" or "M4A" or "M4B" or "FLAC" or "OGG" or "WMA" or "OPUS" or "AIFF" => "Audio",
            _ => ext ?? "Unknown"
        };
    }

    private static HashSet<string> GetSupportedExtensions(PluginConfiguration config)
    {
        if (config.IngestionFileExtensions == null || config.IngestionFileExtensions.Count == 0)
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
}
