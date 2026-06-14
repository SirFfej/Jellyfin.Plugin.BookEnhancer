using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class HardcoverApiClient
{
    private static readonly Uri BaseUrl = new("https://api.hardcover.app/v1/graphql");
    private static readonly SimpleRateLimiter _rateLimiter = new(60, TimeSpan.FromMinutes(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HardcoverApiClient> _logger;

    public HardcoverApiClient(IHttpClientFactory httpClientFactory, ILogger<HardcoverApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FileMetadata?> SearchByTitleAuthorAsync(string title, string author, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var bookData = await QueryBookByTitleAuthor(title, author, apiKey, ct).ConfigureAwait(false);
            if (bookData is null) return null;

            var meta = MapBookToMetadata(bookData);
            return meta;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Hardcover", $"title/author search failed for \"{title}\" by \"{author}\"", ex);
            _logger.LogDebug("Hardcover title/author search failed for {Title} by {Author}; details in api-responses log", title, author);
            return null;
        }
    }

    private async Task<BookResult?> QueryBookByTitleAuthor(string title, string author, string apiKey, CancellationToken ct)
    {
        var query = @"
query($title: String!) {
  books(where: {title: {_iregex: $title}}, limit: 10) {
    id
    title
    subtitle
    description
    release_year
    release_date
    cached_tags
    headline
    contributions {
      contribution
      author { name }
    }
    book_series {
      position
      details
      series { name }
    }
  }
}";
        var body = new { query, variables = new { title } };
        var response = await SendGraphQlAsync<BookResponse>(body, apiKey, ct).ConfigureAwait(false);
        if (response?.Data?.Books is null) return null;

        var loweredAuthor = author.ToLowerInvariant();
        return response.Data.Books.FirstOrDefault(b =>
            b.Contributions?.Any(c =>
                c.Author?.Name != null &&
                c.Author.Name.Contains(loweredAuthor, StringComparison.OrdinalIgnoreCase)) == true);
    }

    private static FileMetadata? MapBookToMetadata(BookResult book)
    {
        if (string.IsNullOrWhiteSpace(book.Title)) return null;

        var meta = new FileMetadata
        {
            FileFormat = "Hardcover",
            Title = book.Title,
            Subtitle = book.Subtitle,
            Description = book.Description
        };

        if (book.ReleaseYear.HasValue) meta.PublishYear = book.ReleaseYear;
        if (book.ReleaseDate != null && DateTime.TryParse(book.ReleaseDate, out var dt))
            meta.PublishDate = dt;

        if (book.Contributions != null)
        {
            foreach (var c in book.Contributions)
            {
                if (c.Author?.Name is null) continue;
                var mappedRole = NormalizeRole(c.Contribution);
                if (mappedRole == "Author")
                    meta.Authors.Add(c.Author.Name);
                else if (mappedRole == "Narrator")
                    meta.Narrators.Add(c.Author.Name);
                else
                    meta.ComicPeople.Add(new ComicPersonInfo { Name = c.Author.Name, Role = mappedRole });
            }
        }

        if (book.BookSeries != null)
        {
            var primary = book.BookSeries.FirstOrDefault();
            if (primary != null)
            {
                meta.SeriesName = primary.Series?.Name;
                if (primary.Position.HasValue) meta.SeriesIndex = (float)primary.Position.Value;
            }
        }

        return meta;
    }

    public async Task<FileMetadata?> SearchByIsbnAsync(string isbn, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var cleanIsbn = CleanIsbn(isbn);
        if (cleanIsbn is null) return null;

        try
        {
            var editionData = await QueryEditionByIsbn(cleanIsbn, apiKey, ct).ConfigureAwait(false);
            if (editionData is null) return null;

            var meta = MapEdition(editionData);

            if (editionData.BookId > 0)
            {
                var bookData = await QueryBookById(editionData.BookId, apiKey, ct).ConfigureAwait(false);
                if (bookData != null)
                    MapBook(bookData, meta);
            }

            return meta;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Hardcover", $"ISBN lookup failed for \"{isbn}\"", ex);
            _logger.LogDebug("Hardcover API lookup failed for ISBN {Isbn}; details in api-responses log", isbn);
            return null;
        }
    }

    private async Task<EditionResult?> QueryEditionByIsbn(string isbn, string apiKey, CancellationToken ct)
    {
        var query = @"
query($isbn: String!) {
  editions(where: {isbn_13: {_eq: $isbn}}, limit: 1) {
    id
    isbn_13
    isbn_10
    asin
    title
    subtitle
    pages
    release_date
    release_year
    book_id
    image { url }
    publisher { name }
    language { code2 }
  }
}";
        var body = new { query, variables = new { isbn } };
        var response = await SendGraphQlAsync<EditionResponse>(body, apiKey, ct).ConfigureAwait(false);
        return response?.Data?.Editions?.FirstOrDefault();
    }

    private async Task<BookResult?> QueryBookById(long bookId, string apiKey, CancellationToken ct)
    {
        var query = @"
query($bookId: Int!) {
  books(where: {id: {_eq: $bookId}}, limit: 1) {
    id
    title
    subtitle
    description
    release_year
    release_date
    rating
    cached_tags
    headline
    contributions {
      contribution
      author { name }
    }
    book_series {
      position
      details
      series { name }
    }
  }
}";
        var body = new { query, variables = new { bookId } };
        var response = await SendGraphQlAsync<BookResponse>(body, apiKey, ct).ConfigureAwait(false);
        return response?.Data?.Books?.FirstOrDefault();
    }

    private async Task<GraphQlResponse<T>?> SendGraphQlAsync<T>(object body, string apiKey, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

        var httpResponse = await client.PostAsJsonAsync(BaseUrl, body, ct).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        return await httpResponse.Content.ReadFromJsonAsync<GraphQlResponse<T>>(ct).ConfigureAwait(false);
    }

    private static FileMetadata MapEdition(EditionResult ed)
    {
        var meta = new FileMetadata
        {
            FileFormat = "Hardcover",
            Title = ed.Title ?? ed.BookTitle,
            Subtitle = ed.Subtitle,
            Isbn = ed.Isbn13 ?? ed.Isbn10,
            Asin = ed.Asin,
            Publisher = ed.Publisher?.Name,
            Language = ed.Language?.Code2,
            CoverUrl = ed.Image?.Url
        };

        if (ed.Pages.HasValue)
            meta.PageCount = ed.Pages;

        if (DateTime.TryParse(ed.ReleaseDate, out var dt))
        {
            meta.PublishDate = dt;
            meta.PublishYear = dt.Year;
        }
        else if (ed.ReleaseYear.HasValue)
        {
            meta.PublishYear = ed.ReleaseYear;
        }

        return meta;
    }

    private void MapBook(BookResult book, FileMetadata meta)
    {
        meta.Title ??= book.Title;
        meta.Subtitle ??= book.Subtitle;
        meta.Description ??= book.Description;
        meta.PublishYear ??= book.ReleaseYear;

        if (book.ReleaseDate != null && meta.PublishDate is null &&
            DateTime.TryParse(book.ReleaseDate, out var dt))
            meta.PublishDate = dt;

        if (book.Headline != null && meta.Subtitle is null)
            meta.Subtitle = book.Headline;

        if (book.Rating.HasValue && book.Rating.Value > 0)
        {
            if (meta.Tags is null) meta.Tags = new();
            meta.Tags.Add($"HardcoverRating:{(decimal)book.Rating:F1}");
        }

        if (book.CachedTags != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(book.CachedTags);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var tag = element.TryGetProperty("tag", out var t) ? t.GetString() : null;
                    var category = element.TryGetProperty("tag_category", out var c) ? c.GetString() : null;
                    if (tag is null) continue;

                    if (category == "genre")
                        meta.Genres.Add(tag);
                    else
                        meta.Tags.Add(tag);
                }
            }
            catch (Exception ex)
            {
                ApiResponseLogger.Log("Hardcover", $"Failed to parse cached_tags for book {book.Id}", ex);
                _logger.LogDebug("Failed to parse Hardcover cached_tags for book {BookId}; details in api-responses log", book.Id);
            }
        }

        if (book.Contributions != null)
        {
            foreach (var c in book.Contributions)
            {
                if (c.Author?.Name is null) continue;
                var mappedRole = NormalizeRole(c.Contribution);

                if (mappedRole == "Narrator")
                    meta.Narrators.Add(c.Author.Name);
                else if (mappedRole == "Author")
                    meta.Authors.Add(c.Author.Name);
                else
                    meta.ComicPeople.Add(new ComicPersonInfo { Name = c.Author.Name, Role = mappedRole });
            }
        }

        if (book.BookSeries != null)
        {
            var primary = book.BookSeries.FirstOrDefault();
            if (primary != null)
            {
                meta.SeriesName ??= primary.Series?.Name;
                if (primary.Position.HasValue && meta.SeriesIndex is null)
                    meta.SeriesIndex = (float)primary.Position.Value;
            }
        }
    }

    private static string NormalizeRole(string? role)
    {
        return role?.Trim() switch
        {
            "Author" => "Author",
            "Narrator" => "Narrator",
            "Illustrator" => "Illustrator",
            "Translator" => "Translator",
            "Editor" => "Editor",
            "Cover Artist" => "CoverArtist",
            _ => role ?? "Contributor"
        };
    }

    private static string? CleanIsbn(string isbn)
    {
        var cleaned = new string(isbn.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private class GraphQlResponse<T>
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }

    private class EditionResponse
    {
        [JsonPropertyName("editions")]
        public List<EditionResult>? Editions { get; set; }
    }

    private class EditionResult
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("isbn_13")]
        public string? Isbn13 { get; set; }

        [JsonPropertyName("isbn_10")]
        public string? Isbn10 { get; set; }

        [JsonPropertyName("asin")]
        public string? Asin { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; set; }

        [JsonPropertyName("pages")]
        public int? Pages { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("release_year")]
        public int? ReleaseYear { get; set; }

        [JsonPropertyName("book_id")]
        public long BookId { get; set; }

        [JsonPropertyName("image")]
        public ImageResult? Image { get; set; }

        [JsonPropertyName("publisher")]
        public PublisherResult? Publisher { get; set; }

        [JsonPropertyName("language")]
        public LanguageResult? Language { get; set; }

        // Fallback title directly on edition
        [JsonPropertyName("book")]
        public BookRef? Book { get; set; }

        public string? BookTitle => Book?.Title;
    }

    private class BookRef
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    private class BookResponse
    {
        [JsonPropertyName("books")]
        public List<BookResult>? Books { get; set; }
    }

    private class BookResult
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("release_year")]
        public int? ReleaseYear { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("cached_tags")]
        public string? CachedTags { get; set; }

        [JsonPropertyName("headline")]
        public string? Headline { get; set; }

        [JsonPropertyName("contributions")]
        public List<ContributionResult>? Contributions { get; set; }

        [JsonPropertyName("book_series")]
        public List<BookSeriesResult>? BookSeries { get; set; }
    }

    private class ContributionResult
    {
        [JsonPropertyName("contribution")]
        public string? Contribution { get; set; }

        [JsonPropertyName("author")]
        public AuthorResult? Author { get; set; }
    }

    private class AuthorResult
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class BookSeriesResult
    {
        [JsonPropertyName("position")]
        public double? Position { get; set; }

        [JsonPropertyName("details")]
        public string? Details { get; set; }

        [JsonPropertyName("series")]
        public SeriesResult? Series { get; set; }
    }

    private class SeriesResult
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class ImageResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private class PublisherResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class LanguageResult
    {
        [JsonPropertyName("code2")]
        public string? Code2 { get; set; }
    }
}
