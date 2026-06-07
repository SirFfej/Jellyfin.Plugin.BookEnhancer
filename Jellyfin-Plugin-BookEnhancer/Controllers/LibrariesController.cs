using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Authorize]
[Route("Books/Libraries")]
public class LibrariesController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;

    public LibrariesController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    [HttpGet("")]
    public ActionResult<List<LibraryDto>> GetLibraries()
    {
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(lf => new LibraryDto
            {
                Id = lf.ItemId,
                Name = lf.Name ?? string.Empty,
                CollectionType = lf.CollectionType?.ToString() ?? "unknown",
                Locations = lf.Locations?.ToList() ?? new List<string>()
            })
            .OrderBy(l => l.Name)
            .ToList();

        return Ok(libraries);
    }
}

public class LibraryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CollectionType { get; set; } = string.Empty;
    public List<string> Locations { get; set; } = new();
}
