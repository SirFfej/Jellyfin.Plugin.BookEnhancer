using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

/// <summary>
/// Represents a detected duplicate where the source and target files exist but have different sizes.
/// </summary>
public sealed class DuplicateReviewEntry
{
    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target file path.
    /// </summary>
    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file size in bytes.
    /// </summary>
    [JsonPropertyName("sourceSize")]
    public long SourceSize { get; set; }

    /// <summary>
    /// Gets or sets the target file size in bytes.
    /// </summary>
    [JsonPropertyName("targetSize")]
    public long TargetSize { get; set; }

    /// <summary>
    /// Gets or sets the first time this mismatch was observed.
    /// </summary>
    [JsonPropertyName("firstSeen")]
    public DateTime FirstSeen { get; set; }

    /// <summary>
    /// Gets or sets the most recent time this mismatch was observed.
    /// </summary>
    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; }
}
