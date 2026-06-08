using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Jellyfin.Plugin.BookEnhancer.Services.Parsers;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class FileMetadataExtractor
{
    private readonly IReadOnlyList<IFileParser> _parsers;

    public FileMetadataExtractor()
    {
        _parsers =
        [
            new EpubParser(),
            new ComicInfoParser(),
            new PdfParser(),
            new AudioParser()
        ];
    }

    public async Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(filePath))
                return await parser.ExtractAsync(filePath, ct).ConfigureAwait(false);
        }
        return null;
    }
}
