using Jellyfin.Plugin.BookEnhancer.Models.Shared;
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

    /// <summary>
    /// Gets or sets the ComicVine rate limit in requests per hour. 0 = use default (180).
    /// </summary>
    public int ComicVineRateLimitPerHour { get; set; } = 180;

    /// <summary>
    /// Gets or sets the maximum time an API call will wait for a rate limit slot before falling back to the next API.
    /// 0 = wait indefinitely (legacy behavior).
    /// </summary>
    public int ApiRateLimitMaxWaitSeconds { get; set; } = 5;
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

    /// <summary>
    /// Gets or sets the maximum runtime in minutes for the ingestion scan task. 0 = no limit.
    /// </summary>
    public int IngestionScanTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum runtime in minutes for the library cleanup task. 0 = no limit.
    /// </summary>
    public int LibraryCleanupTimeoutMinutes { get; set; } = 180;

    /// <summary>
    /// Gets or sets the maximum runtime in minutes for the metadata enrichment task. 0 = no limit.
    /// </summary>
    public int MetadataEnrichmentTimeoutMinutes { get; set; } = 120;

    public List<string> IncludedLibraryIds { get; set; } = new();

    public int EnrichmentCooldownDays { get; set; } = 7;

    public string TrashDirectory { get; set; } = string.Empty;
    public int TrashCleanupIntervalDays { get; set; } = 7;

    public string BackupDirectory { get; set; } = string.Empty;
    public int BackupCleanupIntervalDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the number of days after which stale duplicate-review entries are pruned. 0 = never prune.
    /// </summary>
    public int DuplicateReviewTtlDays { get; set; } = 30;

    public bool EnableNonBookDirectoryCleanup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether files in a series that spans multiple authors are organized under the series folder first.
    /// </summary>
    public bool EnableSeriesFirstOrganization { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum number of distinct authors a series must span before series-first organization is used.
    /// </summary>
    public int SeriesFirstAuthorThreshold { get; set; } = 2;

    /// <summary>
    /// Gets or sets the custom template for series-first organization.
    /// </summary>
    public string SeriesFirstTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local file path to a ComicInfo.xml template used as fallback metadata for comics.
    /// </summary>
    public string ComicInfoTemplatePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional URL from which to download the ComicInfo.xml template.
    /// </summary>
    public string ComicInfoTemplateUrl { get; set; } = string.Empty;

    /// <summary>
    /// Resolves the effective API configuration for a managed source directory.
    /// When the directory has an explicit <see cref="ManagedSourceDirectory.EnabledApiSources"/> list,
    /// only APIs named in that list are enabled; otherwise the global toggles are used.
    /// </summary>
    /// <param name="dir">The managed source directory, or null for global defaults.</param>
    /// <returns>The resolved API configuration.</returns>
    public EnrichmentApiConfig GetEffectiveApiConfig(ManagedSourceDirectory? dir)
    {
        var overrides = dir?.EnabledApiSources;
        var hasOverrides = overrides is not null && overrides.Count > 0;

        bool IsEnabled(bool global, string apiName)
        {
            if (!hasOverrides)
                return global;

            return overrides!.Contains(apiName, StringComparer.OrdinalIgnoreCase);
        }

        return new EnrichmentApiConfig
        {
            HardcoverEnabled = IsEnabled(HardcoverEnabled, "Hardcover"),
            HardcoverApiKey = HardcoverApiKey,
            GoogleBooksEnabled = IsEnabled(GoogleBooksEnabled, "Google Books"),
            GoogleBooksApiKey = GoogleBooksApiKey,
            OpenLibraryEnabled = IsEnabled(OpenLibraryEnabled, "OpenLibrary"),
            ComicVineEnabled = IsEnabled(ComicVineEnabled, "Comic Vine"),
            ComicVineApiKey = ComicVineApiKey,
            MetronEnabled = IsEnabled(MetronEnabled, "Metron"),
            MetronUsername = MetronUsername,
            MetronPassword = MetronPassword,
            VerseDbEnabled = IsEnabled(VerseDbEnabled, "VerseDB"),
            VerseDbApiKey = VerseDbApiKey,
            GrandComicsDbEnabled = IsEnabled(GrandComicsDbEnabled, "Grand Comics Database"),
            GrandComicsDbUsername = GrandComicsDbUsername,
            GrandComicsDbPassword = GrandComicsDbPassword
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
    public bool EnableTitleAuthorSearch { get; set; } = true;
    public bool EnableMetadataWriting { get; set; } = false;
    public bool FlatSeriesStructure { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether files in this directory should be treated as comics.
    /// When true, PDF files are parsed with comic filename rules and the ComicIssue grouping strategy applies.
    /// </summary>
    public bool IsComicLibrary { get; set; }

    /// <summary>
    /// Gets or sets the list of API source names enabled for this directory.
    /// When empty, the global API toggles are used.
    /// Known values: Hardcover, Google Books, OpenLibrary, Comic Vine, Metron, VerseDB, Grand Comics Database.
    /// </summary>
    public List<string> EnabledApiSources { get; set; } = new();
}
