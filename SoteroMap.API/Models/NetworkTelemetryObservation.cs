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
