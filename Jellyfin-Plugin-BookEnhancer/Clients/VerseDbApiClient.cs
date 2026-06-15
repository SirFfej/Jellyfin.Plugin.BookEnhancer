using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class VerseDbApiClient
{
    private static readonly Uri BaseUrl = new("https://www.versedb.app/api");
    private static readonly SimpleRateLimiter _rateLimiter = new(30, TimeSpan.FromMinutes(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VerseDbApiClient> _logger;

    public VerseDbApiClient(IHttpClientFactory httpClientFactory, ILogger<VerseDbApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<VerseDbSearchResult>> SearchIssuesAsync(string query, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return [];

        try
        {
            var url = $"{BaseUrl}/issues?search={Uri.EscapeDataString(query)}";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.GetFromJsonAsync<VerseDbListResponse>(url, ct).ConfigureAwait(false);
            if (response?.Data is null) return [];

            return response.Data
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(MapSearchResult)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("VerseDB", $"search failed for query \"{query}\"", ex);
            _logger.LogDebug("VerseDB search failed for query {Query}; details in api-responses log", query);
            return [];
        }
    }

    public async Task<FileMetadata?> GetIssueDetailAsync(int issueId, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            var url = $"{BaseUrl}/issues/{issueId}";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var response = await client.GetFromJsonAsync<VerseDbIssueDetail>(url, ct).ConfigureAwait(false);
            return response is not null ? MapIssueDetail(response) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("VerseDB", $"issue detail failed for ID {issueId}", ex);
            _logger.LogDebug("VerseDB issue detail failed for ID {IssueId}; details in api-responses log", issueId);
            return null;
        }
    }

    private static VerseDbSearchResult MapSearchResult(VerseDbIssueListItem issue)
    {
        return new VerseDbSearchResult
        {
            Id = issue.Id,
            Title = issue.Name ?? string.Empty,
            IssueNumber = issue.IssueNumber ?? string.Empty,
            SeriesName = issue.SeriesName,
            CoverDate = ParseDate(issue.CoverDate),
            Description = StripHtml(issue.Description),
            ImageUrl = issue.CoverUrl,
            PublisherName = issue.PublisherName
        };
    }

    private static FileMetadata? MapIssueDetail(VerseDbIssueDetail detail)
    {
        if (string.IsNullOrWhiteSpace(detail.Name)) return null;

        var meta = new FileMetadata
        {
            FileFormat = "Comic",
            IsComic = true,
            Title = detail.Name,
            SeriesName = detail.SeriesName,
            SeriesNumber = detail.IssueNumber,
            Description = StripHtml(detail.Description),
            CoverUrl = detail.CoverUrl,
            Publisher = detail.PublisherName
        };

        if (!string.IsNullOrWhiteSpace(detail.CoverDate))
        {
            var parsed = ParseDate(detail.CoverDate);
            if (parsed.HasValue)
            {
                meta.PublishDate = parsed.Value;
                meta.PublishYear = parsed.Value.Year;
            }
        }

        if (detail.Creators is not null)
        {
            foreach (var credit in detail.Creators)
            {
                if (string.IsNullOrWhiteSpace(credit.Name)) continue;
                var role = MapVerseDbRole(credit.Role);
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

        if (detail.Characters is not null)
        {
            foreach (var character in detail.Characters)
            {
                if (!string.IsNullOrWhiteSpace(character))
                    meta.Tags.Add($"Character: {character}");
            }
        }

        if (detail.IssueNumber is not null && float.TryParse(detail.IssueNumber, out var idx))
            meta.SeriesIndex = idx;

        if (!string.IsNullOrWhiteSpace(detail.CoverUrl))
            meta.HasCover = true;

        return meta;
    }

    private static string? MapVerseDbRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        return role.ToLowerInvariant() switch
        {
            "writer" => "Writer",
            "penciller" => "Penciller",
            "penciler" => "Penciller",
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

    private class VerseDbListResponse
    {
        [JsonPropertyName("data")]
        public List<VerseDbIssueListItem>? Data { get; set; }
    }

    private class VerseDbIssueListItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("series_name")]
        public string? SeriesName { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("cover_url")]
        public string? CoverUrl { get; set; }

        [JsonPropertyName("publisher_name")]
        public string? PublisherName { get; set; }
    }

    private class VerseDbIssueDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("series_name")]
        public string? SeriesName { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("cover_url")]
        public string? CoverUrl { get; set; }

        [JsonPropertyName("publisher_name")]
        public string? PublisherName { get; set; }

        [JsonPropertyName("creators")]
        public List<VerseDbCredit>? Creators { get; set; }

        [JsonPropertyName("characters")]
        public List<string>? Characters { get; set; }
    }

    private class VerseDbCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }
}

public class VerseDbSearchResult
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string IssueNumber { get; init; } = string.Empty;
    public string? SeriesName { get; init; }
    public DateTime? CoverDate { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? PublisherName { get; init; }
}
