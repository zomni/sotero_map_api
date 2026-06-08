namespace SoteroMap.API.ViewModels;

public class CreateWalkingRoutePathRequest
{
    public string? Campus { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public bool DisableLastPointSnap { get; set; }
    public List<List<double>> Coordinates { get; set; } = [];
}

public class UpdateWalkingRouteEdgeRequest
{
    public string? Status { get; set; }
    public string? Notes { get; set; }
}

public class UpdateWalkingRouteNodeRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class SplitWalkingRouteNodeRequest
{
    public string? EdgeExternalId { get; set; }
}

public class RestoreWalkingRouteNetworkRequest
{
    public string? Campus { get; set; }
    public List<RestoreWalkingRouteNodeRequest> Nodes { get; set; } = [];
    public List<RestoreWalkingRouteEdgeRequest> Edges { get; set; } = [];
}

public class RestoreWalkingRouteNodeRequest
{
    public string? ExternalId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Notes { get; set; }
}

public class RestoreWalkingRouteEdgeRequest
{
    public string? ExternalId { get; set; }
    public string? FromNodeExternalId { get; set; }
    public string? ToNodeExternalId { get; set; }
    public double DistanceMeters { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
}
