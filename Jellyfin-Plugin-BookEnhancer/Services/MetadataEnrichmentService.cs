using Jellyfin.Plugin.BookEnhancer.Clients;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class MetadataEnrichmentService
{
    private readonly HardcoverApiClient _hardcover;
    private readonly GoogleBooksApiClient _googleBooks;
    private readonly OpenLibraryApiClient _openLibrary;
    private readonly ComicVineApiClient _comicVine;
    private readonly MetronApiClient _metron;
    private readonly VerseDbApiClient _verseDb;
    private readonly GrandComicsDbApiClient _grandComicsDb;
    private readonly ILogger<MetadataEnrichmentService> _logger;

    public MetadataEnrichmentService(
        HardcoverApiClient hardcover,
        GoogleBooksApiClient googleBooks,
        OpenLibraryApiClient openLibrary,
        ComicVineApiClient comicVine,
        MetronApiClient metron,
        VerseDbApiClient verseDb,
        GrandComicsDbApiClient grandComicsDb,
        ILogger<MetadataEnrichmentService> logger)
    {
        _hardcover = hardcover;
        _googleBooks = googleBooks;
        _openLibrary = openLibrary;
        _comicVine = comicVine;
        _metron = metron;
        _verseDb = verseDb;
        _grandComicsDb = grandComicsDb;
        _logger = logger;
    }

    public async Task<EnrichmentResult> EnrichAsync(
        FileMetadata source,
        EnrichmentApiConfig apiConfig,
        bool titleAuthorSearchEnabled = true,
        string? title = null,
        string? author = null,
        CancellationToken ct = default)
    {
        var apiMatchFound = false;

        string? enrichedBy = null;

        if (!string.IsNullOrWhiteSpace(source.Isbn))
        {
            (apiMatchFound, enrichedBy) = await SearchByIsbnCascade(source, source.Isbn, apiConfig, ct).ConfigureAwait(false);
        }

        if (!apiMatchFound && titleAuthorSearchEnabled)
        {
            var searchTitle = title ?? source.Title;
            var searchAuthor = author ?? (source.Authors.Count > 0 ? source.Authors[0] : null);

            if (!string.IsNullOrWhiteSpace(searchTitle))
            {
                _logger.LogDebug("ISBN lookup returned no data, falling back to title/author search");
                (apiMatchFound, enrichedBy) = await SearchByTitleAuthorCascade(source, searchTitle, searchAuthor, apiConfig, ct).ConfigureAwait(false);
            }
        }

        if (!apiMatchFound)
        {
            (apiMatchFound, enrichedBy) = await SearchByComicCascade(source, apiConfig, ct).ConfigureAwait(false);
        }

        return new EnrichmentResult
        {
            Metadata = source,
            ApiMatchFound = apiMatchFound,
            EnrichedBy = enrichedBy
        };
    }

    private async Task<(bool Matched, string? ApiName)> SearchByIsbnCascade(
        FileMetadata source,
        string isbn,
        EnrichmentApiConfig apiConfig,
        CancellationToken ct)
    {
        var anyApiReturnedData = false;
        string? apiName = null;

        if (apiConfig.HardcoverEnabled && !string.IsNullOrWhiteSpace(apiConfig.HardcoverApiKey))
        {
            var hcMeta = await _hardcover.SearchByIsbnAsync(isbn, apiConfig.HardcoverApiKey, ct).ConfigureAwait(false);
            if (hcMeta != null)
            {
                anyApiReturnedData = true;
                apiName ??= "Hardcover";
                MergeSource(source, hcMeta);
                _logger.LogDebug("Hardcover returned data for ISBN {Isbn}", isbn);
                if (HasCompleteMetadata(source)) return (true, apiName);
            }
        }

        if (apiConfig.GoogleBooksEnabled && !string.IsNullOrWhiteSpace(apiConfig.GoogleBooksApiKey))
        {
            var gbMeta = await _googleBooks.SearchByIsbnAsync(isbn, apiConfig.GoogleBooksApiKey, ct).ConfigureAwait(false);
            if (gbMeta != null)
            {
                anyApiReturnedData = true;
                apiName ??= "Google Books";
                MergeNulls(source, gbMeta);
                _logger.LogDebug("Google Books returned data for ISBN {Isbn}", isbn);
                if (HasCompleteMetadata(source)) return (true, apiName);
            }
        }

        if (apiConfig.OpenLibraryEnabled)
        {
            var olMeta = await _openLibrary.SearchByIsbnAsync(isbn, ct).ConfigureAwait(false);
            if (olMeta != null)
            {
                anyApiReturnedData = true;
                apiName ??= "OpenLibrary";
                MergeNulls(source, olMeta);
                _logger.LogDebug("OpenLibrary returned data for ISBN {Isbn}", isbn);
                if (HasCompleteMetadata(source)) return (true, apiName);
            }
        }

        return (anyApiReturnedData, apiName);
    }

    private async Task<(bool Matched, string? ApiName)> SearchByTitleAuthorCascade(
        FileMetadata source,
        string title,
        string? author,
        EnrichmentApiConfig apiConfig,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            _logger.LogDebug("No author available for title/author search, skipping");
            return (false, null);
        }

        var anyApiReturnedData = false;
        string? apiName = null;

        if (apiConfig.HardcoverEnabled && !string.IsNullOrWhiteSpace(apiConfig.HardcoverApiKey))
        {
            var hcMeta = await _hardcover.SearchByTitleAuthorAsync(title, author, apiConfig.HardcoverApiKey, ct).ConfigureAwait(false);
            if (hcMeta != null)
            {
                anyApiReturnedData = true;
                apiName ??= "Hardcover";
                MergeSource(source, hcMeta);
                _logger.LogDebug("Hardcover title/author search matched {Title}", title);
                if (HasCompleteMetadata(source)) return (true, apiName);
            }
        }

        if (apiConfig.GoogleBooksEnabled && !string.IsNullOrWhiteSpace(apiConfig.GoogleBooksApiKey))
        {
            var gbMeta = await _googleBooks.SearchByTitleAuthorAsync(title, author, apiConfig.GoogleBooksApiKey, ct).ConfigureAwait(false);
            if (gbMeta != null)
            {
                anyApiReturnedData = true;
                apiName ??= "Google Books";
                MergeNulls(source, gbMeta);
                _logger.LogDebug("Google Books title/author search matched {Title}", title);
                if (HasCompleteMetadata(source)) return (true, apiName);
            }
        }

        if (apiConfig.OpenLibraryEnabled)
        {
            var olMeta = await _openLibrary.SearchByTitleAuthorAsync(title, author, ct).ConfigureAwait(false);
            if (olMeta != null)
            {
                anyApiReturnedData = true;
                apiName ??= "OpenLibrary";
                MergeNulls(source, olMeta);
                _logger.LogDebug("OpenLibrary title/author search matched {Title}", title);
            }
        }

        return (anyApiReturnedData, apiName);
    }

    private async Task<(bool Matched, string? ApiName)> SearchByComicCascade(
        FileMetadata source,
        EnrichmentApiConfig apiConfig,
        CancellationToken ct)
    {
        var series = source.SeriesName;
        var issue = source.SeriesNumber;

        if (string.IsNullOrWhiteSpace(series))
        {
            _logger.LogDebug("No series info available for comic API search, skipping");
            return (false, null);
        }

        if (apiConfig.ComicVineEnabled && !string.IsNullOrWhiteSpace(apiConfig.ComicVineApiKey))
        {
            var query = issue is not null ? $"{series} {issue}" : series;
            var cvResults = await _comicVine.SearchIssuesAsync(query, apiConfig.ComicVineApiKey, ct).ConfigureAwait(false);
            if (cvResults.Count > 0)
            {
                var detail = await _comicVine.GetIssueDetailAsync(cvResults[0].Id, apiConfig.ComicVineApiKey, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeNulls(source, detail);
                    if (!string.IsNullOrWhiteSpace(cvResults[0].PublisherName) && string.IsNullOrWhiteSpace(source.Publisher))
                        source.Publisher = cvResults[0].PublisherName;
                    _logger.LogDebug("Comic Vine enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                    return (true, "Comic Vine");
                }
            }
        }

        if (apiConfig.MetronEnabled && !string.IsNullOrWhiteSpace(apiConfig.MetronUsername) && !string.IsNullOrWhiteSpace(apiConfig.MetronPassword) && !string.IsNullOrWhiteSpace(issue))
        {
            var mtResults = await _metron.SearchIssuesAsync(series, issue, apiConfig.MetronUsername, apiConfig.MetronPassword, ct).ConfigureAwait(false);
            if (mtResults.Count > 0)
            {
                var detail = await _metron.GetIssueDetailAsync(mtResults[0].Id, apiConfig.MetronUsername, apiConfig.MetronPassword, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeNulls(source, detail);
                    if (!string.IsNullOrWhiteSpace(mtResults[0].PublisherName) && string.IsNullOrWhiteSpace(source.Publisher))
                        source.Publisher = mtResults[0].PublisherName;
                    _logger.LogDebug("Metron enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                    return (true, "Metron");
                }
            }
        }

        if (apiConfig.VerseDbEnabled && !string.IsNullOrWhiteSpace(apiConfig.VerseDbApiKey))
        {
            var query = issue is not null ? $"{series} {issue}" : series;
            var vdResults = await _verseDb.SearchIssuesAsync(query, apiConfig.VerseDbApiKey, ct).ConfigureAwait(false);
            if (vdResults.Count > 0)
            {
                var detail = await _verseDb.GetIssueDetailAsync(vdResults[0].Id, apiConfig.VerseDbApiKey, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeNulls(source, detail);
                    if (!string.IsNullOrWhiteSpace(vdResults[0].PublisherName) && string.IsNullOrWhiteSpace(source.Publisher))
                        source.Publisher = vdResults[0].PublisherName;
                    _logger.LogDebug("VerseDB enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                    return (true, "VerseDB");
                }
            }
        }

        if (apiConfig.GrandComicsDbEnabled && !string.IsNullOrWhiteSpace(apiConfig.GrandComicsDbUsername) && !string.IsNullOrWhiteSpace(apiConfig.GrandComicsDbPassword) && !string.IsNullOrWhiteSpace(issue))
        {
            var gcdMeta = await _grandComicsDb.SearchBySeriesAndIssueAsync(series, issue, apiConfig.GrandComicsDbUsername, apiConfig.GrandComicsDbPassword, ct).ConfigureAwait(false);
            if (gcdMeta is not null)
            {
                MergeNulls(source, gcdMeta);
                _logger.LogDebug("Grand Comics Database enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                return (true, "Grand Comics Database");
            }
        }

        return (false, null);
    }

    private static void MergeSource(FileMetadata target, FileMetadata source)
    {
        target.Title ??= source.Title;
        target.Subtitle ??= source.Subtitle;
        target.Description ??= source.Description;
        target.Publisher ??= source.Publisher;
        target.Isbn ??= source.Isbn;
        target.Asin ??= source.Asin;
        target.Language ??= source.Language;
        target.SeriesName ??= source.SeriesName;
        target.SeriesIndex ??= source.SeriesIndex;
        target.SeriesNumber ??= source.SeriesNumber;
        target.Volume ??= source.Volume;
        target.AgeRating ??= source.AgeRating;
        target.StoryArc ??= source.StoryArc;
        target.Format ??= source.Format;
        target.Manga ??= source.Manga;
        target.CoverUrl ??= source.CoverUrl;
        target.PageCount ??= source.PageCount;
        target.DurationMs ??= source.DurationMs;
        target.PublishDate ??= source.PublishDate;
        target.PublishYear ??= source.PublishYear;

        if (!target.HasCover && source.HasCover)
        {
            target.HasCover = source.HasCover;
            target.CoverUrl ??= source.CoverUrl;
        }

        MergeLists(target.Authors, source.Authors);
        MergeLists(target.Genres, source.Genres);
        MergeLists(target.Tags, source.Tags);
        MergeLists(target.Narrators, source.Narrators);

        foreach (var person in source.ComicPeople)
        {
            if (!target.ComicPeople.Any(p =>
                    string.Equals(p.Name, person.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Role, person.Role, StringComparison.OrdinalIgnoreCase)))
            {
                target.ComicPeople.Add(person);
            }
        }
    }

    private static void MergeNulls(FileMetadata target, FileMetadata fallback)
    {
        if (string.IsNullOrWhiteSpace(target.Title)) target.Title = fallback.Title;
        if (string.IsNullOrWhiteSpace(target.Subtitle)) target.Subtitle = fallback.Subtitle;
        if (string.IsNullOrWhiteSpace(target.Description)) target.Description = fallback.Description;
        if (string.IsNullOrWhiteSpace(target.Publisher)) target.Publisher = fallback.Publisher;
        if (string.IsNullOrWhiteSpace(target.Language)) target.Language = fallback.Language;
        if (string.IsNullOrWhiteSpace(target.SeriesName)) target.SeriesName = fallback.SeriesName;
        if (string.IsNullOrWhiteSpace(target.AgeRating)) target.AgeRating = fallback.AgeRating;
        if (string.IsNullOrWhiteSpace(target.Format)) target.Format = fallback.Format;
        if (string.IsNullOrWhiteSpace(target.CoverUrl)) target.CoverUrl = fallback.CoverUrl;

        target.SeriesIndex ??= fallback.SeriesIndex;
        target.PageCount ??= fallback.PageCount;
        target.PublishDate ??= fallback.PublishDate;
        target.PublishYear ??= fallback.PublishYear;

        if (!target.HasCover && fallback.HasCover)
        {
            target.HasCover = fallback.HasCover;
            target.CoverUrl ??= fallback.CoverUrl;
        }

        if (target.Authors.Count == 0 && fallback.Authors.Count > 0)
            target.Authors.AddRange(fallback.Authors);

        if (target.Genres.Count == 0 && fallback.Genres.Count > 0)
            target.Genres.AddRange(fallback.Genres);

        if (target.Narrators.Count == 0 && fallback.Narrators.Count > 0)
            target.Narrators.AddRange(fallback.Narrators);
    }

    private static void MergeLists(List<string> target, List<string> source)
    {
        foreach (var item in source)
        {
            if (!target.Contains(item, StringComparer.OrdinalIgnoreCase))
                target.Add(item);
        }
    }

    private static bool HasCompleteMetadata(FileMetadata meta)
    {
        return !string.IsNullOrWhiteSpace(meta.Title)
            && !string.IsNullOrWhiteSpace(meta.Description)
            && meta.Authors.Count > 0
            && !string.IsNullOrWhiteSpace(meta.Publisher)
            && meta.PublishYear.HasValue;
    }
}
