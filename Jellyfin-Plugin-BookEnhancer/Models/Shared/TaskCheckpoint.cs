namespace Jellyfin.Plugin.BookEnhancer.Models.Shared;

/// <summary>
/// Represents a persisted task checkpoint so long-running tasks can resume after a service restart.
/// </summary>
public class TaskCheckpoint
{
    /// <summary>
    /// Gets or sets the unique key for the task/checkpoint (e.g. "IngestionScan:{SourcePath}").
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last file path that was successfully processed.
    /// </summary>
    public string LastProcessedPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp when the checkpoint was written.
    /// </summary>
    public DateTime TimestampUtc { get; set; }
}
