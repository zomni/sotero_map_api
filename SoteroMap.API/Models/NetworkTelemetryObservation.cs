namespace SoteroMap.API.Models;

public class NetworkTelemetryObservation
{
    public int Id { get; set; }
    public int NetworkTelemetrySnapshotId { get; set; }
    public NetworkTelemetrySnapshot NetworkTelemetrySnapshot { get; set; } = null!;
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
    public int? ImportedInventoryItemId { get; set; }
    public int? SyncedEquipmentId { get; set; }
    public int? AuthUserId { get; set; }
    public string Status { get; set; } = "observed";
    public string RiskLevel { get; set; } = "low";
    public int RiskScore { get; set; }
    public string RiskReasonsJson { get; set; } = "[]";
    public string RawJson { get; set; } = "{}";
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
