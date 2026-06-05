namespace SoteroMap.API.Models;

public class WalkingRouteEdge
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Campus { get; set; } = "sotero";
    public string FromNodeExternalId { get; set; } = string.Empty;
    public string ToNodeExternalId { get; set; } = string.Empty;
    public double DistanceMeters { get; set; }
    public string Status { get; set; } = "open";
    public string Notes { get; set; } = string.Empty;
    public string CreatedByUsername { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
