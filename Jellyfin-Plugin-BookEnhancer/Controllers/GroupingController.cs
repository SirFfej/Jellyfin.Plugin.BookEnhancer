using Jellyfin.Plugin.BookEnhancer.Models.Api;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Route("Books/Grouping")]
public class GroupingController : ControllerBase
{
    private readonly BookGroupingService _groupingService;
    private readonly GroupingPostProcessingService _postProcessing;

    public GroupingController(
        BookGroupingService groupingService,
        GroupingPostProcessingService postProcessing)
    {
        _groupingService = groupingService;
        _postProcessing = postProcessing;
    }

    [HttpPost("Process")]
    public async Task<ActionResult<GroupingProcessResult>> Process()
    {
        var groups = _groupingService.GetAllGroupsWithMultipleFormats();
        var result = new GroupingProcessResult
        {
            ProcessedGroups = groups.Count,
            TotalFormatsMerged = groups.Sum(g => g.Formats.Count(f => !f.IsPrimary))
        };

        await _postProcessing.ProcessAllGroupsAsync();

        return Ok(result);
    }
}
