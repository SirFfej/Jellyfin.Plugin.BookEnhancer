using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class ComicInfoParser : IFileParser
{
    /// <summary>Matches "Series Name 123 (2024)" — existing pattern for scene-release comics with year.</summary>
    private static readonly Regex ComicSeriesYearPattern = new(
        @"^(.+?)\s+(\d+)\s*\((\d{4})\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches "Series Name #123", "Series Name#123", "Series Name #123 Title", "Series Name#123 of 6".</summary>
    private static readonly Regex ComicHashPattern = new(
        @"^(.+?)\s*#(\d+)(?:\s*of\s*\d+)?(?:\s+(.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches "Series_Name_123" — underscore separator with trailing digits.</summary>
    private static readonly Regex ComicUnderscorePattern = new(
        @"^(.+?)_(\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches "Series Name 123" or "Series Name 1234" — space separator with trailing digits.</summary>
    private static readonly Regex ComicTrailingNumberPattern = new(
        @"^(.{3,}?)\s+(\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches "Series Name v1 01", "Series Name vol. 1 #01", "Series Name Volume 1 01" etc.</summary>
    private static readonly Regex ComicVolumeIssuePattern = new(
        @"^(.+?)\s+(?:v|vol\.?|volume)\s*(\d+)\s*#?(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cbr", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cb7", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(() => ExtractInternal(filePath), ct).ConfigureAwait(false);
    }

    private static FileMetadata? ExtractInternal(string filePath)
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

            if (doc is null)
                return ParseFromFileName(filePath);
            return ParseComicInfo(doc, filePath);
        }
        catch
        {
            return ParseFromFileName(filePath);
        }
    }

    private static string NormalizeComicFileName(string name)
    {
        // Strip common leading numeric ordering prefixes such as "001 - ", "001_", "01 -".
        name = Regex.Replace(name, @"^\d+\s*[-_]\s*", string.Empty, RegexOptions.IgnoreCase);

        // Normalize underscores to spaces so "Batman_Death_and_the_Maidens_01" parses like "Batman Death and the Maidens 01".
        name = name.Replace('_', ' ');

        return SceneTagCleaner.Clean(name);
    }

    private static XDocument? ExtractFromZip(string filePath, string entryName)
    {
        using var stream = File.OpenRead(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName);
        if (entry is null) return null;
        using var entryStream = entry.Open();
        return XDocument.Load(entryStream);
    }

    private static XDocument? ExtractFromCbr(string filePath, string entryName)
    {
        using var archive = ArchiveFactory.Open(filePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Key, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var ms = new MemoryStream();
        entry.WriteTo(ms);
        ms.Position = 0;
        return XDocument.Load(ms);
    }

    private static FileMetadata? ParseFromFileName(string filePath)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            return null;

        var cleanedName = NormalizeComicFileName(nameWithoutExt);

        var meta = new FileMetadata
        {
            FilePath = filePath,
            FileFormat = "Comic",
            IsComic = true,
            Title = cleanedName
        };

        var match = TryMatchComicPattern(cleanedName);
        if (match is not null)
        {
            var cleanedSeries = SceneTagCleaner.Clean(match.Value.Series);
            if (!string.IsNullOrWhiteSpace(cleanedSeries))
                meta.SeriesName = cleanedSeries.Trim();

            meta.SeriesNumber = match.Value.Number;
            if (float.TryParse(meta.SeriesNumber, out var num))
                meta.SeriesIndex = num;

            if (!string.IsNullOrWhiteSpace(match.Value.Volume))
                meta.Volume = match.Value.Volume.Trim();

            if (!string.IsNullOrWhiteSpace(match.Value.Title))
                meta.Title = SceneTagCleaner.Clean(match.Value.Title).Trim();
        }

        var template = ComicInfoTemplateLoader.LoadTemplate();
        ComicInfoTemplateLoader.MergeTemplate(meta, template);

        return meta;
    }

    public static void ApplyFallback(FileMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.FilePath))
            return;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(metadata.FilePath);
        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            return;

        var cleanedName = NormalizeComicFileName(nameWithoutExt);
        var match = TryMatchComicPattern(cleanedName);
        if (match is null)
            return;

        var cleanedSeries = SceneTagCleaner.Clean(match.Value.Series);
        if (string.IsNullOrWhiteSpace(metadata.SeriesName) && !string.IsNullOrWhiteSpace(cleanedSeries))
            metadata.SeriesName = cleanedSeries.Trim();

        if (string.IsNullOrWhiteSpace(metadata.SeriesNumber) && !string.IsNullOrWhiteSpace(match.Value.Number))
        {
            metadata.SeriesNumber = match.Value.Number;
            if (float.TryParse(metadata.SeriesNumber, out var num))
                metadata.SeriesIndex = num;
        }

        if (string.IsNullOrWhiteSpace(metadata.Volume) && !string.IsNullOrWhiteSpace(match.Value.Volume))
            metadata.Volume = match.Value.Volume.Trim();

        if (string.IsNullOrWhiteSpace(metadata.Title) && !string.IsNullOrWhiteSpace(match.Value.Title))
            metadata.Title = SceneTagCleaner.Clean(match.Value.Title).Trim();

        var template = ComicInfoTemplateLoader.LoadTemplate();
        ComicInfoTemplateLoader.MergeTemplate(metadata, template);
    }

    private static (string Series, string Number, string? Volume, string? Title)? TryMatchComicPattern(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var match = ComicVolumeIssuePattern.Match(name);
        if (match.Success)
            return (match.Groups[1].Value, match.Groups[3].Value, match.Groups[2].Value, null);

        match = ComicSeriesYearPattern.Match(name);
        if (match.Success)
            return (match.Groups[1].Value, match.Groups[2].Value, null, null);

        match = ComicHashPattern.Match(name);
        if (match.Success)
        {
            var title = match.Groups[3].Success ? match.Groups[3].Value : null;
            return (match.Groups[1].Value, match.Groups[2].Value, null, title);
        }

        match = ComicUnderscorePattern.Match(name);
        if (match.Success)
            return (match.Groups[1].Value, match.Groups[2].Value, null, null);

        match = ComicTrailingNumberPattern.Match(name);
        if (match.Success)
            return (match.Groups[1].Value, match.Groups[2].Value, null, null);

        return null;
    }

    private static FileMetadata ParseComicInfo(XDocument doc, string filePath)
    {
        var root = doc.Root;
        if (root is null) throw new InvalidDataException("Empty ComicInfo.xml");

        var meta = new FileMetadata
        {
            FilePath = filePath,
            FileFormat = "Comic",
            IsComic = true,
            Title = root.Element("Title")?.Value
        };

        if (string.IsNullOrWhiteSpace(meta.Title))
            meta.Title = SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(filePath));

        meta.SeriesName = root.Element("Series")?.Value;
        meta.SeriesNumber = root.Element("Number")?.Value;
        if (meta.SeriesNumber != null && float.TryParse(meta.SeriesNumber, out var num))
            meta.SeriesIndex = num;

        if (string.IsNullOrWhiteSpace(meta.SeriesName) || string.IsNullOrWhiteSpace(meta.SeriesNumber))
        {
            var cleanedName = NormalizeComicFileName(Path.GetFileNameWithoutExtension(filePath));
            var fallback = TryMatchComicPattern(cleanedName);
            if (fallback is not null)
            {
                if (string.IsNullOrWhiteSpace(meta.SeriesName) && !string.IsNullOrWhiteSpace(fallback.Value.Series))
                    meta.SeriesName = SceneTagCleaner.Clean(fallback.Value.Series).Trim();

                if (string.IsNullOrWhiteSpace(meta.SeriesNumber) && !string.IsNullOrWhiteSpace(fallback.Value.Number))
                {
                    meta.SeriesNumber = fallback.Value.Number;
                    if (float.TryParse(meta.SeriesNumber, out var fallbackNum))
                        meta.SeriesIndex = fallbackNum;
                }
            }
        }

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

        var template = ComicInfoTemplateLoader.LoadTemplate();
        ComicInfoTemplateLoader.MergeTemplate(meta, template);

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

    /// <summary>
    /// Loads a sidecar <c>.xml</c> file with the same base name as the comic file and merges its values
    /// into <paramref name="metadata"/> before the comic is written back.
    /// </summary>
    /// <param name="metadata">The comic metadata to update.</param>
    /// <param name="comicFilePath">Full path to the comic archive.</param>
    public static void ApplySidecarMetadata(FileMetadata metadata, string comicFilePath)
    {
        if (metadata is null || string.IsNullOrWhiteSpace(comicFilePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(comicFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var sidecarPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(comicFilePath) + ".xml");
        if (!File.Exists(sidecarPath))
        {
            return;
        }

        try
        {
            var doc = XDocument.Load(sidecarPath);
            var sidecar = MapComicInfoDocument(doc);
            MergeSidecar(metadata, sidecar);
        }
        catch
        {
            // Ignore unreadable sidecar files.
        }
    }

    private static FileMetadata MapComicInfoDocument(XDocument doc)
    {
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ComicInfo", StringComparison.OrdinalIgnoreCase))
        {
            return new FileMetadata();
        }

        var meta = new FileMetadata
        {
            Title = root.Element("Title")?.Value,
            SeriesName = root.Element("Series")?.Value,
            SeriesNumber = root.Element("Number")?.Value,
            Volume = root.Element("Volume")?.Value,
            Description = root.Element("Summary")?.Value,
            Publisher = root.Element("Publisher")?.Value,
            AgeRating = root.Element("AgeRating")?.Value,
            Manga = root.Element("Manga")?.Value,
            StoryArc = root.Element("StoryArc")?.Value,
            Format = root.Element("Format")?.Value,
            Language = root.Element("Language")?.Value,
            Isbn = root.Element("ISBN")?.Value ?? root.Element("GTIN")?.Value
        };

        if (!string.IsNullOrWhiteSpace(meta.SeriesNumber) && float.TryParse(meta.SeriesNumber, out var num))
        {
            meta.SeriesIndex = num;
        }

        var bw = root.Element("BlackAndWhite")?.Value;
        if (bw != null && bool.TryParse(bw, out var isBw))
        {
            meta.BlackAndWhite = isBw;
        }

        var countStr = root.Element("PageCount")?.Value;
        if (countStr != null && int.TryParse(countStr, out var pages))
        {
            meta.PageCount = pages;
        }

        var yearStr = root.Element("Year")?.Value;
        var monthStr = root.Element("Month")?.Value;
        var dayStr = root.Element("Day")?.Value;
        if (yearStr != null && int.TryParse(yearStr, out var year))
        {
            meta.PublishYear = year;
            if (monthStr != null && int.TryParse(monthStr, out var month))
            {
                if (dayStr != null && int.TryParse(dayStr, out var day))
                {
                    meta.PublishDate = new DateTime(year, month, day);
                }
                else
                {
                    meta.PublishDate = new DateTime(year, month, 1);
                }
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

    private static void MergeSidecar(FileMetadata target, FileMetadata sidecar)
    {
        if (!string.IsNullOrWhiteSpace(sidecar.Title))
        {
            target.Title = sidecar.Title;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.SeriesName))
        {
            target.SeriesName = sidecar.SeriesName;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.SeriesNumber))
        {
            target.SeriesNumber = sidecar.SeriesNumber;
            if (float.TryParse(sidecar.SeriesNumber, out var num))
            {
                target.SeriesIndex = num;
            }
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Volume))
        {
            target.Volume = sidecar.Volume;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Description))
        {
            target.Description = sidecar.Description;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Publisher))
        {
            target.Publisher = sidecar.Publisher;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.AgeRating))
        {
            target.AgeRating = sidecar.AgeRating;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Manga))
        {
            target.Manga = sidecar.Manga;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.StoryArc))
        {
            target.StoryArc = sidecar.StoryArc;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Format))
        {
            target.Format = sidecar.Format;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Language))
        {
            target.Language = sidecar.Language;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Isbn))
        {
            target.Isbn = sidecar.Isbn;
        }

        if (sidecar.BlackAndWhite.HasValue)
        {
            target.BlackAndWhite = sidecar.BlackAndWhite.Value;
        }

        if (sidecar.PageCount.HasValue)
        {
            target.PageCount = sidecar.PageCount.Value;
        }

        if (sidecar.PublishYear.HasValue)
        {
            target.PublishYear = sidecar.PublishYear.Value;
        }

        if (sidecar.PublishDate.HasValue)
        {
            target.PublishDate = sidecar.PublishDate.Value;
        }

        foreach (var genre in sidecar.Genres)
        {
            if (!target.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
            {
                target.Genres.Add(genre);
            }
        }

        foreach (var tag in sidecar.Tags)
        {
            if (!target.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                target.Tags.Add(tag);
            }
        }

        foreach (var person in sidecar.ComicPeople)
        {
            if (string.IsNullOrWhiteSpace(person.Name) || string.IsNullOrWhiteSpace(person.Role))
            {
                continue;
            }

            var existing = target.ComicPeople.FirstOrDefault(p =>
                string.Equals(p.Name, person.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Role, person.Role, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                target.ComicPeople.Add(person);
            }
        }
    }
}
