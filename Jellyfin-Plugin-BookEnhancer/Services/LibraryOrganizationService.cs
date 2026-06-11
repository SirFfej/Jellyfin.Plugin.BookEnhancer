using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class LibraryOrganizationService
{
    private readonly ILogger<LibraryOrganizationService> _logger;

    private static readonly HashSet<string> _invalidPathChars = new(
        Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Select(c => c.ToString()));

    public LibraryOrganizationService(ILogger<LibraryOrganizationService> logger)
    {
        _logger = logger;
    }

    public string BuildTargetPath(string libraryRoot, FileMetadata metadata, string template)
    {
        var path = ResolveTemplate(template, metadata);
        return Path.Combine(libraryRoot, path);
    }

    public string ResolveTemplate(string template, FileMetadata metadata)
    {
        var components = template.Split('/', '\\')
            .Select(part => SanitizePathComponent(ResolveToken(part.Trim(), metadata)))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var fileName = GetFileName(metadata);
        components.Add(fileName);
        return Path.Combine(components.ToArray());
    }

    private static string ResolveToken(string part, FileMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(part))
            return string.Empty;

        return part
            .Replace("{Author}", GetAuthorDirectoryName(metadata))
            .Replace("{Series}", GetSeriesDirectoryName(metadata))
            .Replace("{Title}", GetTitleDirectoryName(metadata))
            .Replace("{Publisher}", GetPublisherDirectoryName(metadata));
    }

    public async Task<MoveResult> MoveFile(string sourcePath, string targetPath, bool copy = false, Func<string, Task>? logCallback = null)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return MoveResult.CreateError("Source file no longer exists");

            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir is null)
                return MoveResult.CreateError("Invalid target path");

            if (File.Exists(targetPath))
            {
                var sourceInfo = new FileInfo(sourcePath);
                var targetInfo = new FileInfo(targetPath);

                if (sourceInfo.Exists && targetInfo.Exists && sourceInfo.Length == targetInfo.Length)
                {
                    File.Delete(sourcePath);
                    if (logCallback is not null)
                        await logCallback($"Removed stale original (already at target): {sourcePath}").ConfigureAwait(false);
                    else
                        _logger.LogInformation("Removed stale original (already at target): {Src}", sourcePath);

                    return MoveResult.CreateSuccess(targetPath);
                }

                if (logCallback is not null)
                    await logCallback($"Target already exists, skipping: {targetPath}").ConfigureAwait(false);
                else
                    _logger.LogWarning("Target already exists, skipping: {Target}", targetPath);

                return MoveResult.CreateSkipped("Target file already exists");
            }

            Directory.CreateDirectory(targetDir);

            if (copy)
            {
                File.Copy(sourcePath, targetPath);
                if (logCallback is not null)
                    await logCallback($"Copied file: {sourcePath} -> {targetPath}").ConfigureAwait(false);
                else
                    _logger.LogInformation("Copied file: {Src} -> {Dst}", sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
                if (logCallback is not null)
                    await logCallback($"Moved file: {sourcePath} -> {targetPath}").ConfigureAwait(false);
                else
                    _logger.LogInformation("Moved file: {Src} -> {Dst}", sourcePath, targetPath);
            }

            return MoveResult.CreateSuccess(targetPath);
        }
        catch (Exception ex)
        {
            if (logCallback is not null)
                await logCallback($"Failed to move file {sourcePath} -> {targetPath}: {ex.Message}").ConfigureAwait(false);
            else
                _logger.LogWarning(ex, "Failed to move file {Src} -> {Dst}", sourcePath, targetPath);

            return MoveResult.CreateError(ex.Message);
        }
    }

    private static string GetAuthorDirectoryName(FileMetadata metadata)
    {
        if (metadata.Authors.Count > 0 && !string.IsNullOrWhiteSpace(metadata.Authors[0]))
        {
            var author = SanitizePathComponent(metadata.Authors[0]);
            if (!string.IsNullOrWhiteSpace(author))
                return author;
        }

        return "Unknown Author";
    }

    private static string GetSeriesDirectoryName(FileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.SeriesName))
        {
            var series = SanitizePathComponent(metadata.SeriesName);
            if (!string.IsNullOrWhiteSpace(series))
                return series;
        }

        return "Standalone";
    }

    private static string GetTitleDirectoryName(FileMetadata metadata)
    {
        var title = !string.IsNullOrWhiteSpace(metadata.Title)
            ? SanitizePathComponent(metadata.Title)
            : null;

        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(metadata.FilePath);

        return SanitizePathComponent(title) ?? "Untitled";
    }

    private static string GetPublisherDirectoryName(FileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Publisher))
        {
            var publisher = SanitizePathComponent(metadata.Publisher);
            if (!string.IsNullOrWhiteSpace(publisher))
                return publisher;
        }

        return "Unknown Publisher";
    }

    private static string GetFileName(FileMetadata metadata)
    {
        var baseName = !string.IsNullOrWhiteSpace(metadata.Title)
            ? SanitizePathComponent(metadata.Title)
            : Path.GetFileNameWithoutExtension(metadata.FilePath);

        var ext = Path.GetExtension(metadata.FilePath);
        return (baseName ?? "book") + ext;
    }

    private static string SanitizePathComponent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        input = input.Trim();

        foreach (var c in _invalidPathChars)
        {
            input = input.Replace(c, "_");
        }

        if (input.Length > 200)
            input = input[..200];

        return input.TrimEnd('.');
    }
}

public class MoveResult
{
    public bool Success { get; private set; }
    public bool Skipped { get; private set; }
    public string? TargetPath { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static MoveResult CreateSuccess(string targetPath) =>
        new() { Success = true, TargetPath = targetPath };

    public static MoveResult CreateSkipped(string reason) =>
        new() { Skipped = true, ErrorMessage = reason };

    public static MoveResult CreateError(string message) =>
        new() { ErrorMessage = message };
}
