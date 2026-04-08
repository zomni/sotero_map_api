using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoteroMap.API.Data;
using SoteroMap.API.Models;

namespace SoteroMap.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LocationsController : ControllerBase
{
    private readonly AppDbContext _context;

    public LocationsController(AppDbContext context)
    {
        _context = context;
    }

    // GET /api/locations?campus=sotero&floor=0
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? campus, [FromQuery] string? floor)
    {
        var query = _context.Locations
            .Include(l => l.Equipments)
            .Where(l => l.IsActive);

        if (!string.IsNullOrEmpty(campus))
            query = query.Where(l => l.Campus == campus);

        if (!string.IsNullOrEmpty(floor))
            query = query.Where(l => l.Floor == floor);

        var locations = await query.ToListAsync();

        // Devuelve GeoJSON compatible con Leaflet
        var geoJson = new
        {
            type = "FeatureCollection",
            features = locations.Select(l => new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { l.Longitude, l.Latitude }
                },
                properties = new
                {
                    id = l.Id,
                    name = l.Name,
                    description = l.Description,
                    floor = l.Floor,
                    campus = l.Campus,
                    locationType = l.Type,
                    equipmentCount = l.Equipments.Count
                }
            })
        };

        return Ok(geoJson);
    }

    // GET /api/locations/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Location>> GetById(int id)
    {
        var location = await _context.Locations
            .Include(l => l.Equipments)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (location == null) return NotFound();
        return Ok(location);
    }

    // POST /api/locations
    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost]
    public async Task<ActionResult<Location>> Create(Location location)
    {
        location.CreatedAt = DateTime.UtcNow;
        _context.Locations.Add(location);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = location.Id }, location);
    }

    // PUT /api/locations/5
    [Authorize(Roles = AppRoles.Admin)]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Location location)
    {
        if (id != location.Id) return BadRequest();

        _context.Entry(location).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!_context.Locations.Any(l => l.Id == id)) return NotFound();
            throw;
        }

        return NoContent();
    }

    // DELETE /api/locations/5
    [Authorize(Roles = AppRoles.Admin)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var location = await _context.Locations.FindAsync(id);
        if (location == null) return NotFound();

        location.IsActive = false; // Soft delete
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
