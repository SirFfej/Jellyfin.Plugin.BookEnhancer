namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

/// <summary>
/// Represents a detected duplicate where the source and target files exist but have different sizes.
/// </summary>
public sealed class DuplicateReviewEntry
{
    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target file path.
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file size in bytes.
    /// </summary>
    public long SourceSize { get; set; }

    /// <summary>
    /// Gets or sets the target file size in bytes.
    /// </summary>
    public long TargetSize { get; set; }

    /// <summary>
    /// Gets or sets the first time this mismatch was observed.
    /// </summary>
    public DateTime FirstSeen { get; set; }

    /// <summary>
    /// Gets or sets the most recent time this mismatch was observed.
    /// </summary>
    public DateTime LastSeen { get; set; }
}
