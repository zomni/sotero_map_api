namespace SoteroMap.API.ViewModels;

public class CreateWalkingRoutePathRequest
{
    public string? Campus { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public List<List<double>> Coordinates { get; set; } = [];
}

public class UpdateWalkingRouteEdgeRequest
{
    public string? Status { get; set; }
    public string? Notes { get; set; }
}
