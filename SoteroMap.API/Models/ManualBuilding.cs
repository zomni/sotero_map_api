namespace SoteroMap.API.Models;

public class ManualBuilding
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Campus { get; set; } = "sotero";
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = "manual";
    public string Notes { get; set; } = string.Empty;
    public string FloorsJson { get; set; } = "[]";
    public string GeometryJson { get; set; } = string.Empty;
    public double? CentroidLatitude { get; set; }
    public double? CentroidLongitude { get; set; }
    public string CreatedByUsername { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
