namespace Jellyfin.Plugin.BookEnhancer.Models.Api;

public class ScanResult
{
    public int FilesFound { get; set; }
    public int FilesAdded { get; set; }
    public int FilesSkipped { get; set; }
    public int Errors { get; set; }
    public int EnrichmentFailures { get; set; }
}
