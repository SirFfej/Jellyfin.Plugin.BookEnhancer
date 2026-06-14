using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace Jellyfin.Plugin.BookEnhancer.Services.Parsers;

public class PdfParser : IFileParser
{
    private readonly ILogger<PdfParser> _logger;

    public PdfParser(ILogger<PdfParser>? logger = null)
    {
        _logger = logger ?? NullLogger<PdfParser>.Instance;
    }

    public bool CanParse(string filePath)
    {
        return filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public Task<FileMetadata?> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PdfPig failed to parse {FilePath}; falling back to filename metadata", filePath);

            return Task.FromResult<FileMetadata?>(new FileMetadata
            {
                FilePath = filePath,
                FileFormat = "PDF",
                Title = SceneTagCleaner.Clean(Path.GetFileNameWithoutExtension(filePath)),
                HasCover = false
            });
        }
    }
}
