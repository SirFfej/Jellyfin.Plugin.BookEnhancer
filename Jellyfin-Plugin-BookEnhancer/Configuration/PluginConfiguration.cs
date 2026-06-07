using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BookEnhancer.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool UnifiedMetadataEnabled { get; set; } = true;
    public bool HardcoverEnabled { get; set; } = true;
    public string HardcoverApiKey { get; set; } = string.Empty;
    public bool GoogleBooksEnabled { get; set; } = true;
    public string GoogleBooksApiKey { get; set; } = string.Empty;
    public bool OpenLibraryEnabled { get; set; } = true;
    public double MatchThreshold { get; set; } = 0.85;

    public bool EnableFormatGrouping { get; set; } = true;
    public string GroupingStrategy { get; set; } = "IsbnOnly";

    public List<ManagedSourceDirectory> ManagedDirectories { get; set; } = new();
    public int AutoScanIntervalMinutes { get; set; } = 0;
}

public class ManagedSourceDirectory
{
    public string SourcePath { get; set; } = string.Empty;
    public string LibraryPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
