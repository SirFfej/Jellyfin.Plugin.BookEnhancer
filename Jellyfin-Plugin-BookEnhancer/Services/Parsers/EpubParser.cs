using System.IO.Compression;
using System.Xml.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class EpubParser : IFileParser
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    public bool CanParse(string filePath)
    {
        return filePath.EndsWith(".epub", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var opfPath = ResolveOpfPath(archive);
            if (opfPath == null) return null;

            var opfEntry = archive.GetEntry(opfPath);
            if (opfEntry == null) return null;

            using var opfStream = opfEntry.Open();
            var opfDoc = await XDocument.LoadAsync(opfStream, LoadOptions.None, ct);

            var meta = ParseOpf(opfDoc, filePath);
            ExtractCover(meta, archive, opfDoc, opfPath);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveOpfPath(ZipArchive archive)
    {
        var containerEntry = archive.GetEntry("META-INF/container.xml");
        if (containerEntry == null) return null;

        using var stream = containerEntry.Open();
        var container = XDocument.Load(stream);
        var ns = container.Root?.GetDefaultNamespace();
        var nsStr = ns?.NamespaceName ?? "";

        var rootfile = container.Descendants()
            .FirstOrDefault(e =>
                e.Name.LocalName == "rootfile" &&
                e.Attribute("media-type")?.Value == "application/oebps-package+xml" &&
                e.Name.NamespaceName == nsStr);

        return rootfile?.Attribute("full-path")?.Value;
    }

    private static FileMetadata ParseOpf(XDocument opf, string filePath)
    {
        var meta = new FileMetadata
        {
            FilePath = filePath,
            FileFormat = "EPUB"
        };

        var metadataEl = opf.Root?.Element(Opf + "metadata");
        if (metadataEl == null) return meta;

        meta.Title = metadataEl.Element(Dc + "title")?.Value;

        var subtitleMeta = metadataEl.Elements(Opf + "meta")
            .FirstOrDefault(e => e.Attribute("name")?.Value == "subtitle");
        meta.Subtitle = subtitleMeta?.Attribute("content")?.Value;

        foreach (var creator in metadataEl.Elements(Dc + "creator"))
        {
            var name = creator.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                meta.Authors.Add(name);
        }

        foreach (var contributor in metadataEl.Elements(Dc + "contributor"))
        {
            var role = contributor.Attribute(Opf + "role")?.Value;
            var name = contributor.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (role == "nrt")
                meta.Narrators.Add(name);
            else
            {
                var mappedRole = role switch
                {
                    "ill" => "Illustrator",
                    "trl" => "Translator",
                    "edt" => "Editor",
                    "cov" => "CoverArtist",
                    _ => "Contributor"
                };
                meta.ComicPeople.Add(new ComicPersonInfo { Name = name, Role = mappedRole });
            }
        }

        meta.Isbn = ExtractIsbn(metadataEl);
        meta.Publisher = metadataEl.Element(Dc + "publisher")?.Value;

        var dateStr = metadataEl.Element(Dc + "date")?.Value;
        if (dateStr != null && DateTime.TryParse(dateStr, out var date))
        {
            meta.PublishDate = date;
            meta.PublishYear = date.Year;
        }

        meta.Language = metadataEl.Element(Dc + "language")?.Value;
        meta.Description = metadataEl.Element(Dc + "description")?.Value;

        foreach (var subject in metadataEl.Elements(Dc + "subject"))
        {
            var val = subject.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(val))
                meta.Tags.Add(val);
        }

        foreach (var met in metadataEl.Elements(Opf + "meta"))
        {
            var name = met.Attribute("name")?.Value;
            var content = met.Attribute("content")?.Value;
            if (name == "calibre:series" && !string.IsNullOrWhiteSpace(content))
                meta.SeriesName ??= content;
            if (name == "calibre:series_index" && content != null
                && float.TryParse(content, out var idx))
                meta.SeriesIndex ??= idx;
        }

        return meta;
    }

    private static string? ExtractIsbn(XElement metadataEl)
    {
        foreach (var id in metadataEl.Elements(Dc + "identifier"))
        {
            var scheme = id.Attribute(Opf + "scheme")?.Value;
            if (string.Equals(scheme, "ISBN", StringComparison.OrdinalIgnoreCase))
            {
                var val = id.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                    return CleanIsbn(val);
            }
        }

        foreach (var id in metadataEl.Elements(Dc + "identifier"))
        {
            var val = id.Value?.Trim();
            if (val == null) continue;
            if (val.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase))
                return CleanIsbn(val["urn:isbn:".Length..]);
            if (val.StartsWith("isbn:", StringComparison.OrdinalIgnoreCase))
                return CleanIsbn(val["isbn:".Length..]);
        }

        foreach (var meta in metadataEl.Elements(Opf + "meta"))
        {
            if (meta.Attribute("property")?.Value == "identifier-type" &&
                meta.Value?.Trim().Equals("ISBN", StringComparison.OrdinalIgnoreCase) == true)
            {
                var refines = meta.Attribute("refines")?.Value?.TrimStart('#');
                if (refines == null) continue;
                var id = metadataEl.Elements(Dc + "identifier")
                    .FirstOrDefault(e => e.Attribute("id")?.Value == refines);
                if (id != null)
                    return CleanIsbn(id.Value?.Trim());
            }
        }

        return null;
    }

    private static string? CleanIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned.ToUpperInvariant();
    }

    private static void ExtractCover(FileMetadata meta, ZipArchive archive, XDocument opf, string opfPath)
    {
        try
        {
            var metadataEl = opf.Root?.Element(Opf + "metadata");
            var manifestEl = opf.Root?.Element(Opf + "manifest");
            if (metadataEl == null || manifestEl == null) return;

            var coverMeta = metadataEl.Elements(Opf + "meta")
                .FirstOrDefault(e => e.Attribute("name")?.Value == "cover");
            var coverId = coverMeta?.Attribute("content")?.Value;
            if (coverId == null) return;

            var coverItem = manifestEl.Elements(Opf + "item")
                .FirstOrDefault(e => e.Attribute("id")?.Value == coverId);
            var href = coverItem?.Attribute("href")?.Value;
            var mediaType = coverItem?.Attribute("media-type")?.Value;
            if (href == null) return;

            var opfDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/');
            var coverPath = opfDir != null ? $"{opfDir}/{href}" : href;
            coverPath = coverPath.Replace("//", "/").TrimStart('/');

            var coverEntry = archive.GetEntry(coverPath);
            if (coverEntry == null) return;

            using var coverStream = coverEntry.Open();
            using var ms = new MemoryStream();
            coverStream.CopyTo(ms);
            meta.CoverBytes = ms.ToArray();
            meta.CoverMimeType = mediaType ?? "image/jpeg";
            meta.HasCover = true;
        }
        catch
        {
            // best-effort
        }
    }
}
