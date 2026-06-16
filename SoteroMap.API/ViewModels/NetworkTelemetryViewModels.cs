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
    public int LatestSnapshotId { get; set; }
    public DateTime? LatestObservedAtUtc { get; set; }
    public DateTime? LatestWindowStartUtc { get; set; }
    public DateTime? LatestWindowEndUtc { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<NetworkTelemetrySnapshotViewModel> RecentSnapshots { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryObservationViewModel> TopRiskObservations { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryBuildingRiskSummaryViewModel> BuildingRiskSummaries { get; set; } = [];
    public IReadOnlyList<NetworkTelemetrySubnetRiskSummaryViewModel> SubnetRiskSummaries { get; set; } = [];
    public NetworkTelemetrySessionOverviewViewModel SessionOverview { get; set; } = new();
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

public class NetworkTelemetrySessionOverviewViewModel
{
    public int ActiveUserCount { get; set; }
    public int LockedUserCount { get; set; }
    public int ExpiredUserCount { get; set; }
    public int PendingMfaUserCount { get; set; }
    public int InactiveUserCount { get; set; }
    public int TotalEvaluatedUsers { get; set; }
    public IReadOnlyList<NetworkTelemetrySessionUserViewModel> Users { get; set; } = [];
}

public class NetworkTelemetrySessionUserViewModel
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string SessionState { get; set; } = string.Empty;
    public string SessionStateLabel { get; set; } = string.Empty;
    public string EndpointKey { get; set; } = string.Empty;
    public string SubnetCidr { get; set; } = string.Empty;
    public string NetworkProfile { get; set; } = string.Empty;
    public string OpenPorts { get; set; } = string.Empty;
    public int? PingMs { get; set; }
    public bool? IsOnline { get; set; }
    public int RiskScore { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime? LastLogoutAtUtc { get; set; }
    public DateTime? LastMfaVerifiedAtUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public bool IsActive { get; set; }
    public bool MfaEnabled { get; set; }
    public int LinkedDeviceCount { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}
