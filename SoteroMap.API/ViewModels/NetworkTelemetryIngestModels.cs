namespace SoteroMap.API.ViewModels;

public class NetworkTelemetryIngestRequest
{
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public DateTime? ObservedAtUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public IReadOnlyList<NetworkTelemetryDeviceInput> Devices { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryUserInput> Users { get; set; } = [];
}

public class NetworkTelemetryLiveScanRequest
{
    public bool ResolveInteractiveSessions { get; set; } = true;
    public string ScanMode { get; set; } = "simple";
    public string TriggerType { get; set; } = string.Empty;
    public string DirectoryUsername { get; set; } = string.Empty;
    public string DirectoryPassword { get; set; } = string.Empty;
    public string DirectoryDomain { get; set; } = string.Empty;
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
    public string DeviceCategory { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string OperatingSystemVersion { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Processor { get; set; } = string.Empty;
    public double? MemoryGb { get; set; }
    public double? DiskTotalGb { get; set; }
    public double? DiskFreeGb { get; set; }
    public DateTime? LastBootAtUtc { get; set; }
    public bool? IsOnline { get; set; }
    public bool? DomainJoined { get; set; }
    public bool? IsVirtualMachine { get; set; }
    public int? PingMs { get; set; }
    public string AntivirusStatus { get; set; } = string.Empty;
    public string AntivirusVersion { get; set; } = string.Empty;
    public string PatchStatus { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string OpenPorts { get; set; } = string.Empty;
    public string SubnetCidr { get; set; } = string.Empty;
    public string NetworkProfile { get; set; } = string.Empty;
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
    public string DeviceCategory { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string OperatingSystemVersion { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Processor { get; set; } = string.Empty;
    public double? MemoryGb { get; set; }
    public double? DiskTotalGb { get; set; }
    public double? DiskFreeGb { get; set; }
    public DateTime? LastBootAtUtc { get; set; }
    public bool? IsOnline { get; set; }
    public bool? DomainJoined { get; set; }
    public bool? IsVirtualMachine { get; set; }
    public int? PingMs { get; set; }
    public string AntivirusStatus { get; set; } = string.Empty;
    public string AntivirusVersion { get; set; } = string.Empty;
    public string PatchStatus { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string OpenPorts { get; set; } = string.Empty;
    public string SubnetCidr { get; set; } = string.Empty;
    public string NetworkProfile { get; set; } = string.Empty;
    public string BuildingExternalId { get; set; } = string.Empty;
    public string RoomExternalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public IReadOnlyList<string> RiskReasons { get; set; } = [];
    public DateTime ObservedAtUtc { get; set; }
    public int? ImportedInventoryItemId { get; set; }
}

public class NetworkTelemetryObservationQueryRequest
{
    public string Search { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string BuildingExternalId { get; set; } = string.Empty;
    public string SubnetCidr { get; set; } = string.Empty;
    public string OnlineState { get; set; } = string.Empty;
    public string ObservationType { get; set; } = "device";
    public string SortBy { get; set; } = "risk";
    public string SortDirection { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public class NetworkTelemetryObservationPageViewModel
{
    public int SnapshotId { get; set; }
    public string Search { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string BuildingExternalId { get; set; } = string.Empty;
    public string SubnetCidr { get; set; } = string.Empty;
    public string OnlineState { get; set; } = string.Empty;
    public string ObservationType { get; set; } = "device";
    public string SortBy { get; set; } = "risk";
    public string SortDirection { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalCount { get; set; }
    public int TotalPages { get; set; } = 1;
    public IReadOnlyList<NetworkTelemetryObservationViewModel> Items { get; set; } = [];
    public IReadOnlyList<NetworkTelemetryBuildingRiskSummaryViewModel> BuildingRiskSummaries { get; set; } = [];
}

public class NetworkTelemetrySnapshotQueryRequest
{
    public string Search { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string Weekday { get; set; } = string.Empty;
    public string TimeSlot { get; set; } = string.Empty;
    public string SortBy { get; set; } = "observedAt";
    public string SortDirection { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public class NetworkTelemetrySnapshotPageViewModel
{
    public string Search { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string Weekday { get; set; } = string.Empty;
    public string TimeSlot { get; set; } = string.Empty;
    public string SortBy { get; set; } = "observedAt";
    public string SortDirection { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalCount { get; set; }
    public int TotalPages { get; set; } = 1;
    public IReadOnlyList<NetworkTelemetrySnapshotViewModel> Items { get; set; } = [];
}

public class NetworkTelemetryBuildingRiskSummaryViewModel
{
    public string BuildingExternalId { get; set; } = string.Empty;
    public int DeviceCount { get; set; }
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
    public int MaxRiskScore { get; set; }
    public string MaxRiskLevel { get; set; } = "low";
}

public class NetworkTelemetrySubnetRiskSummaryViewModel
{
    public string SubnetCidr { get; set; } = string.Empty;
    public int DeviceCount { get; set; }
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
    public int MaxRiskScore { get; set; }
    public string MaxRiskLevel { get; set; } = "low";
}
