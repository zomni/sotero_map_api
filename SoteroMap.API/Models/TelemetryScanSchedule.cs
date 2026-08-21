namespace SoteroMap.API.Models;

public class TelemetryScanSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = string.Empty;
    public string Cron { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "America/Santiago";
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}
