namespace SoteroMap.API.Models;

public class SyncedBuilding
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Campus { get; set; } = "sotero";
    public string ManualCampus { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ManualDisplayName { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string RealName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ResponsibleArea { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public double? CentroidLatitude { get; set; }
    public double? CentroidLongitude { get; set; }
    public bool HasInteriorMap { get; set; }
    public bool HasInventory { get; set; }
    public string MappingStatus { get; set; } = string.Empty;
    public string InventoryStatus { get; set; } = string.Empty;
    public string OperationalNotes { get; set; } = string.Empty;
    public string TechnicalNotes { get; set; } = string.Empty;
    public string LastUpdate { get; set; } = string.Empty;
    public string FloorsJson { get; set; } = "[]";
    public string ManualFloorsJson { get; set; } = string.Empty;
    public string FloorSummariesJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "[]";
    public string ContactsJson { get; set; } = "[]";
    public DateTime SyncedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<SyncedRoom> Rooms { get; set; } = new List<SyncedRoom>();
    public ICollection<SyncedEquipment> Equipments { get; set; } = new List<SyncedEquipment>();

    public string EffectiveCampus => string.IsNullOrWhiteSpace(ManualCampus) ? Campus : ManualCampus;
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(ManualDisplayName) ? DisplayName : ManualDisplayName;
    public string EffectiveFloorsJson => string.IsNullOrWhiteSpace(ManualFloorsJson) ? FloorsJson : ManualFloorsJson;
}
