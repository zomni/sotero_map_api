namespace SoteroMap.API.Models;

public class ScheduledScanRun
{
    public int Id { get; set; }
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public string? ErrorMessage { get; set; }
    public int? SnapshotId { get; set; }
    public NetworkTelemetrySnapshot? Snapshot { get; set; }
    public string ScheduledTimeLocal { get; set; } = string.Empty;
    public string ScheduledDayLocal { get; set; } = string.Empty;
    public int? DeviceCount { get; set; }
    public int? UserCount { get; set; }
    public string NormalizedCron { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
