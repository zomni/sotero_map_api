using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/frontend-sync")]
[Authorize]
public class FrontendSyncController : ControllerBase
{
    private readonly FrontendSyncService _frontendSyncService;

    public FrontendSyncController(FrontendSyncService frontendSyncService)
    {
        _frontendSyncService = frontendSyncService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await _frontendSyncService.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken cancellationToken)
    {
        var result = await _frontendSyncService.SyncAsync(cancellationToken);
        return Ok(result);
    }
}
