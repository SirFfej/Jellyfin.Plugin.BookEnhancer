using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class LibraryOrganizationService
{
    private readonly ILogger<LibraryOrganizationService> _logger;

    private static readonly HashSet<string> InvalidPathChars = new(
        Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Select(c => c.ToString()));

    public LibraryOrganizationService(ILogger<LibraryOrganizationService> logger)
    {
        _logger = logger;
    }

    public string BuildTargetPath(string libraryRoot, FileMetadata metadata)
    {
        var author = GetAuthorDirectoryName(metadata);
        var title = GetTitleDirectoryName(metadata);
        var fileName = GetFileName(metadata);

        return Path.Combine(libraryRoot, author, title, fileName);
    }

    public string BuildAlternateFormatPath(string primaryFilePath, FileMetadata metadata)
    {
        var primaryDir = Path.GetDirectoryName(primaryFilePath);
        if (primaryDir == null)
            throw new InvalidOperationException("Primary file path has no directory");

        var formatsDir = Path.Combine(primaryDir, ".formats");
        var fileName = GetFileName(metadata);

        return Path.Combine(formatsDir, fileName);
    }

    public MoveResult MoveFile(string sourcePath, string targetPath, bool copy = false)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return MoveResult.CreateError("Source file no longer exists");

            var targetDir = Path.GetDirectoryName(targetPath);
            if (targetDir == null)
                return MoveResult.CreateError("Invalid target path");

            if (File.Exists(targetPath))
            {
                _logger.LogWarning("Target already exists, skipping: {Target}", targetPath);
                return MoveResult.CreateSkipped("Target file already exists");
            }

            Directory.CreateDirectory(targetDir);

            if (copy)
            {
                File.Copy(sourcePath, targetPath);
                _logger.LogInformation("Copied file: {Src} -> {Dst}", sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
                _logger.LogInformation("Moved file: {Src} -> {Dst}", sourcePath, targetPath);
            }

            return MoveResult.CreateSuccess(targetPath);
        }
        catch (Exception ex)
        {
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

    private static string GetTitleDirectoryName(FileMetadata metadata)
    {
        var title = !string.IsNullOrWhiteSpace(metadata.Title)
            ? SanitizePathComponent(metadata.Title)
            : null;

        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileNameWithoutExtension(metadata.FilePath);

        return SanitizePathComponent(title) ?? "Untitled";
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

        foreach (var c in InvalidPathChars)
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
