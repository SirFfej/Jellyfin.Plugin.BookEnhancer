using System.Text.RegularExpressions;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class AudioParser : IFileParser
{
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
    };

    /// <summary>
    /// Matches disc/part markers such as "Disc 08", "Disk 8", "CD 08", "Part 08", "Disc 08 of 12".
    /// </summary>
    private static readonly Regex DiscMarkerRegex = new(
        @"[\(\[]?\b(?:disc|disk|cd|part)\s*(\d+)(?:\s*of\s*\d+)?\b[\)\]]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _extensions.Contains(ext);
    }

    public async Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(() => ExtractInternal(filePath), ct).ConfigureAwait(false);
    }

    private static FileMetadata ExtractInternal(string filePath)
    {
        using var file = TagLib.File.Create(filePath);

        var meta = new FileMetadata
        {
            FilePath = filePath,
            FileFormat = "Audio",
            Title = file.Tag?.Title,
            Publisher = file.Tag?.Publisher
        };

        if (file.Properties != null)
            meta.DurationMs = (long)file.Properties.Duration.TotalMilliseconds;

        if (file.Tag != null)
        {
            if (!string.IsNullOrWhiteSpace(file.Tag.FirstPerformer))
                meta.Authors.Add(file.Tag.FirstPerformer);
            else if (!string.IsNullOrWhiteSpace(file.Tag.FirstAlbumArtist))
                meta.Authors.Add(file.Tag.FirstAlbumArtist);

            var album = file.Tag.Album;
            if (!string.IsNullOrWhiteSpace(album))
            {
                meta.DiscNumber = ExtractDiscNumber(album);
                meta.SeriesName = NormalizeBookTitle(album, meta.Authors);
            }

            if (!string.IsNullOrWhiteSpace(meta.Title))
            {
                var titleDisc = ExtractDiscNumber(meta.Title);
                if (!string.IsNullOrWhiteSpace(titleDisc))
                    meta.DiscNumber = titleDisc;

                var cleanedTitle = NormalizeBookTitle(meta.Title, meta.Authors);

                // Prefer the album/series as the canonical book title; fall back to the cleaned title tag.
                meta.BookTitle = !string.IsNullOrWhiteSpace(meta.SeriesName)
                    ? meta.SeriesName
                    : cleanedTitle;
            }

            if (file.Tag.Year != 0)
            {
                meta.PublishYear = (int)file.Tag.Year;
                try { meta.PublishDate = new DateTime((int)file.Tag.Year, 1, 1); } catch { meta.PublishDate = null; }
            }

            if (file.Tag.Track != 0)
                meta.SeriesIndex = (int)file.Tag.Track;

            if (file.Tag.Genres != null)
            {
                foreach (var genre in file.Tag.Genres)
                {
                    if (!string.IsNullOrWhiteSpace(genre) && !meta.Genres.Contains(genre))
                        meta.Genres.Add(genre);
                }
            }

            if (file.Tag.AlbumArtists != null)
            {
                foreach (var artist in file.Tag.AlbumArtists)
                {
                    if (!string.IsNullOrWhiteSpace(artist) && !meta.Narrators.Contains(artist))
                        meta.Narrators.Add(artist);
                }
            }

            if (file.Tag.Pictures != null && file.Tag.Pictures.Length > 0)
            {
                var pic = file.Tag.Pictures[0];
                meta.CoverBytes = pic.Data.Data;
                meta.CoverMimeType = pic.MimeType;
                meta.HasCover = true;
            }
        }

        return meta;
    }

    private static string? ExtractDiscNumber(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var match = DiscMarkerRegex.Match(input);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var disc))
            return $"Disc {disc:00}";

        return null;
    }

    private static string NormalizeBookTitle(string title, List<string> authors)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var normalized = title.Trim();

        // Strip leading author prefix when it matches a known author, e.g. "Anne Perry - Dark Assassin Disc 08"
        foreach (var author in authors)
        {
            if (string.IsNullOrWhiteSpace(author))
                continue;

            var prefix = author.Trim() + " - ";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..].Trim();
                break;
            }
        }

        // Strip disc/part markers, e.g. "Disc 08", "(Disc 08)", "[Disc 08 of 12]"
        normalized = DiscMarkerRegex.Replace(normalized, string.Empty).Trim();

        // Clean up trailing punctuation/spaces left after stripping markers
        normalized = normalized.TrimEnd(' ', '-', '_', '(', '[', ')', ']');

        return normalized;
    }
}
