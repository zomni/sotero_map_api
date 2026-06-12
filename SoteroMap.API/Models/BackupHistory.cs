namespace SoteroMap.API.Models;

public class BackupHistory
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "success";
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string CreatedByUsername { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
