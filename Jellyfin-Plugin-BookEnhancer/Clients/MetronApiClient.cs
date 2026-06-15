using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class MetronApiClient
{
    private static readonly Uri BaseUrl = new("https://metron.cloud/api");
    private static readonly SimpleRateLimiter _rateLimiter = new(20, TimeSpan.FromMinutes(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetronApiClient> _logger;

    public MetronApiClient(IHttpClientFactory httpClientFactory, ILogger<MetronApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static AuthenticationHeaderValue BasicAuthHeader(string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<List<MetronSearchResult>> SearchIssuesAsync(string seriesName, string issueNumber, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return [];

        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return [];

        try
        {
            var query = $"?series_name={Uri.EscapeDataString(seriesName)}&number={Uri.EscapeDataString(issueNumber)}&limit=5";
            var url = $"{BaseUrl}/issue/{query}";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            client.DefaultRequestHeaders.Authorization = BasicAuthHeader(username, password);

            var response = await client.GetFromJsonAsync<MetronListResponse>(url, ct).ConfigureAwait(false);
            if (response?.Results is null) return [];

            return response.Results
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
            ApiResponseLogger.Log("Metron", $"search failed for \"{seriesName}\" #{issueNumber}", ex);
            _logger.LogDebug("Metron search failed for {Series} #{Issue}; details in api-responses log", seriesName, issueNumber);
            return [];
        }
    }

    public async Task<FileMetadata?> GetIssueDetailAsync(int issueId, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            var url = $"{BaseUrl}/issue/{issueId}/";
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            client.DefaultRequestHeaders.Authorization = BasicAuthHeader(username, password);

            var response = await client.GetFromJsonAsync<MetronIssueDetail>(url, ct).ConfigureAwait(false);
            return response is not null ? MapIssueDetail(response) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Metron", $"issue detail failed for ID {issueId}", ex);
            _logger.LogDebug("Metron issue detail failed for ID {IssueId}; details in api-responses log", issueId);
            return null;
        }
    }

    private static MetronSearchResult MapSearchResult(MetronIssueListItem issue)
    {
        return new MetronSearchResult
        {
            Id = issue.Id,
            Title = issue.Name ?? string.Empty,
            IssueNumber = issue.IssueNumber ?? string.Empty,
            SeriesName = issue.Series?.Name,
            CoverDate = ParseDate(issue.CoverDate),
            Description = StripHtml(issue.Description),
            ImageUrl = issue.CoverImage,
            PublisherName = issue.Publisher?.Name
        };
    }

    private static FileMetadata? MapIssueDetail(MetronIssueDetail detail)
    {
        if (string.IsNullOrWhiteSpace(detail.Name)) return null;

        var meta = new FileMetadata
        {
            FileFormat = "Comic",
            IsComic = true,
            Title = detail.Name,
            SeriesName = detail.Series?.Name,
            SeriesNumber = detail.IssueNumber,
            Description = StripHtml(detail.Description),
            CoverUrl = detail.CoverImage
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

        if (detail.Creators is not null)
        {
            foreach (var credit in detail.Creators)
            {
                if (string.IsNullOrWhiteSpace(credit.Name)) continue;
                var role = MapMetronRole(credit.Role);
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
                if (!string.IsNullOrWhiteSpace(character.Name))
                    meta.Tags.Add($"Character: {character.Name}");
            }
        }

        if (detail.IssueNumber is not null && float.TryParse(detail.IssueNumber, out var idx))
            meta.SeriesIndex = idx;

        if (!string.IsNullOrWhiteSpace(detail.CoverImage))
            meta.HasCover = true;

        return meta;
    }

    private static string? MapMetronRole(string? role)
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

    private class MetronListResponse
    {
        [JsonPropertyName("results")]
        public List<MetronIssueListItem>? Results { get; set; }
    }

    private class MetronIssueListItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("series")]
        public MetronRef? Series { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("cover_image")]
        public string? CoverImage { get; set; }

        [JsonPropertyName("publisher")]
        public MetronRef? Publisher { get; set; }
    }

    private class MetronIssueDetail
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("series")]
        public MetronRef? Series { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("cover_image")]
        public string? CoverImage { get; set; }

        [JsonPropertyName("publisher")]
        public MetronRef? Publisher { get; set; }

        [JsonPropertyName("creators")]
        public List<MetronCredit>? Creators { get; set; }

        [JsonPropertyName("characters")]
        public List<MetronRef>? Characters { get; set; }
    }

    private class MetronRef
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class MetronCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }
}

public class MetronSearchResult
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
