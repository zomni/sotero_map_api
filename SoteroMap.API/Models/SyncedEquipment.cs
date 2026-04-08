namespace SoteroMap.API.Models;

public class SyncedEquipment
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public int SyncedBuildingId { get; set; }
    public SyncedBuilding SyncedBuilding { get; set; } = null!;
    public int? SyncedRoomId { get; set; }
    public SyncedRoom? SyncedRoom { get; set; }
    public string BuildingExternalId { get; set; } = string.Empty;
    public string RoomExternalId { get; set; } = string.Empty;
    public int? Floor { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Subtype { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string InventoryCode { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
    public string ResponsiblePerson { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
    public string PurchaseDate { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string HistoryJson { get; set; } = "[]";
    public string Source { get; set; } = "frontend_sync";
    public DateTime SyncedAtUtc { get; set; } = DateTime.UtcNow;
}
