using System.Globalization;
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
    public async Task<ActionResult<GroupingProcessResult>> Process(CancellationToken ct)
    {
        var groups = _groupingService.GetAllGroupsWithMultipleFormats();
        var result = new GroupingProcessResult
        {
            ProcessedGroups = groups.Count,
            TotalFormatsMerged = groups.Sum(g => g.Formats.Count(f => !f.IsPrimary))
        };

        await _postProcessing.ProcessAllGroupsAsync(ct: ct).ConfigureAwait(false);

        return Ok(result);
    }

    [HttpGet("Preview")]
    [Authorize]
    public async Task<ActionResult<GroupingPreviewResult>> Preview(CancellationToken ct)
    {
        const int maxCandidates = 5;
        const int maxFilesToScan = 500;

        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return BadRequest("Plugin not initialized.");

        var dirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath) && Directory.Exists(d.LibraryPath))
            .ToList();

        var supportedExts = GetSupportedExtensions(config);
        var result = new GroupingPreviewResult
        {
            MatchingStrategy = config.GroupingStrategy ?? "IsbnOnly"
        };

        var groups = new List<PreviewGroup>();
        var filesScanned = 0;
        var candidatesFound = 0;
        var totalFiles = 0;

        foreach (var dir in dirs)
        {
            if (candidatesFound >= maxCandidates)
                break;

            try
            {
                var files = Directory.EnumerateFiles(dir.LibraryPath, "*", SearchOption.AllDirectories)
                    .Where(f => supportedExts.Contains(Path.GetExtension(f)));

                foreach (var file in files)
                {
                    if (filesScanned >= maxFilesToScan || candidatesFound >= maxCandidates)
                        break;

                    totalFiles++;
                    filesScanned++;

                    try
                    {
                        var meta = await _fileExtractor.ExtractAsync(file, ct).ConfigureAwait(false);
                        if (meta is null)
                            continue;

                        var matchKey = BuildMatchKey(meta, config.GroupingStrategy ?? "IsbnOnly");
                        if (string.IsNullOrWhiteSpace(matchKey))
                            continue;

                        var formatType = GetFormatType(file);
                        var priority = BookGroupingService.GetFormatPriority(formatType);

                        var existing = groups.Find(g => g.MatchKey == matchKey);
                        if (existing is null)
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
                                        FilePath = file,
                                        FormatType = formatType,
                                        Priority = priority
                                    }
                                }
                            });
                        }
                        else
                        {
                            existing.Formats.Add(new PreviewFormat
                            {
                                FilePath = file,
                                FormatType = formatType,
                                Priority = priority
                            });

                            if (existing.Formats.Count == 2)
                                candidatesFound++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Skipping file (metadata extraction failed): {File}", file);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning directory for preview: {Path}", dir.LibraryPath);
            }
        }

        result.TotalFiles = totalFiles;
        result.IsPartial = filesScanned >= maxFilesToScan || candidatesFound >= maxCandidates;
        result.Groups = groups.Where(g => g.Formats.Count > 1).ToList();
        result.GroupedCount = result.Groups.Count;
        result.UngroupedCount = groups.Count(g => g.Formats.Count == 1);

        return Ok(result);
    }

    [HttpPost("Repair")]
    [Authorize]
    public async Task<ActionResult<RepairResult>> Repair(CancellationToken ct)
    {
        var result = await _postProcessing.RepairFormatPathsAsync(ct: ct).ConfigureAwait(false);
        return Ok(result);
    }

    private static string BuildMatchKey(FileMetadata meta, string strategy)
    {
        var normalizedIsbn = !string.IsNullOrWhiteSpace(meta.Isbn)
            ? $"isbn:{new string(meta.Isbn.Where(char.IsDigit).ToArray())}"
            : string.Empty;

        var title = meta.Title?.Trim().ToLowerInvariant() ?? string.Empty;
        var author = meta.Authors.Count > 0 ? meta.Authors[0].Trim().ToLowerInvariant() : string.Empty;
        var titleAuthorKey = !string.IsNullOrWhiteSpace(title)
            ? $"title:{title}|author:{author}"
            : string.Empty;

        var prefix = !string.IsNullOrWhiteSpace(meta.SeriesName)
            ? meta.SeriesName.Trim().ToLowerInvariant()
            : title;
        var fileNamePrefixKey = !string.IsNullOrWhiteSpace(prefix)
            ? $"prefix:{prefix}"
            : string.Empty;

        var isComic = meta.IsComic;
        var series = !string.IsNullOrWhiteSpace(meta.SeriesName)
            ? meta.SeriesName.Trim().ToLowerInvariant()
            : title;
        var issue = !string.IsNullOrWhiteSpace(meta.SeriesNumber)
            ? meta.SeriesNumber.Trim().ToLowerInvariant()
            : (meta.SeriesIndex.HasValue ? meta.SeriesIndex.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        var comicIssueKey = isComic && !string.IsNullOrWhiteSpace(series) && !string.IsNullOrWhiteSpace(issue)
            ? $"series:{series}|issue:{issue}"
            : titleAuthorKey;

        if (strategy.Equals("IsbnOnly", StringComparison.OrdinalIgnoreCase))
            return normalizedIsbn;

        if (strategy.Equals("TitleAuthor", StringComparison.OrdinalIgnoreCase))
            return titleAuthorKey;

        if (strategy.Equals("FileNamePrefix", StringComparison.OrdinalIgnoreCase))
            return fileNamePrefixKey;

        if (strategy.Equals("ComicIssue", StringComparison.OrdinalIgnoreCase))
            return comicIssueKey;

        // "Both" — ISBN first, then title/author
        if (!string.IsNullOrWhiteSpace(normalizedIsbn))
            return normalizedIsbn;

        return titleAuthorKey;
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
        if (config.IngestionFileExtensions is null || config.IngestionFileExtensions.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".epub", ".mobi", ".pdf",
                ".cbz", ".cbr", ".cb7",
                ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
            };
        }

        return new HashSet<string>(config.IngestionFileExtensions, StringComparer.OrdinalIgnoreCase);
    }
}
