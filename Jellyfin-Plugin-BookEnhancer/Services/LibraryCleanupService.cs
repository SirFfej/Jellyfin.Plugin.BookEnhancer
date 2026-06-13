using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class LibraryCleanupService
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly LibraryOrganizationService _organization;
    private readonly BookGroupingService _groupingService;
    private readonly IApplicationPaths _appPaths;

    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".avif", ".svg"
    };

    private static readonly HashSet<string> _imageBaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover", "folder", "poster", "thumbnail", "thumb", "front", "back", "spine"
    };

    private static readonly HashSet<string> _sharedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".djvu",
        ".cbz", ".cbr", ".cb7",
        ".mp3", ".m4a", ".m4b", ".m4p", ".flac", ".ogg", ".wma", ".opus", ".aiff", ".aac", ".wav"
    };

    public LibraryCleanupService(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        LibraryOrganizationService organization,
        BookGroupingService groupingService,
        IApplicationPaths appPaths)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _organization = organization;
        _groupingService = groupingService;
        _appPaths = appPaths;
    }

    private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

    public async Task<CleanupResult> RunCleanupAsync(IProgress<double> progress, Func<string, Task> logCallback, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return new CleanupResult { Errors = 1 };

        var result = new CleanupResult();
        var dirs = config.ManagedDirectories
            .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.LibraryPath))
            .ToList();

        if (dirs.Count == 0)
        {
            await logCallback("No enabled source directories with library paths configured.").ConfigureAwait(false);
            return result;
        }

        var supportedExts = GetSupportedExtensions(config);

        var libraryGroups = dirs
            .GroupBy(d => d.LibraryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var grandTotal = 0;
        var grandProcessed = 0;

        foreach (var libGroup in libraryGroups)
        {
            var libraryRoot = libGroup.Key;
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(libraryRoot))
            {
                await logCallback($"[{libraryRoot}] Path does not exist, skipping.").ConfigureAwait(false);
                continue;
            }

            var libFiles = libGroup
                .SelectMany(d => Directory.EnumerateFiles(d.LibraryPath, "*", SearchOption.AllDirectories)
                    .Where(f => supportedExts.Contains(Path.GetExtension(f))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (libFiles.Count == 0)
            {
                await logCallback($"[{libraryRoot}] No files found.").ConfigureAwait(false);
                await CleanupNonBookDirectories(libGroup, result, logCallback, ct).ConfigureAwait(false);
                continue;
            }

            grandTotal += libFiles.Count;
            var libResult = new CleanupResult();
            var libLabel = Path.GetFileName(libraryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.PathSeparator));

            await logCallback($"[{libLabel}] Scanning {libFiles.Count} files for metadata...").ConfigureAwait(false);

            var fileData = new List<(string Path, FileMetadata Metadata, ManagedSourceDirectory Dir, string Template)>();
            var enrichmentQueue = new List<(string Path, FileMetadata Metadata, ManagedSourceDirectory Dir, string Template)>();

            foreach (var file in libFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var metadata = await _fileExtractor.ExtractAsync(file, ct).ConfigureAwait(false);
                    if (metadata is null)
                        metadata = CreateMinimalMetadata(file);

                    var dir = libGroup
                        .Where(d => file.StartsWith(d.LibraryPath, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(d => d.LibraryPath.Length)
                        .First();

                    var template = string.IsNullOrWhiteSpace(dir.OrganizeTemplate)
                        ? "{Author}/{Series}/{Title}"
                        : dir.OrganizeTemplate;

                    if (config.UnifiedMetadataEnabled && NeedsEnrichment(metadata, template))
                    {
                        enrichmentQueue.Add((file, metadata, dir, template));
                    }
                    else
                    {
                        if (config.UnifiedMetadataEnabled)
                            await logCallback($"[{libLabel}] Rich Local Metadata - Skipping enrichment: {file}").ConfigureAwait(false);

                        fileData.Add((file, metadata, dir, template));
                    }
                }
                catch (Exception ex)
                {
                    await logCallback($"[{libLabel}] Error reading {file}: {ex.Message}").ConfigureAwait(false);
                    libResult.Errors++;
                }
            }

            var duplicateIndex = BuildDuplicateIndex(fileData);

            var processed = 0;

            foreach (var (file, metadata, dir, template) in fileData)
            {
                ct.ThrowIfCancellationRequested();
                processed++;

                try
                {
                    var expectedPath = _organization.BuildTargetPath(libraryRoot, metadata, template);

                    if (string.Equals(file, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        libResult.FilesSkipped++;
                        continue;
                    }

                    var dup = FindDuplicate(file, metadata, duplicateIndex);
                    if (dup is not null)
                    {
                        File.Delete(file);
                        libResult.DuplicatesFound++;
                        await logCallback($"[{libLabel}] Duplicate of {dup}, removed: {file}").ConfigureAwait(false);

                        var dupDir = Path.GetDirectoryName(file);
                        await MoveCompanionImagesAsync(dupDir, Path.GetDirectoryName(expectedPath), logCallback).ConfigureAwait(false);
                        await CleanupEmptyDirectories(dupDir, libResult, logCallback).ConfigureAwait(false);
                        continue;
                    }

                    var dirToClean = Path.GetDirectoryName(file);
                    var moveResult = await _organization.MoveFile(file, expectedPath, copy: false, logCallback).ConfigureAwait(false);

                    if (moveResult.Success)
                    {
                        libResult.FilesMoved++;
                        await logCallback($"[{libLabel}] Moved: {file} -> {expectedPath}").ConfigureAwait(false);
                        _groupingService.UpdateFormatPath(file, expectedPath);

                        var targetDir = Path.GetDirectoryName(expectedPath);
                        await MoveCompanionImagesAsync(dirToClean, targetDir, logCallback).ConfigureAwait(false);
                        await CleanupEmptyDirectories(dirToClean, libResult, logCallback).ConfigureAwait(false);
                    }
                    else if (moveResult.Skipped)
                    {
                        libResult.FilesSkipped++;
                    }
                    else
                    {
                        await logCallback($"[{libLabel}] Failed to move {file}: {moveResult.ErrorMessage}").ConfigureAwait(false);
                        libResult.Errors++;
                    }
                }
                catch (Exception ex)
                {
                    await logCallback($"[{libLabel}] Error processing {file}: {ex.Message}").ConfigureAwait(false);
                    libResult.Errors++;
                }

                grandProcessed++;
                if (grandTotal > 0)
                    progress.Report((double)grandProcessed / grandTotal);
            }

            if (enrichmentQueue.Count > 0)
            {
                await logCallback($"[{libLabel}] Enriching {enrichmentQueue.Count} files...").ConfigureAwait(false);
                var enrichmentIssues = new List<string>();

                foreach (var (file, rawMetadata, dir, template) in enrichmentQueue)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var enrichmentResult = await _enrichment.EnrichAsync(
                            rawMetadata,
                            config.HardcoverApiKey,
                            config.GoogleBooksApiKey,
                            config.HardcoverEnabled,
                            config.GoogleBooksEnabled,
                            config.OpenLibraryEnabled,
                            comicVineEnabled: config.ComicVineEnabled,
                            comicVineApiKey: config.ComicVineApiKey ?? "",
                            metronEnabled: config.MetronEnabled,
                            metronUsername: config.MetronUsername ?? "",
                            metronPassword: config.MetronPassword ?? "",
                            versedbEnabled: config.VerseDbEnabled,
                            versedbApiKey: config.VerseDbApiKey ?? "",
                            grandComicsDbEnabled: config.GrandComicsDbEnabled,
                            grandComicsDbUsername: config.GrandComicsDbUsername ?? "",
                            grandComicsDbPassword: config.GrandComicsDbPassword ?? "",
                            titleAuthorSearchEnabled: dir.EnableTitleAuthorSearch,
                            title: rawMetadata.Title,
                            author: rawMetadata.Authors.Count > 0 ? rawMetadata.Authors[0] : null,
                            ct: ct).ConfigureAwait(false);

                        var enriched = enrichmentResult.Metadata;

                        if (NeedsEnrichment(enriched, template))
                        {
                            enrichmentIssues.Add(file);
                            libResult.FilesSkipped++;
                            await logCallback($"[{libLabel}] Enrichment could not resolve fields, skipped: {file}").ConfigureAwait(false);
                            continue;
                        }

                        var expectedPath = _organization.BuildTargetPath(libraryRoot, enriched, template);

                        if (string.Equals(file, expectedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            libResult.FilesSkipped++;
                            continue;
                        }

                        var dup = FindDuplicate(file, enriched, duplicateIndex);
                        if (dup is not null)
                        {
                            File.Delete(file);
                            libResult.DuplicatesFound++;
                            await logCallback($"[{libLabel}] Duplicate (enriched) of {dup}, removed: {file}").ConfigureAwait(false);

                            var dupDir = Path.GetDirectoryName(file);
                            await MoveCompanionImagesAsync(dupDir, Path.GetDirectoryName(expectedPath), logCallback).ConfigureAwait(false);
                            await CleanupEmptyDirectories(dupDir, libResult, logCallback).ConfigureAwait(false);
                            continue;
                        }

                        var dirToClean = Path.GetDirectoryName(file);
                        var moveResult = await _organization.MoveFile(file, expectedPath, copy: false, logCallback).ConfigureAwait(false);

                        if (moveResult.Success)
                        {
                            libResult.FilesMoved++;
                            await logCallback($"[{libLabel}] Moved (enriched): {file} -> {expectedPath}").ConfigureAwait(false);
                            _groupingService.UpdateFormatPath(file, expectedPath);

                            var targetDir = Path.GetDirectoryName(expectedPath);
                            await MoveCompanionImagesAsync(dirToClean, targetDir, logCallback).ConfigureAwait(false);
                            await CleanupEmptyDirectories(dirToClean, libResult, logCallback).ConfigureAwait(false);
                        }
                        else if (moveResult.Skipped)
                        {
                            libResult.FilesSkipped++;
                        }
                        else
                        {
                            await logCallback($"[{libLabel}] Failed to move (enriched) {file}: {moveResult.ErrorMessage}").ConfigureAwait(false);
                            libResult.Errors++;
                        }
                    }
                    catch (Exception ex)
                    {
                        await logCallback($"[{libLabel}] Error enriching {file}: {ex.Message}").ConfigureAwait(false);
                        libResult.Errors++;
                    }

                    grandProcessed++;
                    if (grandTotal > 0)
                        progress.Report((double)grandProcessed / grandTotal);
                }

                if (enrichmentIssues.Count > 0 && _appPaths is not null)
                {
                    var summaryPath = Path.Combine(
                        _appPaths.LogDirectoryPath,
                        $"log_LibraryCleanup-{DateTime.Now:yyyyMMdd}-enrichment-issues.log");
                    await File.WriteAllLinesAsync(summaryPath, enrichmentIssues, ct).ConfigureAwait(false);
                    await logCallback($"[{libLabel}] Wrote enrichment issues log ({enrichmentIssues.Count} files): {summaryPath}").ConfigureAwait(false);
                }

                await logCallback($"[{libLabel}] Enrichment pass complete — {enrichmentQueue.Count} processed, {enrichmentIssues.Count} unresolved.").ConfigureAwait(false);
            }

            await CleanupNonBookDirectories(libGroup, libResult, logCallback, ct).ConfigureAwait(false);

            var removedEmpty = await SweepEmptyDirectoriesAsync(libraryRoot, logCallback, ct).ConfigureAwait(false);
            libResult.EmptyDirectoriesRemoved += removedEmpty;
            if (removedEmpty > 0)
                await logCallback($"[{libLabel}] Removed {removedEmpty} empty directories from library").ConfigureAwait(false);

            await logCallback(
                $"[{libLabel}] Library complete — " +
                $"Moved: {libResult.FilesMoved}, Skipped: {libResult.FilesSkipped}, " +
                $"Errors: {libResult.Errors}, Duplicates: {libResult.DuplicatesFound}, " +
                $"Empty dirs: {libResult.EmptyDirectoriesRemoved}, " +
                $"Non-book dirs: {libResult.NonBookDirectoriesRemoved}").ConfigureAwait(false);

            result.FilesMoved += libResult.FilesMoved;
            result.FilesSkipped += libResult.FilesSkipped;
            result.Errors += libResult.Errors;
            result.DuplicatesFound += libResult.DuplicatesFound;
            result.EmptyDirectoriesRemoved += libResult.EmptyDirectoriesRemoved;
            result.NonBookDirectoriesRemoved += libResult.NonBookDirectoriesRemoved;
        }

        await logCallback(
            $"Cleanup complete — Libraries: {libraryGroups.Count}, " +
            $"Moved: {result.FilesMoved}, Skipped: {result.FilesSkipped}, " +
            $"Errors: {result.Errors}, Duplicates: {result.DuplicatesFound}, " +
            $"Empty dirs removed: {result.EmptyDirectoriesRemoved}, " +
            $"Non-book dirs removed: {result.NonBookDirectoriesRemoved}").ConfigureAwait(false);
        await logCallback("IMPORTANT: Run a Jellyfin library scan to re-link moved files to library items.").ConfigureAwait(false);

        progress.Report(1.0);
        return result;
    }

    private Dictionary<string, List<(long Size, string Path, string Title, string Author)>> BuildDuplicateIndex(
        List<(string Path, FileMetadata Metadata, ManagedSourceDirectory Dir, string Template)> fileData)
    {
        var index = new Dictionary<string, List<(long Size, string Path, string Title, string Author)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, metadata, _, _) in fileData)
        {
            var ext = Path.GetExtension(path);
            var size = new FileInfo(path).Length;
            var title = NormalizeString(metadata.Title);
            var author = metadata.Authors.Count > 0 ? NormalizeString(metadata.Authors[0]) : string.Empty;

            if (!index.TryGetValue(ext, out var list))
            {
                list = new List<(long, string, string, string)>();
                index[ext] = list;
            }

            list.Add((size, path, title, author));
        }

        return index;
    }

    private static string? FindDuplicate(
        string sourcePath,
        FileMetadata metadata,
        Dictionary<string, List<(long Size, string Path, string Title, string Author)>> index)
    {
        var ext = Path.GetExtension(sourcePath);
        if (!index.TryGetValue(ext, out var candidates))
            return null;

        var sourceSize = new FileInfo(sourcePath).Length;
        var sourceTitle = NormalizeString(metadata.Title);
        var sourceAuthor = metadata.Authors.Count > 0 ? NormalizeString(metadata.Authors[0]) : string.Empty;

        foreach (var (size, path, title, author) in candidates)
        {
            if (string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (size != sourceSize)
                continue;

            if (!string.IsNullOrWhiteSpace(sourceTitle) &&
                !string.IsNullOrWhiteSpace(title) &&
                string.Equals(title, sourceTitle, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(author, sourceAuthor, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    private static string NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().ToLowerInvariant();

        var result = new System.Text.StringBuilder(normalized.Length);
        var prevWasSpace = false;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace)
                    result.Append(' ');
                prevWasSpace = true;
            }
            else
            {
                result.Append(c);
                prevWasSpace = false;
            }
        }

        return result.ToString().Trim();
    }

    private async Task CleanupNonBookDirectories(
        IEnumerable<ManagedSourceDirectory> libraryDirs,
        CleanupResult result,
        Func<string, Task> logCallback,
        CancellationToken ct)
    {
        var supportedExts = GetSupportedExtensions(Config);

        foreach (var dir in libraryDirs)
        {
            if (string.IsNullOrWhiteSpace(dir.LibraryPath) || !Directory.Exists(dir.LibraryPath))
                continue;

            try
            {
                var allDirs = Directory.GetDirectories(dir.LibraryPath, "*", SearchOption.AllDirectories);
                Array.Sort(allDirs, (a, b) => b.Length.CompareTo(a.Length));

                foreach (var subDir in allDirs)
                {
                    ct.ThrowIfCancellationRequested();

                    var hasBookFile = Directory.EnumerateFiles(subDir)
                        .Any(f => supportedExts.Contains(Path.GetExtension(f)));

                    if (!hasBookFile)
                    {
                        foreach (var file in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
                        {
                            await logCallback($"  Deleting file: {file}").ConfigureAwait(false);
                        }

                        Directory.Delete(subDir, recursive: true);
                        result.NonBookDirectoriesRemoved++;
                        await logCallback($"Removed non-book directory: {subDir}").ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await logCallback($"Failed to cleanup non-book directories under {dir.LibraryPath}: {ex.Message}").ConfigureAwait(false);
            }
        }
    }

    private async Task MoveCompanionImagesAsync(string? sourceDir, string? targetDir, Func<string, Task> logCallback)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(sourceDir))
            return;

        if (sourceDir == targetDir)
            return;

        try
        {
            foreach (var imagePath in Directory.EnumerateFiles(sourceDir))
            {
                var ext = Path.GetExtension(imagePath);
                if (!_imageExtensions.Contains(ext)) continue;

                var nameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);

                if (!_imageBaseNames.Contains(nameWithoutExt) && !nameWithoutExt.Contains("cover", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetPath = Path.Combine(targetDir, Path.GetFileName(imagePath));

                if (File.Exists(targetPath))
                {
                    var sourceInfo = new FileInfo(imagePath);
                    var targetInfo = new FileInfo(targetPath);
                    if (sourceInfo.Exists && targetInfo.Exists && sourceInfo.Length == targetInfo.Length)
                    {
                        File.Delete(imagePath);
                        await logCallback($"  Removed stale companion image (already at target): {imagePath}").ConfigureAwait(false);
                    }
                    continue;
                }

                Directory.CreateDirectory(targetDir);
                File.Move(imagePath, targetPath);
                await logCallback($"  Moved companion image: {imagePath} -> {targetPath}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await logCallback($"Failed to move companion images from {sourceDir}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task CleanupEmptyDirectories(string? startPath, CleanupResult result, Func<string, Task> logCallback)
    {
        if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
            return;

        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (dir.EnumerateFileSystemInfos().Any())
                    break;

                dir.Delete();
                result.EmptyDirectoriesRemoved++;
                await logCallback($"Removed empty directory: {dir.FullName}").ConfigureAwait(false);
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            await logCallback($"Failed to remove empty directory {startPath}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static bool NeedsEnrichment(FileMetadata metadata, string template)
    {
        var tokens = ExtractTemplateTokens(template);

        if (tokens.Contains("Author") &&
            (metadata.Authors.Count == 0 || string.IsNullOrWhiteSpace(metadata.Authors[0])))
            return true;

        return false;
    }

    private static HashSet<string> ExtractTemplateTokens(string template)
    {
        var tokens = new HashSet<string>();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i + 1)
                {
                    tokens.Add(template.AsSpan(i + 1, end - i - 1).ToString());
                    i = end;
                }
            }
        }

        return tokens;
    }

    private static async Task<int> SweepEmptyDirectoriesAsync(string rootPath, Func<string, Task> logCallback, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return 0;

        var count = 0;
        try
        {
            var allDirs = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories);
            Array.Sort(allDirs, (a, b) => b.Length.CompareTo(a.Length));

            foreach (var dir in allDirs)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (new DirectoryInfo(dir).EnumerateFileSystemInfos().Any())
                        continue;

                    Directory.Delete(dir);
                    count++;
                    await logCallback($"Removed empty directory: {dir}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await logCallback($"Failed to remove empty directory {dir}: {ex.Message}").ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            await logCallback($"Failed to sweep empty directories under {rootPath}: {ex.Message}").ConfigureAwait(false);
        }

        return count;
    }

    private static HashSet<string> GetSupportedExtensions(PluginConfiguration? config)
    {
        if (config is null)
            return new HashSet<string>(_sharedExtensions, StringComparer.OrdinalIgnoreCase);

        if (config.IngestionFileExtensions is null || config.IngestionFileExtensions.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".epub", ".pdf",
                ".cbz", ".cbr", ".cb7",
                ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
            };
        }

        return new HashSet<string>(config.IngestionFileExtensions, StringComparer.OrdinalIgnoreCase);
    }

    private static FileMetadata CreateMinimalMetadata(string filePath)
    {
        return new FileMetadata
        {
            FilePath = filePath,
            FileFormat = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
            Title = SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(filePath))
        };
    }
}

public class CleanupResult
{
    public int FilesMoved { get; set; }
    public int FilesSkipped { get; set; }
    public int Errors { get; set; }
    public int DuplicatesFound { get; set; }
    public int EmptyDirectoriesRemoved { get; set; }
    public int NonBookDirectoriesRemoved { get; set; }
}
