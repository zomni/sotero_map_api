using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EquipmentsController : ControllerBase
{
    private readonly AppDbContext _context;

    public EquipmentsController(AppDbContext context)
    {
        _context = context;
    }

    // GET /api/equipments?locationId=1&status=active
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Equipment>>> GetAll(
        [FromQuery] int? locationId,
        [FromQuery] string? status,
        [FromQuery] string? category)
    {
        var query = _context.Equipments.Include(e => e.Location).AsQueryable();

        if (locationId.HasValue)
            query = query.Where(e => e.LocationId == locationId.Value);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(e => e.Status == status);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(e => e.Category == category);

        return Ok(await query.ToListAsync());
    }

    // GET /api/equipments/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Equipment>> GetById(int id)
    {
        var equipment = await _context.Equipments
            .Include(e => e.Location)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (equipment == null) return NotFound();
        return Ok(equipment);
    }

    // POST /api/equipments
    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    public async Task<ActionResult<Equipment>> Create(Equipment equipment)
    {
        equipment.CreatedAt = DateTime.UtcNow;
        _context.Equipments.Add(equipment);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = equipment.Id }, equipment);
    }

    // PUT /api/equipments/5
    [Authorize(Roles = AppRoles.Admin)]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Equipment equipment)
    {
        if (id != equipment.Id) return BadRequest();

        _context.Entry(equipment).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!_context.Equipments.Any(e => e.Id == id)) return NotFound();
            throw;
        }

        return NoContent();
    }

    // DELETE /api/equipments/5
    [Authorize(Roles = AppRoles.Admin)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var equipment = await _context.Equipments.FindAsync(id);
        if (equipment == null) return NotFound();

        _context.Equipments.Remove(equipment);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/equipments/summary
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var summary = await _context.Equipments
            .GroupBy(e => e.Status)
            .Select(g => new { status = g.Key, count = g.Count() })
            .ToListAsync();

        return Ok(summary);
    }
}
