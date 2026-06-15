namespace SoteroMap.API.ViewModels;

public class NetworkTelemetryIngestRequest
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public IReadOnlyList<NetworkTelemetryDeviceInput> Devices { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryUserInput> Users { get; set; } = [];
}

public class NetworkTelemetryDeviceInput
{
    public string ExternalKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string BuildingExternalId { get; set; } = string.Empty;
    public string RoomExternalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class NetworkTelemetryUserInput
{
    public string ExternalKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? DeviceCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class NetworkTelemetryIngestResultViewModel
{
    public int SnapshotId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
    public int DeviceCount { get; set; }
    public int UserCount { get; set; }
    public int HighRiskDeviceCount { get; set; }
    public int MediumRiskDeviceCount { get; set; }
    public int LowRiskDeviceCount { get; set; }
    public int HighRiskUserCount { get; set; }
    public int MediumRiskUserCount { get; set; }
    public int LowRiskUserCount { get; set; }
    public int OverallRiskScore { get; set; }
    public string OverallRiskLevel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class NetworkTelemetryObservationViewModel
{
    public int Id { get; set; }
    public string ObservationType { get; set; } = string.Empty;
    public string ExternalKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string BuildingExternalId { get; set; } = string.Empty;
    public string RoomExternalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public IReadOnlyList<string> RiskReasons { get; set; } = [];
    public DateTime ObservedAtUtc { get; set; }
}
