using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>
/// In-memory cache for comic API enrichment results. Caches the final metadata returned by
/// a comic API lookup keyed by API name, series name, and issue number. This avoids repeated
/// calls when the same series/issue is encountered multiple times in one enrichment run.
/// </summary>
public sealed class ComicApiCache : IDisposable
{
    private readonly MemoryCache _cache;

    public ComicApiCache()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public async Task<FileMetadata?> GetOrAddAsync(
        string apiName,
        string seriesName,
        string? issueNumber,
        Func<Task<FileMetadata?>> factory,
        CancellationToken ct = default)
    {
        var key = BuildKey(apiName, seriesName, issueNumber);
        if (_cache.TryGetValue<FileMetadata?>(key, out var cached))
        {
            return cached;
        }

        var result = await factory().ConfigureAwait(false);
        if (result is not null)
        {
            _cache.Set(key, result, GetEntryOptions());
        }

        return result;
    }

    private static string BuildKey(string apiName, string seriesName, string? issueNumber)
    {
        var normalizedApi = Normalize(apiName);
        var normalizedSeries = Normalize(seriesName);
        var normalizedIssue = Normalize(issueNumber);

        return string.IsNullOrEmpty(normalizedIssue)
            ? $"comic:{normalizedApi}:{normalizedSeries}"
            : $"comic:{normalizedApi}:{normalizedSeries}:{normalizedIssue}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.ToLowerInvariant().Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static MemoryCacheEntryOptions GetEntryOptions()
    {
        // Short sliding expiration keeps entries alive during a batch enrichment run
        // without persisting stale data across long-running server sessions.
        return new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(30))
            .SetAbsoluteExpiration(TimeSpan.FromHours(2));
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
