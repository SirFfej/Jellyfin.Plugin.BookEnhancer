using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public interface IFileMetadataWriter
{
    Task<bool> WriteMetadataAsync(string filePath, FileMetadata metadata, CancellationToken ct = default);
}
