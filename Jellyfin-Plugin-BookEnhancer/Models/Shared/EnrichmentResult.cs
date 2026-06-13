namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

public class EnrichmentResult
{
    public FileMetadata Metadata { get; init; } = null!;
    public bool ApiMatchFound { get; init; }
}
