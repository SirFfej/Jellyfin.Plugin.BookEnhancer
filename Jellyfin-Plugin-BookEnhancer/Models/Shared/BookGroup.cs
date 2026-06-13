namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

public class BookGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? Isbn { get; set; }

    public string? Title { get; set; }

    public string? Author { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<BookFormat> Formats { get; set; } = new();
}

public class BookFormat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string GroupId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string FormatType { get; set; } = string.Empty;

    public string? JellyfinItemId { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EnrichedAt { get; set; }
}
