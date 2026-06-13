using Jellyfin.Data.Enums;
using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Providers;

public class UnifiedMetadataProvider : IRemoteMetadataProvider<Book, BookInfo>, IHasOrder
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly BookGroupingService _grouping;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileMetadataWriter _writer;
    private readonly ILogger<UnifiedMetadataProvider> _logger;

    public UnifiedMetadataProvider(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        BookGroupingService grouping,
        ILibraryManager libraryManager,
        IFileMetadataWriter writer,
        ILogger<UnifiedMetadataProvider> logger)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _grouping = grouping;
        _libraryManager = libraryManager;
        _writer = writer;
        _logger = logger;
    }

    public string Name => "BookEnhancer";

    public int Order => -2;

    public async Task<MetadataResult<Book>> GetMetadata(BookInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Book>();
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.UnifiedMetadataEnabled) return result;

        if (!IsLibrarySelected(info.Path))
            return result;

        try
        {
            var fileMeta = await ExtractFileMetadata(info.Path, cancellationToken).ConfigureAwait(false);
            if (fileMeta is null) return result;

            var dir = FindMatchingDirectory(config, info.Path);
            var titleAuthorEnabled = dir?.EnableTitleAuthorSearch ?? true;

            var enrichmentResult = await _enrichment.EnrichAsync(
                fileMeta,
                config.HardcoverApiKey,
                config.GoogleBooksApiKey,
                config.HardcoverEnabled,
                config.GoogleBooksEnabled,
                config.OpenLibraryEnabled,
                comicVineEnabled: config.ComicVineEnabled,
                comicVineApiKey: config.ComicVineApiKey ?? "",
                metronEnabled: config.MetronEnabled,
                metronApiKey: config.MetronApiKey ?? "",
                versedbEnabled: config.VerseDbEnabled,
                versedbApiKey: config.VerseDbApiKey ?? "",
                titleAuthorSearchEnabled: titleAuthorEnabled,
                title: fileMeta.Title,
                author: fileMeta.Authors.Count > 0 ? fileMeta.Authors[0] : null,
                ct: cancellationToken).ConfigureAwait(false);

            var enriched = enrichmentResult.Metadata;

            if (string.IsNullOrWhiteSpace(enriched.Title)) return result;

            if (dir?.EnableMetadataWriting == true && !string.IsNullOrWhiteSpace(info.Path) && File.Exists(info.Path))
                await _writer.WriteMetadataAsync(info.Path, enriched, cancellationToken).ConfigureAwait(false);

            if (config.EnableFormatGrouping && !string.IsNullOrWhiteSpace(enriched.Isbn))
            {
                var formatType = enriched.FileFormat;
                var existingGroup = _grouping.GetGroupByIsbn(enriched.Isbn);

                if (existingGroup is null)
                {
                    var group = _grouping.CreateGroup(enriched);
                    _grouping.AddFormatToGroup(group.Id, info.Path, formatType, isPrimary: true);
                    _logger.LogDebug("Created new book group for ISBN {Isbn} ({Title})", enriched.Isbn, enriched.Title);
                }
                else
                {
                    var currentPrimary = existingGroup.Formats.FirstOrDefault(f => f.IsPrimary);
                    var newPriority = BookGroupingService.GetFormatPriority(formatType);
                    var currentPriority = currentPrimary != null
                        ? BookGroupingService.GetFormatPriority(currentPrimary.FormatType)
                        : int.MaxValue;

                    _grouping.AddFormatToGroup(existingGroup.Id, info.Path, formatType, isPrimary: newPriority < currentPriority);
                    _logger.LogDebug(
                        "Added alternate format to existing group for ISBN {Isbn} ({Title})",
                        enriched.Isbn,
                        enriched.Title);
                }
            }

            result.HasMetadata = true;
            result.Item = MapToBook(enriched, info);
            result.Provider = Name;

            MapPeople(result, enriched);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unified metadata enrichment failed for {Path}", info.Path);
        }

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(BookInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.UnifiedMetadataEnabled) return results;

        try
        {
            if (!string.IsNullOrWhiteSpace(searchInfo.Name))
            {
                var localMeta = await ExtractFileMetadata(searchInfo.Path, cancellationToken).ConfigureAwait(false);
                if (localMeta is not null)
                {
                    if (localMeta.Isbn is not null)
                    {
                        var enrichmentResult = await _enrichment.EnrichAsync(
                            localMeta,
                            config.HardcoverApiKey,
                            config.GoogleBooksApiKey,
                            config.HardcoverEnabled,
                            config.GoogleBooksEnabled,
                            config.OpenLibraryEnabled,
                            comicVineEnabled: config.ComicVineEnabled,
                            comicVineApiKey: config.ComicVineApiKey ?? "",
                            metronEnabled: config.MetronEnabled,
                            metronApiKey: config.MetronApiKey ?? "",
                            versedbEnabled: config.VerseDbEnabled,
                            versedbApiKey: config.VerseDbApiKey ?? "",
                            title: searchInfo.Name,
                            ct: cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(enrichmentResult.Metadata.Title))
                        {
                            results.Add(MapSearchResult(enrichmentResult.Metadata));
                            return results;
                        }
                    }

                    if (HasComicMetadata(localMeta))
                    {
                        var enriched = await _enrichment.EnrichAsync(
                            localMeta,
                            config.HardcoverApiKey,
                            config.GoogleBooksApiKey,
                            config.HardcoverEnabled,
                            config.GoogleBooksEnabled,
                            config.OpenLibraryEnabled,
                            comicVineEnabled: config.ComicVineEnabled,
                            comicVineApiKey: config.ComicVineApiKey ?? "",
                            metronEnabled: config.MetronEnabled,
                            metronApiKey: config.MetronApiKey ?? "",
                            versedbEnabled: config.VerseDbEnabled,
                            versedbApiKey: config.VerseDbApiKey ?? "",
                            titleAuthorSearchEnabled: false,
                            title: searchInfo.Name,
                            ct: cancellationToken).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(enriched.Metadata.Title))
                        {
                            results.Add(MapSearchResult(enriched.Metadata));
                            return results;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search failed for {Name}", searchInfo.Name);
        }

        return results;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Use the cover bytes from metadata or a separate image provider");
    }

    private async Task<FileMetadata?> ExtractFileMetadata(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            return await _fileExtractor.ExtractAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static Book MapToBook(FileMetadata meta, BookInfo info)
    {
        var book = new Book
        {
            Name = meta.Title ?? info.Name,
            Overview = SanitizeDescription(meta.Description),
            SeriesName = meta.SeriesName,
            IndexNumber = meta.SeriesIndex.HasValue ? (int)Math.Round(meta.SeriesIndex.Value) : null
        };

        if (meta.PublishYear.HasValue)
            book.ProductionYear = meta.PublishYear.Value;

        if (meta.PublishDate.HasValue)
            book.PremiereDate = meta.PublishDate;

        if (!string.IsNullOrWhiteSpace(meta.Publisher))
            book.Studios = [meta.Publisher];

        if (!string.IsNullOrWhiteSpace(meta.Language))
            book.PreferredMetadataLanguage = meta.Language;

        foreach (var genre in meta.Genres)
        {
            if (!string.IsNullOrWhiteSpace(genre))
                book.AddGenre(genre);
        }

        if (!string.IsNullOrWhiteSpace(meta.Isbn))
            book.SetProviderId("Isbn", meta.Isbn);

        if (!string.IsNullOrWhiteSpace(meta.Asin))
            book.SetProviderId("Asin", meta.Asin);

        return book;
    }

    private static void MapPeople(MetadataResult<Book> result, FileMetadata meta)
    {
        foreach (var author in meta.Authors)
        {
            if (!string.IsNullOrWhiteSpace(author))
                result.AddPerson(new PersonInfo { Name = author, Type = PersonKind.Author });
        }

        foreach (var narrator in meta.Narrators)
        {
            if (!string.IsNullOrWhiteSpace(narrator))
                result.AddPerson(new PersonInfo { Name = narrator, Type = PersonKind.Unknown, Role = "Narrator" });
        }

        foreach (var person in meta.ComicPeople)
        {
            if (string.IsNullOrWhiteSpace(person.Name)) continue;
            var kind = MapRoleToPersonKind(person.Role);
            result.AddPerson(new PersonInfo { Name = person.Name, Type = kind, Role = person.Role });
        }
    }

    private static PersonKind MapRoleToPersonKind(string role)
    {
        return role switch
        {
            "Writer" => PersonKind.Writer,
            "Penciller" => PersonKind.Penciller,
            "Inker" => PersonKind.Inker,
            "Colorist" => PersonKind.Colorist,
            "Letterer" => PersonKind.Letterer,
            "CoverArtist" => PersonKind.CoverArtist,
            "Editor" => PersonKind.Editor,
            "Translator" => PersonKind.Translator,
            "Illustrator" => PersonKind.Illustrator,
            "Author" => PersonKind.Author,
            _ => PersonKind.Unknown
        };
    }

    private static RemoteSearchResult MapSearchResult(FileMetadata meta)
    {
        return new RemoteSearchResult
        {
            Name = meta.Title,
            Overview = Truncate(meta.Description, 300),
            ProductionYear = meta.PublishYear,
            ImageUrl = meta.CoverUrl,
            SearchProviderName = "BookEnhancer"
        };
    }

    private static string? SanitizeDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        return desc.Length > 10000 ? desc[..10000] : desc;
    }

    private static string? Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }

    private static bool HasComicMetadata(FileMetadata meta)
    {
        return !string.IsNullOrWhiteSpace(meta.SeriesName) ||
               !string.IsNullOrWhiteSpace(meta.SeriesNumber) ||
               meta.ComicPeople.Count > 0;
    }

    private bool IsLibrarySelected(string itemPath)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.IncludedLibraryIds.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(itemPath))
            return true;

        var folders = _libraryManager.GetVirtualFolders();
        foreach (var folder in folders)
        {
            if (!config.IncludedLibraryIds.Contains(folder.ItemId.ToString()))
                continue;

            foreach (var location in folder.Locations)
            {
                if (itemPath.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static ManagedSourceDirectory? FindMatchingDirectory(PluginConfiguration config, string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath)) return null;

        return config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .FirstOrDefault(d => itemPath.StartsWith(d.LibraryPath, StringComparison.OrdinalIgnoreCase));
    }
}
