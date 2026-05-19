namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

public class ProgressInfo
{
    public string ItemId { get; set; } = string.Empty;
    public double Progress { get; set; }
    public string UserId { get; set; } = string.Empty;
}
