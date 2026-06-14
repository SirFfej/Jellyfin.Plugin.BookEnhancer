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
    public bool ComicVineEnabled { get; set; } = false;
    public string ComicVineApiKey { get; set; } = string.Empty;
    public bool MetronEnabled { get; set; } = false;
    public string MetronUsername { get; set; } = string.Empty;
    public string MetronPassword { get; set; } = string.Empty;
    public bool VerseDbEnabled { get; set; } = false;
    public string VerseDbApiKey { get; set; } = string.Empty;
    public bool GrandComicsDbEnabled { get; set; } = false;
    public string GrandComicsDbUsername { get; set; } = string.Empty;
    public string GrandComicsDbPassword { get; set; } = string.Empty;
    public double MatchThreshold { get; set; } = 0.85;

    public bool EnableFormatGrouping { get; set; } = true;
    public string GroupingStrategy { get; set; } = "IsbnOnly";

    public List<FormatPriorityEntry> FormatPriority { get; set; } = new();

    public bool CopyMode { get; set; } = false;

    public List<string> IngestionFileExtensions { get; set; } = new();

    public List<ManagedSourceDirectory> ManagedDirectories { get; set; } = new();
    public int AutoScanIntervalMinutes { get; set; } = 0;

    public List<string> IncludedLibraryIds { get; set; } = new();

    public int EnrichmentCooldownDays { get; set; } = 7;

    public string TrashDirectory { get; set; } = string.Empty;
    public int TrashCleanupIntervalDays { get; set; } = 7;

    public string BackupDirectory { get; set; } = string.Empty;
    public int BackupCleanupIntervalDays { get; set; } = 30;
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
    public bool EnableTitleAuthorSearch { get; set; } = true;
    public bool EnableMetadataWriting { get; set; } = false;
    public bool FlatSeriesStructure { get; set; }
}
