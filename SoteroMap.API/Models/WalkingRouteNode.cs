namespace SoteroMap.API.Models;

public class WalkingRouteNode
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Campus { get; set; } = "sotero";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string CreatedByUsername { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
