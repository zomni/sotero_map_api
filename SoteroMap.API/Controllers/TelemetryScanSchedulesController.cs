using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoteroMap.API.Models;
using SoteroMap.API.Services;
using SoteroMap.API.ViewModels;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/network-telemetry/schedule")]
[Authorize]
public class TelemetryScanSchedulesController : ControllerBase
{
    private readonly TelemetryScanScheduleService _service;

    public TelemetryScanSchedulesController(TelemetryScanScheduleService service)
    {
        _service = service;
    }

    [Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var schedules = await _service.GetSchedulesAsync(cancellationToken);
        return Ok(schedules);
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] TelemetryScanSchedulePreviewRequest request, CancellationToken cancellationToken)
    {
        string cron;
        if (request.Slots is { Count: > 0 })
        {
            cron = TelemetryScanScheduleService.BuildCronFromSlots(request.Slots);
        }
        else
        {
            cron = request.Cron ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(cron) || !TelemetryScanScheduleService.IsValidCompoundCron(cron))
        {
            return BadRequest(new { message = "No se pudo generar una expresion cron valida." });
        }

        var fromUtc = request.FromUtc ?? DateTime.UtcNow;
        var count = request.Count is > 0 and <= 20 ? request.Count.Value : 5;
        var occurrences = TelemetryScanScheduleService.GetNextOccurrencesUtc(cron, request.TimeZone, fromUtc, count);
        var timeZone = TelemetryScanScheduleService.ResolveTimeZone(request.TimeZone);

        var items = occurrences
            .Select(utc => new
            {
                utc,
                local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone)
            })
            .ToList();

        return Ok(new
        {
            valid = true,
            cron,
            timeZone = timeZone.Id,
            occurrences = items
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] TelemetryScanScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Slots is not { Count: > 0 } && string.IsNullOrWhiteSpace(request.Cron))
        {
            return BadRequest(new { message = "Debe agregar al menos un horario." });
        }

        string cron;
        try
        {
            cron = request.Slots is { Count: > 0 }
                ? TelemetryScanScheduleService.BuildCronFromSlots(request.Slots)
                : (request.Cron ?? string.Empty).Trim();
        }
        catch
        {
            return BadRequest(new { message = "No se pudo generar la expresion cron." });
        }

        var overlaps = await _service.DetectOverlapsAsync(cron, null, cancellationToken);
        if (overlaps.Count > 0)
        {
            return Conflict(new
            {
                message = "Este horario se superpone con horarios existentes:",
                overlaps
            });
        }

        TelemetryScanScheduleDto schedule;
        try
        {
            schedule = await _service.CreateAsync(request, User.Identity?.Name, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Ok(schedule);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] TelemetryScanScheduleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Slots is not { Count: > 0 } && string.IsNullOrWhiteSpace(request.Cron))
        {
            return BadRequest(new { message = "Debe agregar al menos un horario." });
        }

        string cron;
        try
        {
            cron = request.Slots is { Count: > 0 }
                ? TelemetryScanScheduleService.BuildCronFromSlots(request.Slots)
                : (request.Cron ?? string.Empty).Trim();
        }
        catch
        {
            return BadRequest(new { message = "No se pudo generar la expresion cron." });
        }

        var overlaps = await _service.DetectOverlapsAsync(cron, id, cancellationToken);
        if (overlaps.Count > 0)
        {
            return Conflict(new
            {
                message = "Este horario se superpone con horarios existentes:",
                overlaps
            });
        }

        TelemetryScanScheduleDto? schedule;
        try
        {
            schedule = await _service.UpdateAsync(id, request, User.Identity?.Name, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        if (schedule is null)
        {
            return NotFound(new { message = $"Planificacion #{id} no encontrada." });
        }

        return Ok(schedule);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _service.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound(new { message = $"Planificacion #{id} no encontrada." });
        }

        return NoContent();
    }
}
