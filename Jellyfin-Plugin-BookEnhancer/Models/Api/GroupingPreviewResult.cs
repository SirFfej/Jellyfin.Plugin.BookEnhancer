namespace Jellyfin.Plugin.BookEnhancer.Models.Api;

public class GroupingPreviewResult
{
    public string MatchingStrategy { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public int GroupedCount { get; set; }
    public int UngroupedCount { get; set; }
    public List<PreviewGroup> Groups { get; set; } = new();
}

public class PreviewGroup
{
    public string MatchKey { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Author { get; set; }
    public List<PreviewFormat> Formats { get; set; } = new();
}

public class PreviewFormat
{
    public string FilePath { get; set; } = string.Empty;
    public string FormatType { get; set; } = string.Empty;
    public int Priority { get; set; }
}
