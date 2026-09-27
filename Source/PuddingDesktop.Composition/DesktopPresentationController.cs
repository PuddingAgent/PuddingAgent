using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.Composition;

/// <summary>Authenticated Workbench navigation; the Core action calls Desktop directly through DI.</summary>
[ApiController, Route("api/desktop"), Authorize(Roles = "admin")]
public sealed class DesktopPresentationController(IDesktopServices desktop) : ControllerBase
{
    [HttpPost("show/{page}")]
    public async Task<IActionResult> Show(ShellPage page, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(page)) return BadRequest();
        await desktop.ShowAsync(page, cancellationToken);
        return NoContent();
    }
}
