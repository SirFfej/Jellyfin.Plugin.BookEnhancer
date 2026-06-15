using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class ComicVineApiClient
{
    private static readonly Uri BaseUrl = new("https://comicvine.gamespot.com/api");
    private static readonly SimpleRateLimiter _rateLimiter = new(180, TimeSpan.FromHours(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ComicVineApiClient> _logger;

    public ComicVineApiClient(IHttpClientFactory httpClientFactory, ILogger<ComicVineApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<ComicVineSearchResult>> SearchIssuesAsync(string query, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        ConfigureRateLimiter();
        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return [];

        try
        {
            var url = $"{BaseUrl}/search/?api_key={apiKey}&format=json&resources=issue&query={Uri.EscapeDataString(query)}&field_list=id,name,issue_number,image,volume,cover_date,description,publisher";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

            var response = await client.GetFromJsonAsync<CvSearchResponse>(url, ct).ConfigureAwait(false);
            if (response?.Results is null) return [];

            return response.Results
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(MapSearchResult)
                .ToList();
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Comic Vine", $"search failed for query \"{query}\"", ex);
            _logger.LogDebug("Comic Vine search failed for query {Query}; details in api-responses log", query);
            return [];
        }
    }

    public async Task<FileMetadata?> GetIssueDetailAsync(int issueId, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        ConfigureRateLimiter();
        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            var url = $"{BaseUrl}/issue/4000-{issueId}/?api_key={apiKey}&format=json&field_list=id,name,issue_number,volume,cover_date,description,publisher,person_credits,character_credits,image";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");

            var response = await client.GetFromJsonAsync<CvIssueResponse>(url, ct).ConfigureAwait(false);
            return response?.Results is not null ? MapIssue(response.Results) : null;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Comic Vine", $"issue lookup failed for ID {issueId}", ex);
            _logger.LogDebug("Comic Vine issue lookup failed for ID {IssueId}; details in api-responses log", issueId);
            return null;
        }
    }

    private static ComicVineSearchResult MapSearchResult(CvSearchIssue issue)
    {
        return new ComicVineSearchResult
        {
            Id = issue.Id,
            Title = issue.Name ?? string.Empty,
            IssueNumber = issue.IssueNumber ?? string.Empty,
            SeriesName = issue.Volume?.Name,
            SeriesId = issue.Volume?.Id,
            CoverDate = ParseDate(issue.CoverDate),
            Description = StripHtml(issue.Description),
            ImageUrl = issue.Image?.MediumUrl ?? issue.Image?.SuperUrl,
            PublisherName = issue.Publisher?.Name
        };
    }

    private static FileMetadata MapIssue(CvIssueDetail detail)
    {
        var meta = new FileMetadata
        {
            FileFormat = "Comic",
            IsComic = true,
            Title = detail.Name ?? string.Empty,
            SeriesName = detail.Volume?.Name,
            SeriesNumber = detail.IssueNumber,
            Description = StripHtml(detail.Description),
            CoverUrl = detail.Image?.MediumUrl ?? detail.Image?.SuperUrl
        };

        if (detail.Publisher is not null)
            meta.Publisher = detail.Publisher.Name;

        if (!string.IsNullOrWhiteSpace(detail.CoverDate))
        {
            var parsed = ParseDate(detail.CoverDate);
            if (parsed.HasValue)
            {
                meta.PublishDate = parsed.Value;
                meta.PublishYear = parsed.Value.Year;
            }
        }

        if (detail.PersonCredits is not null)
        {
            foreach (var credit in detail.PersonCredits)
            {
                if (string.IsNullOrWhiteSpace(credit.Name)) continue;
                var role = MapComicVineRole(credit.Role);
                if (role is not null)
                {
                    meta.ComicPeople.Add(new ComicPersonInfo { Name = credit.Name, Role = role });
                }
                else if (string.Equals(credit.Role, "writer", StringComparison.OrdinalIgnoreCase))
                {
                    meta.Authors.Add(credit.Name);
                }
            }
        }

        if (detail.CharacterCredits is not null)
        {
            foreach (var character in detail.CharacterCredits)
            {
                if (!string.IsNullOrWhiteSpace(character.Name))
                    meta.Tags.Add($"Character: {character.Name}");
            }
        }

        if (meta.SeriesNumber is not null && float.TryParse(meta.SeriesNumber, out var idx))
            meta.SeriesIndex = idx;

        if (!string.IsNullOrWhiteSpace(detail.Image?.MediumUrl))
            meta.HasCover = true;

        return meta;
    }

    private static string? MapComicVineRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        return role.ToLowerInvariant() switch
        {
            "writer" => "Writer",
            "penciller" => "Penciller",
            "inker" => "Inker",
            "colorist" => "Colorist",
            "letterer" => "Letterer",
            "cover" => "CoverArtist",
            "editor" => "Editor",
            "translator" => "Translator",
            _ => null
        };
    }

    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        if (DateTime.TryParse(date, out var dt)) return dt;
        return null;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty).Trim();
    }

    private static void ConfigureRateLimiter()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return;

        var limit = config.ComicVineRateLimitPerHour > 0 ? config.ComicVineRateLimitPerHour : 180;
        _rateLimiter.Configure(limit, TimeSpan.FromHours(1));
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

    private class CvSearchResponse
    {
        [JsonPropertyName("results")]
        public List<CvSearchIssue>? Results { get; set; }
    }

    private class CvSearchIssue
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("volume")]
        public CvVolume? Volume { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("image")]
        public CvImage? Image { get; set; }

        [JsonPropertyName("publisher")]
        public CvPublisher? Publisher { get; set; }
    }

    private class CvIssueResponse
    {
        [JsonPropertyName("results")]
        public CvIssueDetail? Results { get; set; }
    }

    private class CvIssueDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("volume")]
        public CvVolume? Volume { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("publisher")]
        public CvPublisher? Publisher { get; set; }

        [JsonPropertyName("person_credits")]
        public List<CvCredit>? PersonCredits { get; set; }

        [JsonPropertyName("character_credits")]
        public List<CvCharacter>? CharacterCredits { get; set; }

        [JsonPropertyName("image")]
        public CvImage? Image { get; set; }
    }

    private class CvVolume
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class CvImage
    {
        [JsonPropertyName("medium_url")]
        public string? MediumUrl { get; set; }

        [JsonPropertyName("super_url")]
        public string? SuperUrl { get; set; }
    }

    private class CvPublisher
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class CvCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }

    private class CvCharacter
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

public class ComicVineSearchResult
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string IssueNumber { get; init; } = string.Empty;
    public string? SeriesName { get; init; }
    public int? SeriesId { get; init; }
    public DateTime? CoverDate { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? PublisherName { get; init; }
}
