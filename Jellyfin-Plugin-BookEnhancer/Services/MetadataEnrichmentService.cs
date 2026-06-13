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
        string hardcoverApiKey,
        string? googleBooksApiKey,
        bool hardcoverEnabled,
        bool googleBooksEnabled,
        bool openLibraryEnabled,
        bool comicVineEnabled = false,
        string comicVineApiKey = "",
        bool metronEnabled = false,
        string metronUsername = "",
        string metronPassword = "",
        bool versedbEnabled = false,
        string versedbApiKey = "",
        bool grandComicsDbEnabled = false,
        string grandComicsDbUsername = "",
        string grandComicsDbPassword = "",
        bool titleAuthorSearchEnabled = true,
        string? title = null,
        string? author = null,
        CancellationToken ct = default)
    {
        var apiMatchFound = false;

        if (!string.IsNullOrWhiteSpace(source.Isbn))
        {
            apiMatchFound = await SearchByIsbnCascade(source, source.Isbn, hardcoverApiKey, googleBooksApiKey, hardcoverEnabled, googleBooksEnabled, openLibraryEnabled, ct).ConfigureAwait(false);
        }

        if (!apiMatchFound && titleAuthorSearchEnabled)
        {
            var searchTitle = title ?? source.Title;
            var searchAuthor = author ?? (source.Authors.Count > 0 ? source.Authors[0] : null);

            if (!string.IsNullOrWhiteSpace(searchTitle))
            {
                _logger.LogDebug("ISBN lookup returned no data, falling back to title/author search");
                apiMatchFound = await SearchByTitleAuthorCascade(source, searchTitle, searchAuthor, hardcoverApiKey, googleBooksApiKey, hardcoverEnabled, googleBooksEnabled, openLibraryEnabled, ct).ConfigureAwait(false);
            }
        }

        if (!apiMatchFound)
        {
            apiMatchFound = await SearchByComicCascade(source, comicVineEnabled, comicVineApiKey, metronEnabled, metronUsername, metronPassword, versedbEnabled, versedbApiKey, grandComicsDbEnabled, grandComicsDbUsername, grandComicsDbPassword, ct).ConfigureAwait(false);
        }

        return new EnrichmentResult
        {
            Metadata = source,
            ApiMatchFound = apiMatchFound
        };
    }

    private async Task<bool> SearchByIsbnCascade(
        FileMetadata source,
        string isbn,
        string hardcoverApiKey,
        string? googleBooksApiKey,
        bool hardcoverEnabled,
        bool googleBooksEnabled,
        bool openLibraryEnabled,
        CancellationToken ct)
    {
        var anyApiReturnedData = false;

        if (hardcoverEnabled && !string.IsNullOrWhiteSpace(hardcoverApiKey))
        {
            var hcMeta = await _hardcover.SearchByIsbnAsync(isbn, hardcoverApiKey, ct).ConfigureAwait(false);
            if (hcMeta != null)
            {
                anyApiReturnedData = true;
                MergeSource(source, hcMeta);
                _logger.LogDebug("Hardcover returned data for ISBN {Isbn}", isbn);
                if (HasCompleteMetadata(source)) return true;
            }
        }

        if (googleBooksEnabled && !string.IsNullOrWhiteSpace(googleBooksApiKey))
        {
            var gbMeta = await _googleBooks.SearchByIsbnAsync(isbn, googleBooksApiKey, ct).ConfigureAwait(false);
            if (gbMeta != null)
            {
                anyApiReturnedData = true;
                MergeNulls(source, gbMeta);
                _logger.LogDebug("Google Books returned data for ISBN {Isbn}", isbn);
                if (HasCompleteMetadata(source)) return true;
            }
        }

        if (openLibraryEnabled)
        {
            var olMeta = await _openLibrary.SearchByIsbnAsync(isbn, ct).ConfigureAwait(false);
            if (olMeta != null)
            {
                anyApiReturnedData = true;
                MergeNulls(source, olMeta);
                _logger.LogDebug("OpenLibrary returned data for ISBN {Isbn}", isbn);
                if (HasCompleteMetadata(source)) return true;
            }
        }

        return anyApiReturnedData;
    }

    private async Task<bool> SearchByTitleAuthorCascade(
        FileMetadata source,
        string title,
        string? author,
        string hardcoverApiKey,
        string? googleBooksApiKey,
        bool hardcoverEnabled,
        bool googleBooksEnabled,
        bool openLibraryEnabled,
        CancellationToken ct)
    {
        var anyApiReturnedData = false;

        if (string.IsNullOrWhiteSpace(author))
        {
            _logger.LogDebug("No author available for title/author search, skipping");
            return false;
        }

        if (hardcoverEnabled && !string.IsNullOrWhiteSpace(hardcoverApiKey))
        {
            var hcMeta = await _hardcover.SearchByTitleAuthorAsync(title, author, hardcoverApiKey, ct).ConfigureAwait(false);
            if (hcMeta != null)
            {
                anyApiReturnedData = true;
                MergeSource(source, hcMeta);
                _logger.LogDebug("Hardcover title/author search matched {Title}", title);
                if (HasCompleteMetadata(source)) return true;
            }
        }

        if (googleBooksEnabled && !string.IsNullOrWhiteSpace(googleBooksApiKey))
        {
            var gbMeta = await _googleBooks.SearchByTitleAuthorAsync(title, author, googleBooksApiKey, ct).ConfigureAwait(false);
            if (gbMeta != null)
            {
                anyApiReturnedData = true;
                MergeNulls(source, gbMeta);
                _logger.LogDebug("Google Books title/author search matched {Title}", title);
                if (HasCompleteMetadata(source)) return true;
            }
        }

        if (openLibraryEnabled)
        {
            var olMeta = await _openLibrary.SearchByTitleAuthorAsync(title, author, ct).ConfigureAwait(false);
            if (olMeta != null)
            {
                anyApiReturnedData = true;
                MergeNulls(source, olMeta);
                _logger.LogDebug("OpenLibrary title/author search matched {Title}", title);
            }
        }

        return anyApiReturnedData;
    }

    private async Task<bool> SearchByComicCascade(
        FileMetadata source,
        bool comicVineEnabled,
        string comicVineApiKey,
        bool metronEnabled,
        string metronUsername,
        string metronPassword,
        bool versedbEnabled,
        string versedbApiKey,
        bool grandComicsDbEnabled,
        string grandComicsDbUsername,
        string grandComicsDbPassword,
        CancellationToken ct)
    {
        var series = source.SeriesName;
        var issue = source.SeriesNumber;

        if (string.IsNullOrWhiteSpace(series))
        {
            _logger.LogDebug("No series info available for comic API search, skipping");
            return false;
        }

        if (comicVineEnabled && !string.IsNullOrWhiteSpace(comicVineApiKey))
        {
            var query = issue is not null ? $"{series} {issue}" : series;
            var cvResults = await _comicVine.SearchIssuesAsync(query, comicVineApiKey, ct).ConfigureAwait(false);
            if (cvResults.Count > 0)
            {
                var detail = await _comicVine.GetIssueDetailAsync(cvResults[0].Id, comicVineApiKey, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeNulls(source, detail);
                    if (!string.IsNullOrWhiteSpace(cvResults[0].PublisherName) && string.IsNullOrWhiteSpace(source.Publisher))
                        source.Publisher = cvResults[0].PublisherName;
                    _logger.LogDebug("Comic Vine enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                    return true;
                }
            }
        }

        if (metronEnabled && !string.IsNullOrWhiteSpace(metronUsername) && !string.IsNullOrWhiteSpace(metronPassword) && !string.IsNullOrWhiteSpace(issue))
        {
            var mtResults = await _metron.SearchIssuesAsync(series, issue, metronUsername, metronPassword, ct).ConfigureAwait(false);
            if (mtResults.Count > 0)
            {
                var detail = await _metron.GetIssueDetailAsync(mtResults[0].Id, metronUsername, metronPassword, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeNulls(source, detail);
                    if (!string.IsNullOrWhiteSpace(mtResults[0].PublisherName) && string.IsNullOrWhiteSpace(source.Publisher))
                        source.Publisher = mtResults[0].PublisherName;
                    _logger.LogDebug("Metron enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                    return true;
                }
            }
        }

        if (versedbEnabled && !string.IsNullOrWhiteSpace(versedbApiKey))
        {
            var query = issue is not null ? $"{series} {issue}" : series;
            var vdResults = await _verseDb.SearchIssuesAsync(query, versedbApiKey, ct).ConfigureAwait(false);
            if (vdResults.Count > 0)
            {
                var detail = await _verseDb.GetIssueDetailAsync(vdResults[0].Id, versedbApiKey, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    MergeNulls(source, detail);
                    if (!string.IsNullOrWhiteSpace(vdResults[0].PublisherName) && string.IsNullOrWhiteSpace(source.Publisher))
                        source.Publisher = vdResults[0].PublisherName;
                    _logger.LogDebug("VerseDB enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                    return true;
                }
            }
        }

        if (grandComicsDbEnabled && !string.IsNullOrWhiteSpace(grandComicsDbUsername) && !string.IsNullOrWhiteSpace(grandComicsDbPassword) && !string.IsNullOrWhiteSpace(issue))
        {
            var gcdMeta = await _grandComicsDb.SearchBySeriesAndIssueAsync(series, issue, grandComicsDbUsername, grandComicsDbPassword, ct).ConfigureAwait(false);
            if (gcdMeta is not null)
            {
                MergeNulls(source, gcdMeta);
                _logger.LogDebug("Grand Comics Database enriched {Title} (series: {Series}, issue: {Issue})", source.Title, source.SeriesName, source.SeriesNumber);
                return true;
            }
        }

        return false;
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
