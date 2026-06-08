using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class OpenLibraryApiClient
{
    private static readonly Uri BaseUrl = new("https://openlibrary.org");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenLibraryApiClient> _logger;

    public OpenLibraryApiClient(IHttpClientFactory httpClientFactory, ILogger<OpenLibraryApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FileMetadata?> SearchByIsbnAsync(string isbn, CancellationToken ct = default)
    {
        var cleanIsbn = CleanIsbn(isbn);
        if (cleanIsbn is null) return null;

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
            _logger.LogWarning(ex, "OpenLibrary lookup failed for ISBN {Isbn}", isbn);
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
