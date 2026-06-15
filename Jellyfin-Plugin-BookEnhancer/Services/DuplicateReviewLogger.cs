using System.Text.Json;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>
/// Maintains a persisted list of duplicate files where the target already exists but the sizes differ.
/// The list is pruned to entries where both the source and target files still exist.
/// </summary>
public static class DuplicateReviewLogger
{
    private static readonly SemaphoreSlim Semaphore = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Merges newly detected mismatches into the persisted duplicate-review list and saves it.
    /// </summary>
    /// <param name="newEntries">New mismatches detected during this operation.</param>
    /// <param name="logCallback">Optional callback for logging the operation.</param>
    /// <param name="logger">Logger for debug output.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task MergeAndSaveAsync(
        List<DuplicateReviewEntry> newEntries,
        Func<string, Task>? logCallback,
        ILogger logger)
    {
        if (newEntries.Count == 0)
            return;

        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
            return;

        var dir = Path.Combine(dataPath, "plugins", "BookEnhancer", "duplicate-reviews");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "duplicate-reviews.json");

        await Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await LoadExistingAsync(path).ConfigureAwait(false);
            var dictionary = existing.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in newEntries)
            {
                var key = Key(entry);
                if (dictionary.TryGetValue(key, out var existingEntry))
                {
                    existingEntry.SourceSize = entry.SourceSize;
                    existingEntry.TargetSize = entry.TargetSize;
                    existingEntry.LastSeen = DateTime.UtcNow;
                }
                else
                {
                    entry.LastSeen = DateTime.UtcNow;
                    dictionary[key] = entry;
                }
            }

            var filtered = dictionary.Values
                .Where(e => File.Exists(e.SourcePath) && File.Exists(e.TargetPath))
                .OrderBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(filtered, JsonOptions)).ConfigureAwait(false);
        }
        finally
        {
            Semaphore.Release();
        }

        if (logCallback is not null)
            await logCallback($"Updated duplicate-review log: {path}").ConfigureAwait(false);

        logger.LogDebug("Updated duplicate-review log with {Count} new entries", newEntries.Count);
    }

    private static async Task<List<DuplicateReviewEntry>> LoadExistingAsync(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<DuplicateReviewEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string Key(DuplicateReviewEntry entry)
    {
        return entry.SourcePath + "|" + entry.TargetPath;
    }
}
