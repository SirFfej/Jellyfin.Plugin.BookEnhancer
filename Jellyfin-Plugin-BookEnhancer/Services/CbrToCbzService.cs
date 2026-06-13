using System.IO.Compression;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.BookEnhancer.Configuration;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class CbrToCbzService
{
    private readonly FileMetadataExtractor _fileExtractor;
    private readonly MetadataEnrichmentService _enrichment;
    private readonly LibraryOrganizationService _organization;
    private readonly BookGroupingService _grouping;
    private readonly IFileMetadataWriter _writer;
    private readonly ILogger<CbrToCbzService> _logger;

    private static readonly HashSet<string> _comicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbr", ".cb7"
    };

    public CbrToCbzService(
        FileMetadataExtractor fileExtractor,
        MetadataEnrichmentService enrichment,
        LibraryOrganizationService organization,
        BookGroupingService grouping,
        IFileMetadataWriter writer,
        ILogger<CbrToCbzService> logger)
    {
        _fileExtractor = fileExtractor;
        _enrichment = enrichment;
        _organization = organization;
        _grouping = grouping;
        _writer = writer;
        _logger = logger;
    }

    public async Task<CbrToCbzResult> ConvertAsync(
        string scanPath,
        Func<string, Task>? logCallback = null,
        CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return new CbrToCbzResult { Errors = 1, ErrorDetails = ["Plugin configuration not available."] };

        if (string.IsNullOrWhiteSpace(scanPath) || !Directory.Exists(scanPath))
        {
            var msg = $"Directory does not exist: {scanPath}";
            await LogAsync(logCallback, msg).ConfigureAwait(false);
            return new CbrToCbzResult { Errors = 1, ErrorDetails = [msg] };
        }

        var backupDir = !string.IsNullOrWhiteSpace(config.BackupDirectory)
            ? config.BackupDirectory
            : config.TrashDirectory;

        if (string.IsNullOrWhiteSpace(backupDir))
        {
            var msg = "No backup or trash directory configured. Set a backup directory in plugin settings before running conversion.";
            await LogAsync(logCallback, msg).ConfigureAwait(false);
            return new CbrToCbzResult { Errors = 1, ErrorDetails = [msg] };
        }

        var originalsRunDir = Path.Combine(backupDir, $"convert-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(originalsRunDir);

        var files = Directory.EnumerateFiles(scanPath, "*", SearchOption.AllDirectories)
            .Where(f => _comicExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var result = new CbrToCbzResult { FilesFound = files.Count };

        if (files.Count == 0)
        {
            await LogAsync(logCallback, $"No CBR or CB7 files found in {scanPath}").ConfigureAwait(false);
            return result;
        }

        await LogAsync(logCallback, $"Found {files.Count} comic archives to convert in {scanPath}").ConfigureAwait(false);

        for (var i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested)
                break;

            var file = files[i];
            await LogAsync(logCallback, $"[{i + 1}/{files.Count}] Converting: {file}").ConfigureAwait(false);

            try
            {
                await ConvertSingleFileAsync(file, originalsRunDir, config, logCallback, ct).ConfigureAwait(false);
                result.Converted++;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorDetails.Add($"{file}: {ex.Message}");
                await LogAsync(logCallback, $"  ERROR: {file} — {ex.Message}").ConfigureAwait(false);
            }
        }

        await PurgeOldBackupsAsync(backupDir, config.BackupCleanupIntervalDays, logCallback).ConfigureAwait(false);

        await LogAsync(logCallback, $"Conversion complete — Converted: {result.Converted}, Errors: {result.Errors}").ConfigureAwait(false);
        return result;
    }

    private async Task ConvertSingleFileAsync(
        string cbrPath,
        string originalsRunDir,
        PluginConfiguration config,
        Func<string, Task>? logCallback,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tempDir);

            // Step 1: Extract CBR/CB7 to temp directory
            await LogAsync(logCallback, $"  Extracting to temporary directory...").ConfigureAwait(false);
            ExtractArchive(cbrPath, tempDir);

            // Step 2: Repack as CBZ alongside original
            var cbzPath = Path.ChangeExtension(cbrPath, ".cbz");
            await LogAsync(logCallback, $"  Repacking as CBZ: {cbzPath}").ConfigureAwait(false);
            ZipFile.CreateFromDirectory(tempDir, cbzPath);

            // Step 3: Verify CBZ is valid before moving original
            await LogAsync(logCallback, $"  Verifying CBZ integrity...").ConfigureAwait(false);
            try
            {
                using (var testArchive = ZipFile.OpenRead(cbzPath))
                {
                    var entryCount = testArchive.Entries.Count;
                    if (entryCount == 0)
                        throw new InvalidDataException("CBZ archive is empty — no entries found");
                }
            }
            catch
            {
                try { File.Delete(cbzPath); } catch { }
                throw;
            }

            // Step 4: Move original to backup directory (only after CBZ is verified valid)
            await MoveOriginalToBackup(cbrPath, originalsRunDir, logCallback).ConfigureAwait(false);

            // Step 5: Extract metadata from new CBZ
            await LogAsync(logCallback, $"  Extracting metadata...").ConfigureAwait(false);
            var metadata = await _fileExtractor.ExtractAsync(cbzPath, ct).ConfigureAwait(false);
            if (metadata is null)
            {
                metadata = new FileMetadata
                {
                    FilePath = cbzPath,
                    FileFormat = "Comic",
                    Title = SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(cbzPath))
                };
            }

            // Step 5: Run enrichment if unified metadata is enabled
            if (config.UnifiedMetadataEnabled)
            {
                await LogAsync(logCallback, $"  Running metadata enrichment...").ConfigureAwait(false);
                var template = LibraryOrganizationService.GetDefaultTemplate(metadata);
                if (NeedsEnrichment(metadata, template))
                {
                    if (config.EnrichmentCooldownDays > 0 && _grouping.IsEnrichmentOnCooldown(cbzPath, config.EnrichmentCooldownDays))
                    {
                        await LogAsync(logCallback, $"  Skipped enrichment (cooldown).").ConfigureAwait(false);
                    }
                    else
                    {
                        var enrichmentResult = await _enrichment.EnrichAsync(
                            metadata,
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
                            titleAuthorSearchEnabled: true,
                            title: metadata.Title,
                            author: metadata.Authors.Count > 0 ? metadata.Authors[0] : null,
                            ct: ct).ConfigureAwait(false);

                        metadata = enrichmentResult.Metadata;
                        _grouping.SetLastEnrichmentTime(cbzPath);
                    }
                }
            }

            // Step 6: Write ComicInfo.xml into CBZ
            await LogAsync(logCallback, $"  Writing ComicInfo.xml...").ConfigureAwait(false);
            var written = await _writer.WriteMetadataAsync(cbzPath, metadata, ct).ConfigureAwait(false);
            if (written)
                await LogAsync(logCallback, $"  ComicInfo.xml written successfully.").ConfigureAwait(false);
            else
                await LogAsync(logCallback, $"  Warning: Failed to write ComicInfo.xml.").ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static void ExtractArchive(string archivePath, string targetDir)
    {
        var normalizedTarget = targetDir.EndsWith(Path.DirectorySeparatorChar)
            ? targetDir : targetDir + Path.DirectorySeparatorChar;

        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory)
                continue;

            var entryKey = entry.Key ?? string.Empty;
            var destPath = Path.GetFullPath(Path.Combine(targetDir, entryKey));
            if (!destPath.StartsWith(normalizedTarget, StringComparison.Ordinal))
                continue;

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            entry.WriteToFile(destPath);
        }
    }

    private static async Task MoveOriginalToBackup(string path, string backupRunDir, Func<string, Task>? logCallback)
    {
        var dest = Path.Combine(backupRunDir, Path.GetFileName(path));

        if (File.Exists(dest))
        {
            var baseName = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var counter = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(backupRunDir, $"{baseName}_{counter}{ext}");
                counter++;
            }
        }

        File.Move(path, dest);
        await LogAsync(logCallback, $"  Moved original to backup: {path} -> {dest}").ConfigureAwait(false);
    }

    private static async Task PurgeOldBackupsAsync(string backupDir, int cleanupIntervalDays, Func<string, Task>? logCallback)
    {
        if (cleanupIntervalDays <= 0 || !Directory.Exists(backupDir))
            return;

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-cleanupIntervalDays);
            var minValidDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            foreach (var dir in Directory.GetDirectories(backupDir))
            {
                // Skip non-convert directories
                var dirName = Path.GetFileName(dir);
                if (!dirName.StartsWith("convert-", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var created = Directory.GetCreationTimeUtc(dir);

                    if (created < minValidDate || created > DateTime.UtcNow)
                    {
                        await LogAsync(logCallback, $"Skipping backup directory with invalid creation time ({created:yyyy-MM-dd}): {dir}").ConfigureAwait(false);
                        continue;
                    }

                    if (created >= cutoff)
                        continue;

                    Directory.Delete(dir, recursive: true);
                    await LogAsync(logCallback, $"Purged old backup: {dir} (from {created:yyyy-MM-dd})").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await LogAsync(logCallback, $"Failed to purge backup directory {dir}: {ex.Message}").ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            await LogAsync(logCallback, $"Backup cleanup failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static bool NeedsEnrichment(FileMetadata metadata, string template)
    {
        var tokens = ExtractTemplateTokens(template);

        if (tokens.Contains("Author") &&
            (metadata.Authors.Count == 0 || string.IsNullOrWhiteSpace(metadata.Authors[0])))
            return true;

        if (tokens.Contains("Publisher") &&
            string.IsNullOrWhiteSpace(metadata.Publisher))
            return true;

        if (tokens.Contains("Series") &&
            string.IsNullOrWhiteSpace(metadata.SeriesName))
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

    private static async Task LogAsync(Func<string, Task>? logCallback, string message)
    {
        if (logCallback is not null)
            await logCallback(message).ConfigureAwait(false);
    }
}

public class CbrToCbzResult
{
    [JsonPropertyName("filesFound")]
    public int FilesFound { get; set; }

    [JsonPropertyName("converted")]
    public int Converted { get; set; }

    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    [JsonPropertyName("errorDetails")]
    public List<string> ErrorDetails { get; set; } = new();
}
