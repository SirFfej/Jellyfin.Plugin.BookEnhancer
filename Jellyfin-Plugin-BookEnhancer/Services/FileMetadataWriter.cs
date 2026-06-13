using System.IO.Compression;
using System.Xml.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class FileMetadataWriter : IFileMetadataWriter
{
    private readonly ILogger<FileMetadataWriter> _logger;

    private static readonly HashSet<string> _audioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
    };

    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";

    public FileMetadataWriter(ILogger<FileMetadataWriter> logger)
    {
        _logger = logger;
    }

    public async Task<bool> WriteMetadataAsync(string filePath, FileMetadata metadata, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(ext)) return false;

            if (_audioExtensions.Contains(ext))
                return WriteAudioTags(filePath, metadata);

            if (ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase))
                return await WriteComicInfoXmlAsync(filePath, metadata, ct).ConfigureAwait(false);

            if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase))
                return await WriteEpubMetadataAsync(filePath, metadata, ct).ConfigureAwait(false);

            _logger.LogDebug("No writer available for {Ext}: {Path}", ext, filePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write metadata to {Path}", filePath);
            return false;
        }
    }

    private bool WriteAudioTags(string filePath, FileMetadata metadata)
    {
        try
        {
            using var tfile = TagLib.File.Create(filePath);

            tfile.Tag.Title = metadata.Title;
            tfile.Tag.Performers = metadata.Authors.ToArray();
            tfile.Tag.Publisher = metadata.Publisher;
            tfile.Tag.Genres = metadata.Genres.ToArray();
            tfile.Tag.Description = metadata.Description;
            tfile.Tag.Composers = metadata.Narrators.ToArray();
            if (metadata.PublishYear.HasValue)
                tfile.Tag.Year = (uint)metadata.PublishYear.Value;

            if (!string.IsNullOrWhiteSpace(metadata.SeriesName))
                tfile.Tag.Grouping = metadata.SeriesName;

            tfile.Save();
            _logger.LogDebug("Wrote audio tags to {Path}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write audio tags to {Path}", filePath);
            return false;
        }
    }

    private async Task<bool> WriteComicInfoXmlAsync(string filePath, FileMetadata metadata, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var tempCbz = filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(tempDir);

            using (var archive = ZipFile.OpenRead(filePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (ct.IsCancellationRequested) return false;

                    var entryName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destPath = Path.GetFullPath(Path.Combine(tempDir, entryName));
                    var normalizedTempDir = tempDir.EndsWith(Path.DirectorySeparatorChar)
                        ? tempDir : tempDir + Path.DirectorySeparatorChar;
                    if (!destPath.StartsWith(normalizedTempDir, StringComparison.Ordinal))
                        continue;

                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrWhiteSpace(destDir) && !Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    entry.ExtractToFile(destPath, true);
                }
            }

            var comicInfoPath = Path.Combine(tempDir, "ComicInfo.xml");
            XDocument doc;
            if (File.Exists(comicInfoPath))
            {
                doc = XDocument.Load(comicInfoPath);
            }
            else
            {
                doc = new XDocument(new XElement("ComicInfo"));
            }

            UpdateComicInfoXml(doc, metadata);
            doc.Save(comicInfoPath);

            ZipFile.CreateFromDirectory(tempDir, tempCbz);

            File.Delete(filePath);
            File.Move(tempCbz, filePath);
            tempCbz = null;

            _logger.LogDebug("Wrote ComicInfo.xml to {Path}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write ComicInfo.xml to {Path}", filePath);
            return false;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            if (tempCbz is not null && File.Exists(tempCbz))
                File.Delete(tempCbz);
        }
    }

    private static void UpdateComicInfoXml(XDocument doc, FileMetadata metadata)
    {
        var root = doc.Root;
        if (root is null) return;

        SetElementValue(root, "Title", metadata.Title);
        SetElementValue(root, "Series", metadata.SeriesName);
        SetElementValue(root, "Number", metadata.SeriesNumber);
        SetElementValue(root, "Volume", metadata.Volume);
        SetElementValue(root, "Summary", metadata.Description);
        SetElementValue(root, "Publisher", metadata.Publisher);
        SetElementValue(root, "PageCount", metadata.PageCount?.ToString());
        SetElementValue(root, "Language", metadata.Language);
        SetElementValue(root, "Format", metadata.Format);
        SetElementValue(root, "AgeRating", metadata.AgeRating);
        SetElementValue(root, "Manga", metadata.Manga);
        SetElementValue(root, "BlackAndWhite", metadata.BlackAndWhite?.ToString());
        SetElementValue(root, "StoryArc", metadata.StoryArc);

        if (metadata.PublishYear.HasValue)
            SetElementValue(root, "Year", metadata.PublishYear.Value.ToString());

        if (metadata.Authors.Count > 0)
            SetElementValue(root, "Writer", string.Join("; ", metadata.Authors));

        if (metadata.Genres.Count > 0)
            SetElementValue(root, "Genre", string.Join("; ", metadata.Genres));

        if (metadata.PublishDate.HasValue)
            SetElementValue(root, "Date", metadata.PublishDate.Value.ToString("yyyy-MM-dd"));

        foreach (var person in metadata.ComicPeople)
        {
            if (!string.IsNullOrWhiteSpace(person.Name) && !string.IsNullOrWhiteSpace(person.Role))
            {
                var existing = root.Element(person.Role);
                if (existing is null)
                    root.Add(new XElement(person.Role, person.Name));
            }
        }
    }

    private async Task<bool> WriteEpubMetadataAsync(string filePath, FileMetadata metadata, CancellationToken ct)
    {
        var tempPath = filePath + ".tmp";
        try
        {
            File.Copy(filePath, tempPath, true);

            using var archive = ZipFile.Open(tempPath, ZipArchiveMode.Update);
            var opfEntry = ResolveOpfEntry(archive);
            if (opfEntry is null)
            {
                return false;
            }

            using (var opfStream = opfEntry.Open())
            {
                var opfDoc = await XDocument.LoadAsync(opfStream, LoadOptions.None, ct).ConfigureAwait(false);
                UpdateOpfXml(opfDoc, metadata);

                opfStream.SetLength(0);
                opfDoc.Save(opfStream);
            }

            archive.Dispose();
            File.Delete(filePath);
            File.Move(tempPath, filePath);

            _logger.LogDebug("Wrote OPF metadata to {Path}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write EPUB metadata to {Path}", filePath);
            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static ZipArchiveEntry? ResolveOpfEntry(ZipArchive archive)
    {
        var containerEntry = archive.GetEntry("META-INF/container.xml");
        if (containerEntry is null) return null;

        using var stream = containerEntry.Open();
        var containerDoc = XDocument.Load(stream);
        var rootfile = containerDoc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rootfile");
        var opfPath = rootfile?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(opfPath)) return null;

        return archive.GetEntry(opfPath);
    }

    private static void UpdateOpfXml(XDocument opfDoc, FileMetadata metadata)
    {
        var metadataEl = opfDoc.Root?.Element(OpfNs + "metadata");
        if (metadataEl is null) return;

        SetDcElement(metadataEl, "title", metadata.Title);
        SetDcElement(metadataEl, "publisher", metadata.Publisher);
        SetDcElement(metadataEl, "language", metadata.Language);
        SetDcElement(metadataEl, "description", metadata.Description);

        if (!string.IsNullOrWhiteSpace(metadata.Isbn))
            SetDcElement(metadataEl, "identifier", metadata.Isbn, ("id", "Isbn"));

        UpdateOrAddMeta(metadataEl, "series", metadata.SeriesName);
        if (metadata.SeriesIndex.HasValue)
            UpdateOrAddMeta(metadataEl, "series_index", metadata.SeriesIndex.Value.ToString());

        metadataEl.Elements(DcNs + "creator").Remove();
        foreach (var author in metadata.Authors)
        {
            if (!string.IsNullOrWhiteSpace(author))
                metadataEl.Add(new XElement(DcNs + "creator", author));
        }

        metadataEl.Elements(DcNs + "subject").Remove();
        foreach (var genre in metadata.Genres)
        {
            if (!string.IsNullOrWhiteSpace(genre))
                metadataEl.Add(new XElement(DcNs + "subject", genre));
        }

        if (metadata.PublishDate.HasValue)
            UpdateOrAddMeta(metadataEl, "calibre:timestamp", metadata.PublishDate.Value.ToString("yyyy-MM-dd"));
    }

    private static void SetDcElement(XElement parent, string elementName, string? value, params (string Name, string Value)[] attributes)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var existing = parent.Elements(DcNs + elementName).FirstOrDefault();
        if (existing is null)
        {
            existing = new XElement(DcNs + elementName, value);
            parent.Add(existing);
        }
        else
        {
            existing.Value = value;
        }

        foreach (var (name, val) in attributes)
            existing.SetAttributeValue(name, val);
    }

    private static void UpdateOrAddMeta(XElement parent, string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var existing = parent.Elements(OpfNs + "meta")
            .FirstOrDefault(m => m.Attribute("name")?.Value == property);
        if (existing is null)
        {
            parent.Add(
                new XElement(
                    OpfNs + "meta",
                    new XAttribute("name", property),
                    new XAttribute("content", value)));
        }
        else
        {
            existing.SetAttributeValue("content", value);
        }
    }

    private static void SetElementValue(XElement parent, string elementName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var existing = parent.Element(elementName);
        if (existing is null)
            parent.Add(new XElement(elementName, value));
        else
            existing.Value = value;
    }
}
