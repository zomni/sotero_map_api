namespace SoteroMap.API.ViewModels;

public class NetworkTelemetryOfficeSummaryViewModel
{
    public int SnapshotId { get; set; }
    public string HealthLabel { get; set; } = string.Empty;
    public string HealthTone { get; set; } = string.Empty;
    public bool IsFresh { get; set; }
    public bool HasData { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public int DeviceCount { get; set; }
    public int ConnectedUserCount { get; set; }
    public int HighRiskDeviceCount { get; set; }
    public int MediumRiskDeviceCount { get; set; }
    public int LowRiskDeviceCount { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public class NetworkTelemetryOfficeSnapshotDetailViewModel
{
    public NetworkTelemetryOfficeSummaryViewModel Summary { get; set; } = new();
    public IReadOnlyList<NetworkTelemetryBuildingRiskSummaryViewModel> BuildingRisk { get; set; } = [];
    public IReadOnlyList<NetworkTelemetrySubnetRiskSummaryViewModel> SubnetRisk { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryObservationViewModel> TopRiskObservations { get; set; } = [];
}
