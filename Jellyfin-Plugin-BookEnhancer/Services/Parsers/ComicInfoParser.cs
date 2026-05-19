using System.IO.Compression;
using System.Xml.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class ComicInfoParser : IFileParser
{
    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cbr", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cb7", StringComparison.OrdinalIgnoreCase);
    }

    public Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(filePath);

            XDocument? doc = ext.ToLowerInvariant() switch
            {
                ".cbz" => ExtractFromZip(filePath, "ComicInfo.xml"),
                ".cbr" => ExtractFromCbr(filePath, "ComicInfo.xml"),
                ".cb7" => ExtractFromCbr(filePath, "ComicInfo.xml"),
                _ => null
            };

            if (doc == null) return Task.FromResult<FileMetadata?>(null);
            return Task.FromResult<FileMetadata?>(ParseComicInfo(doc, filePath));
        }
        catch
        {
            return Task.FromResult<FileMetadata?>(null);
        }
    }

    private static XDocument? ExtractFromZip(string filePath, string entryName)
    {
        using var stream = File.OpenRead(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName);
        if (entry == null) return null;
        using var entryStream = entry.Open();
        return XDocument.Load(entryStream);
    }

    private static XDocument? ExtractFromCbr(string filePath, string entryName)
    {
        using var archive = ArchiveFactory.Open(filePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Key, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var ms = new MemoryStream();
        entry.WriteTo(ms);
        ms.Position = 0;
        return XDocument.Load(ms);
    }

    private static FileMetadata ParseComicInfo(XDocument doc, string filePath)
    {
        var root = doc.Root;
        if (root == null) throw new InvalidDataException("Empty ComicInfo.xml");

        var meta = new FileMetadata
        {
            FilePath = filePath,
            FileFormat = "Comic",
            Title = root.Element("Title")?.Value
        };

        if (string.IsNullOrWhiteSpace(meta.Title))
            meta.Title = Path.GetFileNameWithoutExtension(filePath);

        meta.SeriesName = root.Element("Series")?.Value;
        meta.SeriesNumber = root.Element("Number")?.Value;
        if (meta.SeriesNumber != null && float.TryParse(meta.SeriesNumber, out var num))
            meta.SeriesIndex = num;

        meta.Volume = root.Element("Volume")?.Value;
        meta.Description = root.Element("Summary")?.Value;
        meta.Publisher = root.Element("Publisher")?.Value;
        meta.AgeRating = root.Element("AgeRating")?.Value;
        meta.Manga = root.Element("Manga")?.Value;
        meta.StoryArc = root.Element("StoryArc")?.Value;
        meta.Format = root.Element("Format")?.Value;

        var bw = root.Element("BlackAndWhite")?.Value;
        if (bw != null && bool.TryParse(bw, out var isBw))
            meta.BlackAndWhite = isBw;

        var countStr = root.Element("PageCount")?.Value;
        if (countStr != null && int.TryParse(countStr, out var pages))
            meta.PageCount = pages;

        meta.Language = root.Element("Language")?.Value;
        meta.Isbn = root.Element("ISBN")?.Value ?? root.Element("GTIN")?.Value;

        var yearStr = root.Element("Year")?.Value;
        var monthStr = root.Element("Month")?.Value;
        var dayStr = root.Element("Day")?.Value;
        if (yearStr != null && int.TryParse(yearStr, out var year))
        {
            meta.PublishYear = year;
            if (monthStr != null && int.TryParse(monthStr, out var month))
            {
                if (dayStr != null && int.TryParse(dayStr, out var day))
                    meta.PublishDate = new DateTime(year, month, day);
                else
                    meta.PublishDate = new DateTime(year, month, 1);
            }
        }

        AddCommaSeparated(meta.Genres, root.Element("Genre")?.Value);
        AddCommaSeparated(meta.Tags, root.Element("Tags")?.Value);
        AddCommaSeparated(meta.Tags, root.Element("Characters")?.Value);
        AddCommaSeparated(meta.Tags, root.Element("Teams")?.Value);
        AddCommaSeparated(meta.Tags, root.Element("Locations")?.Value);

        AddComicPeople(meta.ComicPeople, root.Element("Writer")?.Value, "Writer");
        AddComicPeople(meta.ComicPeople, root.Element("Penciller")?.Value, "Penciller");
        AddComicPeople(meta.ComicPeople, root.Element("Inker")?.Value, "Inker");
        AddComicPeople(meta.ComicPeople, root.Element("Colorist")?.Value, "Colorist");
        AddComicPeople(meta.ComicPeople, root.Element("Letterer")?.Value, "Letterer");
        AddComicPeople(meta.ComicPeople, root.Element("CoverArtist")?.Value, "CoverArtist");
        AddComicPeople(meta.ComicPeople, root.Element("Editor")?.Value, "Editor");
        AddComicPeople(meta.ComicPeople, root.Element("Translator")?.Value, "Translator");

        return meta;
    }

    private static void AddCommaSeparated(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!list.Contains(item, StringComparer.OrdinalIgnoreCase))
                list.Add(item);
        }
    }

    private static void AddComicPeople(List<ComicPersonInfo> people, string? names, string role)
    {
        if (string.IsNullOrWhiteSpace(names)) return;
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(name))
                people.Add(new ComicPersonInfo { Name = name, Role = role });
        }
    }
}
