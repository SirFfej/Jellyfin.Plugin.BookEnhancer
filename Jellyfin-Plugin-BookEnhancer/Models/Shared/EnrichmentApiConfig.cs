namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

/// <summary>
/// Resolved API configuration used by the metadata enrichment cascade.
/// </summary>
public class EnrichmentApiConfig
{
    public bool HardcoverEnabled { get; set; }

    public string HardcoverApiKey { get; set; } = string.Empty;

    public bool GoogleBooksEnabled { get; set; }

    public string? GoogleBooksApiKey { get; set; }

    public bool OpenLibraryEnabled { get; set; }

    public bool ComicVineEnabled { get; set; }

    public string ComicVineApiKey { get; set; } = string.Empty;

    public bool MetronEnabled { get; set; }

    public string MetronUsername { get; set; } = string.Empty;

    public string MetronPassword { get; set; } = string.Empty;

    public bool VerseDbEnabled { get; set; }

    public string VerseDbApiKey { get; set; } = string.Empty;

    public bool GrandComicsDbEnabled { get; set; }

    public string GrandComicsDbUsername { get; set; } = string.Empty;

    public string GrandComicsDbPassword { get; set; } = string.Empty;
}
