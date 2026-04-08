namespace SoteroMap.API.Models;

public class Equipment
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // PC, Projector, Server, Printer, etc.
    public string SerialNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "active"; // active, maintenance, inactive
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
}
