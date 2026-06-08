using Jellyfin.Plugin.BookEnhancer.Models.Api;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Route("Books/Ingestion")]
public class IngestionController : ControllerBase
{
    private readonly BookIngestionService _ingestion;

    public IngestionController(BookIngestionService ingestion)
    {
        _ingestion = ingestion;
    }

    [HttpPost("Scan")]
    public async Task<ActionResult<ScanResult>> Scan(CancellationToken ct)
    {
        var result = await _ingestion.ScanAllAsync(ct).ConfigureAwait(false);

        return Ok(new ScanResult
        {
            FilesFound = result.FilesFound,
            FilesAdded = result.FilesAdded,
            FilesSkipped = result.FilesSkipped,
            Errors = result.Errors
        });
    }
}
