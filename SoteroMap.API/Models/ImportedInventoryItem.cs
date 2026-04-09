namespace SoteroMap.API.Models;

public class ImportedInventoryItem
{
    public int Id { get; set; }
    public int RowNumber { get; set; }
    public string ItemNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Lot { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string UnitOrDepartment { get; set; } = string.Empty;
    public string OrganizationalUnit { get; set; } = string.Empty;
    public string ResponsibleUser { get; set; } = string.Empty;
    public string Run { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string AnnexPhone { get; set; } = string.Empty;
    public string ReplacedEquipment { get; set; } = string.Empty;
    public string TicketMda { get; set; } = string.Empty;
    public string Installer { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public string Rut { get; set; } = string.Empty;
    public string InventoryDate { get; set; } = string.Empty;
    public string InferredCategory { get; set; } = string.Empty;
    public string InferredStatus { get; set; } = string.Empty;
    public int? MatchedSyncedBuildingId { get; set; }
    public int? MatchedSyncedRoomId { get; set; }
    public string MatchedBuildingExternalId { get; set; } = string.Empty;
    public string MatchedRoomExternalId { get; set; } = string.Empty;
    public string MatchConfidence { get; set; } = string.Empty;
    public string MatchNotes { get; set; } = string.Empty;
    public string AssignedBuildingExternalId { get; set; } = string.Empty;
    public string AssignedRoomExternalId { get; set; } = string.Empty;
    public int? AssignedFloor { get; set; }
    public string AssignmentNotes { get; set; } = string.Empty;
    public DateTime? AssignmentUpdatedAtUtc { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
}
