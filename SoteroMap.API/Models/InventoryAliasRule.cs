namespace SoteroMap.API.Models;

public class InventoryAliasRule
{
    public int Id { get; set; }
    public string SourceText { get; set; } = string.Empty;
    public string NormalizedSourceText { get; set; } = string.Empty;
    public string TargetBuildingExternalId { get; set; } = string.Empty;
    public string TargetRoomExternalId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
