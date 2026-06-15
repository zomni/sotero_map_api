namespace SoteroMap.API.ViewModels;

public class NetworkTelemetryDashboardViewModel
{
    public bool Enabled { get; set; }
    public bool HasData { get; set; }
    public bool IsFresh { get; set; }
    public string HealthLabel { get; set; } = "Sin datos";
    public string HealthTone { get; set; } = "secondary";
    public string LatestSourceName { get; set; } = string.Empty;
    public string LatestSourceType { get; set; } = string.Empty;
    public string LatestRiskLevel { get; set; } = string.Empty;
    public string LatestStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int LatestRiskScore { get; set; }
    public int TotalSnapshots { get; set; }
    public int LatestDeviceCount { get; set; }
    public int LatestConnectedUserCount { get; set; }
    public int LatestHighRiskDeviceCount { get; set; }
    public int LatestMediumRiskDeviceCount { get; set; }
    public int LatestLowRiskDeviceCount { get; set; }
    public DateTime? LatestObservedAtUtc { get; set; }
    public DateTime? LatestWindowStartUtc { get; set; }
    public DateTime? LatestWindowEndUtc { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<NetworkTelemetrySnapshotViewModel> RecentSnapshots { get; set; } = [];
}

public class NetworkTelemetrySnapshotViewModel
{
    public int Id { get; set; }
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
    public DateTime ObservedAtUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string CreatedByUsername { get; set; } = string.Empty;
}
