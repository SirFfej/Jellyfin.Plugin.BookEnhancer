using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class OpenLibraryApiClient
{
    private static readonly Uri BaseUrl = new("https://openlibrary.org");
    private static readonly SimpleRateLimiter _rateLimiter = new(100, TimeSpan.FromMinutes(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenLibraryApiClient> _logger;

    public OpenLibraryApiClient(IHttpClientFactory httpClientFactory, ILogger<OpenLibraryApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FileMetadata?> SearchByTitleAuthorAsync(string title, string author, CancellationToken ct = default)
    {
        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            var query = $"{BaseUrl}/search.json?q={Uri.EscapeDataString(title)}+{Uri.EscapeDataString(author)}&limit=5";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

            var response = await client.GetFromJsonAsync<OlSearchResponse>(query, ct).ConfigureAwait(false);
            var doc = response?.Docs?.FirstOrDefault();
            if (doc is null) return null;

            return MapSearchResult(doc);
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("OpenLibrary", $"title/author search failed for \"{title}\" by \"{author}\"", ex);
            _logger.LogDebug("OpenLibrary title/author search failed for {Title} by {Author}; details in api-responses log", title, author);
            return null;
        }
    }

    private static FileMetadata? MapSearchResult(OlSearchDoc doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Title)) return null;

        var meta = new FileMetadata
        {
            FileFormat = "OpenLibrary",
            Title = doc.Title,
            Isbn = doc.Isbn?.FirstOrDefault(),
            Publisher = doc.Publisher?.FirstOrDefault(),
            PublishYear = doc.FirstPublishYear
        };

        if (doc.AuthorName != null)
        {
            foreach (var name in doc.AuthorName)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    meta.Authors.Add(name);
            }
        }

        if (doc.Subject != null)
        {
            foreach (var s in doc.Subject)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    meta.Genres.Add(s);
            }
        }

        return meta;
    }

    public async Task<FileMetadata?> SearchByIsbnAsync(string isbn, CancellationToken ct = default)
    {
        var cleanIsbn = CleanIsbn(isbn);
        if (cleanIsbn is null) return null;

        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            var url = $"{BaseUrl}api/books?bibkeys=ISBN:{cleanIsbn}&jscmd=data&format=json";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

            var response = await client.GetFromJsonAsync<Dictionary<string, OlBookEntry>>(url, ct).ConfigureAwait(false);
            if (response is null) return null;

            var key = $"ISBN:{cleanIsbn}";
            if (!response.TryGetValue(key, out var entry) || entry?.Details is null)
                return null;

            return MapEntry(entry, cleanIsbn);
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("OpenLibrary", $"ISBN lookup failed for \"{isbn}\"", ex);
            _logger.LogDebug("OpenLibrary lookup failed for ISBN {Isbn}; details in api-responses log", isbn);
            return null;
        }
    }

    private static FileMetadata MapEntry(OlBookEntry entry, string isbn)
    {
        var d = entry.Details!;
        var meta = new FileMetadata
        {
            FileFormat = "OpenLibrary",
            Title = d.Title,
            Subtitle = d.Subtitle,
            Description = d.Subjects?.FirstOrDefault()?.Name,
            Publisher = d.Publishers?.FirstOrDefault()?.Name,
            Isbn = isbn,
            Language = "eng",
            CoverUrl = entry.ThumbnailUrl?.Replace("-S.jpg", "-M.jpg")
        };

        if (d.Authors != null)
        {
            foreach (var a in d.Authors)
            {
                if (!string.IsNullOrWhiteSpace(a.Name))
                    meta.Authors.Add(a.Name);
            }
        }

        if (d.Subjects != null)
        {
            foreach (var s in d.Subjects)
            {
                if (!string.IsNullOrWhiteSpace(s.Name))
                    meta.Genres.Add(s.Name);
            }
        }

        if (d.NumberOfPages.HasValue)
            meta.PageCount = d.NumberOfPages;

        if (!string.IsNullOrWhiteSpace(d.PublishDate))
        {
            if (DateTime.TryParse(d.PublishDate, out var dt))
            {
                meta.PublishDate = dt;
                meta.PublishYear = dt.Year;
            }
        }

        meta.HasCover = entry.ThumbnailUrl != null;

        return meta;
    }

    private static string? CleanIsbn(string isbn)
    {
        var cleaned = new string(isbn.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static async Task<bool> WaitForRateLimitSlotAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var maxWaitSeconds = config?.ApiRateLimitMaxWaitSeconds ?? 5;
        if (maxWaitSeconds <= 0)
        {
            await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }

        return await _rateLimiter.TryWaitAsync(TimeSpan.FromSeconds(maxWaitSeconds), ct).ConfigureAwait(false);
    }

    private class OlSearchResponse
    {
        [JsonPropertyName("docs")]
        public List<OlSearchDoc>? Docs { get; set; }
    }

    private class OlSearchDoc
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonPropertyName("isbn")]
        public List<string>? Isbn { get; set; }

        [JsonPropertyName("publisher")]
        public List<string>? Publisher { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("subject")]
        public List<string>? Subject { get; set; }
    }

    private class OlBookEntry
    {
        [JsonPropertyName("bib_key")]
        public string? BibKey { get; set; }

        [JsonPropertyName("info_url")]
        public string? InfoUrl { get; set; }

        [JsonPropertyName("preview_url")]
        public string? PreviewUrl { get; set; }

        [JsonPropertyName("thumbnail_url")]
        public string? ThumbnailUrl { get; set; }

        [JsonPropertyName("details")]
        public OlDetails? Details { get; set; }
    }

    private class OlDetails
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; set; }

        [JsonPropertyName("authors")]
        public List<OlAuthor>? Authors { get; set; }

        [JsonPropertyName("publishers")]
        public List<OlPublisher>? Publishers { get; set; }

        [JsonPropertyName("publish_date")]
        public string? PublishDate { get; set; }

        [JsonPropertyName("number_of_pages")]
        public int? NumberOfPages { get; set; }

        [JsonPropertyName("subjects")]
        public List<OlSubject>? Subjects { get; set; }
    }

    private class OlAuthor
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private class OlPublisher
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class OlSubject
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
