using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Clients;

public class GrandComicsDbApiClient
{
    private static readonly Uri BaseUrl = new("https://www.comics.org/api");
    private static readonly SimpleRateLimiter _rateLimiter = new(80, TimeSpan.FromHours(1));
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrandComicsDbApiClient> _logger;

    public GrandComicsDbApiClient(IHttpClientFactory httpClientFactory, ILogger<GrandComicsDbApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static AuthenticationHeaderValue BasicAuthHeader(string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<FileMetadata?> SearchBySeriesAndIssueAsync(
        string seriesName, string issueNumber, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        try
        {
            // Step 1: Direct issue search by series name + issue number
            var issueUrl = await FindIssueBySeriesAndNumberAsync(seriesName, issueNumber, username, password, ct).ConfigureAwait(false);
            if (issueUrl is null)
            {
                _logger.LogDebug("GCD: no issue #{Issue} found for series '{Series}'", issueNumber, seriesName);
                return null;
            }

            var issueId = ExtractIssueId(issueUrl);
            if (issueId is null)
            {
                _logger.LogDebug("GCD: could not extract issue ID from URL: {Url}", issueUrl);
                return null;
            }

            return await GetIssueDetailAsync(issueId.Value, username, password, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (oce.IsCallerCancellation(ct))
        {
            throw;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Grand Comics Database", $"search failed for \"{seriesName}\" #{issueNumber}", ex);
            _logger.LogDebug("GCD search failed for {Series} #{Issue}; details in api-responses log", seriesName, issueNumber);
            return null;
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

            var response = await client.GetFromJsonAsync<GcdIssueDetail>(url, ct).ConfigureAwait(false);
            if (response is null) return null;

            return MapIssueDetail(response);
        }
        catch (OperationCanceledException oce) when (oce.IsCallerCancellation(ct))
        {
            throw;
        }
        catch (Exception ex)
        {
            ApiResponseLogger.Log("Grand Comics Database", $"issue detail failed for ID {issueId}", ex);
            _logger.LogDebug("GCD issue detail failed for ID {IssueId}; details in api-responses log", issueId);
            return null;
        }
    }

    /// <summary>
    /// Searches for an issue using the direct series/name/{name}/issue/{number} endpoint.
    /// Falls back to stripping leading zeros if no results found.
    /// </summary>
    private async Task<string?> FindIssueBySeriesAndNumberAsync(
        string seriesName, string issueNumber, string username, string password, CancellationToken ct)
    {
        if (!await WaitForRateLimitSlotAsync(ct).ConfigureAwait(false))
            return null;

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
        client.DefaultRequestHeaders.Authorization = BasicAuthHeader(username, password);

        // Try the exact number first, then try stripping leading zeros
        var numbersToTry = new HashSet<string>(StringComparer.Ordinal);

        numbersToTry.Add(issueNumber);

        var stripped = issueNumber.TrimStart('0');
        if (stripped != issueNumber && !string.IsNullOrWhiteSpace(stripped))
            numbersToTry.Add(stripped);

        foreach (var num in numbersToTry)
        {
            var escapedName = Uri.EscapeDataString(seriesName);
            var escapedNum = Uri.EscapeDataString(num);
            var searchUrl = $"{BaseUrl}/series/name/{escapedName}/issue/{escapedNum}/";

            var response = await client.GetFromJsonAsync<GcdIssueSearchResponse>(searchUrl, ct).ConfigureAwait(false);
            if (response?.Results is null || response.Results.Count == 0)
                continue;

            var bestMatch = PickBestIssue(response.Results, seriesName, num);
            if (bestMatch is not null)
                return bestMatch.ApiUrl;
        }

        return null;
    }

    /// <summary>
    /// From a list of candidate issues, pick the best match by scoring series_name similarity.
    /// </summary>
    private static GcdIssueSearchEntry? PickBestIssue(List<GcdIssueSearchEntry> issues, string seriesName, string issueNumber)
    {
        var normalizedName = seriesName.ToLowerInvariant().Replace("  ", " ").Trim();
        var normalizedIssue = issueNumber.TrimStart('0');

        return issues
            .Select(i => new
            {
                Issue = i,
                Score = ScoreIssue(i, normalizedName, normalizedIssue)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Issue.HasVariant ? 1 : 0) // prefer non-variant
            .Select(x => x.Issue)
            .FirstOrDefault();
    }

    private static int ScoreIssue(GcdIssueSearchEntry issue, string normalizedSeriesName, string normalizedIssueNumber)
    {
        var seriesName = (issue.SeriesName ?? "").ToLowerInvariant().Replace("  ", " ").Trim();
        var score = 0;

        if (seriesName == normalizedSeriesName)
            score += 100;
        else if (seriesName.StartsWith(normalizedSeriesName, StringComparison.OrdinalIgnoreCase))
            score += 80;
        else if (seriesName.Contains(normalizedSeriesName, StringComparison.OrdinalIgnoreCase))
            score += 60;

        return score;
    }

    private static int? ExtractIssueId(string issueUrl)
    {
        var trimmed = issueUrl.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 0) return null;

        if (int.TryParse(trimmed[(lastSlash + 1)..], out var id))
            return id;

        return null;
    }

    private static FileMetadata? MapIssueDetail(GcdIssueDetail detail)
    {
        if (string.IsNullOrWhiteSpace(detail.Number)) return null;

        var meta = new FileMetadata
        {
            FileFormat = "Comic",
            IsComic = true,
            Title = !string.IsNullOrWhiteSpace(detail.Title)
                ? detail.Title
                : $"{detail.SeriesName} #{detail.Number}",
            SeriesName = detail.SeriesName,
            SeriesNumber = detail.Number,
            Publisher = detail.IndiciaPublisher
        };

        if (!string.IsNullOrWhiteSpace(detail.OnSaleDate))
        {
            if (DateOnly.TryParse(detail.OnSaleDate, out var saleDate))
            {
                meta.PublishDate = saleDate.ToDateTime(TimeOnly.MinValue);
                meta.PublishYear = saleDate.Year;
            }
        }

        if (!string.IsNullOrWhiteSpace(detail.Cover))
        {
            meta.HasCover = true;
            meta.CoverUrl = detail.Cover;
        }

        if (!string.IsNullOrWhiteSpace(detail.PageCount))
        {
            var cleaned = detail.PageCount.TrimEnd('.');
            if (int.TryParse(cleaned, out var pages))
                meta.PageCount = pages;
        }

        if (!string.IsNullOrWhiteSpace(detail.Rating))
            meta.AgeRating = detail.Rating;

        if (!string.IsNullOrWhiteSpace(detail.Isbn))
            meta.Isbn = detail.Isbn;

        if (detail.Number is not null && float.TryParse(detail.Number, out var idx))
            meta.SeriesIndex = idx;

        if (detail.StorySet is not null)
        {
            foreach (var story in detail.StorySet)
            {
                if (!string.IsNullOrWhiteSpace(story.Characters))
                {
                    foreach (var character in story.Characters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrWhiteSpace(character) && !meta.Tags.Contains($"Character: {character}"))
                            meta.Tags.Add($"Character: {character}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(story.Synopsis) && string.IsNullOrWhiteSpace(meta.Description))
                    meta.Description = story.Synopsis;

                if (!string.IsNullOrWhiteSpace(story.Genre) && !meta.Genres.Contains(story.Genre))
                    meta.Genres.Add(story.Genre);

                if (!string.IsNullOrWhiteSpace(story.Script))
                    AddComicPeople(meta.ComicPeople, story.Script, "Writer");
                if (!string.IsNullOrWhiteSpace(story.Pencils))
                    AddComicPeople(meta.ComicPeople, story.Pencils, "Penciller");
                if (!string.IsNullOrWhiteSpace(story.Inks))
                    AddComicPeople(meta.ComicPeople, story.Inks, "Inker");
                if (!string.IsNullOrWhiteSpace(story.Colors))
                    AddComicPeople(meta.ComicPeople, story.Colors, "Colorist");
                if (!string.IsNullOrWhiteSpace(story.Letters))
                    AddComicPeople(meta.ComicPeople, story.Letters, "Letterer");
            }
        }

        if (!string.IsNullOrWhiteSpace(detail.Editing))
        {
            foreach (var editor in detail.Editing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = editor.Split('(')[0].Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    meta.ComicPeople.Add(new ComicPersonInfo { Name = name, Role = "Editor" });
            }
        }

        return meta;
    }

    private static void AddComicPeople(List<ComicPersonInfo> people, string names, string role)
    {
        foreach (var name in names.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var clean = name.Split('(')[0].Trim();
            if (!string.IsNullOrWhiteSpace(clean))
                people.Add(new ComicPersonInfo { Name = clean, Role = role });
        }
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

    private class GcdIssueSearchResponse
    {
        [JsonPropertyName("results")]
        public List<GcdIssueSearchEntry>? Results { get; set; }
    }

    private class GcdIssueSearchEntry
    {
        [JsonPropertyName("api_url")]
        public string? ApiUrl { get; set; }

        [JsonPropertyName("series_name")]
        public string? SeriesName { get; set; }

        [JsonPropertyName("descriptor")]
        public string? Descriptor { get; set; }

        [JsonIgnore]
        public bool HasVariant => Descriptor is not null && Descriptor.Contains('[');
    }

    private class GcdIssueDetail
    {
        [JsonPropertyName("series_name")]
        public string? SeriesName { get; set; }

        [JsonPropertyName("number")]
        public string? Number { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("on_sale_date")]
        public string? OnSaleDate { get; set; }

        [JsonPropertyName("page_count")]
        public string? PageCount { get; set; }

        [JsonPropertyName("isbn")]
        public string? Isbn { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; }

        [JsonPropertyName("editing")]
        public string? Editing { get; set; }

        [JsonPropertyName("indicia_publisher")]
        public string? IndiciaPublisher { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("story_set")]
        public List<GcdStory>? StorySet { get; set; }
    }

    private class GcdStory
    {
        [JsonPropertyName("script")]
        public string? Script { get; set; }

        [JsonPropertyName("pencils")]
        public string? Pencils { get; set; }

        [JsonPropertyName("inks")]
        public string? Inks { get; set; }

        [JsonPropertyName("colors")]
        public string? Colors { get; set; }

        [JsonPropertyName("letters")]
        public string? Letters { get; set; }

        [JsonPropertyName("genre")]
        public string? Genre { get; set; }

        [JsonPropertyName("characters")]
        public string? Characters { get; set; }

        [JsonPropertyName("synopsis")]
        public string? Synopsis { get; set; }
    }
}
