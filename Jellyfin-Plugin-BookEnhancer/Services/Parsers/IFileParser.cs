using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public interface IFileParser
{
    bool CanParse(string filePath);
    Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default);
}
