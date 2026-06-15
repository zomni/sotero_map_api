namespace SoteroMap.API.Models;

public class NetworkTelemetrySnapshot
{
    public int Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string RiskLevel { get; set; } = "unknown";
    public int RiskScore { get; set; }
    public int DeviceCount { get; set; }
    public int ConnectedUserCount { get; set; }
    public int HighRiskDeviceCount { get; set; }
    public int MediumRiskDeviceCount { get; set; }
    public int LowRiskDeviceCount { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? WindowEndUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string CreatedByUsername { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
