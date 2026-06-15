using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class LibraryOrganizationService
{
    private readonly ILogger<LibraryOrganizationService> _logger;

    private static readonly HashSet<char> _invalidPathChars = new(
        Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()));

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
            .ToArray();

        var fileName = GetFileName(metadata);
        var allComponents = new string[components.Length + 1];
        components.CopyTo(allComponents, 0);
        allComponents[^1] = fileName;
        return Path.Combine(allComponents);
    }

    private static string ResolveToken(string part, FileMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(part))
            return string.Empty;

        return part
            .Replace("{Author}", GetAuthorDirectoryName(metadata))
            .Replace("{Series}", GetSeriesDirectoryName(metadata))
            .Replace("{Volume}", GetVolumeDirectoryName(metadata))
            .Replace("{Title}", GetTitleDirectoryName(metadata))
            .Replace("{BookTitle}", GetBookTitleDirectoryName(metadata))
            .Replace("{Disc}", GetDiscDirectoryName(metadata))
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
                if (logCallback is not null)
                    await logCallback($"Target already exists, skipping: {targetPath}").ConfigureAwait(false);
                else
                    _logger.LogWarning("Target already exists, skipping: {Target}", targetPath);

                return MoveResult.CreateSkipped("Target file already exists");
            }

            Directory.CreateDirectory(targetDir);

            if (copy)
            {
                await SafeFileOperations.CopyFileAsync(sourcePath, targetPath).ConfigureAwait(false);
                if (logCallback is not null)
                    await logCallback($"Copied file: {sourcePath} -> {targetPath}").ConfigureAwait(false);
                else
                    _logger.LogInformation("Copied file: {Src} -> {Dst}", sourcePath, targetPath);
            }
            else
            {
                await SafeFileOperations.MoveFileAsync(sourcePath, targetPath).ConfigureAwait(false);
                if (logCallback is not null)
                    await logCallback($"Moved file: {sourcePath} -> {targetPath}").ConfigureAwait(false);
                else
                    _logger.LogInformation("Moved file: {Src} -> {Dst}", sourcePath, targetPath);
            }

            return MoveResult.CreateSuccess(targetPath);
        }
        catch (OperationCanceledException)
        {
            throw;
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

    public static string GetDefaultTemplate(FileMetadata metadata, bool flatSeriesStructure = false)
    {
        if (flatSeriesStructure)
        {
            if (!string.IsNullOrWhiteSpace(metadata.Publisher))
            {
                return !string.IsNullOrWhiteSpace(metadata.SeriesName)
                    ? "{Publisher}/{Series}"
                    : "{Publisher}/{Title}";
            }

            return !string.IsNullOrWhiteSpace(metadata.SeriesName)
                ? "{Author}/{Series}"
                : "{Author}/{Title}";
        }

        if (!string.IsNullOrWhiteSpace(metadata.Publisher))
            return "{Publisher}/{Series}/{Title}";

        return "{Author}/{Series}/{Title}";
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

    private static string GetVolumeDirectoryName(FileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Volume))
        {
            var volume = SanitizePathComponent(metadata.Volume);
            if (!string.IsNullOrWhiteSpace(volume))
                return volume;
        }

        return string.Empty;
    }

    private static string GetTitleDirectoryName(FileMetadata metadata)
    {
        var title = !string.IsNullOrWhiteSpace(metadata.Title)
            ? SanitizePathComponent(metadata.Title)
            : null;

        if (string.IsNullOrWhiteSpace(title))
            title = SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(metadata.FilePath));

        return SanitizePathComponent(title) ?? "Untitled";
    }

    private static string GetBookTitleDirectoryName(FileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.BookTitle))
        {
            var bookTitle = SanitizePathComponent(metadata.BookTitle);
            if (!string.IsNullOrWhiteSpace(bookTitle))
                return bookTitle;
        }

        return string.Empty;
    }

    private static string GetDiscDirectoryName(FileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.DiscNumber))
        {
            var disc = SanitizePathComponent(metadata.DiscNumber);
            if (!string.IsNullOrWhiteSpace(disc))
                return disc;
        }

        return string.Empty;
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
            : SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(metadata.FilePath));

        var ext = Path.GetExtension(metadata.FilePath);
        return (baseName ?? "book") + ext;
    }

    private static string SanitizePathComponent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var trimmed = input.Trim();
        var chars = trimmed.ToCharArray();
        var changed = false;

        for (var i = 0; i < chars.Length; i++)
        {
            if (_invalidPathChars.Contains(chars[i]))
            {
                chars[i] = '_';
                changed = true;
            }
        }

        var result = changed ? new string(chars) : trimmed;

        if (result.Length > 200)
            result = result[..200];

        return result.TrimEnd('.');
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
