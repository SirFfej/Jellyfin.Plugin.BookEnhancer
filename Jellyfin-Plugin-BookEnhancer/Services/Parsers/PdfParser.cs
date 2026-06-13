using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using UglyToad.PdfPig;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class PdfParser : IFileParser
{
    public bool CanParse(string filePath)
    {
        return filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        using var doc = PdfDocument.Open(filePath);
        var info = doc.Information;

        var meta = new FileMetadata
        {
            FilePath = filePath,
            FileFormat = "PDF",
            Title = info.Title,
            Description = info.Subject,
            PageCount = doc.NumberOfPages
        };

        if (!string.IsNullOrWhiteSpace(info.Author))
        {
            foreach (var author in info.Author.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                meta.Authors.Add(author);
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Keywords))
        {
            foreach (var keyword in info.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                meta.Tags.Add(keyword);
            }
        }

        meta.HasCover = doc.NumberOfPages > 0;

        return Task.FromResult<FileMetadata?>(meta);
    }
}
