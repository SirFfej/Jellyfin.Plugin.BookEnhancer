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

    public List<FormatPriorityEntry> FormatPriority { get; set; } = DefaultFormatPriority();

    public bool CopyMode { get; set; } = false;

    public List<string> IngestionFileExtensions { get; set; } = new()
    {
        ".epub", ".pdf",
        ".cbz", ".cbr", ".cb7",
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".wma", ".opus", ".aiff"
    };

    public List<ManagedSourceDirectory> ManagedDirectories { get; set; } = new();
    public int AutoScanIntervalMinutes { get; set; } = 0;

    public List<string> IncludedLibraryIds { get; set; } = new();

    private static List<FormatPriorityEntry> DefaultFormatPriority()
    {
        return new List<FormatPriorityEntry>
        {
            new() { FormatName = "EPUB", Priority = 0 },
            new() { FormatName = "MOBI", Priority = 1 },
            new() { FormatName = "PDF", Priority = 2 },
            new() { FormatName = "Comic", Priority = 3 },
            new() { FormatName = "Audio", Priority = 4 },
        };
    }
}

public class FormatPriorityEntry
{
    public string FormatName { get; set; } = string.Empty;
    public int Priority { get; set; }
}

public class ManagedSourceDirectory
{
    public string SourcePath { get; set; } = string.Empty;
    public string LibraryPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string LibraryId { get; set; } = string.Empty;
    public string OrganizeTemplate { get; set; } = string.Empty;
}
