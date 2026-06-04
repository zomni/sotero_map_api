namespace SoteroMap.API.Models;

public class BuildingGeometryOverride
{
    public int Id { get; set; }
    public string BuildingExternalId { get; set; } = string.Empty;
    public string GeometryJson { get; set; } = string.Empty;
    public double? CentroidLatitude { get; set; }
    public double? CentroidLongitude { get; set; }
    public string UpdatedByUsername { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
