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
    /// Loads the current duplicate-review list from disk.
    /// </summary>
    /// <returns>The persisted duplicate-review entries.</returns>
    public static async Task<List<DuplicateReviewEntry>> LoadAsync()
    {
        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return [];
        }

        var path = GetPath(dataPath);

        await Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = await LoadExistingAsync(path).ConfigureAwait(false);
            return entries;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    /// <summary>
    /// Removes a single duplicate-review entry if it exists.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="targetPath">The target file path.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RemoveAsync(string sourcePath, string targetPath)
    {
        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
            return;

        var path = GetPath(dataPath);

        await Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await LoadExistingAsync(path).ConfigureAwait(false);
            var dictionary = existing.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
            var key = Key(sourcePath, targetPath);
            if (!dictionary.Remove(key))
                return;

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dictionary.Values.ToList(), JsonOptions)).ConfigureAwait(false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    /// <summary>
    /// Removes a range of duplicate-review entries in a single write.
    /// </summary>
    /// <param name="entriesToRemove">The entries to remove.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RemoveRangeAsync(IEnumerable<DuplicateReviewEntry> entriesToRemove)
    {
        var removals = entriesToRemove.ToList();
        if (removals.Count == 0)
            return;

        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
            return;

        var path = GetPath(dataPath);

        await Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await LoadExistingAsync(path).ConfigureAwait(false);
            var dictionary = existing.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in removals)
                dictionary.Remove(Key(entry));

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dictionary.Values.ToList(), JsonOptions)).ConfigureAwait(false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    /// <summary>
    /// Prunes entries older than the cutoff, entries pointing to missing files, and
    /// entries whose source file has been modified more recently than the recorded last-seen time.
    /// </summary>
    /// <param name="cutoffUtc">Entries with a LastSeen older than this are removed.</param>
    /// <param name="logger">Logger for debug output.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task PruneAsync(DateTime cutoffUtc, ILogger logger)
    {
        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
            return;

        var path = GetPath(dataPath);

        await Semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await LoadExistingAsync(path).ConfigureAwait(false);
            var pruned = existing
                .Where(e => e.LastSeen >= cutoffUtc)
                .Where(e => File.Exists(e.SourcePath) && File.Exists(e.TargetPath))
                .Where(e => !IsSourceNewerThanRecorded(e))
                .ToList();

            if (pruned.Count == existing.Count)
                return;

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(pruned, JsonOptions)).ConfigureAwait(false);
            logger.LogDebug("Pruned duplicate-review log from {Before} to {After} entries", existing.Count, pruned.Count);
        }
        finally
        {
            Semaphore.Release();
        }
    }

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
        {
            return;
        }

        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            logger.LogWarning("Cannot persist duplicate-review entries because the plugin data path is not available");
            return;
        }

        var path = GetPath(dataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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
            logger.LogInformation("Persisted {Count} duplicate-review entries to {Path}", filtered.Count, path);
        }
        finally
        {
            Semaphore.Release();
        }

        if (logCallback is not null)
            await logCallback($"Updated duplicate-review log: {path}").ConfigureAwait(false);

        logger.LogDebug("Updated duplicate-review log with {Count} new entries", newEntries.Count);
    }

    private static string GetPath(string dataPath)
    {
        return Path.Combine(dataPath, "plugins", "BookEnhancer", "duplicate-reviews", "duplicate-reviews.json");
    }

    private static bool IsSourceNewerThanRecorded(DuplicateReviewEntry entry)
    {
        try
        {
            if (!File.Exists(entry.SourcePath))
                return false;

            var lastWrite = File.GetLastWriteTimeUtc(entry.SourcePath);
            return lastWrite > entry.LastSeen;
        }
        catch
        {
            return false;
        }
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

    private static string Key(string sourcePath, string targetPath)
    {
        return sourcePath + "|" + targetPath;
    }

    private static string Key(DuplicateReviewEntry entry)
    {
        return Key(entry.SourcePath, entry.TargetPath);
    }
}
