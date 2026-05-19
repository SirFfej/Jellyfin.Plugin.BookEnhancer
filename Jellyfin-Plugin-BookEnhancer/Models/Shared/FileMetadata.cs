namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

public class FileMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public string FileFormat { get; set; } = string.Empty;

    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string> Authors { get; set; } = new();
    public string? Description { get; set; }

    public string? Isbn { get; set; }
    public string? Asin { get; set; }

    public string? Publisher { get; set; }
    public DateTime? PublishDate { get; set; }
    public int? PublishYear { get; set; }
    public string? Language { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Genres { get; set; } = new();

    public string? SeriesName { get; set; }
    public float? SeriesIndex { get; set; }
    public string? SeriesNumber { get; set; }
    public string? Volume { get; set; }

    public int? PageCount { get; set; }
    public long? DurationMs { get; set; }

    public string? AgeRating { get; set; }
    public string? Manga { get; set; }
    public bool? BlackAndWhite { get; set; }
    public List<ComicPersonInfo> ComicPeople { get; set; } = new();
    public string? StoryArc { get; set; }
    public string? Format { get; set; }

    public List<string> Narrators { get; set; } = new();
    public List<ChapterInfo> Chapters { get; set; } = new();

    public bool HasCover { get; set; }
    public byte[]? CoverBytes { get; set; }
    public string? CoverMimeType { get; set; }
    public string? CoverUrl { get; set; }
}

public class ComicPersonInfo
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class ChapterInfo
{
    public int Index { get; set; }
    public string? Title { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public long DurationMs => EndMs - StartMs;
}
