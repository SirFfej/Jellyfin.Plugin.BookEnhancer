using Jellyfin.Plugin.BookEnhancer.Models.Api;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Authorize]
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
        var result = await _ingestion.ScanAllAsync(ct: ct).ConfigureAwait(false);

        return Ok(new ScanResult
        {
            FilesFound = result.FilesFound,
            FilesAdded = result.FilesAdded,
            FilesSkipped = result.FilesSkipped,
            EnrichmentFailures = result.EnrichmentFailures,
            Errors = result.Errors
        });
    }
}
