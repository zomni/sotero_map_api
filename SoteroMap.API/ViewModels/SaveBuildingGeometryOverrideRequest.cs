namespace SoteroMap.API.ViewModels;

public class SaveBuildingGeometryOverrideRequest
{
    public string BuildingExternalId { get; set; } = string.Empty;
    public List<List<double>> Coordinates { get; set; } = [];
}
