using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class GoogleBooksApiClient
{
    private static readonly Uri BaseUrl = new("https://www.googleapis.com/books/v1");
    private static readonly SimpleRateLimiter _rateLimiter = new(60, TimeSpan.FromMinutes(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleBooksApiClient> _logger;

    public GoogleBooksApiClient(IHttpClientFactory httpClientFactory, ILogger<GoogleBooksApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FileMetadata?> SearchByTitleAuthorAsync(string title, string author, string? apiKey, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}volumes?q=intitle:{Uri.EscapeDataString(title)}+inauthor:{Uri.EscapeDataString(author)}&maxResults=1";
            if (!string.IsNullOrWhiteSpace(apiKey))
                url += $"&key={apiKey}";

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

            var response = await client.GetFromJsonAsync<VolumeResponse>(url, ct).ConfigureAwait(false);
            var volume = response?.Items?.FirstOrDefault();
            if (volume?.VolumeInfo is null) return null;

            return MapVolume(volume.VolumeInfo, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Books title/author search failed for {Title} by {Author}", title, author);
            return null;
        }
    }

    public async Task<FileMetadata?> SearchByIsbnAsync(string isbn, string? apiKey, CancellationToken ct = default)
    {
        var cleanIsbn = CleanIsbn(isbn);
        if (cleanIsbn is null) return null;

        try
        {
            var url = $"{BaseUrl}volumes?q=isbn:{cleanIsbn}&maxResults=1";
            if (!string.IsNullOrWhiteSpace(apiKey))
                url += $"&key={apiKey}";

            await _rateLimiter.WaitAsync(ct).ConfigureAwait(false);
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

            var response = await client.GetFromJsonAsync<VolumeResponse>(url, ct).ConfigureAwait(false);
            var volume = response?.Items?.FirstOrDefault();
            if (volume?.VolumeInfo is null) return null;

            return MapVolume(volume.VolumeInfo, cleanIsbn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Books API lookup failed for ISBN {Isbn}", isbn);
            return null;
        }
    }

    private static FileMetadata MapVolume(VolumeInfo info, string isbn)
    {
        var meta = new FileMetadata
        {
            FileFormat = "GoogleBooks",
            Title = info.Title,
            Subtitle = info.Subtitle,
            Description = info.Description,
            Publisher = info.Publisher,
            Isbn = isbn,
            Language = info.Language
        };

        if (info.Authors != null)
        {
            foreach (var author in info.Authors)
            {
                if (!string.IsNullOrWhiteSpace(author))
                    meta.Authors.Add(author);
            }
        }

        if (info.Categories != null)
        {
            foreach (var cat in info.Categories)
            {
                if (!string.IsNullOrWhiteSpace(cat))
                    meta.Genres.Add(cat);
            }
        }

        if (info.PageCount.HasValue)
            meta.PageCount = info.PageCount;

        if (info.AverageRating.HasValue)
        {
            meta.Tags.Add($"GoogleRating:{info.AverageRating:F1}");
        }

        if (!string.IsNullOrWhiteSpace(info.PublishedDate))
        {
            if (DateTime.TryParse(info.PublishedDate, out var dt))
            {
                meta.PublishDate = dt;
                meta.PublishYear = dt.Year;
            }
            else if (info.PublishedDate.Length >= 4 &&
                     int.TryParse(info.PublishedDate[..4], out var year))
            {
                meta.PublishYear = year;
            }
        }

        if (info.ImageLinks != null)
        {
            meta.CoverUrl = info.ImageLinks.Thumbnail
                            ?? info.ImageLinks.Small
                            ?? info.ImageLinks.Medium;
            meta.HasCover = meta.CoverUrl != null;
        }

        return meta;
    }

    private static string? CleanIsbn(string isbn)
    {
        var cleaned = new string(isbn.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private class VolumeResponse
    {
        [JsonPropertyName("items")]
        public List<VolumeItem>? Items { get; set; }
    }

    private class VolumeItem
    {
        [JsonPropertyName("volumeInfo")]
        public VolumeInfo? VolumeInfo { get; set; }
    }

    private class VolumeInfo
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; set; }

        [JsonPropertyName("authors")]
        public List<string>? Authors { get; set; }

        [JsonPropertyName("publisher")]
        public string? Publisher { get; set; }

        [JsonPropertyName("publishedDate")]
        public string? PublishedDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; set; }

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("averageRating")]
        public double? AverageRating { get; set; }

        [JsonPropertyName("imageLinks")]
        public ImageLinks? ImageLinks { get; set; }
    }

    private class ImageLinks
    {
        [JsonPropertyName("smallThumbnail")]
        public string? SmallThumbnail { get; set; }

        [JsonPropertyName("thumbnail")]
        public string? Thumbnail { get; set; }

        [JsonPropertyName("small")]
        public string? Small { get; set; }

        [JsonPropertyName("medium")]
        public string? Medium { get; set; }

        [JsonPropertyName("large")]
        public string? Large { get; set; }

        [JsonPropertyName("extraLarge")]
        public string? ExtraLarge { get; set; }
    }
}
