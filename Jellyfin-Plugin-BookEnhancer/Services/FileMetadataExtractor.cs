using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services.Parsers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class FileMetadataExtractor
{
    private readonly IReadOnlyList<IFileParser> _parsers;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FileMetadataExtractor> _logger;

    public FileMetadataExtractor(
        ILibraryManager libraryManager,
        ILogger<FileMetadataExtractor> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _parsers =
        [
            new EpubParser(),
            new ComicInfoParser(),
            new PdfParser(),
            new AudioParser()
        ];
    }

    public async Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        FileMetadata? result = null;

        foreach (var parser in _parsers)
        {
            if (parser.CanParse(filePath))
            {
                try
                {
                    result = await parser.ExtractAsync(filePath, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Parser {Parser} failed for {Path}", parser.GetType().Name, filePath);
                }

                break;
            }
        }

        if (result is not null)
        {
            FillNullsFromDb(filePath, result);
            return result;
        }

        return TryCreateFromDb(filePath);
    }

    private void FillNullsFromDb(string filePath, FileMetadata metadata)
    {
        try
        {
            var item = _libraryManager.FindByPath(filePath, false);
            if (item is null) return;

            var filled = 0;

            if (string.IsNullOrWhiteSpace(metadata.Title))
            {
                metadata.Title = item.Name;
                filled++;
            }

            if (string.IsNullOrWhiteSpace(metadata.Description))
            {
                metadata.Description = item.Overview;
                filled++;
            }

            if (string.IsNullOrWhiteSpace(metadata.Publisher) && item.Studios is { Length: > 0 })
            {
                metadata.Publisher = item.Studios[0];
                filled++;
            }

            if (string.IsNullOrWhiteSpace(metadata.SeriesName) && item is Book book && !string.IsNullOrWhiteSpace(book.SeriesName))
            {
                metadata.SeriesName = book.SeriesName;
                filled++;
            }

            if (!metadata.SeriesIndex.HasValue && item.IndexNumber.HasValue)
            {
                metadata.SeriesIndex = item.IndexNumber.Value;
                filled++;
            }

            if (!metadata.PublishYear.HasValue && item.ProductionYear.HasValue)
            {
                metadata.PublishYear = item.ProductionYear.Value;
                filled++;
            }

            if (!metadata.PublishDate.HasValue && item.PremiereDate.HasValue)
            {
                metadata.PublishDate = item.PremiereDate.Value;
                filled++;
            }

            if (string.IsNullOrWhiteSpace(metadata.Isbn) && item.ProviderIds?.TryGetValue("Isbn", out var isbn) == true && !string.IsNullOrWhiteSpace(isbn))
            {
                metadata.Isbn = isbn;
                filled++;
            }

            if (string.IsNullOrWhiteSpace(metadata.Asin) && item.ProviderIds?.TryGetValue("Asin", out var asin) == true && !string.IsNullOrWhiteSpace(asin))
            {
                metadata.Asin = asin;
                filled++;
            }

            if (metadata.Genres.Count == 0 && item.Genres is { Length: > 0 })
            {
                foreach (var genre in item.Genres)
                {
                    if (!string.IsNullOrWhiteSpace(genre))
                        metadata.Genres.Add(genre);
                }
                filled++;
            }

            if (filled > 0)
                _logger.LogDebug("Jellyfin DB filled {Count} fields for {Path}", filled, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin DB null-fill failed for {Path}", filePath);
        }
    }

    private FileMetadata? TryCreateFromDb(string filePath)
    {
        try
        {
            var item = _libraryManager.FindByPath(filePath, false);
            if (item is null) return null;

            var meta = new FileMetadata
            {
                FilePath = filePath,
                FileFormat = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
                Title = item.Name,
                Description = item.Overview,
                Publisher = item.Studios is { Length: > 0 } ? item.Studios[0] : null
            };

            if (item is Book book)
                meta.SeriesName = book.SeriesName;

            if (item.IndexNumber.HasValue)
                meta.SeriesIndex = item.IndexNumber.Value;

            if (item.ProductionYear.HasValue)
                meta.PublishYear = item.ProductionYear.Value;

            if (item.PremiereDate.HasValue)
                meta.PublishDate = item.PremiereDate.Value;

            if (item.ProviderIds is not null)
            {
                if (item.ProviderIds.TryGetValue("Isbn", out var isbn) && !string.IsNullOrWhiteSpace(isbn))
                    meta.Isbn = isbn;
                if (item.ProviderIds.TryGetValue("Asin", out var asin) && !string.IsNullOrWhiteSpace(asin))
                    meta.Asin = asin;
            }

            if (item.Genres is { Length: > 0 })
            {
                foreach (var genre in item.Genres)
                {
                    if (!string.IsNullOrWhiteSpace(genre))
                        meta.Genres.Add(genre);
                }
            }

            return meta;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin DB fallback failed for {Path}", filePath);
            return null;
        }
    }
}
