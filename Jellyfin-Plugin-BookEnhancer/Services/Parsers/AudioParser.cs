using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class AudioParser : IFileParser
{
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
    };

    public bool CanParse(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _extensions.Contains(ext);
    }

    public Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        try
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

                if (!string.IsNullOrWhiteSpace(file.Tag.Album))
                    meta.SeriesName = file.Tag.Album;

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

            return Task.FromResult<FileMetadata?>(meta);
        }
        catch
        {
            return Task.FromResult<FileMetadata?>(null);
        }
    }
}
